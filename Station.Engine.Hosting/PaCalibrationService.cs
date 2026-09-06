// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading.Channels;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed record PaCalibrationStartRequest(bool AmplifierOffConfirmed);

public sealed record PaCalibrationStatus(
    string State,
    string? Band,
    double? TargetWatts,
    float? MeasuredWatts,
    int CompletedSteps,
    int TotalSteps,
    string? Message);

/// <summary>
/// Owns the RF-sensitive, single-flight PA calibration sequence. Calibration
/// values live in PaSettingsStore's transient overlay and become durable only
/// after every band and target converges.
/// </summary>
public sealed class PaCalibrationService
{
    private static readonly int[] TargetsWatts = [10, 25, 50];
    internal const double CalibrationToleranceFraction = 0.10d;
    // Full-byte PA gain is attenuation: 70 dB is the least-drive value the
    // encoder accepts. Each target must approach from here because neither a
    // persisted seed nor the preceding target proves the PA's response.
    private const double ConservativeStartGainDb = 70d;
    private const double MinimumMeasurableWatts = 0.1d;
    // A 1 dB output-raising correction changes ideal power by only ~26%.
    // Real PA/meter paths are not linear across a large gain change, so this
    // deliberately slow ramp prevents a low reading from commanding a leap
    // past the configured safety limit.
    internal const double MaxGainAdjustmentDb = 1d;
    private const int AdjustmentSampleCount = 3;
    private static readonly TimeSpan AdjustmentSampleSpacing = TimeSpan.FromMilliseconds(75);
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
    private long? _expectedTxFrequencyHz;
    private RxMode? _expectedMode;
    private string? _safetyTripMessage;
    private string? _externalStateChangeMessage;

