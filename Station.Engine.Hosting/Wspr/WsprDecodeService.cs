// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Engine-hosted WSPR decoder (docs/designs/wspr-return.md). Taps every
/// receiver's pre-mix demodulated audio, captures two-minute UTC slots, and
/// decodes them with the vendored GPL wsprd. Idle until a product client
/// configures it through <c>/api/wspr/decoder</c>; the configuration is a lease
/// that lapses 90 s after the client stops polling.
/// </summary>
/// <remarks>
/// Threads: the DSP thread only copies into per-receiver rings; a capture
/// worker decimates and assembles slots; a decode worker runs the serialised
/// native decode (which itself runs on a large-stack thread, see
/// <see cref="NativeWsprSlotDecoder"/>).
/// </remarks>
public sealed class WsprDecodeService : IHostedService, IDisposable
{
    private const int PumpIntervalMs = 250;
    private const int JoinTimeoutMs = 5_000;

    private readonly DspPipelineService? _dsp;
    private readonly RadioService? _radio;
    private readonly ILogger<WsprDecodeService> _log;
    private readonly TimeProvider _time;
    private readonly WsprDecoderCore _core;
    private readonly AutoResetEvent _captureWake = new(false);
    private readonly AutoResetEvent _decodeWake = new(false);

    private CancellationTokenSource? _stop;
    private Thread? _captureThread;
    private Thread? _decodeThread;
    private int _disposed;
    private int _tunActive;
    private int _mox;

    public WsprDecodeService(
        DspPipelineService dsp,
        RadioService radio,
        ILogger<WsprDecodeService> log)
        : this(dsp, radio, log, TimeProvider.System, new NativeWsprSlotDecoder(NativeWsprSlotDecoder.DefaultDataDirectory()))
    {
    }

    /// <summary>Test seam: no DSP/radio wiring when they are null.</summary>
    internal WsprDecodeService(
        DspPipelineService? dsp,
        RadioService? radio,
        ILogger<WsprDecodeService> log,
        TimeProvider time,
        IWsprSlotDecoder decoder)
    {
        _dsp = dsp;
        _radio = radio;
        _log = log;
        _time = time;
        _core = new WsprDecoderCore(decoder);
    }

    internal WsprDecoderCore Core => _core;

    private long UtcNowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public WsprDecoderStatus Status() => _core.Status(UtcNowMs);

    public WsprDecoderStatus Configure(WsprDecoderConfig config)
    {
        var status = _core.Configure(config, UtcNowMs);
        _log.LogInformation(
            "wspr.decoder config enabled={Enabled} receivers={Receivers} depth={Depth} offsetMs={Offset} available={Available}",
            status.Enabled,
            string.Join(',', status.Receivers.Select(r => r.Receiver)),
            status.Depth,
            config.ClockOffsetMs,
            status.Available);
        SignalWake(_captureWake);
        return status;
    }

