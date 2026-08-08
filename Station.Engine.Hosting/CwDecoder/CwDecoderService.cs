// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>On-demand receive-side CW decoder for RX0 post-AGC audio.</summary>
public sealed class CwDecoderService : IHostedService, IDisposable
{
    private const int RingCapacity = 1 << 17;
    private const int DrainSamples = 4096;
    private const int WorkerFaultLogSeconds = 10;
    private const int DropLogSeconds = 60;

    private readonly DspPipelineService _dsp;
    private readonly RadioService _radio;
    private readonly StreamingHub _hub;
    private readonly ILogger<CwDecoderService> _log;
    private readonly FloatSpscRing _ring = new(RingCapacity);
    private readonly AutoResetEvent _wake = new(false);
    private readonly float[] _drain = new float[DrainSamples];
    private readonly CwDecoderCore _decoder = new(DspPipelineService.AudioOutputRateHz, CwDefaults.PitchHz);
    private readonly CwBroadcastCadence _cadence = new(Stopwatch.Frequency);
    private readonly StringBuilder _batch = new(CwDecodedTextFrame.MaxTextBytes);
    private readonly Action<MorseDecodedSymbol> _decodedHandler;
    private readonly bool _lifecycleOnly;

    private CancellationTokenSource? _stop;
    private Task? _worker;
    private int _isCwMode;
    private int _isMox;
    private int _pitchHz = CwDefaults.PitchHz;
    private int _disposed;
    private long _droppedSamples;
    private long _lastLoggedDroppedSamples;
    private long _nextDropLogTimestamp;
    private long _nextWorkerFaultLogTimestamp;
    private double _confidenceSum;
    private int _confidenceCount;

    public CwDecoderService(
        DspPipelineService dsp,
        RadioService radio,
        StreamingHub hub,
        ILogger<CwDecoderService> log)
    {
        _dsp = dsp;
        _radio = radio;
        _hub = hub;
        _log = log;
        _decodedHandler = OnDecoded;
    }

