// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>On-demand receive-side CW decoder. Each receiver is copied from its own pre-mix tap.</summary>
public sealed class CwDecoderService : IHostedService, IDisposable
{
    private const int RingCapacity = 1 << 17;
    // The CW quality harness feeds this same drain. Internal via InternalsVisibleTo.
    internal const int DrainSamples = 4096;
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
    private readonly CwStatusSchedule _status = new(Stopwatch.Frequency);
    private readonly StringBuilder _batch = new(CwDecodedTextFrame.MaxTextBytes);
    private readonly Action<MorseDecodedSymbol> _decodedHandler;
    private readonly bool _lifecycleOnly;

    private CancellationTokenSource? _stop;
    private Task? _worker;
    private int _isCwMode;
    private readonly int[] _cwModeByReceiver = new int[WireContract.MaxReceivers];
    private readonly long[] _acceptedSamples = new long[WireContract.MaxReceivers];
    private readonly ExtraLane?[] _lanes = new ExtraLane?[WireContract.MaxReceivers];
    private int _isMox;
    private long _transmitSequence;
    private long _appliedTransmitSequence;
    private int _pitchHz = CwDefaults.PitchHz;
    private StreamingHub.CwDecodeAim _aim = StreamingHub.CwDecodeAim.FollowPitch;
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
    internal CwDecoderService(ILogger<CwDecoderService> log, CwDecoderCore? decoder = null, FloatSpscRing? ring = null)
    {
        _dsp = null!;
        _radio = null!;
        _hub = null!;
        _log = log;
        _decodedHandler = OnDecoded;
        _lifecycleOnly = true;
        if (decoder is not null) _decoder = decoder;
        if (ring is not null) _ring = ring;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_worker is not null) return Task.CompletedTask;
        if (!_lifecycleOnly)
        {
            UpdateRadioState(_radio.Snapshot());
            Volatile.Write(ref _isMox, _radio.IsMox ? 1 : 0);
            // ReceiverAudioAvailable is the per-receiver tap, RX1 included,
            // before the speaker mix and before mute. RxAudioAvailable is the
            // mixed bus and only ever names receiver 0.
            _dsp.ReceiverAudioAvailable += OnRxAudioAvailable;
            _radio.StateChanged += OnRadioStateChanged;
            _radio.MoxChanged += OnMoxChanged;
            _hub.CwDecodeRequestChanged += OnGateChanged;
            _hub.CwDecodeTargetChanged += OnTargetChanged;
            OnTargetChanged();
            EnsureSubscribedLanes();
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
            _dsp.ReceiverAudioAvailable -= OnRxAudioAvailable;
            _radio.StateChanged -= OnRadioStateChanged;
            _radio.MoxChanged -= OnMoxChanged;
            _hub.CwDecodeRequestChanged -= OnGateChanged;
            _hub.CwDecodeTargetChanged -= OnTargetChanged;
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
        if ((uint)receiver >= WireContract.MaxReceivers) return;
        bool requested = _hub.CwDecodeRequestedOn(receiver);
        bool cw = receiver == 0
            ? Volatile.Read(ref _isCwMode) != 0
            : Volatile.Read(ref _cwModeByReceiver[receiver]) != 0;
        bool mox = Volatile.Read(ref _isMox) != 0;
        int written;
        if (receiver == 0)
        {
            written = WriteTapCore(
                requested, cw, mox, receiver, sampleRateHz, samples,
                _ring, _wake, ref _droppedSamples, subscribedReceiver: 0);
        }
        else
        {
            // The audio callback never allocates. A lane appears only after the
            // subscriber edge, on the gate callback.
            ExtraLane? lane = Volatile.Read(ref _lanes[receiver]);
            if (lane is null) return;
            written = WriteTapCore(
                requested, cw, mox, receiver, sampleRateHz, samples,
                lane.Ring, _wake, ref lane.Dropped, subscribedReceiver: receiver);
        }
        if (written > 0)
            Interlocked.Add(ref _acceptedSamples[receiver], written);
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
        ref long dropCounter,
        int subscribedReceiver = 0)
    {
        if (!requested || !isCwMode || isMox) return 0;
        if (receiver != subscribedReceiver || sampleRateHz != DspPipelineService.AudioOutputRateHz) return 0;
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
        CopyReceiverCwModes(state, _cwModeByReceiver);
        Volatile.Write(ref _isCwMode, _cwModeByReceiver[0]);
    }

