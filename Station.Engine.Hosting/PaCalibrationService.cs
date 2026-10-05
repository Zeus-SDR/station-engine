// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Threading.Channels;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed record PaCalibrationStartRequest(bool AmplifierOffConfirmed);

public sealed record PaCalibrationBandFailure(string Band, string Reason);

/// <summary>
/// <see cref="PendingSave"/> is true when a run ended without calibrating
/// every band but some bands passed all three targets: those results are held
/// (neither live nor durable) until the operator saves or discards them.
/// </summary>
public sealed record PaCalibrationStatus(
    string State,
    string? Band,
    double? TargetWatts,
    float? MeasuredWatts,
    int CompletedSteps,
    int TotalSteps,
    string? Message,
    IReadOnlyList<string>? PassedBands = null,
    IReadOnlyList<PaCalibrationBandFailure>? FailedBands = null,
    bool PendingSave = false);

/// <summary>
/// Timing knobs for the calibration controller. Production uses
/// <see cref="Default"/>; tests shrink them so a full eleven-band run stays
/// fast without changing the control logic.
/// </summary>
internal sealed record PaCalibrationTiming(
    TimeSpan MinimumSettle,
    TimeSpan PlateauSampleSpacing,
    TimeSpan ResponseTimeout,
    TimeSpan FloorResponseTimeout,
    TimeSpan PlateauTimeout,
    TimeSpan QuietSilence,
    TimeSpan QuietTimeout,
    TimeSpan TargetPause)
{
    public static PaCalibrationTiming Default { get; } = new(
        MinimumSettle: TimeSpan.FromMilliseconds(300),
        PlateauSampleSpacing: TimeSpan.FromMilliseconds(60),
        ResponseTimeout: TimeSpan.FromSeconds(4),
        FloorResponseTimeout: TimeSpan.FromSeconds(1),
        PlateauTimeout: TimeSpan.FromSeconds(10),
        QuietSilence: TimeSpan.FromSeconds(1),
        QuietTimeout: TimeSpan.FromSeconds(15),
        TargetPause: TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// Owns the RF-sensitive, single-flight PA calibration sequence. Calibration
/// values live in PaSettingsStore's transient overlay. When every band
/// converges they become durable automatically. Otherwise the overlay is
/// rolled back and the bands that passed all three targets are held for the
/// operator to save or discard; a band that failed always keeps its previous
/// settings.
/// </summary>
public sealed class PaCalibrationService
{
    private static readonly int[] TargetsWatts = [10, 25, 50];
    internal const double CalibrationToleranceFraction = 0.10d;
    // Converge tighter than the acceptance tolerance when the drive byte can
    // resolve it; the 10% band is only the floor when trims stop helping.
    internal const double FineToleranceFraction = 0.03d;
    private const int MaxFineTrims = 2;
    // Top of the PA gain slider range (PA_GAIN_MAX_DB in PaSettingsPanel).
    internal const double MaxGainDb = 70d;
    // Full-byte PA gain is attenuation. Every target restarts at maximum
    // attenuation and works down, because neither a persisted seed nor the
    // preceding target proves the PA's response. The highest shipped seed is
    // ~51 dB (Saturn/G2), so the first carrier lands ~19 dB under target;
    // Hermes-class seeds (38.8-41.3 dB) start under the meter floor and climb
    // through FloorResponseTimeout-paced 1 dB steps until measurable.
    internal const double ConservativeStartGainDb = MaxGainDb;
    private const double MinimumMeasurableWatts = 0.1d;
    // Output-raising steps stay small. A 1 dB step changes ideal power by only
    // ~26%; a 2 dB step is allowed only while a verified reading shows the PA
    // is still far enough below target that the step lands at least 4 dB
    // under it, so the final approach is always 1 dB at a time.
    internal const double MaxGainAdjustmentDb = 1d;
    internal const double MaxCoarseGainAdjustmentDb = 2d;
    private const double CoarseStepMarginDb = 4d;
    private const double QuantizationSlopDb = 0.4d;
    private const int MaxAdjustmentsPerTarget = 60;
    // Without any measurable carrier, attenuation may come down at most this
    // far from the start before the run stops. The lowest shipped seed is
    // 38.8 dB, so a working PA shows forward power within ~12 dB; a dead
    // meter or coupler reads 0 W forever and must not walk the drive up.
    internal const double MaxBlindClimbDb = 25d;
    // Two bands in a row failing points at the load or the meter, not at
    // the bands themselves.
    private const int MaxConsecutiveBandFailures = 2;
    private const int PlateauSampleCount = 4;
    private const double PlateauSpreadFraction = 0.06d;
    private const double PlateauSpreadFloorWatts = 0.05d;
    // Forward power below this after unkey counts as "carrier gone".
    private const double QuietWatts = 0.5d;
    private const int QuietSampleCount = 3;
    private static readonly TimeSpan InvariantCheckInterval = TimeSpan.FromMilliseconds(50);
    // Status/invariant refresh cadence on the consumer side, so a telemetry
    // flood does not cost a StateDto projection per sample.
    private static readonly TimeSpan ConsumerRefreshInterval = TimeSpan.FromMilliseconds(50);
    // Monotonic: deadlines that keep RF keyed must not follow wall-clock
    // steps (NTP on RTC-less Pi / CM5 hosts).
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly PaCalibrationTiming _timing;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly TxMetersService _meters;
    private readonly DspPipelineService _pipeline;
    private readonly PaSettingsStore _pa;
    private readonly IBandPlanService _bandPlan;
    private readonly ILogger<PaCalibrationService> _log;
    private readonly object _sync = new();
    private CancellationTokenSource? _runCancellation;
    private PaCalibrationStatus _status = IdleStatus();
    private double? _armedSafetyTargetWatts;
    private bool _armedSixMeters;
    private long? _expectedTxFrequencyHz;
    private RxMode? _expectedMode;
    private string? _safetyTripMessage;
    private string? _externalStateChangeMessage;
    // Longest observed delay between a change and the meter showing it, for
    // the current run. Response windows scale with it so telemetry that
    // trails the radio by more than the configured window still cannot make
    // the controller accept a stale plateau as current. Run task only.
    private TimeSpan _observedLatency;
    // Per-band outcome of the current or most recent run. Guarded by _sync.
    private readonly List<string> _passedBands = new();
    private readonly List<PaCalibrationBandFailure> _failedBands = new();
    // Results of bands that passed, held after a run that did not calibrate
    // every band, until the operator saves or discards them. Guarded by _sync.
    private PendingResults? _pending;
    // True while held results are being committed. Guarded by _sync.
    private bool _savingPending;

    public PaCalibrationService(
        RadioService radio,
        TxService tx,
        TxMetersService meters,
        DspPipelineService pipeline,
        PaSettingsStore pa,
        IBandPlanService bandPlan,
        ILogger<PaCalibrationService> log)
        : this(radio, tx, meters, pipeline, pa, bandPlan, log, PaCalibrationTiming.Default)
    {
    }

    internal PaCalibrationService(
        RadioService radio,
        TxService tx,
        TxMetersService meters,
        DspPipelineService pipeline,
        PaSettingsStore pa,
        IBandPlanService bandPlan,
        ILogger<PaCalibrationService> log,
        PaCalibrationTiming timing)
    {
        _timing = timing;
        _radio = radio;
        _tx = tx;
        _meters = meters;
        _pipeline = pipeline;
        _pa = pa;
        _bandPlan = bandPlan;
        _log = log;
    }

    public PaCalibrationStatus Status { get { lock (_sync) return _status; } }

    public bool TryStart(PaCalibrationStartRequest request, out string? error)
    {
        if (!request.AmplifierOffConfirmed)
        {
            error = "Confirm that the external amplifier is turned off.";
            return false;
        }

        StateDto state = _radio.Snapshot();
        PaSettingsDto settings = _pa.GetAll(
            _radio.EffectiveBoardKind,
            _radio.EffectiveOrionMkIIVariant);
        error = ValidateStart(state, settings);
        if (error is not null) return false;

        lock (_sync)
        {
            if (_runCancellation is not null)
            {
                error = "PA calibration is already running.";
                return false;
            }
            if (_savingPending)
            {
                error = "Calibration results are still being saved.";
                return false;
            }
            // Held results are never dropped by a new start; the operator
            // decides them first.
            if (_pending is not null)
            {
                error = "Save or discard the results from the previous PA calibration before starting a new one.";
                return false;
            }

            if (!_tx.TryBeginPaCalibrationLease(out error))
                return false;
            if (!_radio.TryBeginPaCalibrationInvariantLease(out error))
            {
                _tx.EndPaCalibrationLease();
                return false;
            }

            bool overlayStarted = false;
            try
            {
                settings = _pa.BeginCalibrationOverlay(
                    _radio.EffectiveBoardKind,
                    _radio.EffectiveOrionMkIIVariant);
                overlayStarted = true;
                state = _radio.Snapshot();
                error = ValidateStart(state, settings);
                if (error is not null)
                {
                    _pa.CompleteCalibrationOverlay(persist: false);
                    overlayStarted = false;
                    _radio.EndPaCalibrationInvariantLease();
                    _tx.EndPaCalibrationLease();
                    return false;
                }
                state = _radio.DisarmPureSignalForPaCalibration();
            }
            catch
            {
                try
                {
                    if (overlayStarted)
                        _pa.CompleteCalibrationOverlay(persist: false);
                }
                finally
                {
                    _radio.EndPaCalibrationInvariantLease();
                    _tx.EndPaCalibrationLease();
                }
                throw;
            }

            var invariant = new RunInvariant(
                _radio.ConnectedBoardKind,
                _radio.EffectiveOrionMkIIVariant,
                state.DriveMaxPct);
            _runCancellation = new CancellationTokenSource();
            _passedBands.Clear();
            _failedBands.Clear();
            _status = new(
                "running", null, null, null, 0,
                BandUtils.HfBands.Count * TargetsWatts.Length,
                "Preparing calibration");
            _ = Task.Run(() => RunAsync(
                state,
                settings,
                invariant,
                _runCancellation.Token));
        }

        error = null;
        return true;
    }

    public void Cancel()
    {
        lock (_sync)
        {
            if (_runCancellation is null) return;
            _status = _status with
            {
                State = "cancelling",
                Message = "Stopping and restoring PA settings",
            };
            _runCancellation.Cancel();
        }
    }

    /// <summary>
    /// Persists the bands that passed in the last run that did not calibrate
    /// every band. Bands that failed or were never reached are untouched.
    /// </summary>
    public bool TrySavePassedBands(out string? error)
    {
        PendingResults pending;
        lock (_sync)
        {
            if (_savingPending)
            {
                error = "Calibration results are already being saved.";
                return false;
            }
            if (_pending is null)
            {
                error = "No PA calibration results are waiting to be saved.";
                return false;
            }
            // Claimed under the lock: discard and a new run both refuse
            // while the commit is in flight.
            pending = _pending;
            _savingPending = true;
        }

        bool committed = false;
        try
        {
            if (!_radio.IsConnected ||
                _radio.EffectiveBoardKind != pending.Board ||
                _radio.EffectiveOrionMkIIVariant != pending.Variant)
            {
                error = "Reconnect the radio that was calibrated before saving; if the radio changed, run calibration again.";
                return false;
            }
            // Committed outside _sync: the store notifies RadioService, which
            // recomputes drive. The store re-checks the rated output inside
            // its own lock.
            _pa.CommitCalibrationResults(
                pending.Results, pending.Board, pending.Variant, pending.MaxPowerWatts);
            committed = true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            lock (_sync)
            {
                _savingPending = false;
                if (committed)
                {
                    _pending = null;
                    _status = _status with
                    {
                        State = "completed",
                        PendingSave = false,
                        Message = $"Saved calibration for {BandList(pending.Results.Keys)}. Every other band kept its previous settings.",
                    };
                }
            }
        }
        error = null;
        return true;
    }

    /// <summary>Drops results waiting for a decision; nothing is saved.</summary>
    public bool TryDiscardPassedBands(out string? error)
    {
        lock (_sync)
        {
            if (_savingPending)
            {
                error = "Calibration results are already being saved.";
                return false;
            }
            if (_pending is not null)
            {
                _pending = null;
                _status = _status with
                {
                    PendingSave = false,
                    Message = "Calibration results discarded. Original PA settings were kept.",
                };
            }
        }
        error = null;
        return true;
    }

    private string? ValidateStart(StateDto state, PaSettingsDto settings)
    {
        if (!_radio.IsConnected || state.Status != ConnectionStatus.Connected)
            return "Connect a radio before calibrating the PA.";
        if (_radio.ConnectedBoardKind is HpsdrBoardKind.Unknown)
            return "The connected radio model is unknown.";
        if (_radio.ConnectedBoardKind is HpsdrBoardKind.HermesLite2)
            return "Automatic PA calibration is not supported on Hermes Lite 2.";
        if (_tx.IsMoxOn || _tx.IsTunOn || _radio.IsMox)
            return "Unkey MOX/TUN before starting calibration.";
        if (state.TxReceiverIndex != 0 ||
            RadioFrequencyResolver.IsSplitEnabledForTx(state) ||
            state.XitEnabled)
            return "Select RX1 for TX and turn SPLIT and XIT off before starting calibration.";
        if (!settings.Global.PaEnabled)
            return "Enable the PA before starting calibration.";
        if (settings.Global.PaMaxPowerWatts < 50)
            return "Rated PA output must be at least 50 W.";
        // The drive MAX ceiling clamps TUN drive at the final TX seam. If it
        // sits below the highest target, the drive math aims at less power
        // than the controller is converging on and the recorded gain is wrong.
        int requiredDrivePct = TunePercentFor(
            TargetsWatts[^1], settings.Global.PaMaxPowerWatts);
        if (state.DriveMaxPct < requiredDrivePct)
            return $"Raise the drive MAX limit to at least {requiredDrivePct}% so calibration can reach {TargetsWatts[^1]} W.";

        if (settings.Bands.Any(b => b.DisablePa))
            return "Enable the PA on every band before starting calibration.";
        if (ResolveBandFrequencies().Count != BandUtils.HfBands.Count)
            return "The active band plan does not provide a legal calibration frequency for every band.";
        return null;
    }

    private async Task RunAsync(
        StateDto originalState,
        PaSettingsDto originalSettings,
        RunInvariant invariant,
        CancellationToken cancellationToken)
    {
        bool success = false;
        // Set when the run stopped early (cancel, safety trip, fault): the
        // terminal state and message to report.
        string? abortState = null;
        string? abortMessage = null;
        int safetyPercent = originalSettings.Global.PaCalibrationSafetyPercent;
        double ratedOutputWatts = originalSettings.Global.PaMaxPowerWatts;
        int originalTune = originalState.TunePct;
        int currentTune = originalTune;
        long expectedVfoHz = originalState.VfoHz;
        RxMode expectedMode = originalState.Mode;
        // Board and variant are run invariants, so the meter calibration is
        // resolved once instead of on every telemetry packet.
        RadioCalibration calibration = RadioCalibrations.For(
            invariant.Board, invariant.Variant);
        long lastInvariantCheckMs = 0;
        var samples = Channel.CreateBounded<ForwardPowerSample>(
            new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        // Runs on the radio RX thread for every forward-power packet. At high
        // power a G2 sends a hi-priority status packet on every ADC-overload
        // event (thousands per second, P2_app OutHighPriority.c), and that
        // thread also carries RX IQ. Keep the per-packet work to the over-power
        // check; the state-invariant check needs a full StateDto projection
        // and is rate-limited.
        void OnRawPower(ushort forwardAdc, ushort reflectedAdc)
        {
            double? armedTarget;
            bool sixMeters;
            lock (_sync)
            {
                armedTarget = _armedSafetyTargetWatts;
                sixMeters = _armedSixMeters;
            }
            var (forwardWatts, _, _) = TxMetersService.ComputeMeters(
                forwardAdc, reflectedAdc, calibration, sixMeters);

            string? trip = null;
            long nowMs = Environment.TickCount64;
            if (armedTarget is not null &&
                nowMs - Interlocked.Read(ref lastInvariantCheckMs) >=
                    (long)InvariantCheckInterval.TotalMilliseconds)
            {
                Interlocked.Exchange(ref lastInvariantCheckMs, nowMs);
                trip = CheckExternalStateChange(invariant);
            }

            lock (_sync)
            {
                if (_armedSafetyTargetWatts is double target &&
                    _tx.TunOwner == MoxSource.Analyzer &&
                    IsOverPower(
                        forwardWatts,
                        target,
                        safetyPercent,
                        ratedOutputWatts) &&
                    _safetyTripMessage is null)
                {
                    trip = SafetyStopMessage(
                        forwardWatts, target, safetyPercent, ratedOutputWatts);
                    _safetyTripMessage = trip;
                }
            }

            if (trip is not null)
                _tx.TrySetTun(false, MoxSource.UI, out _);
            samples.Writer.TryWrite(new ForwardPowerSample(
                (float)forwardWatts, Clock.Elapsed));
        }

        // Trips latch for the whole run: re-arming for the next band or
        // target must never clear a trip that has not been acted on yet.
        lock (_sync)
        {
            _safetyTripMessage = null;
            _externalStateChangeMessage = null;
        }
        _observedLatency = TimeSpan.Zero;
        _meters.RawPowerTelemetryUpdated += OnRawPower;
        try
        {
            if (!await _pipeline.WaitForPsDisarmAsync().ConfigureAwait(false))
                throw new InvalidOperationException(
                    "PureSignal did not disarm completely; PA calibration was not started.");
            EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
            Dictionary<string, CalibrationPoint> frequencies = ResolveBandFrequencies();
            int completed = 0;
            int bandIndex = 0;
            int consecutiveFailures = 0;

            foreach (string band in BandUtils.HfBands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfCalibrationAborted();
                EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
                CalibrationPoint point = frequencies[band];
                // Apply the mode BEFORE pinning the exact VFO. Entering CW
                // deliberately bumps the dial by ±cw_pitch
                // (CwOffset.DialBumpForModeTransition), so writing the frequency
                // first would leave VfoHz off the requested midpoint once the
                // mode change lands, and the invariant check would misread that
                // internal bump as operator interference and abort. Setting mode
                // first lets the bump happen, then the VFO write pins the exact
                // midpoint the invariant expects — mirroring RestoreModeIfCurrent.
                _radio.SetPaCalibrationMode(point.Mode);
                _radio.SetPaCalibrationVfo(point.FrequencyHz);
                expectedVfoHz = point.FrequencyHz;
                expectedMode = point.Mode;
                currentTune = _radio.Snapshot().TunePct;
                _pa.SetCalibrationGain(band, ConservativeStartGainDb);

                int firstTargetWatts = TargetsWatts[0];
                int firstTunePct = TunePercentFor(firstTargetWatts, ratedOutputWatts);
                if (!_radio.SetPaCalibrationTuneDriveIfCurrent(firstTunePct, currentTune))
                    throw new ExternalCalibrationStateChangedException(
                        "PA calibration stopped because TUN power changed outside calibration.");
                currentTune = _radio.Snapshot().TunePct;
                while (samples.Reader.TryRead(out _)) { }
                ArmSafetyTarget(
                    CommandedWatts(ratedOutputWatts, currentTune),
                    expectedVfoHz,
                    expectedMode);
                ThrowIfCalibrationAborted();
                Update("running", band, firstTargetWatts, null, completed,
                    $"Keying TUN for {band}; calibrating the shared band gain at {firstTargetWatts:0.0} W");

                if (!_tx.TrySetPaCalibrationTun(true, out string? keyError))
                {
                    if (_tx.TunOwner is not null && _tx.TunOwner != MoxSource.Analyzer)
                        throw new ExternalCalibrationStateChangedException(
                            "PA calibration stopped because another controller keyed TUN.");
                    throw new InvalidOperationException(keyError ?? "TUN was refused.");
                }

                // The last plateau the controller trusted, and the drive model
                // (commanded watts × gain) that produced it. Unknown right
                // after key-up.
                Plateau? previous = null;
                double lastCommandedWatts = 0d;
                // A band that cannot converge or settle fails on its own: it is
                // unkeyed, keeps its previous settings, and the run moves on.
                // Safety trips, telemetry faults, and outside interference
                // still stop the whole run.
                string? bandFailure = null;
                try
                {
                    foreach (int targetWatts in TargetsWatts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
                        bool firstTarget = targetWatts == firstTargetWatts;
                        if (!firstTarget)
                        {
                            // Arm the higher target, then restore maximum attenuation
                            // before raising TUN drive. The PA response between targets
                            // is not necessarily linear, so the preceding gain does not
                            // prove that the nominal higher drive is safe.
                            int requestedTunePct = TunePercentFor(targetWatts, ratedOutputWatts);
                            ArmSafetyTarget(
                                CommandedWatts(ratedOutputWatts, requestedTunePct),
                                expectedVfoHz,
                                expectedMode);
                            _pa.SetCalibrationGain(band, ConservativeStartGainDb);
                            if (!_radio.SetPaCalibrationTuneDriveIfCurrent(requestedTunePct, currentTune))
                                throw new ExternalCalibrationStateChangedException(
                                    "PA calibration stopped because TUN power changed outside calibration.");
                            currentTune = _radio.Snapshot().TunePct;
                            ArmSafetyTarget(
                                CommandedWatts(ratedOutputWatts, currentTune),
                                expectedVfoHz,
                                expectedMode);
                            while (samples.Reader.TryRead(out _)) { }
                            Update("running", band, targetWatts, null, completed,
                                $"Calibrating {band} shared gain at {targetWatts:0.0} W");
                        }

                        lastCommandedWatts = CommandedWatts(ratedOutputWatts, currentTune);
                        previous = await ConvergeAsync(
                            band,
                            targetWatts,
                            currentTune,
                            previous,
                            completed,
                            expectedVfoHz, expectedMode,
                            invariant,
                            safetyPercent,
                            ratedOutputWatts,
                            samples.Reader,
                            cancellationToken).ConfigureAwait(false);

                        _pa.CaptureCalibrationGain(
                            band,
                            targetWatts,
                            _radio.EffectiveBoardKind,
                            _radio.EffectiveOrionMkIIVariant,
                            originalSettings.Global.PaMaxPowerWatts);

                        completed++;
                        Update("running", band, targetWatts, (float)previous.Watts, completed,
                            $"{band} {targetWatts:0.0} W complete");
                        await Task.Delay(_timing.TargetPause, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (BandCalibrationFailedException ex)
                {
                    bandFailure = ex.Message;
                    _log.LogWarning(
                        "pa.calibration.band_failed band={Band} reason={Reason}",
                        band, ex.Message);
                }
                finally
                {
                    _tx.TrySetPaCalibrationTun(false, out _);
                    ArmSafetyTarget(null);
                }
                ThrowIfCalibrationAborted();
                if (_tx.IsTunOn)
                    throw new InvalidOperationException(
                        $"TUN did not release after calibrating {band}.");

                // Forward-power telemetry can trail the radio (a backlog of
                // status packets on the shared RX thread). Readings from this
                // band's highest target must not arrive after the next band has
                // armed its lower limit, so wait until the meter shows the
                // carrier gone. A late over-limit reading still fails the run.
                Update("running", band, TargetsWatts[^1], null, completed,
                    $"Waiting for {band} forward power to clear");
                await WaitForCarrierClearAsync(
                    band,
                    lastCommandedWatts,
                    safetyPercent,
                    ratedOutputWatts,
                    samples.Reader,
                    cancellationToken).ConfigureAwait(false);
                ThrowIfCalibrationAborted();

                bandIndex++;
                completed = bandIndex * TargetsWatts.Length;
                lock (_sync)
                {
                    if (bandFailure is null) _passedBands.Add(band);
                    else _failedBands.Add(new PaCalibrationBandFailure(band, bandFailure));
                }
                Update("running", band, null, null, completed,
                    bandFailure is null
                        ? $"{band} calibrated"
                        : $"{band} did not pass and keeps its previous settings: {bandFailure}");
                consecutiveFailures = bandFailure is null ? 0 : consecutiveFailures + 1;
                if (consecutiveFailures >= MaxConsecutiveBandFailures)
                    throw new InvalidOperationException(
                        $"PA calibration stopped after {consecutiveFailures} bands in a row did not pass. Check the dummy load and the forward-power reading.");
            }

            ThrowIfCalibrationAborted();
            EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
            success = true;
        }
        catch (OperationCanceledException)
        {
            abortState = "cancelled";
            abortMessage = "PA calibration stopped.";
        }
        catch (ExternalCalibrationStateChangedException ex)
        {
            abortState = "failed";
            abortMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "pa.calibration.failed");
            abortState = "failed";
            abortMessage = ex.Message;
        }
        finally
        {
            _tx.TrySetPaCalibrationTun(false, out _);
            ArmSafetyTarget(null);
            _meters.RawPowerTelemetryUpdated -= OnRawPower;

            try
            {
                Exception? cleanupFailure = null;
                string[] passed;
                lock (_sync) passed = _passedBands.ToArray();
                bool allPassed = success && passed.Length == BandUtils.HfBands.Count;
                // Read the passed bands' results before the overlay goes away,
                // so they can still be offered when they are not committed now.
                IReadOnlyDictionary<string, CalibrationBandResult>? results = null;
                if (passed.Length > 0)
                {
                    try { results = _pa.CalibrationResultsFor(passed); }
                    catch (Exception ex) { _log.LogError(ex, "pa.calibration.results.failed"); }
                }
                bool committed = false;
                try
                {
                    _pa.CompleteCalibrationOverlay(allPassed);
                    committed = allPassed;
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                    _log.LogError(ex, "pa.calibration.cleanup.failed");
                }
                if (!committed && results is { Count: > 0 })
                {
                    // Identity the curves were captured under; saving later
                    // requires the same radio and rated output.
                    CalibrationGainCurve captured = results.Values.First().Curve;
                    lock (_sync)
                        _pending = new PendingResults(
                            results,
                            captured.Board,
                            captured.Variant,
                            captured.MaxPowerWatts);
                }

                if (_radio.IsConnected)
                {
                    try { _radio.RestoreVfoIfCurrent(originalState.VfoHz, expectedVfoHz); }
                    catch (Exception ex) { cleanupFailure ??= ex; _log.LogError(ex, "pa.calibration.vfo_restore.failed"); }
                    try { _radio.RestoreModeIfCurrent(originalState.Mode, expectedMode); }
                    catch (Exception ex) { cleanupFailure ??= ex; _log.LogError(ex, "pa.calibration.mode_restore.failed"); }
                    try { _radio.SetPaCalibrationTuneDriveIfCurrent(originalTune, currentTune); }
                    catch (Exception ex) { cleanupFailure ??= ex; _log.LogError(ex, "pa.calibration.tune_restore.failed"); }
                }

                bool pendingSave;
                PaCalibrationBandFailure[] failed;
                lock (_sync)
                {
                    pendingSave = _pending is not null;
                    failed = _failedBands.ToArray();
                }
                string savable = pendingSave
                    ? $" {BandList(passed)} passed: save {(passed.Length == 1 ? "it" : "them")} or discard."
                    : string.Empty;
                if (cleanupFailure is not null)
                {
                    Update("failed", Status.Band, Status.TargetWatts, Status.MeasuredWatts,
                        Status.CompletedSteps,
                        $"PA calibration cleanup failed: {cleanupFailure.Message}{savable}");
                }
                else if (abortState is not null)
                {
                    Update(abortState, Status.Band, Status.TargetWatts, Status.MeasuredWatts,
                        Status.CompletedSteps,
                        $"{abortMessage} Original PA settings were restored.{savable}");
                }
                else if (committed)
                {
                    Update("completed", null, null, null,
                        BandUtils.HfBands.Count * TargetsWatts.Length,
                        "PA calibration applied successfully.");
                }
                else
                {
                    Update(passed.Length > 0 ? "partial" : "failed", null, null, null,
                        BandUtils.HfBands.Count * TargetsWatts.Length,
                        $"{BandList(failed.Select(f => f.Band))} did not pass and kept {(failed.Length == 1 ? "its" : "their")} previous settings.{savable}");
                }
            }
            finally
            {
                lock (_sync)
                {
                    _runCancellation?.Dispose();
                    _runCancellation = null;
                }
                _radio.EndPaCalibrationInvariantLease();
                _tx.EndPaCalibrationLease();
            }
        }
    }

    // Rate-limited external-interference check for the RX-thread handler.
    // Returns the trip message the first time an invariant breaks.
    private string? CheckExternalStateChange(RunInvariant invariant)
    {
        StateDto snap = _radio.Snapshot();
        long txFrequencyHz = RadioFrequencyResolver.TxFrequencyHz(snap);
        lock (_sync)
        {
            if (_armedSafetyTargetWatts is null || _externalStateChangeMessage is not null)
                return null;
            bool calibrationOwnsTun = _tx.TunOwner == MoxSource.Analyzer;
            string? invariantError = CalibrationInvariantError(
                snap,
                _expectedTxFrequencyHz,
                _expectedMode,
                invariant);
            if (invariantError is not null)
            {
                _externalStateChangeMessage = invariantError;
                return invariantError;
            }
            if (_tx.IsTunOn && !calibrationOwnsTun)
            {
                // Not a trip: TUN belongs to someone else, so calibration must
                // not release it, only stop.
                _externalStateChangeMessage =
                    "PA calibration stopped because TUN ownership changed outside calibration.";
                return null;
            }
            if (calibrationOwnsTun &&
                (_expectedTxFrequencyHz != txFrequencyHz || _expectedMode != snap.Mode))
            {
                _externalStateChangeMessage =
                    "PA calibration stopped because the transmit frequency or mode changed outside calibration.";
                return _externalStateChangeMessage;
            }
            return null;
        }
    }

    /// <summary>
    /// Drives one band/target to convergence with at most one gain change in
    /// flight. After each change the controller waits until the meter shows
    /// a stable plateau that reflects that change (the reading must move in
    /// the direction and by at least half the size the drive bytes predict)
    /// before it decides the next step. Telemetry that trails the radio,
    /// which happens at high power when status packets back up on the RX
    /// thread, therefore delays calibration instead of letting the controller
    /// keep stepping on stale readings and overshoot.
    /// </summary>
    private async Task<Plateau> ConvergeAsync(
        string band,
        int nominalTargetWatts,
        int tunePct,
        Plateau? previous,
        int completed,
        long expectedVfoHz,
        RxMode expectedMode,
        RunInvariant invariant,
        int safetyPercent,
        double ratedOutputWatts,
        ChannelReader<ForwardPowerSample> samples,
        CancellationToken cancellationToken)
    {
        // Converge on the watts the drive math actually commands at this TUN
        // percent, so the recorded gain is exact for the operating point it
        // will be used at.
        double targetWatts = CommandedWatts(ratedOutputWatts, tunePct);
        IRadioDriveProfile drive = RadioDriveProfiles.For(invariant.Board);
        // Relative output the radio will produce for a gain, from the drive
        // output it will really be sent (8-bit quantisation included).
        double OutputModel(double gain)
        {
            Zeus.Protocol1.TxDriveOutput output = drive.EncodeDrive(tunePct, gain, ratedOutputWatts);
            double amplitude = output.DriveByte * output.IqScale;
            return amplitude * amplitude;
        }

        double gainDb = CurrentGainDb(band);
        double model = OutputModel(gainDb);
        // Output model before the most recent change, so verification can
        // insist that the newest step itself is visible.
        double modelBeforeLastChange = previous?.Model ?? 0d;
        // Right after key-up there is no trusted baseline yet; the
        // carrier needs longer to appear than a gain change needs to land.
        TimeSpan settle = previous is null
            ? _timing.MinimumSettle + _timing.MinimumSettle
            : _timing.MinimumSettle;
        int fineTrims = 0;
        // Predictions are measured from the last plateau known to reflect its
        // settings. An unverified plateau may be a trailing reading, so it
        // never becomes the baseline while a verified one exists; otherwise a
        // late response to an older step could "verify" a newer one.
        Plateau? baseline = previous;
        // Whether this target has produced any measurable forward power. A
        // target that fails without ever showing a carrier is a meter,
        // coupler, or load fault, not a band problem, so it stops the run.
        bool sawCarrier = false;

        for (int adjustment = 0; ; adjustment++)
        {
            Plateau plateau;
            try
            {
                plateau = await AwaitPlateauAsync(
                    band, nominalTargetWatts, targetWatts, completed,
                    expectedVfoHz, expectedMode, invariant,
                    safetyPercent, ratedOutputWatts,
                    baseline, model, modelBeforeLastChange, gainDb,
                    settle,
                    samples, cancellationToken).ConfigureAwait(false);
            }
            catch (BandCalibrationFailedException ex) when (!sawCarrier)
            {
                throw new InvalidOperationException(NoCarrierMessage(ex.Message));
            }
            if (plateau.Watts >= MinimumMeasurableWatts) sawCarrier = true;
            if (plateau.Verified ||
                baseline is null ||
                baseline.Watts < MinimumMeasurableWatts)
                baseline = plateau;
            settle = _timing.MinimumSettle;

            double measured = plateau.Watts;
            if (measured >= MinimumMeasurableWatts &&
                Math.Abs(measured - targetWatts) <= targetWatts * FineToleranceFraction)
                return plateau;
            bool withinTolerance = measured >= MinimumMeasurableWatts &&
                IsWithinTolerance(measured, targetWatts);
            if (withinTolerance && fineTrims >= MaxFineTrims)
                return plateau;
            if (adjustment >= MaxAdjustmentsPerTarget)
            {
                if (withinTolerance) return plateau;
                throw BandFailure(
                    $"{band} could not converge at {nominalTargetWatts:0.0} W (last reading {measured:0.0} W).");
            }

            double nextGain = Math.Round(NextGainDb(
                gainDb, measured, targetWatts, plateau.Verified), 2);
            if (OutputModel(nextGain) == model)
            {
                // The correction is finer than one drive-byte step, so it
                // would change nothing on the wire.
                if (withinTolerance) return plateau;
                // Out of tolerance yet under one step: move to the adjacent
                // byte in the needed direction.
                double direction = Math.Sign(nextGain - gainDb);
                if (direction == 0)
                    direction = measured > targetWatts ? 1d : -1d;
                for (int i = 0; i < 100 && OutputModel(nextGain) == model; i++)
                    nextGain = Math.Round(nextGain + direction * 0.05, 2);
                if (OutputModel(nextGain) == model)
                    throw BandFailure(
                        $"{band} could not converge at {nominalTargetWatts:0.0} W (last reading {measured:0.0} W).");
            }
            if (!sawCarrier && ConservativeStartGainDb - nextGain > MaxBlindClimbDb)
                throw new InvalidOperationException(NoCarrierMessage(
                    $"No forward power was measured on {band} at {nominalTargetWatts:0.0} W after lowering attenuation {MaxBlindClimbDb:0} dB."));
            // Byte rounding normally adds a fraction of a dB to a step. Near
            // maximum attenuation, though, the drive byte is small (2-5 at
            // 10 W), so the smallest output-raising step can be well over the
            // coarse limit (byte 3 -> 4 is +2.5 dB). Take such a step only
            // while the reading is far enough below target that it still
            // lands the margin under.
            // A trailing (unverified) reading can understate the output, so it
            // cannot justify such a step; a reading under the meter floor only
            // understates the deficit, which is the safe direction.
            double nextModel = OutputModel(nextGain);
            if (nextModel > model && model > 0)
            {
                double raiseDb = 10d * Math.Log10(nextModel / model);
                bool underFloor = measured < MinimumMeasurableWatts;
                double deficitDb = underFloor
                    ? 10d * Math.Log10(targetWatts / MinimumMeasurableWatts)
                    : 10d * Math.Log10(targetWatts / measured);
                if (raiseDb > MaxCoarseGainAdjustmentDb + QuantizationSlopDb &&
                    (!(plateau.Verified || underFloor) ||
                     deficitDb < raiseDb + CoarseStepMarginDb))
                    throw BandFailure(
                        $"{band} drive resolution is too coarse to approach {nominalTargetWatts:0.0} W safely (last reading {measured:0.0} W).");
            }
            if (withinTolerance) fineTrims++;

            EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
            gainDb = nextGain;
            _pa.SetCalibrationGain(band, gainDb);
            while (samples.TryRead(out _)) { }
            ThrowIfCalibrationAborted();
            modelBeforeLastChange = model;
            model = OutputModel(gainDb);
        }

        Exception BandFailure(string message) => sawCarrier
            ? new BandCalibrationFailedException(message)
            : new InvalidOperationException(NoCarrierMessage(message));
    }

    private static string NoCarrierMessage(string detail) =>
        $"{detail} No forward power was measured; check the forward-power meter, the coupler, and the dummy load.";

    /// <summary>
    /// Reads telemetry until a stable plateau appears that can be attributed
    /// to the most recent change. Every sample is still checked against the
    /// safety limit, stale or not.
    /// </summary>
    private async Task<Plateau> AwaitPlateauAsync(
        string band,
        int nominalTargetWatts,
        double targetWatts,
        int completed,
        long expectedVfoHz,
        RxMode expectedMode,
        RunInvariant invariant,
        int safetyPercent,
        double ratedOutputWatts,
        Plateau? previous,
        double model,
        double modelBeforeLastChange,
        double gainDb,
        TimeSpan settle,
        ChannelReader<ForwardPowerSample> samples,
        CancellationToken cancellationToken)
    {
        TimeSpan changedAt = Clock.Elapsed;
        TimeSpan settleUntil = changedAt + settle;
        // Scale with observed telemetry latency, but never beyond a fixed
        // multiple of the configured window: keyed time stays bounded.
        TimeSpan adaptive = Min(_observedLatency * 3, _timing.ResponseTimeout * 3);
        TimeSpan responseDeadline = changedAt + Max(_timing.ResponseTimeout, adaptive);
        TimeSpan floorResponseDeadline = changedAt + Max(_timing.FloorResponseTimeout, adaptive);
        TimeSpan plateauDeadline = changedAt + Max(_timing.PlateauTimeout, adaptive + adaptive);
        var window = new Queue<ForwardPowerSample>(PlateauSampleCount);
        // Seeded one interval back (not MinValue): the subtractions below
        // would overflow.
        TimeSpan lastWindowSample = -_timing.PlateauSampleSpacing;
        TimeSpan lastRefresh = -ConsumerRefreshInterval;

        // How far, in dB, the drive output says this change should move the
        // reading. Unknown when the previous plateau was below the meter
        // floor or when this is the first reading after key-up.
        double? expectedMoveDb =
            previous is { } prior &&
            prior.Watts >= MinimumMeasurableWatts &&
            prior.Model > 0 && model > 0
                ? 10d * Math.Log10(model / prior.Model)
                : null;
        // The newest change alone. Equal to expectedMoveDb unless unverified
        // plateaus sit between the baseline and now.
        double lastStepDb = modelBeforeLastChange > 0 && model > 0
            ? 10d * Math.Log10(model / modelBeforeLastChange)
            : expectedMoveDb ?? 0d;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfCalibrationAborted();
            if (!_radio.IsConnected || !_tx.IsTunOn)
                throw new ExternalCalibrationStateChangedException(
                    "PA calibration stopped because the radio disconnected or TUN was released outside calibration.");

            ForwardPowerSample sample = await ReadSampleAsync(
                samples, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false)
                ?? throw new TimeoutException("Forward-power telemetry became stale.");
            ThrowIfCalibrationAborted();

            // Over-power is checked on every sample; the rest is refreshed at
            // a bounded rate so a telemetry flood stays cheap.
            float measured = sample.Watts;
            if (IsOverPower(measured, targetWatts, safetyPercent, ratedOutputWatts))
            {
                _tx.TrySetTun(false, MoxSource.UI, out _);
                throw new InvalidOperationException(SafetyStopMessage(
                    measured, targetWatts, safetyPercent, ratedOutputWatts));
            }
            TimeSpan now = Clock.Elapsed;
            if (now - lastRefresh >= ConsumerRefreshInterval)
            {
                lastRefresh = now;
                EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
                Update("running", band, nominalTargetWatts, measured, completed,
                    $"Adjusting {band}: {measured:0.0} W / {targetWatts:0.0} W at {gainDb:0.0} dB");
            }

            if (sample.SampledAt < settleUntil) continue;
            if (sample.SampledAt - lastWindowSample < _timing.PlateauSampleSpacing)
                continue;
            lastWindowSample = sample.SampledAt;
            if (window.Count == PlateauSampleCount) window.Dequeue();
            window.Enqueue(sample);
            if (window.Count < PlateauSampleCount) continue;

            double min = window.Min(s => s.Watts);
            double max = window.Max(s => s.Watts);
            double median = window
                .Select(s => (double)s.Watts)
                .OrderBy(w => w)
                .ElementAt(PlateauSampleCount / 2);
            bool stable = max - min <=
                Math.Max(median * PlateauSpreadFraction, PlateauSpreadFloorWatts);
            if (!stable)
            {
                if (sample.SampledAt >= plateauDeadline)
                    throw new BandCalibrationFailedException(
                        $"Forward power on {band} did not settle at {targetWatts:0.0} W (readings {min:0.0}–{max:0.0} W).");
                continue;
            }

            if (expectedMoveDb is double expected)
            {
                // A reduction that falls under the meter floor still counts.
                if (Math.Abs(expected) < 0.01 ||
                    MovedAsExpected(previous!.Watts, median, expected, lastStepDb))
                {
                    NoteLatency(window.Peek().SampledAt - changedAt);
                    return new Plateau(median, model, Verified: true);
                }
                // Still showing the pre-change level: the reading may be
                // trailing the radio. Keep waiting unless the PA genuinely
                // is not responding (compression, foldback).
                if (sample.SampledAt < responseDeadline) continue;
                _log.LogInformation(
                    "pa.calibration.unverified band={Band} target={Target:0.0} expectedMoveDb={Expected:0.00} prior={Prior:0.00} now={Now:0.00}",
                    band, targetWatts, expected, previous!.Watts, median);
                return new Plateau(median, model, Verified: false);
            }

            // No measurable baseline: key-up, or the carrier was under the
            // meter floor. Nothing else is in flight, so the first carrier
            // above the floor reflects the current gain. Do not step blind on
            // a trailing zero; only a carrier still under the floor after the
            // floor window earns the next bounded 1 dB raise. Under the floor
            // the PA is ~20 dB below target and there is no telemetry flood,
            // so this window can be shorter than ResponseTimeout.
            if (median >= MinimumMeasurableWatts)
            {
                NoteLatency(window.Peek().SampledAt - changedAt);
                return new Plateau(median, model, Verified: true);
            }
            if (sample.SampledAt < floorResponseDeadline) continue;
            return new Plateau(median, model, Verified: false);
        }
    }

    /// <summary>
    /// After unkey, reads telemetry until the carrier is gone: several quiet
    /// readings in a row, or silence (no backlog left to drain). Any late
    /// reading above the last target's limit is a real past overshoot that
    /// the meter reported late, so it fails the run.
    /// </summary>
    private async Task WaitForCarrierClearAsync(
        string band,
        double lastTargetWatts,
        int safetyPercent,
        double ratedOutputWatts,
        ChannelReader<ForwardPowerSample> samples,
        CancellationToken cancellationToken)
    {
        TimeSpan deadline = Clock.Elapsed + _timing.QuietTimeout;
        int quiet = 0;
        while (quiet < QuietSampleCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForwardPowerSample? sample = await ReadSampleAsync(
                samples, _timing.QuietSilence, cancellationToken).ConfigureAwait(false);
            if (sample is null) return;
            float watts = sample.Value.Watts;
            if (lastTargetWatts > 0 &&
                IsOverPower(watts, lastTargetWatts, safetyPercent, ratedOutputWatts))
                throw new InvalidOperationException(
                    $"Safety stop: a delayed {band} forward-power reading of {watts:0.0} W exceeded the {SafetyLimitWatts(lastTargetWatts, safetyPercent, ratedOutputWatts):0.0} W limit for the {lastTargetWatts:0.0} W target.");
            quiet = watts < QuietWatts ? quiet + 1 : 0;
            if (Clock.Elapsed >= deadline)
                throw new TimeoutException(
                    $"Forward power did not clear after unkeying {band} (still reading {watts:0.0} W).");
        }
    }

    private static async Task<ForwardPowerSample?> ReadSampleAsync(
        ChannelReader<ForwardPowerSample> samples,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (samples.TryRead(out ForwardPowerSample queued)) return queued;
        using var sampleTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sampleTimeout.CancelAfter(timeout);
        try
        {
            return await samples.ReadAsync(sampleTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // Latency is measured to the first sample of the qualifying window, so it
    // excludes the controller's own settle and window-fill time.
    private void NoteLatency(TimeSpan latency)
    {
        if (latency > _timing.ResponseTimeout * 2)
            throw new TimeoutException(
                $"Forward-power telemetry is running {latency.TotalSeconds:0.0} s behind the radio, too far to calibrate safely.");
        if (latency > _observedLatency) _observedLatency = latency;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private double CurrentGainDb(string band) =>
        _pa.GetAll(
                _radio.EffectiveBoardKind,
                _radio.EffectiveOrionMkIIVariant)
            .Bands.First(b => b.Band == band).PaGainDb;

    private Dictionary<string, CalibrationPoint> ResolveBandFrequencies()
    {
        var result = new Dictionary<string, CalibrationPoint>(StringComparer.Ordinal);
        foreach (BandSegment segment in
                 _bandPlan.CurrentPlan.Where(s => s.Allocation == BandAllocation.Amateur))
        {
            long midpoint = segment.LowHz + ((segment.HighHz - segment.LowHz) / 2);
            string? band = BandUtils.FreqToBand(midpoint);
            if (band is null || result.ContainsKey(band)) continue;
            RxMode mode = segment.ModeRestriction switch
            {
                ModeRestriction.CwOnly => RxMode.CWU,
                ModeRestriction.DigitalOnly or ModeRestriction.CwAndDigital => RxMode.DIGU,
                _ => BandUtils.DefaultSsbModeForBand(band),
            };
            if (_bandPlan.InBand(midpoint, mode))
                result[band] = new(midpoint, mode);
        }
        return result;
    }

    private void Update(
        string state, string? band, double? target, float? measured,
        int completed, string message)
    {
        lock (_sync)
            _status = new(state, band, target, measured, completed,
                BandUtils.HfBands.Count * TargetsWatts.Length, message,
                _passedBands.ToArray(),
                _failedBands.ToArray(),
                _pending is not null);
    }

    // "160m", "160m and 80m", "160m, 80m and 40m", in band order.
    private static string BandList(IEnumerable<string> bands)
    {
        string[] ordered = bands
            .OrderBy(band => IndexOfBand(band))
            .ToArray();
        return ordered.Length switch
        {
            0 => "No band",
            1 => ordered[0],
            _ => $"{string.Join(", ", ordered[..^1])} and {ordered[^1]}",
        };
    }

    private static int IndexOfBand(string band)
    {
        for (int i = 0; i < BandUtils.HfBands.Count; i++)
            if (BandUtils.HfBands[i] == band) return i;
        return int.MaxValue;
    }

    private void ArmSafetyTarget(
        double? targetWatts,
        long? expectedTxFrequencyHz = null,
        RxMode? expectedMode = null)
    {
        bool sixMeters = expectedTxFrequencyHz is long hz &&
            BandUtils.FreqToBand(hz) == "6m";
        lock (_sync)
        {
            _armedSafetyTargetWatts = targetWatts;
            _armedSixMeters = sixMeters;
            _expectedTxFrequencyHz = expectedTxFrequencyHz;
            _expectedMode = expectedMode;
        }
    }

    private void ThrowIfCalibrationAborted()
    {
        string? message;
        bool external;
        lock (_sync)
        {
            external = _externalStateChangeMessage is not null;
            message = _externalStateChangeMessage ?? _safetyTripMessage;
        }
        if (external)
            throw new ExternalCalibrationStateChangedException(message!);
        if (message is not null)
            throw new InvalidOperationException(message);
    }

    private void EnsureCalibrationState(
        long expectedVfoHz,
        RxMode expectedMode,
        RunInvariant invariant)
    {
        StateDto current = _radio.Snapshot();
        string? error = CalibrationInvariantError(
            current,
            expectedVfoHz,
            expectedMode,
            invariant);
        if (error is not null)
            throw new ExternalCalibrationStateChangedException(error);
    }

    private string? CalibrationInvariantError(
        StateDto current,
        long? expectedVfoHz,
        RxMode? expectedMode,
        RunInvariant invariant)
    {
        if (!_radio.IsConnected || current.Status != ConnectionStatus.Connected)
            return "PA calibration stopped because the radio disconnected.";
        if (_radio.ConnectedBoardKind != invariant.Board ||
            _radio.EffectiveOrionMkIIVariant != invariant.Variant)
            return "PA calibration stopped because the connected radio identity changed.";
        if (current.PsEnabled)
            return "PA calibration stopped because PureSignal was armed.";
        if (current.DriveMaxPct != invariant.DriveMaxPct)
            return "PA calibration stopped because the drive maximum changed.";
        if (current.TxReceiverIndex != 0 ||
            RadioFrequencyResolver.IsSplitEnabledForTx(current) ||
            current.XitEnabled)
            return "PA calibration stopped because TX receiver, SPLIT, or XIT changed.";
        if (expectedVfoHz is not null && current.VfoHz != expectedVfoHz)
            return "PA calibration stopped because the frequency changed outside calibration.";
        if (expectedMode is not null && current.Mode != expectedMode)
            return "PA calibration stopped because the mode changed outside calibration.";
        return null;
    }

    internal static bool IsOverPower(
        double measuredWatts,
        double targetWatts,
        int safetyPercent = PaSettingsStore.DefaultCalibrationSafetyPercent,
        double ratedOutputWatts = double.PositiveInfinity) =>
        measuredWatts > SafetyLimitWatts(
            targetWatts, safetyPercent, ratedOutputWatts);

    internal static double SafetyLimitWatts(
        double targetWatts,
        int safetyPercent,
        double ratedOutputWatts) =>
        Math.Min(targetWatts * safetyPercent / 100d, ratedOutputWatts);

    internal static bool IsWithinTolerance(double measuredWatts, double targetWatts) =>
        targetWatts > 0d &&
        Math.Abs(measuredWatts - targetWatts) <=
            targetWatts * CalibrationToleranceFraction + 1e-9d;

    internal static double ComputeNextGainDb(
        double currentGainDb, double measuredWatts, double targetWatts) =>
        Math.Clamp(
            currentGainDb + 10d * Math.Log10(measuredWatts / targetWatts),
            0d, MaxGainDb);

    /// <summary>
    /// Next gain from a trusted plateau. Reducing output (raising the gain
    /// figure) is never rate-limited. Raising output is limited to
    /// <see cref="MaxGainAdjustmentDb"/>, or up to
    /// <see cref="MaxCoarseGainAdjustmentDb"/> when a verified reading shows
    /// the PA is far enough below target that the step still lands at least
    /// <see cref="CoarseStepMarginDb"/> under it.
    /// </summary>
    internal static double NextGainDb(
        double currentGainDb,
        double measuredWatts,
        double targetWatts,
        bool verified)
    {
        if (measuredWatts < MinimumMeasurableWatts)
            return Math.Clamp(currentGainDb - MaxGainAdjustmentDb, 0d, MaxGainDb);
        double requested = ComputeNextGainDb(currentGainDb, measuredWatts, targetWatts);
        if (requested >= currentGainDb) return requested;
        double deficitDb = 10d * Math.Log10(targetWatts / measuredWatts);
        double limit = verified
            ? Math.Clamp(
                deficitDb - CoarseStepMarginDb,
                MaxGainAdjustmentDb,
                MaxCoarseGainAdjustmentDb)
            : MaxGainAdjustmentDb;
        return Math.Max(requested, currentGainDb - limit);
    }

    /// <summary>
    /// True when a reading moved from <paramref name="priorWatts"/> in the
    /// predicted direction by at least half the predicted dB, and by enough
    /// that at least half of the newest step (<paramref name="lastStepDb"/>)
    /// is visible. The second condition stops a late response to an older,
    /// unverified step from confirming a newer one.
    /// </summary>
    internal static bool MovedAsExpected(
        double priorWatts, double nowWatts, double expectedMoveDb, double? lastStepDb = null)
    {
        double observedDb = 10d * Math.Log10(Math.Max(nowWatts, 1e-6) / priorWatts);
        double step = Math.Abs(lastStepDb ?? expectedMoveDb);
        return expectedMoveDb > 0
            ? observedDb >= Math.Max(expectedMoveDb / 2d, expectedMoveDb - step / 2d)
            : observedDb <= Math.Min(expectedMoveDb / 2d, expectedMoveDb + step / 2d);
    }

    internal static int TunePercentFor(int targetWatts, double ratedOutputWatts) =>
        Math.Clamp((int)Math.Round(targetWatts * 100d / ratedOutputWatts), 1, 100);

    private static double CommandedWatts(double ratedOutputWatts, int tunePct) =>
        ratedOutputWatts * tunePct / 100d;

    private static string SafetyStopMessage(
        double measuredWatts, double targetWatts, int safetyPercent, double ratedOutputWatts) =>
        $"Safety stop: measured {measuredWatts:0.0} W exceeds the {SafetyLimitWatts(targetWatts, safetyPercent, ratedOutputWatts):0.0} W limit ({safetyPercent}% of the {targetWatts:0.0} W target, capped at {ratedOutputWatts:0.0} W rated output).";

    private static PaCalibrationStatus IdleStatus() =>
        new("idle", null, null, null, 0,
            BandUtils.HfBands.Count * TargetsWatts.Length, null);

    private sealed record Plateau(double Watts, double Model, bool Verified);
    private sealed record CalibrationPoint(long FrequencyHz, RxMode Mode);
    private sealed record RunInvariant(
        HpsdrBoardKind Board,
        OrionMkIIVariant Variant,
        int DriveMaxPct);
    private readonly record struct ForwardPowerSample(
        float Watts, TimeSpan SampledAt);
    private sealed record PendingResults(
        IReadOnlyDictionary<string, CalibrationBandResult> Results,
        HpsdrBoardKind Board,
        OrionMkIIVariant Variant,
        int MaxPowerWatts);
    private sealed class ExternalCalibrationStateChangedException(string message)
        : InvalidOperationException(message);
    // One band could not converge or settle; the run continues without it.
    private sealed class BandCalibrationFailedException(string message)
        : InvalidOperationException(message);
}