    /// <summary>Lifecycle-only seam; does not subscribe to radio or DSP input.</summary>
    internal CwDecoderService(ILogger<CwDecoderService> log)
    {
        _dsp = null!;
        _radio = null!;
        _hub = null!;
        _log = log;
        _decodedHandler = OnDecoded;
        _lifecycleOnly = true;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_worker is not null) return Task.CompletedTask;
        if (!_lifecycleOnly)
        {
            UpdateRadioState(_radio.Snapshot());
            Volatile.Write(ref _isMox, _radio.IsMox ? 1 : 0);
            _dsp.RxAudioAvailable += OnRxAudioAvailable;
            _radio.StateChanged += OnRadioStateChanged;
            _radio.MoxChanged += OnMoxChanged;
            _hub.CwDecodeRequestChanged += OnGateChanged;
        }
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Factory.StartNew(
            () => WorkerLoop(_stop.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_lifecycleOnly)
        {
            _dsp.RxAudioAvailable -= OnRxAudioAvailable;
            _radio.StateChanged -= OnRadioStateChanged;
            _radio.MoxChanged -= OnMoxChanged;
            _hub.CwDecodeRequestChanged -= OnGateChanged;
        }
        CancellationTokenSource? stop = _stop;
        Task? worker = _worker;
        if (stop is null || worker is null) return;
        try { stop.Cancel(); }
        catch (ObjectDisposedException) { }
        SignalWake(_wake);
        try { await worker.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        stop.Dispose();
        _stop = null;
        _worker = null;
    }

    // DSP tick thread: gate reads, borrowed-buffer copy, preallocated signal.
    // Keep every other operation on the dedicated decoder worker.
    private void OnRxAudioAvailable(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples)
    {
        WriteTapCore(
            _hub.CwDecodeRequested,
            Volatile.Read(ref _isCwMode) != 0,
            Volatile.Read(ref _isMox) != 0,
            receiver,
            sampleRateHz,
            samples,
            _ring,
            _wake,
            ref _droppedSamples);
    }

    internal static int WriteTapCore(
        bool requested,
        bool isCwMode,
        bool isMox,
        int receiver,
        int sampleRateHz,
        ReadOnlyMemory<float> samples,
        FloatSpscRing ring,
        AutoResetEvent wake,
        ref long dropCounter)
    {
        if (!requested || !isCwMode || isMox) return 0;
        if (receiver != 0 || sampleRateHz != DspPipelineService.AudioOutputRateHz) return 0;
        int written = ring.Write(samples.Span);
        if (written < samples.Length)
            Interlocked.Add(ref dropCounter, samples.Length - written);
        if (written > 0) SignalWake(wake);
        return written;
    }

    private void OnRadioStateChanged(StateDto state)
    {
        UpdateRadioState(state);
        SignalWake(_wake);
    }

    private void UpdateRadioState(StateDto state)
    {
        Volatile.Write(ref _pitchHz, NormalizePitchHz(state.CwPitchHz));
        Volatile.Write(ref _isCwMode, state.Mode is RxMode.CWU or RxMode.CWL ? 1 : 0);
    }

    internal static int NormalizePitchHz(int pitchHz) =>
        pitchHz is > 0 and < (DspPipelineService.AudioOutputRateHz / 2)
            ? pitchHz
            : CwDefaults.PitchHz;

    private void OnMoxChanged(bool mox)
    {
        Volatile.Write(ref _isMox, mox ? 1 : 0);
        SignalWake(_wake);
    }

    private void OnGateChanged() => SignalWake(_wake);

    private void WorkerLoop(CancellationToken cancellationToken)
    {
        bool active = false;
        int appliedPitch = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                bool shouldRun = !_lifecycleOnly
                    && _hub.CwDecodeRequested
                    && Volatile.Read(ref _isCwMode) != 0
                    && Volatile.Read(ref _isMox) == 0;
                if (!shouldRun)
                {
                    if (active)
                    {
                        active = false;
                        TryBroadcast(force: true);
                        ResetPipeline();
                    }
                    _wake.WaitOne();
                    continue;
                }

                if (!active)
                {
                    active = true;
                    ResetPipeline();
                    long now = Stopwatch.GetTimestamp();
                    _cadence.Reset(now);
                }

                int pitch = Volatile.Read(ref _pitchHz);
                if (pitch != appliedPitch)
                {
                    _decoder.Retune(pitch);
                    appliedPitch = pitch;
                }

                int read = _ring.Read(_drain);
                long timestamp = Stopwatch.GetTimestamp();
                LogDropsIfDue(timestamp);
                if (read == 0)
                {
                    _wake.WaitOne();
                    continue;
                }

                _decoder.Process(_drain.AsSpan(0, read), _decodedHandler);
                TryBroadcast();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) break;
                LogWorkerFault(ex);
                active = false;
                appliedPitch = 0;
                ResetPipeline();
            }
        }

        ResetPipeline();
    }

    private void OnDecoded(MorseDecodedSymbol symbol)
    {
        if (_batch.Length + symbol.Text.Length > CwDecodedTextFrame.MaxTextBytes)
            TryBroadcast(force: true);
        _batch.Append(symbol.Text);
        _confidenceSum += symbol.Confidence;
        _confidenceCount++;
    }

    private void TryBroadcast(bool force = false)
    {
        if (_batch.Length == 0) return;
        long now = Stopwatch.GetTimestamp();
        if (force)
            _cadence.ConsumeForced(now);
        else if (!_cadence.TryTake(now))
            return;

        var frame = new CwDecodedTextFrame(
            _batch.ToString(),
            (ushort)Math.Clamp((int)Math.Round(_decoder.Wpm), 5, 50),
            (float)_decoder.SnrDb,
            (float)Math.Clamp(_confidenceSum / Math.Max(_confidenceCount, 1), 0, 1));
        _batch.Clear();
        _confidenceSum = 0;
        _confidenceCount = 0;
        try { _hub.Broadcast(in frame); }
        catch (Exception ex) { _log.LogWarning(ex, "cw.decoder broadcast failed"); }
    }

    private void ResetPipeline()
    {
        _ring.Clear();
        _decoder.Reset();
        _batch.Clear();
        _confidenceSum = 0;
        _confidenceCount = 0;
    }

    internal int BufferedSampleCount => _ring.Count;
    internal long DroppedSampleCount => Interlocked.Read(ref _droppedSamples);

    private void LogWorkerFault(Exception ex)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextWorkerFaultLogTimestamp) return;
        _nextWorkerFaultLogTimestamp = now + WorkerFaultLogSeconds * Stopwatch.Frequency;
        try { _log.LogWarning(ex, "cw.decoder worker recovered after a processing failure"); }
        catch { }
    }

    private void LogDropsIfDue(long now)
    {
        if (_nextDropLogTimestamp == 0)
        {
            _nextDropLogTimestamp = now + DropLogSeconds * Stopwatch.Frequency;
            return;
        }
        if (now < _nextDropLogTimestamp) return;
        _nextDropLogTimestamp = now + DropLogSeconds * Stopwatch.Frequency;
        long total = Interlocked.Read(ref _droppedSamples);
        long delta = total - _lastLoggedDroppedSamples;
        if (delta <= 0) return;
        _lastLoggedDroppedSamples = total;
        _log.LogWarning(
            "cw.decoder ring dropped {Delta} sample(s); total={Total}",
            delta,
            total);
    }

    private static void SignalWake(AutoResetEvent wake)
    {
        try { wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            try { _stop?.Cancel(); }
            catch (ObjectDisposedException) { }
            SignalWake(_wake);
        }
        finally
        {
            try { _stop?.Dispose(); }
            finally { _wake.Dispose(); }
        }
    }
}