    public WsprSlotBatch SlotsAfter(long after) => _core.SlotsAfter(after, UtcNowMs);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_captureThread is not null) return Task.CompletedTask;
        if (_dsp is not null && _radio is not null)
        {
            OnRadioStateChanged(_radio.Snapshot());
            Volatile.Write(ref _mox, _radio.IsMox ? 1 : 0);
            PublishTransmit();
            // ReceiverAudioAvailable carries every receiver, RX1 (index 0)
            // included, pre-mix and pre-mute; RxAudioAvailable is the speaker mix.
            _dsp.ReceiverAudioAvailable += OnReceiverAudio;
            _radio.StateChanged += OnRadioStateChanged;
            _radio.MoxChanged += OnMoxChanged;
            _radio.TunActiveChanged += OnTunChanged;
        }

        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _stop.Token;
        _captureThread = new Thread(() => CaptureLoop(token))
        {
            IsBackground = true,
            Name = "wspr-capture",
        };
        _decodeThread = new Thread(() => DecodeLoop(token))
        {
            IsBackground = true,
            Name = "wspr-decode",
        };
        _captureThread.Start();
        _decodeThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_dsp is not null && _radio is not null)
        {
            _dsp.ReceiverAudioAvailable -= OnReceiverAudio;
            _radio.StateChanged -= OnRadioStateChanged;
            _radio.MoxChanged -= OnMoxChanged;
            _radio.TunActiveChanged -= OnTunChanged;
        }
        var stop = _stop;
        if (stop is null) return Task.CompletedTask;
        try { stop.Cancel(); }
        catch (ObjectDisposedException) { }
        SignalWake(_captureWake);
        SignalWake(_decodeWake);
        // A native decode cannot be interrupted; it finishes within seconds.
        // Background threads never block process exit if one is still running.
        if (_captureThread?.Join(JoinTimeoutMs) == false)
            _log.LogWarning("wspr.decoder capture worker did not stop within {Ms} ms", JoinTimeoutMs);
        if (_decodeThread?.Join(JoinTimeoutMs) == false)
            _log.LogWarning("wspr.decoder decode worker did not stop within {Ms} ms", JoinTimeoutMs);
        _captureThread = null;
        _decodeThread = null;
        stop.Dispose();
        _stop = null;
        return Task.CompletedTask;
    }

    // DSP thread: gate check + copy + signal only.
    private void OnReceiverAudio(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples)
    {
        if (_core.OnAudio(receiver, sampleRateHz, samples.Span))
            SignalWake(_captureWake);
    }

    private void OnRadioStateChanged(StateDto state) => _core.OnRadioState(ProjectTuning(state), UtcNowMs);

    internal static IReadOnlyList<WsprReceiverTuning> ProjectTuning(StateDto state)
    {
        var receivers = state.Receivers;
        if (receivers is null || receivers.Count == 0)
            return [new WsprReceiverTuning(true, state.VfoHz, state.Mode)];

        var tuning = new WsprReceiverTuning[Math.Min(receivers.Count, WireContract.MaxReceivers)];
        foreach (var receiver in receivers)
        {
            if ((uint)receiver.Index >= (uint)tuning.Length) continue;
            // RX1 always runs; other DDCs only count while enabled.
            bool present = receiver.Index == 0 || receiver.Enabled;
            tuning[receiver.Index] = new WsprReceiverTuning(present, receiver.VfoHz, receiver.Mode);
        }
        if (!tuning[0].Present)
            tuning[0] = new WsprReceiverTuning(true, state.VfoHz, state.Mode);
        return tuning;
    }

    private void OnMoxChanged(bool on)
    {
        Volatile.Write(ref _mox, on ? 1 : 0);
        PublishTransmit();
    }

    private void OnTunChanged(bool on)
    {
        Volatile.Write(ref _tunActive, on ? 1 : 0);
        PublishTransmit();
    }

    private void PublishTransmit()
    {
        _core.OnTransmit(Volatile.Read(ref _mox) != 0 || Volatile.Read(ref _tunActive) != 0);
        SignalWake(_captureWake);
    }

    private void CaptureLoop(CancellationToken token)
    {
        long nextFaultLog = 0;
        long lastDropped = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Wake on audio, or at least every PumpIntervalMs so stalled
                // captures flush and the lease expires even with no audio.
                _captureWake.WaitOne(PumpIntervalMs);
                if (token.IsCancellationRequested) break;
                if (_core.Pump(UtcNowMs)) SignalWake(_decodeWake);

                long dropped = _core.DroppedSamples;
                if (dropped != lastDropped)
                {
                    _log.LogWarning("wspr.decoder ring dropped {Delta} sample(s); total={Total}", dropped - lastDropped, dropped);
                    lastDropped = dropped;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                nextFaultLog = LogFault(ex, "capture", nextFaultLog);
            }
        }
    }

    private void DecodeLoop(CancellationToken token)
    {
        long nextFaultLog = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _decodeWake.WaitOne(1_000);
                while (!token.IsCancellationRequested
                       && _core.TryTakeCaptured(UtcNowMs, out var captured, out bool deep, out long current))
                {
                    var decoded = _core.Decode(captured, deep, current, () => Environment.TickCount64);
                    _log.LogInformation(
                        "wspr.decoder slot rx={Receiver} start={Start} dialHz={Dial} outcome={Outcome} skip={Skip} spots={Spots} ms={Ms}",
                        decoded.Receiver,
                        decoded.SlotStartUnixMs,
                        decoded.DialHz,
                        decoded.Outcome,
                        decoded.SkipReason,
                        decoded.Spots.Count,
                        decoded.DecodeMs);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                nextFaultLog = LogFault(ex, "decode", nextFaultLog);
            }
        }
    }

    private long LogFault(Exception ex, string worker, long nextLog)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < nextLog) return nextLog;
        try { _log.LogWarning(ex, "wspr.decoder {Worker} worker recovered after a failure", worker); }
        catch { }
        return now + 10 * Stopwatch.Frequency;
    }

    private static void SignalWake(AutoResetEvent wake)
    {
        try { wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stop?.Cancel(); }
        catch (ObjectDisposedException) { }
        SignalWake(_captureWake);
        SignalWake(_decodeWake);
        _captureThread?.Join(JoinTimeoutMs);
        _decodeThread?.Join(JoinTimeoutMs);
        _stop?.Dispose();
        _captureWake.Dispose();
        _decodeWake.Dispose();
    }
}
