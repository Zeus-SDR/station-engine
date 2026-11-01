// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

/// <summary>How the far side answered one two-tone key request.</summary>
internal enum ExternalTwoToneOutcome
{
    /// <summary>The Agent confirmed it applied the request.</summary>
    Accepted,
    /// <summary>The Agent refused the request; nothing changed on its side.</summary>
    Refused,
    /// <summary>
    /// The arm ended before an answer. The Agent drops its own two-tone key when
    /// its feedback source ends, so nothing of this arm can stay keyed.
    /// </summary>
    Ended,
    /// <summary>No answer in time: the request may or may not have been applied.</summary>
    TimedOut,
}

/// <summary>The WDSP calls the feedback port makes on the engine it armed.</summary>
internal interface IExternalFeedbackCorrector
{
    IDisposable SubscribeTxWaveform(TxWaveformHandler handler);
    void SetPsMox(bool moxOn);
    void SetPsEnabled(bool enabled);
    void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
        ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ);

    /// <summary>Two-tone pair armed on the engine at the moment of the call, if any.</summary>
    bool TryGetTwoTone(out double freq1Hz, out double freq2Hz)
    {
        freq1Hz = 0;
        freq2Hz = 0;
        return false;
    }

    /// <summary>calcc's cumulative fit attempts and completed fits (info[7], info[5]).</summary>
    (int Attempts, int Completed) PsFitCounts() => (0, 0);

    /// <summary>calcc's feedback level at its last fit (info[4], 0..256); zero when unknown.</summary>
    double PsFeedbackLevel() => 0;

    /// <summary>Discards calcc's learned correction, so its next fit is taken in full.</summary>
    void ResetPsCalibration() { }

    /// <summary>Keeps the running correction through calibration resets (<see cref="IPsCorrectionHold"/>).</summary>
    void HoldPsCorrection(bool hold) { }

    /// <summary>Writes the running correction to <paramref name="path"/>.</summary>
    void SavePsCorrection(string path) { }

    /// <summary>Applies the correction saved at <paramref name="path"/>.</summary>
    void RestorePsCorrection(string path) { }

    /// <summary>Lock-free two-tone revision. Zero when the corrector has none.</summary>
    long TwoToneRevision => 0;
}