    /// <summary>
    /// RX1 follows the flat radio mode. Every other index follows that
    /// receiver's own row, and only while the row is enabled and in CW.
    /// A missing list leaves every index above RX1 closed.
    /// </summary>
    internal static void CopyReceiverCwModes(StateDto state, int[] modes)
    {
        if (modes.Length == 0) return;
        Volatile.Write(ref modes[0], state.Mode is RxMode.CWU or RxMode.CWL ? 1 : 0);
        for (int i = 1; i < modes.Length; i++)
            Volatile.Write(ref modes[i], 0);
        if (state.Receivers is null) return;
        foreach (ReceiverDto rx in state.Receivers)
        {
            if ((uint)rx.Index >= (uint)modes.Length || rx.Index == 0) continue;
            int cw = rx.Enabled && rx.Name is null && rx.Mode is RxMode.CWU or RxMode.CWL ? 1 : 0;
            Volatile.Write(ref modes[rx.Index], cw);
        }
    }

    internal static int NormalizePitchHz(int pitchHz) =>
        pitchHz is > 0 and < (DspPipelineService.AudioOutputRateHz / 2)
            ? pitchHz
            : CwDefaults.PitchHz;

    /// <summary>0 follows the radio CW pitch. Any other value is clamped to the request range.</summary>
    internal static int NormalizeTargetHz(int targetHz) =>
        targetHz <= 0
            ? 0
            : Math.Clamp(targetHz, CwDecoderRequest.MinTargetHz, CwDecoderRequest.MaxTargetHz);

    internal readonly record struct CwAcquisition(int CenterHz, bool Locked, double SearchHalfWidthHz);

    internal static CwAcquisition ResolveAcquisition(int radioPitchHz, int targetHz, bool locked)
    {
        int center = NormalizeTargetHz(targetHz);
        bool clicked = center != 0;
        if (!clicked) center = NormalizePitchHz(radioPitchHz);
        // With nothing clicked the search stays in the pitch span. Only a
        // clicked target opens the wide search.
        double half = locked
            ? 0
            : clicked ? GoertzelDetector.UnlockedSearchHalfWidthHz : GoertzelDetector.PitchSearchHalfWidthHz;
        return new CwAcquisition(center, locked, half);
    }

    /// <summary>
    /// Retune the live core when the radio pitch, requested tone, or lock changes.
    /// Returns false when the acquisition already matches.
    /// </summary>
    internal static bool ApplyAcquisition(
        CwDecoderCore decoder,
        ref int appliedCenter,
        ref int appliedLocked,
        int radioPitchHz,
        int targetHz,
        bool locked,
        Action<MorseDecodedSymbol>? onDecoded = null)
    {
        long sequence = 0;
        return ApplyAcquisition(
            decoder,
            ref appliedCenter,
            ref appliedLocked,
            ref sequence,
            radioPitchHz,
            targetHz,
            locked,
            aimSequence: 0,
            onDecoded);
    }