    public PaCalibrationService(
        RadioService radio,
        TxService tx,
        TxMetersService meters,
        DspPipelineService pipeline,
        PaSettingsStore pa,
        IBandPlanService bandPlan,
        ILogger<PaCalibrationService> log)
    {
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
        int safetyPercent = originalSettings.Global.PaCalibrationSafetyPercent;
        double ratedOutputWatts = originalSettings.Global.PaMaxPowerWatts;
        int originalTune = originalState.TunePct;
        int currentTune = originalTune;
        long expectedVfoHz = originalState.VfoHz;
        RxMode expectedMode = originalState.Mode;
        var samples = Channel.CreateBounded<ForwardPowerSample>(
            new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        void OnRawPower(ushort forwardAdc, ushort reflectedAdc)
        {
            StateDto snap = _radio.Snapshot();
            long txFrequencyHz = RadioFrequencyResolver.TxFrequencyHz(snap);
            RadioCalibration calibration = RadioCalibrations.For(
                _radio.ConnectedBoardKind,
                _radio.EffectiveOrionMkIIVariant);
            bool sixMeters = BandUtils.FreqToBand(txFrequencyHz) == "6m";
            var (forwardWatts, _, _) = TxMetersService.ComputeMeters(
                forwardAdc, reflectedAdc, calibration, sixMeters);

            string? trip = null;
            lock (_sync)
            {
                bool calibrationOwnsTun = _tx.TunOwner == MoxSource.Analyzer;
                string? invariantError = CalibrationInvariantError(
                    snap,
                    _expectedTxFrequencyHz,
                    _expectedMode,
                    invariant);
                if (_armedSafetyTargetWatts is not null &&
                    invariantError is not null &&
                    _externalStateChangeMessage is null)
                {
                    trip = invariantError;
                    _externalStateChangeMessage = trip;
                }
                if (_armedSafetyTargetWatts is not null &&
                    _tx.IsTunOn &&
                    !calibrationOwnsTun &&
                    _externalStateChangeMessage is null)
                {
                    _externalStateChangeMessage =
                        "PA calibration stopped because TUN ownership changed outside calibration.";
                }
                if (_armedSafetyTargetWatts is not null &&
                    calibrationOwnsTun &&
                    (_expectedTxFrequencyHz != txFrequencyHz || _expectedMode != snap.Mode) &&
                    _externalStateChangeMessage is null)
                {
                    trip = "PA calibration stopped because the transmit frequency or mode changed outside calibration.";
                    _externalStateChangeMessage = trip;
                }
                if (_armedSafetyTargetWatts is double target &&
                    calibrationOwnsTun &&
                    IsOverPower(
                        forwardWatts,
                        target,
                        safetyPercent,
                        ratedOutputWatts) &&
                    _safetyTripMessage is null)
                {
                    double limit = SafetyLimitWatts(
                        target, safetyPercent, ratedOutputWatts);
                    trip = $"Safety stop: measured {forwardWatts:0.0} W exceeds the {limit:0.0} W limit ({safetyPercent}% of the {target:0.0} W target, capped at {ratedOutputWatts:0.0} W rated output).";
                    _safetyTripMessage = trip;
                }
            }

            if (trip is not null)
                _tx.TrySetTun(false, MoxSource.UI, out _);
            samples.Writer.TryWrite(new ForwardPowerSample(
                (float)forwardWatts, DateTimeOffset.UtcNow));
        }

        _meters.RawPowerTelemetryUpdated += OnRawPower;
        try
        {
            if (!await _pipeline.WaitForPsDisarmAsync().ConfigureAwait(false))
                throw new InvalidOperationException(
                    "PureSignal did not disarm completely; PA calibration was not started.");
            EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
            Dictionary<string, CalibrationPoint> frequencies = ResolveBandFrequencies();
            int completed = 0;

            foreach (string band in BandUtils.HfBands)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                int firstTunePct = Math.Clamp(
                    (int)Math.Round(firstTargetWatts * 100d /
                        originalSettings.Global.PaMaxPowerWatts), 1, 100);
                if (!_radio.SetPaCalibrationTuneDriveIfCurrent(firstTunePct, currentTune))
                    throw new ExternalCalibrationStateChangedException(
                        "PA calibration stopped because TUN power changed outside calibration.");
                currentTune = _radio.Snapshot().TunePct;
                while (samples.Reader.TryRead(out _)) { }
                ArmSafetyTarget(firstTargetWatts, expectedVfoHz, expectedMode);
                Update("running", band, firstTargetWatts, null, completed,
                    $"Keying TUN for {band}; calibrating the shared band gain at {firstTargetWatts:0.0} W");

                if (!_tx.TrySetPaCalibrationTun(true, out string? keyError))
                {
                    if (_tx.TunOwner is not null && _tx.TunOwner != MoxSource.Analyzer)
                        throw new ExternalCalibrationStateChangedException(
                            "PA calibration stopped because another controller keyed TUN.");
                    throw new InvalidOperationException(keyError ?? "TUN was refused.");
                }

                try
                {
                    foreach (int targetWatts in TargetsWatts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
                        bool calibratingGain = targetWatts == firstTargetWatts;
                        if (!calibratingGain)
                        {
                            // Arm the higher target before raising TUN drive so a
                            // fresh raw meter sample is checked against the right
                            // limit throughout the live transition.
                            ArmSafetyTarget(targetWatts, expectedVfoHz, expectedMode);
                            int requestedTunePct = Math.Clamp(
                                (int)Math.Round(targetWatts * 100d /
                                    originalSettings.Global.PaMaxPowerWatts), 1, 100);
                            if (!_radio.SetPaCalibrationTuneDriveIfCurrent(requestedTunePct, currentTune))
                                throw new ExternalCalibrationStateChangedException(
                                    "PA calibration stopped because TUN power changed outside calibration.");
                            currentTune = _radio.Snapshot().TunePct;
                            while (samples.Reader.TryRead(out _)) { }
                            Update("running", band, targetWatts, null, completed,
                                $"Holding {band} shared gain; checking {targetWatts:0.0} W");
                        }

                        await ConvergeAsync(
                            band, targetWatts, completed,
                            expectedVfoHz, expectedMode,
                            invariant,
                            safetyPercent,
                            ratedOutputWatts,
                            samples.Reader,
                            calibratingGain,
                            calibratingGain
                                ? TimeSpan.FromMilliseconds(700)
                                : TimeSpan.FromMilliseconds(350),
                            cancellationToken).ConfigureAwait(false);

                        completed++;
                        Update("running", band, targetWatts, null, completed,
                            $"{band} {targetWatts:0.0} W complete");
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _tx.TrySetPaCalibrationTun(false, out _);
                    ArmSafetyTarget(null);
                }
            }

            ThrowIfCalibrationAborted();
            EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
            success = true;
        }
        catch (OperationCanceledException)
        {
            Update("cancelled", null, null, null, Status.CompletedSteps,
                "PA calibration stopped. Original settings were restored.");
        }
        catch (ExternalCalibrationStateChangedException ex)
        {
            Update("failed", Status.Band, Status.TargetWatts, Status.MeasuredWatts,
                Status.CompletedSteps,
                $"{ex.Message} Original PA settings were restored.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "pa.calibration.failed");
            Update("failed", Status.Band, Status.TargetWatts, Status.MeasuredWatts,
                Status.CompletedSteps,
                $"{ex.Message} Original settings were restored.");
        }
        finally
        {
            _tx.TrySetPaCalibrationTun(false, out _);
            ArmSafetyTarget(null);
            _meters.RawPowerTelemetryUpdated -= OnRawPower;

            try
            {
                Exception? cleanupFailure = null;
                try
                {
                    _pa.CompleteCalibrationOverlay(success);
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                    _log.LogError(ex, "pa.calibration.cleanup.failed");
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

                if (cleanupFailure is not null)
                {
                    success = false;
                    Update("failed", Status.Band, Status.TargetWatts, Status.MeasuredWatts,
                        Status.CompletedSteps,
                        $"PA calibration cleanup failed: {cleanupFailure.Message}");
                }
                else if (success)
                {
                    Update("completed", null, null, null,
                        BandUtils.HfBands.Count * TargetsWatts.Length,
                        "PA calibration applied successfully.");
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

    private async Task ConvergeAsync(
        string band,
        double targetWatts,
        int completed,
        long expectedVfoHz,
        RxMode expectedMode,
        RunInvariant invariant,
        int safetyPercent,
        double ratedOutputWatts,
        ChannelReader<ForwardPowerSample> samples,
        bool allowGainAdjustment,
        TimeSpan initialSettleDelay,
        CancellationToken cancellationToken)
    {
        DateTimeOffset settleUntilUtc = DateTimeOffset.UtcNow + initialSettleDelay;
        DateTimeOffset lastToleranceSampleUtc = DateTimeOffset.MinValue;
        int consecutiveInTolerance = 0;
        DateTimeOffset lastAdjustmentSampleUtc = DateTimeOffset.MinValue;
        var adjustmentSamples = new List<float>(AdjustmentSampleCount);

        for (int attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfCalibrationAborted();
            EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
            if (!_radio.IsConnected || !_tx.IsTunOn)
                throw new ExternalCalibrationStateChangedException(
                    "PA calibration stopped because the radio disconnected or TUN was released outside calibration.");

            using var sampleTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sampleTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            ForwardPowerSample sample;
            try
            {
                sample = await samples.ReadAsync(sampleTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Forward-power telemetry became stale.");
            }
            ThrowIfCalibrationAborted();

            float measured = sample.Watts;
            Update("running", band, targetWatts, measured, completed,
                $"Adjusting {band}: {measured:0.0} W / {targetWatts:0.0} W");

            if (IsOverPower(
                measured,
                targetWatts,
                safetyPercent,
                ratedOutputWatts))
            {
                _tx.TrySetTun(false, MoxSource.UI, out _);
                double limit = SafetyLimitWatts(
                    targetWatts, safetyPercent, ratedOutputWatts);
                throw new InvalidOperationException(
                    $"Safety stop: measured {measured:0.0} W exceeds the {limit:0.0} W limit ({safetyPercent}% of the {targetWatts:0.0} W target, capped at {ratedOutputWatts:0.0} W rated output).");
            }

            if (sample.SampledAtUtc < settleUntilUtc)
            {
                attempt--;
                continue;
            }

            if (measured > 0 && IsWithinTolerance(measured, targetWatts))
            {
                adjustmentSamples.Clear();
                lastAdjustmentSampleUtc = DateTimeOffset.MinValue;
                if (sample.SampledAtUtc - lastToleranceSampleUtc >=
                    TimeSpan.FromMilliseconds(75))
                {
                    lastToleranceSampleUtc = sample.SampledAtUtc;
                    if (++consecutiveInTolerance >= 3) return;
                }
                attempt--;
                continue;
            }
            consecutiveInTolerance = 0;
            lastToleranceSampleUtc = DateTimeOffset.MinValue;

            // A single low reading immediately after a key/re-key can be a
            // stale meter value. Base corrections on three fresh readings so
            // one transient cannot command a large drive change.
            if (sample.SampledAtUtc - lastAdjustmentSampleUtc >= AdjustmentSampleSpacing)
            {
                adjustmentSamples.Add(measured);
                lastAdjustmentSampleUtc = sample.SampledAtUtc;
            }
            if (adjustmentSamples.Count < AdjustmentSampleCount)
            {
                attempt--;
                continue;
            }

            double adjustmentWatts = adjustmentSamples
                .OrderBy(watts => watts)
                .ElementAt(adjustmentSamples.Count / 2);
            adjustmentSamples.Clear();
            lastAdjustmentSampleUtc = DateTimeOffset.MinValue;

            if (!allowGainAdjustment)
                throw new InvalidOperationException(
                    $"{band} has one shared PA gain setting and could not hold {targetWatts:0.0} W after the {TargetsWatts[0]:0.0} W calibration.");

            PaBandSettingsDto row = _pa.GetAll(
                    _radio.EffectiveBoardKind,
                    _radio.EffectiveOrionMkIIVariant)
                .Bands.First(b => b.Band == band);
            double requestedGain = adjustmentWatts < MinimumMeasurableWatts
                ? row.PaGainDb - MaxGainAdjustmentDb
                : ComputeNextGainDb(row.PaGainDb, adjustmentWatts, targetWatts);
            double nextGain = LimitGainAdjustment(row.PaGainDb, requestedGain);
            if (Math.Abs(nextGain - row.PaGainDb) < 0.05)
                throw new InvalidOperationException(
                    $"{band} could not converge at {targetWatts:0.0} W.");

            EnsureCalibrationState(expectedVfoHz, expectedMode, invariant);
            _pa.SetCalibrationGain(band, Math.Round(nextGain, 2));
            while (samples.TryRead(out _)) { }
            ThrowIfCalibrationAborted();
            settleUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
        }

        throw new TimeoutException(
            $"No fresh, converged forward-power reading for {band} at {targetWatts:0.0} W.");
    }

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
                BandUtils.HfBands.Count * TargetsWatts.Length, message);
    }

    private void ArmSafetyTarget(
        double? targetWatts,
        long? expectedTxFrequencyHz = null,
        RxMode? expectedMode = null)
    {
        lock (_sync)
        {
            _armedSafetyTargetWatts = targetWatts;
            _expectedTxFrequencyHz = expectedTxFrequencyHz;
            _expectedMode = expectedMode;
            _safetyTripMessage = null;
            _externalStateChangeMessage = null;
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
            0d, 70d);

    internal static double LimitGainAdjustment(double currentGainDb, double requestedGainDb) =>
        Math.Clamp(
            requestedGainDb,
            currentGainDb - MaxGainAdjustmentDb,
            currentGainDb + MaxGainAdjustmentDb);

    private static PaCalibrationStatus IdleStatus() =>
        new("idle", null, null, null, 0,
            BandUtils.HfBands.Count * TargetsWatts.Length, null);

    private sealed record CalibrationPoint(long FrequencyHz, RxMode Mode);
    private sealed record RunInvariant(
        HpsdrBoardKind Board,
        OrionMkIIVariant Variant,
        int DriveMaxPct);
    private readonly record struct ForwardPowerSample(
        float Watts, DateTimeOffset SampledAtUtc);
    private sealed class ExternalCalibrationStateChangedException(string message)
        : InvalidOperationException(message);
}