/// <summary>
/// Family-neutral, loopback-only external feedback seam. It carries the
/// completed WDSP TX IQ outward as a raw reference and takes already aligned
/// PureSignal pairs inward into a <see cref="PureSignalPairIntake"/> (pair
/// protocol version 2, docs/designs/ps-external-pair-intake.md). The source
/// on the far side owns everything between its radio's feedback and an
/// aligned pair. The port is inert until an explicit PureSignal arm
/// transition calls <see cref="ArmAsync"/>. One attached connection carries
/// at most one arm: a disarm releases it, the far side tears its sampler down
/// and closes, and the next arm waits for a fresh connection.
/// </summary>
public sealed class ExternalFeedbackPort : IDisposable, IPureSignalPairIntakeHost
{
    private const int MaximumSamples = PureSignalPairProtocol.MaximumReferenceComplex * 2;
    internal const int MaximumAttenuationDb = 60;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromMilliseconds(100);
    private readonly object _gate = new();
    private readonly RadioService _radio;
    private readonly ExternalReceiverPort _receiver;
    private readonly ILogger<ExternalFeedbackPort> _log;
    private readonly Func<long> _clockMs;
    private readonly Channel<(long Epoch, string Reason)> _failures = Channel.CreateBounded<(long Epoch, string Reason)>(new BoundedChannelOptions(4)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _failureWorker;
    private readonly Task _watchdog;
    private TaskCompletionSource _sourceAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Source? _source;
    private long _armIds;
    private bool _externalSessionObserved;
    private string? _lastFailure;
    private int _attenuationDb;
    private float _feedbackGain = 1f;
    private bool _disposed;

    public ExternalFeedbackPort(RadioService radio, ExternalReceiverPort receiver,
        ILogger<ExternalFeedbackPort> log)
        : this(radio, receiver, log, () => Environment.TickCount64, runWatchdog: true) { }

    internal ExternalFeedbackPort(RadioService radio, ExternalReceiverPort receiver,
        ILogger<ExternalFeedbackPort> log, Func<long> clockMs, bool runWatchdog)
    {
        _radio = radio;
        _receiver = receiver;
        _log = log;
        _clockMs = clockMs;
        _failureWorker = Task.Run(ProcessFailuresAsync);
        _watchdog = runWatchdog ? Task.Run(RunWatchdogAsync) : Task.CompletedTask;
        _radio.StateChanged += OnRadioStateChanged;
        _receiver.ContextChanged += OnReceiverContextChanged;
    }

    internal bool IsAttached { get { lock (_gate) return !_disposed && _source is not null; } }

    /// <summary>
    /// A product receiver or feedback relay has appeared during this
    /// disconnected-radio session. It remains set across relay retries so an
    /// arm cannot fall through either attach gap into the HPSDR path.
    /// </summary>
    internal bool HasExternalSession
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return false;
                if (_receiver.IsActive && !_radio.IsConnected &&
                    _radio.Snapshot().Status == ConnectionStatus.Disconnected)
                    _externalSessionObserved = true;
                return _externalSessionObserved;
            }
        }
    }

    /// <summary>
    /// The engine has no radio of its own and a product-operated receiver is
    /// reporting. The connection itself may still be on its way.
    /// </summary>
    internal bool CanAttemptCorrection
    {
        get
        {
            lock (_gate) if (_disposed) return false;
            return _source is not null && !_radio.IsConnected &&
                _radio.Snapshot().Status == ConnectionStatus.Disconnected;
        }
    }

    /// <summary>Feedback attenuation the port applies digitally before WDSP, 0 to 60 dB.</summary>
    internal int FeedbackAttenuationDb => Volatile.Read(ref _attenuationDb);

    internal ExternalFeedbackStatus CaptureStatus()
    {
        Source? source;
        lock (_gate) source = _source;
        return new(HasExternalSession, FeedbackAttenuationDb, 0, MaximumAttenuationDb,
            Volatile.Read(ref _lastFailure), source?.FullDuplexEnabledForArm == true, source?.Window);
    }

    internal sealed record ExternalFeedbackStatus(bool Available, int PsTxFeedbackAttenuationDb,
        int PsTxFeedbackAttenuationDbMin, int PsTxFeedbackAttenuationDbMax, string? Reason, bool FullDuplexEnabledForArm, string? Window);

    internal float FeedbackGain => Volatile.Read(ref _feedbackGain);

    internal bool TryGetTransmitContext(out ExternalReceiverContext context)
    {
        if (!_receiver.TryGetContext(0, out context)) return false;
        Source? source;
        lock (_gate) source = _source;
        if (source?.EffectiveTransmitOffsetHz is long offsetHz)
            context = context with { VfoHz = checked(context.VfoHz + offsetHz) };
        return true;
    }

    /// <summary>Runs work off the TX observer (the two-tone announcement); the thread pool by default.</summary>
    internal Action<Action> OffTxPath { get; set; } = work => ThreadPool.QueueUserWorkItem(_ => work());

    /// <summary>Runs the intake's work off its feed path; the thread pool when null.</summary>
    internal Action<Action>? OffFeedPath { get; set; }

    /// <summary>The pair intake of the arm the port holds, if any.</summary>
    internal PureSignalPairIntake? CurrentIntake
    {
        get
        {
            Source? source;
            lock (_gate) source = _source;
            return source?.Intake;
        }
    }

    /// <summary>How long a two-tone request waits for the far side before it is reported uncertain.</summary>
    internal TimeSpan TwoToneResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Raised once for every attached connection that detaches or is disposed,
    /// whatever the reason (operator disarm, fail-close, engine replacement,
    /// transport loss), with the arm ID each two-tone request on it reported and
    /// the detach reason (<see cref="OperatorDisarmReason"/> for the operator's
    /// own disarm). By then the far side has been told to drop its sampler
    /// and its guarded two-tone key. Raised on a pool thread, never under a port
    /// or source lock, so a handler may take its own locks.
    /// </summary>
    internal event Action<long, string>? ArmEnded;

    /// <summary>The detach reason of an explicit operator disarm, the one intentional end of an arm.</summary>
    internal const string OperatorDisarmReason = "operator-disarm";

    internal Task<ExternalTwoToneOutcome>? BeginTwoTone(bool enabled, out string? error) =>
        BeginTwoTone(enabled, out error, out _);

    /// <summary>
    /// Queues one two-tone key request to the Agent and returns at once; the
    /// task reports the Agent's answer, or <see cref="ExternalTwoToneOutcome.TimedOut"/>.
    /// Requests are delivered and answered in the order they were queued. Null
    /// (with <paramref name="error"/>) when a request could not be sent; a
    /// release that cannot be sent ends the arm instead (see <see cref="ArmEnded"/>).
    /// A release with no live arm completes as <see cref="ExternalTwoToneOutcome.Ended"/>.
    /// <paramref name="armId"/> names the arm the request went to (0 for none).
    /// It never waits for the radio, so it is safe under the TX transition lock.
    /// </summary>
    internal Task<ExternalTwoToneOutcome>? BeginTwoTone(bool enabled, out string? error, out long armId)
    {
        Source? source;
        lock (_gate) source = _source;
        armId = source?.ArmId ?? 0;
        if (source is null || !source.IsReady)
        {
            error = enabled ? "External PureSignal must be armed on the transmit receiver." : null;
            return enabled ? null : Task.FromResult(ExternalTwoToneOutcome.Ended);
        }
        return source.BeginTwoTone(enabled, out error);
    }

    /// <summary>Armed, confirmed by the far side, and the radio reports it is transmitting.</summary>
    internal bool IsCorrectingTransmit
    {
        get
        {
            Source? source;
            lock (_gate) source = _source;
            return source?.IsCorrectingTransmit == true;
        }
    }

    internal void SetFeedbackAttenuationDb(int db)
    {
        int clamped = ApplyFeedbackAttenuationDb(db);
        // The value the servo settles on belongs to the band it was settled on.
        int band = Volatile.Read(ref _bandKhz);
        if (band > 0 && clamped > 0 && (!_bandAttenuationDb.TryGetValue(band, out int known) || known != clamped))
        {
            _bandAttenuationDb[band] = clamped;
            _radio.SetExternalFeedbackBandAttenuationDb(band, clamped);
        }
    }

    // Every change, whoever makes it, is announced to the arm's source with
    // the next gain sequence; the intake drops repeats.
    private int ApplyFeedbackAttenuationDb(int db)
    {
        int clamped = Math.Clamp(db, 0, MaximumAttenuationDb);
        Volatile.Write(ref _attenuationDb, clamped);
        Volatile.Write(ref _feedbackGain, PureSignalPairIntake.GainFor(clamped));
        CurrentIntake?.OnFeedbackAttenuationChanged(clamped);
        return clamped;
    }

    bool IPureSignalPairIntakeHost.AutoAttenuate => _radio.Snapshot().PsAutoAttenuate;
    int IPureSignalPairIntakeHost.FeedbackAttenuationDb => FeedbackAttenuationDb;
    void IPureSignalPairIntakeHost.ApplyFeedbackAttenuationDb(int db) => ApplyFeedbackAttenuationDb(db);
    void IPureSignalPairIntakeHost.RecordPresetAttenuationDb(int db) => RecordPresetAttenuation(db);
    double IPureSignalPairIntakeHost.HwPeak => _radio.Snapshot().PsHwPeak;
    int IPureSignalPairIntakeHost.BandKhz => Volatile.Read(ref _bandKhz);
    string IPureSignalPairIntakeHost.HeldCorrectionDirectory => HeldCorrectionDirectory;

    // Session only: the corrections live in a directory owned by this process
    // and removed with the port, so nothing carries into a later launch.
    private string? _heldCorrectionDirectory;
    internal string HeldCorrectionDirectory
    {
        get
        {
            if (_heldCorrectionDirectory is null)
            {
                SweepStaleHeldCorrections();
                _heldCorrectionDirectory = Path.Combine(Path.GetTempPath(), $"{HeldCorrectionPrefix}{Environment.ProcessId}");
            }
            return _heldCorrectionDirectory;
        }
        set => _heldCorrectionDirectory = value;
    }

    /// <summary>
    /// Called once the engine has enabled PureSignal for the arm the port holds:
    /// the correction is held through calibration resets from here on, and the
    /// last validated correction for the same geometry, if any, is resumed on
    /// the arm's first key-down.
    /// </summary>
    internal void OnPsEnabled(object engine)
    {
        Source? source;
        lock (_gate) source = _source;
        source?.StartHold(engine);
    }

    private const string HeldCorrectionPrefix = "zeus-ps-hold-";

    // A process that crashed could not remove its directory; one whose process
    // is gone is removed by the next session.
    private void SweepStaleHeldCorrections()
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(Path.GetTempPath(), HeldCorrectionPrefix + "*"))
            {
                if (!int.TryParse(Path.GetFileName(directory)[HeldCorrectionPrefix.Length..], out int pid) ||
                    pid == Environment.ProcessId)
                    continue;
                try { using var process = System.Diagnostics.Process.GetProcessById(pid); continue; }
                catch (ArgumentException) { }
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) { _log.LogDebug(exception, "external_feedback.held_corrections sweep failed"); }
    }

    private void DeleteHeldCorrections()
    {
        string? directory = _heldCorrectionDirectory;
        if (directory is null) return;
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception exception) { _log.LogDebug(exception, "external_feedback.held_corrections cleanup failed"); }
    }

    // The level preset's value, recorded like a servo step unless something
    // has moved the attenuation since.
    private void RecordPresetAttenuation(int db)
    {
        if (FeedbackAttenuationDb != db) return;
        SetFeedbackAttenuationDb(db);
        _radio.SetPsTxAttenuationDbStateOnly(db);
    }

    // Settled feedback attenuation per amateur band, saved across sessions. The
    // coupler-to-feedback level differs by band (42 dB on 40 m, 33 dB on 80 m
    // on the 2026-10-31 bench), so the first fit after a band change was
    // learned with the feedback 9 dB low and the start of the over was dirty.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _bandAttenuationDb = new();
    private int _bandKhz;

    private static readonly int[] BandEdgesKhz =
        [1_800, 3_500, 5_330, 7_000, 10_100, 14_000, 18_068, 21_000, 24_890, 28_000, 50_000, 144_000];

    /// <summary>The lower edge (kHz) of the amateur band holding <paramref name="vfoHz"/>, or 0 below 160 m.</summary>
    internal static int BandKhz(long vfoHz)
    {
        long khz = vfoHz / 1_000;
        int band = 0;
        foreach (int edge in BandEdgesKhz)
            if (khz >= edge) band = edge;
        return band;
    }

    /// <summary>Restores the attenuation last settled on <paramref name="vfoHz"/>'s band, before the arm.</summary>
    private void RestoreBandAttenuation(long vfoHz)
    {
        int band = BandKhz(vfoHz);
        Volatile.Write(ref _bandKhz, band);
        if (band <= 0) return;
        // This session's value first, then the one saved by an earlier session.
        if (!_bandAttenuationDb.TryGetValue(band, out int db))
        {
            if (_radio.GetExternalFeedbackBandAttenuationDb(band) is not int saved || saved <= 0) return;
            db = saved;
            _bandAttenuationDb[band] = db;
        }
        int current = FeedbackAttenuationDb;
        if (db == current) return;
        _log.LogInformation("external_feedback.band_attenuation band={Band}kHz from={From}dB to={To}dB", band, current, db);
        ApplyFeedbackAttenuationDb(db);
        _radio.SetPsTxAttenuationDbStateOnly(db);
    }

    internal async Task RunAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var source = TryAttach(out int refusal);
        if (source is null)
        {
            context.Response.StatusCode = refusal;
            return;
        }
        WebSocket? socket = null;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await source.RunAsync(socket, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            DetachAfterSocket(source, socket);
            socket?.Dispose();
        }
    }

    internal void DetachAfterSocket(Source source, WebSocket? socket) =>
        Detach(source, socket?.CloseStatusDescription is { } reason ? "remote-" + reason : "closed", failClosed: true);

    /// <summary>Attaches a socketless connection; tests drive its messages directly.</summary>
    internal Source? AttachForTest() => TryAttach(out _);

    private Source? TryAttach(out int refusal)
    {
        var source = TryAttachCore(out refusal);
        // A product radio's PureSignal feedback now owns this disconnected
        // session: its own HW peak slot applies. Outside the gate, and never
        // from inside a radio state event.
        if (source is not null) _radio.ApplyExternalFeedbackPsHwPeak();
        return source;
    }

    private Source? TryAttachCore(out int refusal)
    {
        bool engineRadioIdle = !_radio.IsConnected &&
            _radio.Snapshot().Status == ConnectionStatus.Disconnected;
        lock (_gate)
        {
            refusal = _disposed ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
            if (_disposed || !engineRadioIdle || _source is not null) return null;
            var source = new Source(this, _log, ++_armIds, _radio.BeginExternalFeedbackSession());
            _source = source;
            _externalSessionObserved = true;
            _sourceAttached.TrySetResult();
            return source;
        }
    }

    internal Task<bool> ArmAsync(IDspEngine engine, RxMode mode, CancellationToken stop) =>
        engine is ITxWaveformSource waveform && engine.TxOutputSamples > 0
            ? ArmCoreAsync(engine, new EngineCorrector(engine, waveform), mode, stop)
            : Task.FromResult(false);

    /// <summary>
    /// Idempotent for the engine and mode already armed. Any other arm is
    /// released first and the new one waits, bounded, for a fresh connection.
    /// </summary>
    internal async Task<bool> ArmCoreAsync(object engine, IExternalFeedbackCorrector corrector,
        RxMode mode, CancellationToken stop)
    {
        if (mode is not (RxMode.USB or RxMode.LSB) || !CanAttemptCorrection) return false;
        if (IsArmedFor(engine, mode)) return true;
        Volatile.Write(ref _lastFailure, null);
        Release("rearm");
        var source = await WaitForSourceAsync(stop).ConfigureAwait(false);
        if (source is null || !CanAttemptCorrection) return false;
        if (!_receiver.TryGetContext(0, out var context) || context.Mode != mode) return false;
        bool transmitting = context.Transmitting;
        source.NoteReceiverVfo(context.VfoHz);
        RestoreBandAttenuation(context.VfoHz);
        ZeroAmpDelay();
        return await source.ArmAsync(engine, corrector, mode, transmitting, stop).ConfigureAwait(false);
    }

    /// <summary>
    /// The source aligns reference and feedback itself, to a fraction of a
    /// sample, so calcc's amp delay must be zero. With the HPSDR
    /// default (150 ns) calcc runs the reference through a fractional-delay
    /// filter that smears the reference samples the bucket balancer zeroes;
    /// calcc then collects them as pairs and rejected every fit on the
    /// 2026-10-31 bench (binfo1=0x10). At 0 ns the same feed passed 9 of 9.
    /// </summary>
    private void ZeroAmpDelay()
    {
        double current = _radio.Snapshot().PsAmpDelayNs;
        if (current == 0) return;
        _log.LogInformation("external_feedback.amp_delay from={From:F1}ns to=0ns", current);
        _radio.SetPsAdvanced(new PsAdvancedSetRequest(AmpDelayNs: 0));
    }

    internal bool IsArmedFor(object? engine, RxMode mode)
    {
        Source? source;
        lock (_gate) source = _source;
        return engine is not null && source?.IsArmedFor(engine, mode) == true;
    }

    /// <summary>
    /// Releases whatever arm the port holds, whichever engine it was taken
    /// on: clears PS MOX and enable on that engine, sends the disarm, and
    /// detaches the connection so the far side drops its sampler and audio.
    /// </summary>
    internal void Release(string reason)
    {
        Source? source;
        lock (_gate) source = _source;
        if (source is null || !source.IsArmedOrArming) return;
        Detach(source, reason, failClosed: false);
    }

    /// <summary>An engine is being replaced underneath an arm: release it and fail closed.</summary>
    internal void ReleaseIfArmedOn(object? engine, string reason)
    {
        Source? source;
        lock (_gate) source = _source;
        if (engine is null || source is null || !source.IsArmedOn(engine)) return;
        Detach(source, reason, failClosed: true);
    }

    /// <summary>One watchdog pass; the production loop runs it every 100 ms.</summary>
    internal void CheckDeadlines()
    {
        Source? source;
        lock (_gate) source = _source;
        source?.CheckDeadlines(_clockMs());
    }

    private async Task<Source?> WaitForSourceAsync(CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop, _lifetime.Token);
        timeout.CancelAfter(SourceTimeout);
        while (true)
        {
            Task attached;
            Source? current;
            lock (_gate)
            {
                if (_disposed) return null;
                current = _source;
                if (_sourceAttached.Task.IsCompleted)
                    _sourceAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
                attached = _sourceAttached.Task;
            }
            // Port lock is released first: a source never takes it while holding its own.
            if (current is { IsSpent: false }) return current;
            try { await attached.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { return null; }
        }
    }

    private void Detach(Source source, string reason, bool failClosed)
    {
        lock (_gate)
        {
            if (_source != source) return;
            _source = null;
        }
        bool wasArmed = source.IsArmedOrArming;
        source.Stop(reason);
        if (wasArmed)
        {
            // Attenuation is kept across arms of the same external radio.
            // Detach can hold the engine lock. State subscribers run on the
            // existing worker so they cannot invert the TX/engine lock order.
            if (!failClosed && reason != "rearm" && reason != OperatorDisarmReason)
                _failures.Writer.TryWrite((source.RuntimeEpoch, reason));
            if (reason != OperatorDisarmReason && reason != "rearm" && reason != "engine-rebuild")
                Volatile.Write(ref _lastFailure, FailureMessage(reason));
        }
        _log.LogInformation("external_feedback.detached reason={Reason}", reason);
        if (failClosed && wasArmed) FailClosed(reason, source.RuntimeEpoch);
        RaiseArmEnded(source.ArmId, reason);
    }

    // Off the caller's thread: a detach can run under the pipeline's engine
    // lock or the TX transition lock, and a handler takes the latter.
    private void RaiseArmEnded(long armId, string reason)
    {
        if (ArmEnded is null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var handlers = ArmEnded?.GetInvocationList();
            if (handlers is null) return;
            foreach (var handler in handlers)
            {
                try { ((Action<long, string>)handler)(armId, reason); }
                catch (Exception exception)
                {
                    _log.LogWarning(exception, "external_feedback.arm_ended subscriber failed arm={Arm}", armId);
                }
            }
        });
    }

    internal static string FailureMessage(string reason) => reason switch
    {
        "external-feedback-full-duplex" or "remote-external-feedback-full-duplex" =>
            "External PureSignal needs Full Duplex; the radio refused to enable it.",
        _ => $"External PureSignal stopped ({reason}).",
    };

    private void FailClosed(string reason, long epoch)
    {
        Volatile.Write(ref _lastFailure, FailureMessage(reason));
        _log.LogWarning("external_feedback.fail_closed reason={Reason}", reason);
        _failures.Writer.TryWrite((epoch, reason));
    }

    private void OnRadioStateChanged(StateDto state)
    {
        if (!_radio.IsConnected && state.Status == ConnectionStatus.Disconnected)
        {
            // The source conditions with the operator HW peak; it follows every edit.
            CurrentIntake?.AnnounceHwPeak(state.PsHwPeak);
            return;
        }
        // An HPSDR radio owns the station. Drop the external attenuation memory
        // without rewriting the displayed HPSDR value the connect path restores.
        ApplyFeedbackAttenuationDb(0);
        _bandAttenuationDb.Clear();
        _radio.EndExternalFeedbackSession();
        Source? source;
        lock (_gate)
        {
            _externalSessionObserved = false;
            source = _source;
        }
        if (source is not null) Detach(source, "radio-reconnect", failClosed: true);
    }

    private void OnReceiverContextChanged()
    {
        if (!_receiver.IsActive && !_radio.IsConnected &&
            _radio.Snapshot().Status == ConnectionStatus.Disconnected)
        {
            ApplyFeedbackAttenuationDb(0);
            _bandAttenuationDb.Clear();
            _radio.SetPsTxAttenuationDbStateOnly(0);
        }
        Source? source;
        lock (_gate) source = _source;
        if (source is null || !source.IsArmedOrArming) return;
        if (!_receiver.TryGetContext(0, out var context) || context.Mode != source.Mode)
        {
            Detach(source, "receiver-context-changed", failClosed: true);
            return;
        }
        source.NoteReceiverVfo(context.VfoHz);
        source.SetTransmitting(context.Transmitting, _clockMs());
    }

    private async Task RunWatchdogAsync()
    {
        using var timer = new PeriodicTimer(WatchdogPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try { CheckDeadlines(); }
                catch (Exception exception) { _log.LogWarning(exception, "external_feedback.watchdog failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal Action<string>? BeforeRuntimeClearForTest { get; set; }

    private async Task ProcessFailuresAsync()
    {
        await foreach (var (epoch, reason) in _failures.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                BeforeRuntimeClearForTest?.Invoke(reason);
                _radio.ClearExternalFeedbackRuntime(epoch);
            }
            catch (Exception exception)
            {
                _log.LogWarning(exception,
                    "external_feedback.fail_closed state update failed reason={Reason}", reason);
            }
        }
    }

    public void Dispose()
    {
        Source? source;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            source = _source;
            _source = null;
            _externalSessionObserved = false;
            _sourceAttached.TrySetResult();
        }
        _radio.StateChanged -= OnRadioStateChanged;
        _receiver.ContextChanged -= OnReceiverContextChanged;
        _lifetime.Cancel();
        bool wasArmed = source?.IsArmedOrArming == true;
        source?.Stop("disposed");
        if (wasArmed) FailClosed("disposed", source!.RuntimeEpoch);
        if (source is not null) RaiseArmEnded(source.ArmId, "disposed");
        _failures.Writer.TryComplete();
        _failureWorker.GetAwaiter().GetResult();
        _watchdog.GetAwaiter().GetResult();
        _lifetime.Dispose();
        DeleteHeldCorrections();
    }

    internal sealed class EngineCorrector(IDspEngine engine, ITxWaveformSource waveform) : IExternalFeedbackCorrector
    {
        public IDisposable SubscribeTxWaveform(TxWaveformHandler handler) => waveform.SubscribeTxWaveform(handler);
        // The radio's transmit state is authoritative here, so it is the
        // engine's keyed state for PureSignal too (the observed-peak hold).
        public void SetPsMox(bool moxOn)
        {
            if (engine is IExternalPureSignalMox external) external.SetExternalPsMox(moxOn);
            else engine.SetPsMox(moxOn);
        }
        public void SetPsEnabled(bool enabled) => engine.SetPsEnabled(enabled);
        public void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
            ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ) => engine.FeedPsFeedbackBlock(txI, txQ, rxI, rxQ);
        public (int Attempts, int Completed) PsFitCounts()
        {
            var meters = engine.GetPsStageMeters();
            return (meters.CalibrationCalcAttempts, meters.CalibrationAttempts);
        }
        public double PsFeedbackLevel() => engine.GetPsStageMeters().FeedbackLevel;
        public void ResetPsCalibration() => engine.ResetPs();
        public void HoldPsCorrection(bool hold)
        {
            if (engine is IPsCorrectionHold held) held.SetPsHoldCorrection(hold);
        }
        public void SavePsCorrection(string path) => engine.SavePsCorrection(path);
        public void RestorePsCorrection(string path) => engine.RestorePsCorrection(path);
        public bool TryGetTwoTone(out double freq1Hz, out double freq2Hz)
        {
            freq1Hz = 0;
            freq2Hz = 0;
            return engine is ITwoToneFrequencies tones && tones.TryGetTwoTone(out freq1Hz, out freq2Hz);
        }

        public long TwoToneRevision =>
            engine is ITwoToneFrequencies tones ? tones.TwoToneRevision : 0;
    }

    /// <summary>
    /// Moves the operator HW peak to the source's suggestion for this arm. The
    /// value lands in the external feedback slot, so the next session starts
    /// from it. Only with <see cref="ExternalFeedbackAutoHwPeak"/>; otherwise
    /// the suggestion is logged.
    /// </summary>
    internal void ApplyHwPeakSuggestion(double next)
    {
        double current = _radio.Snapshot().PsHwPeak;
        if (!ExternalFeedbackAutoHwPeak)
        {
            _log.LogInformation("external_feedback.hw_peak pick={Pick:F2} current={Current:F2} (auto off)", next, current);
            return;
        }
        _log.LogInformation("external_feedback.hw_peak auto from={From:F2} to={To:F2}", current, next);
        _radio.SetPsAdvanced(new PsAdvancedSetRequest(HwPeak: next));
    }

    /// <summary>
    /// True lets the source's suggestion move the operator's HW peak while
    /// keyed; false, the default, keeps it fixed. Off since the 2026-10-31 bench: the first
    /// build where the tracker ran moved the peak every two seconds
    /// (1.01, 0.94, 0.88, 0.95, 0.66, 1.01) and calcc's amplitude axis with it
    /// mid-collection, so every fit blended two scales; the opposite-sideband
    /// skirt measured 4 to 14 dB worse than with the peak fixed, worst right
    /// after each move. The tracker still logs its pick.
    /// </summary>
    internal bool ExternalFeedbackAutoHwPeak { get; set; } = false;

    /// <summary>
    /// True, the default, feeds calcc the source's conditioned (primary) pairs
    /// until calcc rejects <see cref="ConditioningFallbackRejections"/> fits in
    /// a row, then the aligned (fallback) pairs for the rest of the arm; false
    /// feeds the aligned pairs throughout. Read when an arm starts.
    /// </summary>
    internal bool CalibrationConditioning { get; set; } = true;

    /// <summary>
    /// Rejected fits in a row with the conditioned feed before calcc gets the
    /// aligned pairs for the rest of the arm. On the 2026-10-31 bench the
    /// conditioned feed was rejected every time while the aligned pairs fitted.
    /// </summary>
    internal const int ConditioningFallbackRejections = PureSignalPairIntake.FallbackRejections;

    /// <summary>Forwards the intake's WDSP calls to the corrector the port armed.</summary>
    private sealed class PairTarget(IExternalFeedbackCorrector corrector) : IPureSignalPairTarget
    {
        public void SetPsMox(bool moxOn) => corrector.SetPsMox(moxOn);
        public void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
            ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ) => corrector.FeedPsFeedbackBlock(txI, txQ, rxI, rxQ);
        public (int Attempts, int Completed) PsFitCounts() => corrector.PsFitCounts();
        public double PsFeedbackLevel() => corrector.PsFeedbackLevel();
        public void ResetPsCalibration() => corrector.ResetPsCalibration();
        public void HoldPsCorrection(bool hold) => corrector.HoldPsCorrection(hold);
        public void SavePsCorrection(string path) => corrector.SavePsCorrection(path);
        public void RestorePsCorrection(string path) => corrector.RestorePsCorrection(path);
    }

    /// <summary>
    /// One attached connection: the transport and the arm lifecycle, with the
    /// arm's <see cref="PureSignalPairIntake"/>. Lock order: <c>_gate</c> may
    /// take <c>_referenceGate</c>, never the reverse, and neither is held
    /// while the intake is called, except that <see cref="Stop"/> stops it
    /// under <c>_gate</c>; the intake never takes either. The TX observer
    /// takes only <c>_referenceGate</c> while stamping and queueing the raw
    /// reference.
    /// </summary>
    internal sealed class Source
    {
        internal sealed record Outbound(byte[] Bytes, WebSocketMessageType Type);
        private readonly ExternalFeedbackPort _owner;
        private readonly ILogger _log;
        private readonly object _gate = new();
        private readonly object _referenceGate = new();
        private readonly Channel<Outbound> _outbound = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        private readonly CancellationTokenSource _stop = new();
        private TaskCompletionSource<bool>? _ready;
        // Outstanding two-tone requests by ID. A result must carry the same
        // request ID and enabled value; the far side answers them in order.
        private sealed record PendingTwoTone(long RequestId, bool Enabled,
            TaskCompletionSource<ExternalTwoToneOutcome> Result);
        private const int MaximumPendingTwoTones = 8;
        // Room kept for releases only, so a release is never refused behind key requests.
        private const int ReservedTwoToneReleases = 8;
        private readonly Dictionary<long, PendingTwoTone> _twoTones = [];
        private long _twoToneRequestIds;
        private long? _effectiveTransmitOffsetHz;
        private bool _fullDuplexEnabledForArm;
        private string? _window;
        private long? _receiverHz;
        private IDisposable? _waveformLease;
        private object? _engine;
        private IExternalFeedbackCorrector? _corrector;
        // Published once the arm message is queued; read without _gate. The
        // announcements and the TX observer use it.
        private PureSignalPairIntake? _intake;
        // The same intake from its construction, for what the source sends:
        // its ready can never arrive before the arm, but it may arrive before
        // the arm's intake is published.
        private PureSignalPairIntake? _receiveIntake;
        private IExternalFeedbackCorrector? _toneReader;
        // The two-tone revision the TX observer last saw (under _referenceGate).
        private long _observedTwoToneRevision;
        private RxMode _mode;
        private long _referenceSequence;
        private bool _armQueued;
        private bool _spent;
        private volatile bool _closed;
        private string? _deferredFailure;
        private int _failed;

        internal Source(ExternalFeedbackPort owner, ILogger log, long armId, long runtimeEpoch)
        {
            _owner = owner;
            _log = log;
            ArmId = armId;
            RuntimeEpoch = runtimeEpoch;
        }

        /// <summary>Names this connection's one arm; unique for the port's lifetime.</summary>
        internal long ArmId { get; }
        internal long RuntimeEpoch { get; }
        internal ChannelReader<Outbound> OutboundForTest => _outbound.Reader;
        internal ChannelWriter<Outbound> OutboundWriterForTest => _outbound.Writer;
        internal PureSignalPairIntake? Intake => Volatile.Read(ref _intake);
        private PureSignalPairIntake? ReceiveIntake => Volatile.Read(ref _receiveIntake);
        internal bool IsArmedOrArming { get { lock (_gate) return _engine is not null; } }
        internal bool IsSpent { get { lock (_gate) return _spent || _stop.IsCancellationRequested; } }
        internal RxMode Mode { get { lock (_gate) return _mode; } }

        internal bool IsCorrectingTransmit
        {
            get
            {
                bool ready;
                lock (_gate) ready = _ready?.Task.IsCompletedSuccessfully == true;
                return ready && Intake?.Transmitting == true;
            }
        }

        internal bool IsReady
        {
            get { lock (_gate) return !_spent && _engine is not null && _ready?.Task.IsCompletedSuccessfully == true; }
        }

        internal bool FullDuplexEnabledForArm
        {
            get { lock (_gate) return !_spent && _fullDuplexEnabledForArm && _ready?.Task.IsCompletedSuccessfully == true; }
        }

        internal string? Window
        {
            get { lock (_gate) return !_spent && _ready?.Task.IsCompletedSuccessfully == true ? _window : null; }
        }

        internal long? EffectiveTransmitOffsetHz
        {
            get { lock (_gate) return _effectiveTransmitOffsetHz; }
        }

        internal bool IsArmedOn(object engine) { lock (_gate) return ReferenceEquals(_engine, engine); }

        internal bool IsArmedFor(object engine, RxMode mode)
        {
            lock (_gate)
                return ReferenceEquals(_engine, engine) && _mode == mode && !_spent &&
                    _ready?.Task.IsCompletedSuccessfully == true;
        }

        internal async Task RunAsync(WebSocket socket, CancellationToken aborted)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, _stop.Token);
            var send = SendLoopAsync(socket, linked.Token);
            var receive = ReceiveLoopAsync(socket, linked.Token);
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            if (socket.CloseStatus is null && !aborted.IsCancellationRequested && !_stop.IsCancellationRequested)
                _log.LogWarning("external_feedback.socket_ended abrupt=true state={State}", socket.State);
            if (socket.State == WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    // ReceiveLoop has ended the source and completed the queue.
                    // Join its sender before replying, without aborting a send
                    // that was already in flight when the close arrived.
                    await send.WaitAsync(timeout.Token).ConfigureAwait(false);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                        socket.CloseStatusDescription, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
            }
            await linked.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(send, receive).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
        }

        internal async Task<bool> ArmAsync(object engine, IExternalFeedbackCorrector corrector, RxMode mode,
            bool transmitting, CancellationToken stop)
        {
            Task<bool> ready;
            PureSignalPairIntake? intake = null;
            long? receiverHz = null;
            lock (_gate)
            {
                if (_spent || _stop.IsCancellationRequested || Volatile.Read(ref _failed) != 0) return false;
                if (_engine is not null)
                {
                    if (!ReferenceEquals(_engine, engine) || _mode != mode) return false;
                    ready = _ready!.Task;
                }
                else
                {
                    _engine = engine;
                    _corrector = corrector;
                    _mode = mode;
                    _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    ready = _ready.Task;
                    _referenceSequence = 0;
                    receiverHz = _receiverHz;
                    intake = new PureSignalPairIntake(new PairTarget(corrector), _owner, mode,
                        ExternalFeedbackSampleRate.TxHz, MaximumAttenuationDb, SendControl, _log, _owner._clockMs,
                        _owner.OffFeedPath)
                    {
                        PreferPrimary = _owner.CalibrationConditioning,
                    };
                    Volatile.Write(ref _receiveIntake, intake);
                }
            }
            if (intake is not null)
            {
                // The arm is the first message, built from the intake's own
                // gain, gain sequence and HW peak; later changes follow it.
                long revision = corrector.TwoToneRevision;
                intake.SendArm(revision, receiverHz);
                Volatile.Write(ref _armQueued, true);
                if (Volatile.Read(ref _deferredFailure) == "arm-backlog")
                {
                    Fail("arm-backlog");
                    return false;
                }
                intake.HwPeakSuggested += OnHwPeakSuggested;
                lock (_gate)
                {
                    if (!ReferenceEquals(_engine, engine)) return false;
                    Volatile.Write(ref _intake, intake);
                    Volatile.Write(ref _toneReader, corrector);
                    lock (_referenceGate) _observedTwoToneRevision = revision;
                    _waveformLease = corrector.SubscribeTxWaveform(OnWaveform);
                }
                // Whatever moved between building the arm and publishing the
                // intake; an unchanged value is not announced again.
                intake.OnFeedbackAttenuationChanged(_owner.FeedbackAttenuationDb);
                intake.AnnounceHwPeak(_owner._radio.Snapshot().PsHwPeak);
                long? receiver;
                lock (_gate) receiver = _receiverHz;
                if (receiver is long hz) intake.AnnounceReceiverFrequency(hz);
                SetTransmitting(transmitting, _owner._clockMs());
            }
            try { return await ready.WaitAsync(ReadyTimeout, stop).ConfigureAwait(false); }
            catch (TimeoutException) { Fail("sampler-timeout"); return false; }
        }

        // The intake's control messages, in the order it makes them. Never
        // blocks; a full queue ends the arm on the watchdog.
        private void SendControl(byte[] message)
        {
            if (_outbound.Writer.TryWrite(new(message, WebSocketMessageType.Text))) return;
            Interlocked.CompareExchange(ref _deferredFailure,
                Volatile.Read(ref _armQueued) ? "control-backlog" : "arm-backlog", null);
        }

        // Raised on the receive thread; applying raises radio state events.
        private void OnHwPeakSuggested(double value) =>
            ThreadPool.QueueUserWorkItem(_ => _owner.ApplyHwPeakSuggestion(value));

        /// <summary>
        /// Follows the radio's authoritative transmit state into WDSP's
        /// calibration MOX through the intake. It never keys anything.
        /// </summary>
        internal void SetTransmitting(bool transmitting, long nowMs)
        {
            _ = nowMs;
            string? failure = null;
            while (true)
            {
                IExternalFeedbackCorrector? corrector;
                lock (_gate) corrector = _corrector;
                var intake = Intake;
                if (corrector is null || intake is null || intake.Transmitting == transmitting) return;
                // TryGetTwoTone takes the engine lock. Read it before any port or intake gate.
                long revision = corrector.TwoToneRevision;
                double? toneLow = null, toneHigh = null;
                if (transmitting && corrector.TryGetTwoTone(out double low, out double high))
                {
                    toneLow = low;
                    toneHigh = high;
                }
                if (corrector.TwoToneRevision != revision) continue;
                lock (_gate) if (!ReferenceEquals(_corrector, corrector)) return;
                try { intake.SetTransmitting(transmitting, toneLow, toneHigh, revision); }
                catch (Exception exception)
                {
                    _log.LogWarning(exception, "external_feedback engine PS MOX failed");
                    failure = "engine-mox-failed";
                }
                break;
            }
            if (failure is not null) Fail(failure);
        }

        internal void StartHold(object engine)
        {
            lock (_gate)
                if (_spent || !ReferenceEquals(_engine, engine) || _corrector is null) return;
            Intake?.StartHold();
        }

        /// <summary>The receiver frequency, announced to the source on every change.</summary>
        internal void NoteReceiverVfo(long vfoHz)
        {
            lock (_gate)
            {
                if (_receiverHz == vfoHz) return;
                _receiverHz = vfoHz;
            }
            Intake?.AnnounceReceiverFrequency(vfoHz);
        }

        internal void CheckDeadlines(long nowMs)
        {
            _ = nowMs;
            if (Volatile.Read(ref _deferredFailure) is { } failure) Fail(failure);
        }

        /// <summary>Clears PS MOX and enable on the armed engine and sends the disarm. Idempotent.</summary>
        internal void Stop(string reason)
        {
            _closed = true;
            lock (_gate)
            {
                _spent = true;
                var corrector = _corrector;
                var intake = ReceiveIntake;
                bool armed = _engine is not null || _ready is not null;
                _waveformLease?.Dispose();
                _waveformLease = null;
                _engine = null;
                _corrector = null;
                _ready?.TrySetResult(false);
                _ready = null;
                foreach (var pending in _twoTones.Values) pending.Result.TrySetResult(ExternalTwoToneOutcome.Ended);
                _twoTones.Clear();
                if (armed)
                {
                    try
                    {
                        // Any feed in flight completes first; none follows, and
                        // PS MOX is cleared if it was set.
                        intake?.Stop();
                        corrector?.SetPsEnabled(false);
                    }
                    catch (Exception exception)
                    {
                        _log.LogWarning(exception, "external_feedback engine disarm failed reason={Reason}", reason);
                        _owner.FailClosed("engine-disarm-failed", RuntimeEpoch);
                    }
                    _outbound.Writer.TryWrite(new(Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(new { type = "disarm", reason })), WebSocketMessageType.Text));
                }
                else intake?.Stop();
            }
            // Let the send loop deliver the disarm, then close.
            _outbound.Writer.TryComplete();
            try { _stop.CancelAfter(TimeSpan.FromSeconds(1)); }
            catch (ObjectDisposedException) { }
        }

        internal bool HandleText(ReadOnlySpan<byte> utf8)
        {
            bool accepted = false;
            string? failure = null;
            try
            {
                using var json = JsonDocument.Parse(utf8.ToArray());
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() is { } messageType)
                {
                    switch (messageType)
                    {
                        case "ready": accepted = HandleReady(root, out failure); break;
                        case "two-tone-result": accepted = HandleTwoToneResult(root); break;
                        case "window": accepted = HandleWindow(root, out failure); break;
                        // The source's liveness while it has no pairs to send.
                        case "heartbeat": accepted = true; break;
                        default:
                            if (ReceiveIntake is { } intake)
                            {
                                failure = intake.HandleControl(root, out bool handled);
                                accepted = handled && failure is null;
                            }
                            break;
                    }
                }
            }
            catch (JsonException) { }
            if (!accepted) Fail(failure ?? "control-protocol");
            return accepted;
        }

        private bool HandleReady(JsonElement root, out string? failure)
        {
            failure = null;
            string? windowName = root.TryGetProperty("window", out var window) && window.ValueKind == JsonValueKind.String
                ? window.GetString() : "two-sided";
            long effectiveTxHz = 0;
            ExternalReceiverContext receiverContext = default;
            bool validEffectiveCarrier = root.TryGetProperty("effectiveTxHz", out var effective) &&
                effective.TryGetInt64(out effectiveTxHz) && effectiveTxHz > 0 &&
                _owner._receiver.TryGetContext(0, out receiverContext) &&
                receiverContext.Mode == Mode;
            if (!validEffectiveCarrier || windowName is not ("two-sided" or "one-sided")) return false;
            long offsetHz = effectiveTxHz - receiverContext.VfoHz;
            if (offsetHz is < -99_999 or > 99_999) return false;
            if (!TryGeometry(root, out string? geometry)) return false;
            var intake = ReceiveIntake;
            if (intake is null) return false;
            // Pairs are fed from here on; the source sends none before its ready.
            if (intake.Start(geometry) is { } refused)
            {
                failure = refused;
                return false;
            }
            lock (_gate)
            {
                if (_ready is null || _corrector is null) return false;
                _window = windowName;
                _effectiveTransmitOffsetHz = offsetHz;
                _fullDuplexEnabledForArm = root.TryGetProperty("fullDuplexEnabledForArm", out var duplex) &&
                    duplex.ValueKind == JsonValueKind.True;
                return _ready.TrySetResult(true);
            }
        }

        // A result completes only the request it names. One that is late (its
        // request already timed out), duplicated, for an earlier request or
        // unidentified is logged and ignored: it can neither answer a
        // different request nor end a healthy arm.
        private bool HandleTwoToneResult(JsonElement root)
        {
            bool matched = false;
            if (root.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.Number &&
                id.TryGetInt64(out long requestId) &&
                root.TryGetProperty("enabled", out var enabled) &&
                enabled.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                root.TryGetProperty("accepted", out var result) &&
                result.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                lock (_gate)
                {
                    if (_twoTones.TryGetValue(requestId, out var pending) &&
                        pending.Enabled == enabled.GetBoolean())
                    {
                        matched = pending.Result.TrySetResult(result.GetBoolean()
                            ? ExternalTwoToneOutcome.Accepted : ExternalTwoToneOutcome.Refused);
                        _twoTones.Remove(requestId);
                    }
                }
            }
            if (!matched)
                _log.LogInformation("external_feedback two-tone result ignored: no matching request");
            return true;
        }

        // A late window decision on an arm that is already ready. An unknown
        // type would fail the arm; this one must not.
        private bool HandleWindow(JsonElement root, out string? failure)
        {
            failure = null;
            string? windowName = root.TryGetProperty("window", out var followWindow) &&
                followWindow.ValueKind == JsonValueKind.String
                ? followWindow.GetString() : null;
            if (windowName is not ("two-sided" or "one-sided")) return false;
            if (!TryGeometry(root, out string? geometry)) return false;
            ExternalReceiverContext followContext = default;
            long followHz = 0;
            bool hasCarrier = root.TryGetProperty("effectiveTxHz", out var followCarrier) &&
                followCarrier.TryGetInt64(out followHz) && followHz > 0 &&
                _owner._receiver.TryGetContext(0, out followContext);
            long followOffset = hasCarrier ? followHz - followContext.VfoHz : 0;
            bool live;
            lock (_gate)
            {
                live = !_spent && _ready?.Task.IsCompletedSuccessfully == true;
                if (live)
                {
                    _window = windowName;
                    if (hasCarrier && followOffset is >= -99_999 and <= 99_999)
                        _effectiveTransmitOffsetHz = followOffset;
                }
            }
            if (live && geometry is not null && ReceiveIntake?.SetGeometry(geometry) is { } refused)
            {
                failure = refused;
                return false;
            }
            return true;
        }

        // The source's opaque geometry tag: absent, or a string the intake validates.
        private static bool TryGeometry(JsonElement root, out string? geometry)
        {
            geometry = null;
            if (!root.TryGetProperty("geometry", out var value) || value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.String) return false;
            geometry = value.GetString();
            return true;
        }

        internal Task<ExternalTwoToneOutcome>? BeginTwoTone(bool enabled, out string? error)
        {
            PendingTwoTone? request = null;
            lock (_gate)
            {
                if (_spent || _ready?.Task.IsCompletedSuccessfully != true)
                {
                    // Nothing of an arm that has ended can stay keyed: the far
                    // side drops its own two-tone key with the feedback source.
                    error = enabled ? "The external feedback control channel is not ready." : null;
                    return enabled ? null : Task.FromResult(ExternalTwoToneOutcome.Ended);
                }
                int limit = enabled ? MaximumPendingTwoTones : MaximumPendingTwoTones + ReservedTwoToneReleases;
                if (_twoTones.Count < limit)
                {
                    var pending = new PendingTwoTone(++_twoToneRequestIds, enabled,
                        new(TaskCreationOptions.RunContinuationsAsynchronously));
                    var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        type = "two-tone",
                        enabled,
                        requestId = pending.RequestId,
                    }));
                    if (_outbound.Writer.TryWrite(new(message, WebSocketMessageType.Text)))
                    {
                        _twoTones.Add(pending.RequestId, pending);
                        request = pending;
                    }
                }
                if (request is null && enabled)
                {
                    error = "The external feedback control channel is backlogged.";
                    return null;
                }
            }
            if (request is null)
            {
                // A release that cannot reach the far side ends the arm instead:
                // it drops its guarded key with the feedback source, and
                // ArmEnded tells the key's owner. The caller records the trip.
                Fail("two-tone-release-undeliverable");
                error = "The two-tone release could not be delivered; external PureSignal was ended.";
                return null;
            }
            error = null;
            return AwaitTwoToneAsync(request);
        }

        private async Task<ExternalTwoToneOutcome> AwaitTwoToneAsync(PendingTwoTone request)
        {
            try
            {
                return await request.Result.Task.WaitAsync(_owner.TwoToneResponseTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException) { return ExternalTwoToneOutcome.TimedOut; }
            finally
            {
                // A timed-out request is forgotten here, so its late result is ignored.
                lock (_gate)
                    if (_twoTones.TryGetValue(request.RequestId, out var current) && ReferenceEquals(current, request))
                        _twoTones.Remove(request.RequestId);
            }
        }

        /// <summary>One pair block from the source, fed through the intake.</summary>
        internal bool HandleBinary(ReadOnlySpan<byte> message)
        {
            // An unreadable frame keeps the reason a broken feedback frame always had.
            string? failure = !PureSignalPairProtocol.TryReadPairBlockHeader(message, out _) ? "feedback-geometry"
                : ReceiveIntake is { } intake ? intake.HandleBlock(message) : "pair-before-ready";
            if (failure is null) return true;
            Fail(failure);
            return false;
        }

        private void OnWaveform(int sampleRateHz, ReadOnlySpan<float> iq)
        {
            // The synchronous TX path: no corrector lock, no search, no failure
            // handling inline. Faults are raised by the watchdog.
            if (_closed || Volatile.Read(ref _deferredFailure) is not null) return;
            if (sampleRateHz != ExternalFeedbackSampleRate.TxHz || iq.IsEmpty ||
                (iq.Length & 1) != 0 || iq.Length > MaximumSamples)
            {
                Interlocked.CompareExchange(ref _deferredFailure, "reference-geometry", null);
                return;
            }
            foreach (float value in iq)
            {
                if (float.IsFinite(value)) continue;
                Interlocked.CompareExchange(ref _deferredFailure, "reference-nonfinite", null);
                return;
            }
            var intake = Intake;
            if (intake is null) return;
            long? newRevision = null;
            var message = new byte[PureSignalPairProtocol.ReferenceBytes(iq.Length / 2)];
            lock (_referenceGate)
            {
                var tones = Volatile.Read(ref _toneReader);
                long revision = tones?.TwoToneRevision ?? 0;
                if (revision != _observedTwoToneRevision)
                {
                    _observedTwoToneRevision = revision;
                    newRevision = revision;
                }
                // The epoch is published only after its key edge is queued, so
                // a frame stamped with it is always queued behind the edge.
                PureSignalPairProtocol.WriteReference(message, _referenceSequence++, intake.KeyEpoch, revision,
                    sampleRateHz, iq);
                if (!_outbound.Writer.TryWrite(new(message, WebSocketMessageType.Binary)))
                {
                    Interlocked.CompareExchange(ref _deferredFailure, "reference-backlog", null);
                    return;
                }
            }
            // The tones are read under the engine lock, so never on the TX path.
            if (newRevision is not null) _owner.OffTxPath(AnnounceTwoTone);
        }

        // Announces the tones of the revision in force; a revision that moves
        // while they are read is announced when the TX observer sees it.
        private void AnnounceTwoTone()
        {
            var tones = Volatile.Read(ref _toneReader);
            var intake = Intake;
            if (tones is null || intake is null) return;
            long revision = tones.TwoToneRevision;
            bool twoTone = tones.TryGetTwoTone(out double low, out double high);
            if (tones.TwoToneRevision != revision) return;
            intake.AnnounceTwoTone(revision, twoTone ? low : null, twoTone ? high : null);
        }

        private async Task SendLoopAsync(WebSocket socket, CancellationToken stop)
        {
            await foreach (var message in _outbound.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(message.Bytes, message.Type, true, stop).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken stop)
        {
            var buffer = new byte[PureSignalPairProtocol.PairBlockBytes(PureSignalPairProtocol.MaximumPairComplex)];
            while (true)
            {
                bool ready;
                lock (_gate) ready = _ready?.Task.IsCompletedSuccessfully == true;
                (int filled, WebSocketMessageType type) received;
                try
                {
                    received = ready
                        ? await ReceiveMessageAsync(socket, buffer, stop).WaitAsync(TimeSpan.FromSeconds(2), stop)
                            .ConfigureAwait(false)
                        : await ReceiveMessageAsync(socket, buffer, stop).ConfigureAwait(false);
                }
                catch (TimeoutException) { Fail("feedback-timeout"); return; }
                var (filled, type) = received;
                if (filled < 0)
                {
                    // The far side names why it ended the sampler; keep that reason.
                    if (!IsSpent) Fail("remote-" + (socket.CloseStatusDescription is { Length: > 0 } why ? why : "closed"));
                    return;
                }
                bool accepted = type == WebSocketMessageType.Text
                    ? HandleText(buffer.AsSpan(0, filled))
                    : HandleBinary(buffer.AsSpan(0, filled));
                if (!accepted) return;
            }
        }

        private void Fail(string reason)
        {
            if (Interlocked.Exchange(ref _failed, 1) != 0) return;
            _log.LogWarning("external_feedback.source_failed reason={Reason}", reason);
            bool armed = IsArmedOrArming;
            Stop(reason);
            _owner.Detach(this, reason, failClosed: false);
            if (armed) _owner.FailClosed(reason, RuntimeEpoch);
        }

        private static async Task<(int Count, WebSocketMessageType Type)> ReceiveMessageAsync(
            WebSocket socket, byte[] buffer, CancellationToken stop)
        {
            int filled = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (filled == buffer.Length) return (0, WebSocketMessageType.Binary);
                result = await socket.ReceiveAsync(buffer.AsMemory(filled), stop).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return (-1, result.MessageType);
                filled += result.Count;
            } while (!result.EndOfMessage);
            return (filled, result.MessageType);
        }
    }
}