    /// <summary>
    /// Retune the live core when the radio pitch, requested tone, or lock changes.
    /// Returns false when this aim is already in effect.
    /// A later click on the stored centre still retargets when the decoder has
    /// acquired a different tone: the request is compared with the tone being
    /// copied, not with the centre already stored. The same aim, repeated while
    /// audio is running, does not: auto-acquisition is allowed to leave that centre.
    /// </summary>
    internal static bool ApplyAcquisition(
        CwDecoderCore decoder,
        ref int appliedCenter,
        ref int appliedLocked,
        ref long appliedSequence,
        int radioPitchHz,
        int targetHz,
        bool locked,
        long aimSequence,
        Action<MorseDecodedSymbol>? onDecoded = null)
    {
        CwAcquisition plan = ResolveAcquisition(radioPitchHz, targetHz, locked);
        int lockedBit = plan.Locked ? 1 : 0;
        if (aimSequence == appliedSequence
            && plan.CenterHz == appliedCenter
            && lockedBit == appliedLocked)
            return false;

        double decodedHz = decoder.TrackedToneHz;
        bool onDecodedTone = Math.Abs(plan.CenterHz - decodedHz) <= CwDecoderCore.OperatorRetargetResetHz;
        if (onDecodedTone && lockedBit == appliedLocked && plan.CenterHz == appliedCenter)
        {
            appliedSequence = aimSequence;
            return false;
        }

        decoder.Retune(plan.CenterHz, plan.Locked, plan.SearchHalfWidthHz, onDecoded);
        appliedCenter = plan.CenterHz;
        appliedLocked = lockedBit;
        appliedSequence = aimSequence;
        return true;
    }

    internal void OnMoxChanged(bool mox)
    {
        Volatile.Write(ref _isMox, mox ? 1 : 0);
        if (mox) Interlocked.Increment(ref _transmitSequence);
        SignalWake(_wake);
    }

    private void OnGateChanged()
    {
        EnsureSubscribedLanes();
        SignalWake(_wake);
    }

    private void OnTargetChanged()
    {
        // The callback reads the hub, then stores. A newer callback can land
        // between those two steps; only a higher sequence replaces the aim.
        TryStoreAim(ref _aim, _hub.ReadCwDecodeAim());
        for (int receiver = 1; receiver < _lanes.Length; receiver++)
        {
            ExtraLane? lane = Volatile.Read(ref _lanes[receiver]);
            if (lane is null) continue;
            TryStoreAim(ref lane.Aim, _hub.ReadCwDecodeAim(receiver));
        }
        SignalWake(_wake);
    }

    private void EnsureSubscribedLanes()
    {
        if (_lifecycleOnly) return;
        for (int receiver = 1; receiver < _lanes.Length; receiver++)
        {
            if (!_hub.CwDecodeRequestedOn(receiver)) continue;
            ExtraLane published = PublishLane(receiver);
            // Read the hub after the lane is visible. A target callback that
            // ran while the lane was still null is in the hub now, and a newer
            // callback that lands after this read replaces it by sequence.
            TryStoreAim(ref published.Aim, _hub.ReadCwDecodeAim(receiver));
        }
    }

    /// <summary>
    /// Publish a lane once. A second subscriber keeps the ring that is already
    /// audible. The caller stores the aim after this returns.
    /// </summary>
    private ExtraLane PublishLane(int receiver)
    {
        if (Volatile.Read(ref _lanes[receiver]) is null)
        {
            var created = new ExtraLane(receiver);
            Interlocked.CompareExchange(ref _lanes[receiver], created, null);
        }
        return Volatile.Read(ref _lanes[receiver])!;
    }

    /// <summary>Test seam for the publish. Returns the identity of the lane that stayed published.</summary>
    internal bool TryPublishExtraLane(int receiver, StreamingHub.CwDecodeAim aimAfterPublish, out int identity)
    {
        identity = 0;
        if (receiver <= 0 || (uint)receiver >= (uint)_lanes.Length) return false;
        ExtraLane published = PublishLane(receiver);
        TryStoreAim(ref published.Aim, aimAfterPublish);
        identity = RuntimeHelpers.GetHashCode(published);
        return true;
    }

    internal long? LaneAimSequenceForTest(int receiver)
    {
        if ((uint)receiver >= (uint)_lanes.Length) return null;
        return Volatile.Read(ref _lanes[receiver])?.Aim.Sequence;
    }

    internal long AcceptedSamplesForTest(int receiver) =>
        (uint)receiver >= (uint)_acceptedSamples.Length
            ? 0
            : Interlocked.Read(ref _acceptedSamples[receiver]);

    internal void SetReceiverCwForTest(int receiver, bool cw)
    {
        if ((uint)receiver >= (uint)_cwModeByReceiver.Length) return;
        int bit = cw ? 1 : 0;
        Volatile.Write(ref _cwModeByReceiver[receiver], bit);
        if (receiver == 0) Volatile.Write(ref _isCwMode, bit);
    }

    /// <summary>
    /// The wait until the next extra-receiver status heartbeat. A negative
    /// <paramref name="wait"/> is "no deadline yet" and must not swallow the heartbeat.
    /// </summary>
    internal static int CombineIdleWait(int wait, int heartbeatMs)
    {
        if (heartbeatMs <= 0) heartbeatMs = 1000;
        if (wait == Timeout.Infinite || heartbeatMs < wait) return heartbeatMs;
        return wait;
    }

    /// <summary>
    /// Store the aim a target callback observed. A callback still holding an
    /// older sequence cannot replace a newer one.
    /// </summary>
    internal static bool TryStoreAim(ref StreamingHub.CwDecodeAim slot, StreamingHub.CwDecodeAim observed) =>
        StreamingHub.CwDecodeAim.TryStoreNewer(ref slot, observed, out _);

    private void WorkerLoop(CancellationToken cancellationToken)
    {
        bool active = false;
        int appliedCenter = 0;
        int appliedLocked = -1;
        long appliedSequence = -1;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PumpExtraLanes();
                bool wasActive = active;
                long previousTransmit = _appliedTransmitSequence;
                bool shouldRun = SynchronizeReceiveState(
                    !_lifecycleOnly && _hub.CwDecodeRequestedOn(0),
                    Volatile.Read(ref _isCwMode) != 0,
                    ref active);
                if (!shouldRun)
                {
                    int extraWait = ExtraIdleWaitMs();
                    if (extraWait == Timeout.Infinite) _wake.WaitOne();
                    else _wake.WaitOne(extraWait);
                    continue;
                }

                bool justActivated = !wasActive || previousTransmit != _appliedTransmitSequence;
                if (justActivated)
                {
                    appliedCenter = 0;
                    appliedLocked = -1;
                    appliedSequence = -1;
                    _cadence.Reset(Stopwatch.GetTimestamp());
                    _status.Reset();
                }

                StreamingHub.CwDecodeAim aim = Volatile.Read(ref _aim);
                bool acquisitionChanged = ApplyAcquisition(
                    _decoder,
                    ref appliedCenter,
                    ref appliedLocked,
                    ref appliedSequence,
                    Volatile.Read(ref _pitchHz),
                    aim.TargetHz,
                    aim.Locked,
                    aim.Sequence,
                    _decodedHandler);

                int read = _ring.Read(_drain);
                long timestamp = Stopwatch.GetTimestamp();
                LogDropsIfDue(timestamp);
                if (read > 0)
                    _decoder.Process(_drain.AsSpan(0, read), _decodedHandler);

                // Copy stays on the 10 Hz cadence. An empty status frame is a
                // separate clock: once when the decoder wakes, again when the
                // target, lock, or acquired tone moves, and about once a second
                // while the band is quiet so the client can learn the tone.
                bool sentText = TryBroadcast();
                int pitch = ReportedPitchHz();
                bool searchLocked = _decoder.SearchLocked;
                bool toneChanged = _status.ToneChanged(pitch, searchLocked);
                if (sentText)
                    _status.MarkSent(timestamp, pitch, searchLocked);
                else if (justActivated || acquisitionChanged || toneChanged
                    || (_batch.Length == 0 && _status.HeartbeatDue(timestamp)))
                {
                    EmitStatus(pitch, searchLocked);
                    _status.MarkSent(timestamp, pitch, searchLocked);
                }

                if (read == 0)
                {
                    // Pending copy waits for the next audio block, the same as
                    // before. Silence waits only until the status heartbeat.
                    int waitMs;
                    if (_batch.Length > 0)
                        waitMs = Timeout.Infinite;
                    else
                    {
                        waitMs = _status.MillisecondsUntilHeartbeat(Stopwatch.GetTimestamp());
                        if (waitMs <= 0) waitMs = 1000;
                    }
                    int extraWait = ExtraIdleWaitMs();
                    if (extraWait == 0) waitMs = 0;
                    else if (extraWait > 0 && (waitMs == Timeout.Infinite || extraWait < waitMs))
                        waitMs = extraWait;
                    _wake.WaitOne(waitMs);
                }
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
                appliedCenter = 0;
                appliedLocked = -1;
                appliedSequence = -1;
                _status.Reset();
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

    private bool TryBroadcast(bool force = false)
    {
        // Empty copy is a status frame, not a text frame. The 10 Hz cadence
        // still gates characters only.
        if (_batch.Length == 0) return false;
        long now = Stopwatch.GetTimestamp();
        if (force)
            _cadence.ConsumeForced(now);
        else if (!_cadence.TryTake(now))
            return false;

        var frame = new CwDecodedTextFrame(
            _batch.ToString(),
            (ushort)Math.Clamp((int)Math.Round(_decoder.Wpm), 5, 50),
            (float)_decoder.SnrDb,
            (float)Math.Clamp(_confidenceSum / Math.Max(_confidenceCount, 1), 0, 1),
            ReportedPitchHz(),
            _decoder.SearchLocked);
        _batch.Clear();
        _confidenceSum = 0;
        _confidenceCount = 0;
        try { _hub.Broadcast(in frame); }
        catch (Exception ex) { _log.LogWarning(ex, "cw.decoder broadcast failed"); }
        return true;
    }

    private void EmitStatus(int pitchHz, bool locked)
    {
        float confidence = _confidenceCount > 0
            ? (float)Math.Clamp(_confidenceSum / _confidenceCount, 0, 1)
            : 0f;
        var frame = CreateStatusFrame(
            (int)Math.Round(_decoder.Wpm),
            _decoder.SnrDb,
            confidence,
            pitchHz,
            locked);
        try { _hub.Broadcast(in frame); }
        catch (Exception ex) { _log.LogWarning(ex, "cw.decoder status broadcast failed"); }
    }

    private int ReportedPitchHz() => PitchHzForDecodedTone(_decoder.TrackedToneHz);

    /// <summary>
    /// Pitch written on a decode frame. <paramref name="decodedToneHz"/> is the
    /// bin being copied, never a carrier the search followed.
    /// </summary>
    internal static int PitchHzForDecodedTone(double decodedToneHz)
    {
        int trackedHz = (int)Math.Round(decodedToneHz, MidpointRounding.AwayFromZero);
        return trackedHz < 0 ? 0 : trackedHz;
    }

    /// <summary>Empty-text frame carrying the tone the decoder is actually on.</summary>
    internal static CwDecodedTextFrame CreateStatusFrame(
        int wpm,
        double snrDb,
        float confidence,
        int pitchHz,
        bool locked,
        int receiver = 0) =>
        new(
            string.Empty,
            (ushort)Math.Clamp(wpm, 5, 50),
            (float)snrDb,
            confidence,
            pitchHz,
            locked,
            receiver);

    /// <summary>
    /// Worker-owned activation transition, also exercised without a radio by the
    /// receive harness. Remember the MOX edge even if an entire short over occurs
    /// between two worker drains; its queued audio must never train the decoder.
    /// </summary>
    internal bool SynchronizeReceiveState(bool requested, bool cwMode, ref bool active)
    {
        long transmit = Interlocked.Read(ref _transmitSequence);
        bool ownTransmit = Volatile.Read(ref _isMox) != 0;
        if (transmit != _appliedTransmitSequence)
        {
            if (active && !_lifecycleOnly) TryBroadcast(force: true);
            _appliedTransmitSequence = transmit;
            PauseForOwnTransmit();
            active = false;
            _status.Reset();
        }
        bool shouldRun = requested && cwMode && !ownTransmit;
        if (!shouldRun)
        {
            if (active)
            {
                active = false;
                if (!_lifecycleOnly) TryBroadcast(force: true);
                if (!ownTransmit) ResetPipeline();
                _status.Reset();
            }
            return false;
        }
        if (!active)
        {
            active = true;
            if (_decoder.ConsumeTransmitResume()) ClearTraffic();
            else ResetPipeline();
        }
        return true;
    }

    private void PauseForOwnTransmit()
    {
        ClearTraffic();
        _decoder.NoteOwnTransmit();
    }

    private void ClearTraffic()
    {
        _ring.Clear();
        _batch.Clear();
        _confidenceSum = 0;
        _confidenceCount = 0;
    }

    private void ResetPipeline()
    {
        ClearTraffic();
        _decoder.Reset();
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

    private void PumpExtraLanes()
    {
        if (_lifecycleOnly) return;
        long transmit = Interlocked.Read(ref _transmitSequence);
        bool ownTransmit = Volatile.Read(ref _isMox) != 0;
        long now = Stopwatch.GetTimestamp();
        for (int receiver = 1; receiver < _lanes.Length; receiver++)
        {
            ExtraLane? lane = Volatile.Read(ref _lanes[receiver]);
            if (lane is null) continue;
            bool requested = _hub.CwDecodeRequestedOn(receiver);
            bool cw = Volatile.Read(ref _cwModeByReceiver[receiver]) != 0;
            if (transmit != lane.AppliedTransmit)
            {
                if (lane.Active) TryBroadcastLane(lane, force: true);
                lane.AppliedTransmit = transmit;
                lane.Decoder.NoteOwnTransmit();
                lane.ClearTraffic();
                lane.Active = false;
                lane.Status.Reset();
            }

            if (!requested || !cw || ownTransmit)
            {
                if (lane.Active)
                {
                    lane.Active = false;
                    TryBroadcastLane(lane, force: true);
                    if (!ownTransmit) lane.ResetPipeline();
                    lane.Status.Reset();
                }
                continue;
            }

            bool justActivated = !lane.Active;
            if (!lane.Active)
            {
                lane.Active = true;
                if (lane.Decoder.ConsumeTransmitResume()) lane.ClearTraffic();
                else lane.ResetPipeline();
                lane.AppliedCenter = 0;
                lane.AppliedLocked = -1;
                lane.AppliedSequence = -1;
                lane.Cadence.Reset(now);
                lane.Status.Reset();
            }

            StreamingHub.CwDecodeAim aim = lane.Aim;
            bool acquisitionChanged = ApplyAcquisition(
                lane.Decoder,
                ref lane.AppliedCenter,
                ref lane.AppliedLocked,
                ref lane.AppliedSequence,
                Volatile.Read(ref _pitchHz),
                aim.TargetHz,
                aim.Locked,
                aim.Sequence,
                symbol => OnLaneDecoded(lane, symbol));

            int read = lane.Ring.Read(lane.Drain);
            if (read > 0)
                lane.Decoder.Process(lane.Drain.AsSpan(0, read), symbol => OnLaneDecoded(lane, symbol));

            bool sentText = TryBroadcastLane(lane, force: false);
            int pitch = PitchHzForDecodedTone(lane.Decoder.TrackedToneHz);
            bool searchLocked = lane.Decoder.SearchLocked;
            bool toneChanged = lane.Status.ToneChanged(pitch, searchLocked);
            if (sentText)
                lane.Status.MarkSent(now, pitch, searchLocked);
            else if (justActivated || acquisitionChanged || toneChanged
                || (lane.Batch.Length == 0 && lane.Status.HeartbeatDue(now)))
            {
                EmitLaneStatus(lane, pitch, searchLocked);
                lane.Status.MarkSent(now, pitch, searchLocked);
            }
        }
    }

    private int ExtraIdleWaitMs()
    {
        if (_lifecycleOnly) return Timeout.Infinite;
        int wait = Timeout.Infinite;
        long now = Stopwatch.GetTimestamp();
        bool ownTransmit = Volatile.Read(ref _isMox) != 0;
        for (int receiver = 1; receiver < _lanes.Length; receiver++)
        {
            ExtraLane? lane = Volatile.Read(ref _lanes[receiver]);
            if (lane is null || !lane.Active) continue;
            if (!_hub.CwDecodeRequestedOn(receiver) || ownTransmit) continue;
            if (lane.Ring.Count > 0) return 0;
            if (lane.Batch.Length > 0) continue;
            int heartbeat = lane.Status.MillisecondsUntilHeartbeat(now);
            wait = CombineIdleWait(wait, heartbeat);
        }
        return wait;
    }

    private void OnLaneDecoded(ExtraLane lane, MorseDecodedSymbol symbol)
    {
        if (lane.Batch.Length + symbol.Text.Length > CwDecodedTextFrame.MaxTextBytes)
            TryBroadcastLane(lane, force: true);
        lane.Batch.Append(symbol.Text);
        lane.ConfidenceSum += symbol.Confidence;
        lane.ConfidenceCount++;
    }

    private bool TryBroadcastLane(ExtraLane lane, bool force)
    {
        if (lane.Batch.Length == 0) return false;
        long now = Stopwatch.GetTimestamp();
        if (force) lane.Cadence.ConsumeForced(now);
        else if (!lane.Cadence.TryTake(now)) return false;

        var frame = new CwDecodedTextFrame(
            lane.Batch.ToString(),
            (ushort)Math.Clamp((int)Math.Round(lane.Decoder.Wpm), 5, 50),
            (float)lane.Decoder.SnrDb,
            (float)Math.Clamp(lane.ConfidenceSum / Math.Max(lane.ConfidenceCount, 1), 0, 1),
            PitchHzForDecodedTone(lane.Decoder.TrackedToneHz),
            lane.Decoder.SearchLocked,
            lane.Receiver);
        lane.Batch.Clear();
        lane.ConfidenceSum = 0;
        lane.ConfidenceCount = 0;
        try { _hub.Broadcast(in frame); }
        catch (Exception ex) { _log.LogWarning(ex, "cw.decoder broadcast failed"); }
        return true;
    }

    private void EmitLaneStatus(ExtraLane lane, int pitchHz, bool locked)
    {
        float confidence = lane.ConfidenceCount > 0
            ? (float)Math.Clamp(lane.ConfidenceSum / lane.ConfidenceCount, 0, 1)
            : 0f;
        var frame = CreateStatusFrame(
            (int)Math.Round(lane.Decoder.Wpm),
            lane.Decoder.SnrDb,
            confidence,
            pitchHz,
            locked,
            lane.Receiver);
        try { _hub.Broadcast(in frame); }
        catch (Exception ex) { _log.LogWarning(ex, "cw.decoder status broadcast failed"); }
    }

    private static void SignalWake(AutoResetEvent wake)
    {
        try { wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// One decoder, ring, and cadence for a receiver other than RX1.
    /// RX1 keeps the fields the existing worker already owns. Created only
    /// when that receiver gains a subscriber, and kept for the process.
    /// </summary>
    private sealed class ExtraLane(int receiver)
    {
        public int Receiver { get; } = receiver;
        public FloatSpscRing Ring { get; } = new(RingCapacity);
        public CwDecoderCore Decoder { get; } = new(DspPipelineService.AudioOutputRateHz, CwDefaults.PitchHz);
        public CwBroadcastCadence Cadence { get; } = new(Stopwatch.Frequency);
        public CwStatusSchedule Status { get; } = new(Stopwatch.Frequency);
        public StringBuilder Batch { get; } = new(CwDecodedTextFrame.MaxTextBytes);
        public float[] Drain { get; } = new float[DrainSamples];
        public StreamingHub.CwDecodeAim Aim = StreamingHub.CwDecodeAim.FollowPitch;
        public long Dropped;
        public int AppliedCenter;
        public int AppliedLocked = -1;
        public long AppliedSequence = -1;
        public long AppliedTransmit;
        public bool Active;
        public double ConfidenceSum;
        public int ConfidenceCount;

        public void ClearTraffic()
        {
            Ring.Clear();
            Batch.Clear();
            ConfidenceSum = 0;
            ConfidenceCount = 0;
        }

        public void ResetPipeline()
        {
            ClearTraffic();
            Decoder.Reset();
        }
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
