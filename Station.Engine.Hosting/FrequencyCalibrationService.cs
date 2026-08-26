// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Globalization;
using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

// Per-radio frequency calibration (issue #325). One-shot procedure
// modelled on Thetis's `Console.WWVCalibration` (console.cs:9779-9854):
//
//   1. Snapshot operator state (VFO / LO / mode / filter / zoom).
//   2. Tune the LO ABOVE the reference by FrequencyCalibrationPlan.LoOffsetHz
//      (2 kHz at 10 MHz, 2.5 kHz at 15 MHz), so the reference station's
//      carrier lands below LO on the panadapter — clear of the DC bias spike
//      that HPSDR-class radios always emit at LO. (A naive "tune directly to
//      10 MHz" approach can't distinguish a perfectly-tuned radio from a radio
//      that just doesn't hear WWV, since both produce a peak at exactly LO.
//      This is why the dial reads 10.002 while the procedure runs.)
//   3. Set USB + narrow filter, and pick the DEEPEST zoom whose visible span
//      still contains the whole ±100 ppm search band — deeper zoom is finer
//      Hz/pixel, which is measurement precision.
//   4. Wait for the WDSP analyzer to settle, then capture several panadapter
//      frames instead of one, so a single noisy FFT cannot set the factor.
//   5. In each frame, search for the spectral peak *only* inside the expected
//      band, refine it to sub-pixel with parabolic interpolation, and convert
//      it to an absolute frequency using the centre frequency the pipeline
//      stamped on that very snapshot — never an assumption about where the LO
//      "should" be. The peak must repeat at the same frequency across the
//      captured frames, which admits faint carriers while rejecting noise.
//   6. Compose the measured residual with the factor already in force
//      (FrequencyCalibrationPlan.ComposeFactor) and persist it via
//      RadioService.SetFrequencyCorrectionFactor (write-through to
//      PreferredRadioStore, push to live P1/P2 client, re-push the LO so the
//      new factor takes effect immediately).
//   7. Restore operator state — VFO / mode / filter / zoom / CTUN centre — in
//      finally, so a failed cal leaves the operator exactly where they were.
//
// All four reference clients (piHPSDR, deskHPSDR, Thetis mainline,
// mi0bot HL2 fork) use the same multiplicative-correction-at-tune-write
// model; the per-board variation is in *where* the factor is applied
// (host-side, never on a clock register), which is what Zeus already
// does at `Protocol1Client.SetVfoAHz` + `Protocol2Client.SetVfoAHz`.
public sealed class FrequencyCalibrationService
{
    public const double DefaultReferenceFrequencyHz = 10_000_000.0;

    /// <summary>
    /// Reference stations the UI offers. WWV/WWVH radiate a continuous
    /// carrier on both; 15 MHz is the daytime alternative for operators who
    /// cannot hear 10 MHz, and it resolves 1.5× finer in ppm for the same
    /// Hz measurement error. Any frequency in
    /// <see cref="MinReferenceFrequencyHz"/>..<see cref="MaxReferenceFrequencyHz"/>
    /// is accepted by the API — these are just the ones with a button.
    /// </summary>
    public static readonly double[] SupportedReferenceFrequenciesHz =
        [10_000_000.0, 15_000_000.0];

    public const double MinReferenceFrequencyHz = 1_000_000.0;
    public const double MaxReferenceFrequencyHz = 30_000_000.0;

    private const int SettleMs = 2500;
    // Covers one full publication interval even at the supported 1 Hz display
    // refresh, so every measurement can wait for a distinct analyzer frame.
    private const int CaptureRetries = 35;
    private const int CaptureRetryDelayMs = 40;

    // Independent measurements folded into one median. The panadapter cache
    // publications are versioned, and the gap exceeds three times WDSP's
    // 100 ms display-averaging time constant so successive maxima are not
    // correlated views of the same noise burst.
    private const int MeasureFrames = 7;
    private const int MinAgreeingFrames = 4;

    // Rejected publications do not consume the seven frames needed for the
    // measurement. Bound them separately so even a late CTUN override can
    // restart a full cohort without allowing another client to fight the LO
    // forever.
    private const int MaxDiscardedFramePublications = 6;

    // How far apart per-frame measurements may sit and still be believed,
    // expressed in pixels of the configured zoom. A stable carrier lands on
    // the same bin every frame; ±2 pixels of spread is fading and windowing,
    // more than that is not one signal.
    private const double MaxFrameSpreadPixels = 2.0;

    // Once the temporal median nominates a persistent bin, measure each frame
    // only near that bin. This excludes stronger peaks that wander elsewhere,
    // while leaving a wider window than MaxFrameSpreadPixels so scattered
    // local noise still fails the independent 4-of-7 agreement check.
    private const int NominatedPeakNeighborhoodPixels = 6;

    // The persistent bin must also stand above the median of the temporally
    // combined search spectrum. Without this independent discriminator, pure
    // noise always nominates some bin and the bounded follow-up search can
    // turn that nomination into a false 4-of-7 cluster. Three dB preserves the
    // reported 3.7 dB weak-carrier case while rejecting flat temporal noise.
    private const float MinTemporalPeakAboveMedianDb = 3.0f;

    private readonly RadioService _radio;
    private readonly DspPipelineService _pipeline;
    private readonly ILogger<FrequencyCalibrationService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FrequencyCalibrationService(
        RadioService radio,
        DspPipelineService pipeline,
        ILogger<FrequencyCalibrationService> log)
    {
        _radio = radio;
        _pipeline = pipeline;
        _log = log;
    }

    /// <summary>
    /// Run the auto-calibration procedure. Concurrent invocations are
    /// rejected — only one cal at a time. The radio must be connected and
    /// unkeyed; if not, returns <see cref="CalibrationResult.NotConnected"/>
    /// or the keyed-flavoured <see cref="CalibrationOutcome.Busy"/>.
    /// </summary>
    public async Task<CalibrationResult> CalibrateAsync(
        double referenceFrequencyHz = DefaultReferenceFrequencyHz,
        CancellationToken ct = default)
    {
        if (!double.IsFinite(referenceFrequencyHz) ||
            referenceFrequencyHz < MinReferenceFrequencyHz ||
            referenceFrequencyHz > MaxReferenceFrequencyHz)
            throw new ArgumentOutOfRangeException(nameof(referenceFrequencyHz));

        // Single-shot lock. WaitAsync(0) — fail fast if a previous cal is
        // still running rather than queueing a second attempt.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return CalibrationResult.Busy;

        try
        {
            var startSnap = _radio.Snapshot();
            if (startSnap.Status != ConnectionStatus.Connected)
                return CalibrationResult.NotConnected;

            // Never retune out from under a live carrier. The procedure walks
            // the VFO 2 kHz and back and flips the mode to USB on the way; on
            // a keyed radio that is a transmission on the wrong frequency.
            if (_radio.IsMox)
                return CalibrationResult.Transmitting;

            // Snapshot operator state — restore in finally regardless of outcome.
            long origVfoHz = startSnap.VfoHz;
            long origRadioLoHz = startSnap.RadioLoHz;
            bool origCtun = startSnap.CtunEnabled;
            RxMode origMode = startSnap.Mode;
            int origFilterLo = startSnap.FilterLowHz;
            int origFilterHi = startSnap.FilterHighHz;
            int origZoom = startSnap.ZoomLevel;

            double loOffsetHz = FrequencyCalibrationPlan.LoOffsetHz(referenceFrequencyHz);
            var (searchMinHz, searchMaxHz) = FrequencyCalibrationPlan.SearchBandHz(referenceFrequencyHz);
            int calZoom = FrequencyCalibrationPlan.ZoomForReference(
                referenceFrequencyHz,
                startSnap.SampleRate,
                Math.Min(_radio.CurrentMaxDisplayZoomLevel, SyntheticDspEngine.MaxZoomLevel));

            _log.LogInformation(
                "freqcal.start ref={Ref}Hz loOffset={Off}Hz search={Min}..{Max}Hz zoom={Zoom} rate={Rate} origVfo={Vfo} origMode={Mode} origZoom={OrigZoom}",
                referenceFrequencyHz, loOffsetHz, searchMinHz, searchMaxHz, calZoom,
                startSnap.SampleRate, origVfoHz, origMode, origZoom);

            try
            {
                // Configure for calibration. LO sits above the reference so
                // the carrier lands below centre on the panadapter — well
                // clear of the DC spike at LO.
                _radio.SetMode(RxMode.USB);
                _radio.SetFilter(100, 2700);
                _radio.SetZoom(calZoom);
                long commandedLoHz = (long)Math.Round(referenceFrequencyHz + loOffsetHz);
                // Calibration drives the LO absolutely; bypass the frozen-NCO
                // auto-recenter heuristic so the hardware lands at exactly the
                // commanded freq.
                _radio.SetVfo(commandedLoHz, fromExternal: true);

                await Task.Delay(SettleMs, ct).ConfigureAwait(false);

                var frames = new List<CapturedFrame>(MeasureFrames);
                var pixels = new float[_pipeline.ConfiguredPanadapterWidth];
                float hzPerPixel = 0f;
                long lastSnapshotVersion = 0;
                int captureAttempts = 0;
                int discardedPublications = 0;

                while (frames.Count < MeasureFrames &&
                       discardedPublications < MaxDiscardedFramePublications)
                {
                    if (captureAttempts++ > 0)
                        await Task.Delay(
                            FrequencyCalibrationPlan.MeasurementFrameGapMs,
                            ct).ConfigureAwait(false);

                    // hzPerPixel and centreHz come out of the same call as the
                    // pixels, so all three describe THESE samples. centreHz is
                    // the LO the pipeline actually computed the frame at —
                    // using it instead of assuming "centre pixel == commanded
                    // LO" is what keeps the measurement honest across CTUN, a
                    // mid-retune frame, and the P2 wideband-detail path.
                    var capture = await TryCaptureWithRetryAsync(
                        pixels, lastSnapshotVersion, ct).ConfigureAwait(false);
                    if (capture is null)
                    {
                        discardedPublications++;
                        continue;
                    }
                    (float frameHzPerPixel, long frameCenterHz, long snapshotVersion) = capture.Value;
                    if (frameHzPerPixel <= 0f)
                    {
                        discardedPublications++;
                        continue;
                    }

                    lastSnapshotVersion = snapshotVersion;
                    hzPerPixel = frameHzPerPixel;

                    // CTUN filter-autopan runs in every connected frontend.
                    // Its queued /api/radio/lo request can arrive after the
                    // calibration retune and move the analyzer while leaving
                    // WWV visibly on screen. Never search a self-consistent
                    // but wrong frame cohort: reject the foreign centre,
                    // reassert calibration's LO, and wait for a newer stamp.
                    if (!FrequencyCalibrationPlan.IsExpectedCenter(
                            frameCenterHz,
                            commandedLoHz,
                            frameHzPerPixel))
                    {
                        double centerToleranceHz = Math.Max(1.0, frameHzPerPixel * 2.0);
                        _log.LogInformation(
                            "freqcal.center-overridden ref={Ref}Hz expected={Expected} actual={Actual} toleranceHz={Tolerance:F2}; reasserting calibration LO",
                            referenceFrequencyHz,
                            commandedLoHz,
                            frameCenterHz,
                            centerToleranceHz);
                        frames.Clear();
                        _radio.SetRadioLo(commandedLoHz);
                        discardedPublications++;
                        continue;
                    }

                    double centerIdx = pixels.Length / 2.0;
                    int searchMinIdx = (int)Math.Floor(centerIdx + searchMinHz / frameHzPerPixel);
                    int searchMaxIdx = (int)Math.Ceiling(centerIdx + searchMaxHz / frameHzPerPixel);
                    if (searchMinIdx < 0 || searchMaxIdx > pixels.Length - 1)
                    {
                        // Zoom too deep for this band (only reachable if the
                        // sample rate changed under us). Clamping still yields
                        // a valid measurement over the visible part, but say so.
                        _log.LogWarning(
                            "freqcal.band-clipped min={Min} max={Max} width={W} hzPerPx={Hpp:F2}",
                            searchMinIdx, searchMaxIdx, pixels.Length, frameHzPerPixel);
                        searchMinIdx = Math.Max(0, searchMinIdx);
                        searchMaxIdx = Math.Min(pixels.Length - 1, searchMaxIdx);
                    }
                    if (searchMinIdx > searchMaxIdx)
                    {
                        discardedPublications++;
                        continue;
                    }

                    var capturedFrame = new CapturedFrame(
                        pixels.ToArray(),
                        frameHzPerPixel,
                        frameCenterHz,
                        searchMinIdx,
                        searchMaxIdx);

                    if (frames.Count > 0 && !HasSameGeometry(frames[0], capturedFrame))
                    {
                        _log.LogInformation(
                            "freqcal.geometry-transition ref={Ref}Hz oldCenter={OldCenter} newCenter={NewCenter} oldHpp={OldHpp:F4} newHpp={NewHpp:F4}; restarting frame cohort",
                            referenceFrequencyHz,
                            frames[0].CenterHz,
                            capturedFrame.CenterHz,
                            frames[0].HzPerPixel,
                            capturedFrame.HzPerPixel);
                        frames.Clear();
                        discardedPublications++;
                    }

                    frames.Add(capturedFrame);
                }

                if (frames.Count < MeasureFrames)
                {
                    _log.LogWarning(
                        "freqcal.incomplete-frame-cohort ref={Ref}Hz captured={Captured}/{Required} discarded={Discarded}",
                        referenceFrequencyHz,
                        frames.Count,
                        MeasureFrames,
                        discardedPublications);
                    return CalibrationResult.CaptureFailed;
                }

                // Nominate a persistent bin independently for every measured
                // frame: the temporal median is built from the OTHER frames,
                // then its narrow candidate is located in the excluded frame.
                // A frame can never create the hypothesis used to measure it.
                CapturedFrame firstFrame = frames[0];
                bool geometryStable = frames.All(frame =>
                    HasSameGeometry(firstFrame, frame));
                if (!geometryStable)
                {
                    _log.LogWarning(
                        "freqcal.geometry-changed ref={Ref}Hz frames={Frames}",
                        referenceFrequencyHz,
                        string.Join(';', frames.Select(frame =>
                            $"center={frame.CenterHz},hpp={frame.HzPerPixel.ToString("F4", CultureInfo.InvariantCulture)},range={frame.SearchMinIndex}..{frame.SearchMaxIndex}")));
                    return CalibrationResult.CaptureFailed;
                }

                var candidates = new List<FrameMeasurement>(frames.Count);
                var nominations = new List<FrameNomination>(frames.Count);
                for (int excludedFrame = 0; excludedFrame < frames.Count; excludedFrame++)
                {
                    CapturedFrame frame = frames[excludedFrame];
                    float[][] nominationSpectra = frames
                        .Where((_, index) => index != excludedFrame)
                        .Select(other => other.Pixels)
                        .ToArray();
                    var nomination = FrequencyCalibrationPlan.TemporalMedianPeak(
                        nominationSpectra,
                        firstFrame.SearchMinIndex,
                        firstFrame.SearchMaxIndex);
                    float globalProminenceDb = nomination.PeakDb - nomination.MedianDb;
                    float shoulderProminenceDb = nomination.PeakDb - nomination.ShoulderMedianDb;
                    nominations.Add(new FrameNomination(
                        excludedFrame,
                        nomination.Index,
                        nomination.PeakDb,
                        nomination.MedianDb,
                        globalProminenceDb,
                        shoulderProminenceDb));
                    if (nomination.Index < 0 ||
                        globalProminenceDb < MinTemporalPeakAboveMedianDb ||
                        shoulderProminenceDb < MinTemporalPeakAboveMedianDb)
                    {
                        continue;
                    }

                    int localMin = Math.Max(
                        frame.SearchMinIndex,
                        nomination.Index - NominatedPeakNeighborhoodPixels);
                    int localMax = Math.Min(
                        frame.SearchMaxIndex,
                        nomination.Index + NominatedPeakNeighborhoodPixels);
                    int peakIndex = FrequencyCalibrationPlan.PeakIndexInRange(
                        frame.Pixels,
                        localMin,
                        localMax);
                    if (peakIndex < 0) continue;

                    double refinedIdx = FrequencyCalibrationPlan.InterpolatePeakIndex(
                        frame.Pixels,
                        peakIndex);
                    double measuredHz = FrequencyCalibrationPlan.PixelToHz(
                        refinedIdx,
                        frame.CenterHz,
                        frame.HzPerPixel,
                        frame.Pixels.Length);
                    candidates.Add(new FrameMeasurement(
                        measuredHz,
                        frame.Pixels[peakIndex],
                        excludedFrame));
                }

                double maxSpreadHz = MaxFrameSpreadPixels * hzPerPixel;
                // Frequency agreement across independent frames is the signal
                // discriminator. Unlike the former per-frame 6 dB cut, this
                // admits a faint carrier without accepting scattered noise.
                var pool = candidates.ToArray();
                int[] stableIndices = FrequencyCalibrationPlan.StableClusterIndices(
                    pool.Select(candidate => candidate.MeasuredHz).ToArray(),
                    maxSpreadHz);
                if (stableIndices.Length < MinAgreeingFrames)
                {
                    float diagnosticPeakDb = FrequencyCalibrationPlan.Median(
                        nominations.Select(nomination => nomination.PeakDb).ToArray());
                    float diagnosticMedianDb = FrequencyCalibrationPlan.Median(
                        nominations.Select(nomination => nomination.MedianDb).ToArray());
                    _log.LogWarning(
                        "freqcal.no-stable-carrier ref={Ref}Hz temporalPeakDb={PeakDb:F1} temporalMedianDb={MedianDb:F1} agreeing={Agreeing}/{Total} required={Required} maxSpreadHz={MaxSpread:F2} nominations={Nominations} framePeaksHz={FramePeaks}",
                        referenceFrequencyHz,
                        diagnosticPeakDb,
                        diagnosticMedianDb,
                        stableIndices.Length,
                        MeasureFrames,
                        MinAgreeingFrames,
                        maxSpreadHz,
                        string.Join(';', nominations.Select(nomination =>
                            $"frame={nomination.ExcludedFrame},bin={nomination.Index},global={nomination.GlobalProminenceDb.ToString("F1", CultureInfo.InvariantCulture)},shoulder={nomination.ShoulderProminenceDb.ToString("F1", CultureInfo.InvariantCulture)}")),
                        string.Join(',', pool.Select(candidate =>
                            $"frame={candidate.FrameIndex}:{candidate.MeasuredHz.ToString("F2", CultureInfo.InvariantCulture)}")));
                    return CalibrationResult.NoSignal(
                        diagnosticPeakDb,
                        diagnosticMedianDb,
                        stableIndices.Length,
                        MeasureFrames,
                        MinAgreeingFrames);
                }

                var stable = stableIndices.Select(index => pool[index]).ToArray();
                var measurements = stable.Select(candidate => candidate.MeasuredHz).ToArray();
                var peakDbs = stable.Select(candidate => (double)candidate.PeakDb).ToArray();
                double spreadHz = measurements.Max() - measurements.Min();

                double measuredCarrierHz = FrequencyCalibrationPlan.Median(measurements);
                double medianPeakDb = FrequencyCalibrationPlan.Median(peakDbs);

                // Deviation of the reference carrier from where the dial says
                // it is. Positive = the radio's clock runs slow (signals show
                // up high); negative = fast.
                double deviationHz = measuredCarrierHz - referenceFrequencyHz;
                double maxDeviationHz = FrequencyCalibrationPlan.MaxDeviationHz(referenceFrequencyHz);
                if (Math.Abs(deviationHz) > maxDeviationHz)
                    return CalibrationResult.OffsetOutOfRange(
                        deviationHz, (float)medianPeakDb, referenceFrequencyHz, maxDeviationHz);

                // Compose with the factor already in force — the measurement
                // on a calibrated radio is the RESIDUAL error, not the total.
                double currentFactor = _radio.GetFrequencyCorrectionFactor();
                double factor = FrequencyCalibrationPlan.ComposeFactor(
                    currentFactor, deviationHz, commandedLoHz);
                double applied = _radio.SetFrequencyCorrectionFactor(factor);

                _log.LogInformation(
                    "freqcal.success ref={Ref}Hz measured={Meas:F2}Hz deviation={Dev:F2}Hz spread={Spread:F2}Hz frames={N}/{Total} peakDb={Db:F1} hzPerPx={Hpp:F2} priorFactor={Prior:F9} factor={Factor:F9} applied={Applied:F9}",
                    referenceFrequencyHz, measuredCarrierHz, deviationHz, spreadHz,
                    measurements.Length, MeasureFrames, medianPeakDb, hzPerPixel,
                    currentFactor, factor, applied);

                return CalibrationResult.Success(
                    deviationHz, (float)medianPeakDb, applied, referenceFrequencyHz);
            }
            finally
            {
                // Restore. VFO first, so the per-band stores that SetMode /
                // SetFilter / SetZoom write through land on the operator's own
                // band instead of the reference station's. Then mode (resets
                // the family filter cache), filter (overrides the family
                // default), zoom, and finally the CTUN centre — the cal forced
                // a recenter, and without this the operator's frozen
                // panadapter centre would come back glued to the dial.
                try { _radio.SetVfo(origVfoHz, fromExternal: true); } catch (Exception ex) { _log.LogWarning(ex, "freqcal.restore vfo"); }
                try { _radio.SetMode(origMode); } catch (Exception ex) { _log.LogWarning(ex, "freqcal.restore mode"); }
                try { _radio.SetFilter(origFilterLo, origFilterHi); } catch (Exception ex) { _log.LogWarning(ex, "freqcal.restore filter"); }
                try { _radio.SetZoom(origZoom); } catch (Exception ex) { _log.LogWarning(ex, "freqcal.restore zoom"); }
                if (origCtun && origRadioLoHz > 0 && origRadioLoHz != _radio.Snapshot().RadioLoHz)
                {
                    try { _radio.SetRadioLo(origRadioLoHz); } catch (Exception ex) { _log.LogWarning(ex, "freqcal.restore lo"); }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reset the per-radio correction factor to 1.0 (no correction). Same
    /// write-through-then-push path as SetFrequencyCorrectionFactor.
    /// </summary>
    public void Reset() => _radio.SetFrequencyCorrectionFactor(1.0);

    /// <summary>
    /// Fill <paramref name="dest"/> with one panadapter frame and return the
    /// Hz/pixel and centre frequency that belong to it, or null if no fresh
    /// frame arrived. TryCapturePanadapterSnapshot returns false when the
    /// cache holds nothing recent (the WDSP worker is still producing the
    /// first frame after SetVfo's re-tune, or the analyzer reconfig from
    /// SetZoom is still settling), so retry briefly.
    /// </summary>
    private async Task<(float HzPerPixel, long CenterHz, long SnapshotVersion)?> TryCaptureWithRetryAsync(
        float[] dest, long afterSnapshotVersion, CancellationToken ct)
    {
        for (int attempt = 0; attempt < CaptureRetries; attempt++)
        {
            if (_pipeline.TryCapturePanadapterSnapshot(
                    dest,
                    out float hzPerPixel,
                    out long centerHz,
                    out long snapshotVersion) &&
                snapshotVersion > afterSnapshotVersion)
            {
                return (hzPerPixel, centerHz, snapshotVersion);
            }
            await Task.Delay(CaptureRetryDelayMs, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static bool HasSameGeometry(CapturedFrame left, CapturedFrame right) =>
        left.CenterHz == right.CenterHz &&
        left.HzPerPixel == right.HzPerPixel &&
        left.SearchMinIndex == right.SearchMinIndex &&
        left.SearchMaxIndex == right.SearchMaxIndex;

    private sealed record FrameMeasurement(
        double MeasuredHz,
        float PeakDb,
        int FrameIndex);

    private sealed record FrameNomination(
        int ExcludedFrame,
        int Index,
        float PeakDb,
        float MedianDb,
        float GlobalProminenceDb,
        float ShoulderProminenceDb);

    private sealed record CapturedFrame(
        float[] Pixels,
        float HzPerPixel,
        long CenterHz,
        int SearchMinIndex,
        int SearchMaxIndex);
}

/// <summary>
/// Result of a calibration run. Encoded as a discriminated record so the
/// REST surface can serialise both successes and the various failure
/// modes uniformly.
/// </summary>
public sealed record CalibrationResult(
    CalibrationOutcome Outcome,
    double? OffsetHz,
    float? PeakDb,
    double? AppliedFactor,
    string Message)
{
    public static readonly CalibrationResult Busy = new(
        CalibrationOutcome.Busy, null, null, null,
        "A calibration is already in progress.");

    public static readonly CalibrationResult Transmitting = new(
        CalibrationOutcome.Busy, null, null, null,
        "The radio is transmitting. Unkey, then run calibration.");

    public static readonly CalibrationResult NotConnected = new(
        CalibrationOutcome.NotConnected, null, null, null,
        "No radio is connected. Connect first, then run calibration.");

    public static readonly CalibrationResult CaptureFailed = new(
        CalibrationOutcome.CaptureFailed, null, null, null,
        "Panadapter snapshot was not available — engine offline or pipeline stalled.");

    public static CalibrationResult NoSignal(
        float peakDb,
        float medianDb = float.NaN,
        int goodFrames = 0,
        int totalFrames = 0,
        int requiredFrames = 0)
    {
        string frames = totalFrames > 0
            ? $" ({goodFrames} of {totalFrames} frames agreed; at least {requiredFrames} required)"
            : string.Empty;
        return new(
            CalibrationOutcome.NoSignal, null,
            float.IsNegativeInfinity(peakDb) ? null : peakDb, null,
            float.IsNaN(medianDb)
                ? $"No stable signal detected at the reference frequency{frames}."
                : $"No stable signal detected at the reference frequency (peak {peakDb:F1} dB, median {medianDb:F1} dB){frames}.");
    }

    public static CalibrationResult Unstable(double spreadHz, double allowedHz, float peakDb) => new(
        CalibrationOutcome.NoSignal, null, peakDb, null,
        $"Measurement did not settle — the peak moved {spreadHz:F1} Hz across frames (limit {allowedHz:F1} Hz). Fading, an interferer, or drifting local noise. Try again, or use 15 MHz.");

    public static CalibrationResult OffsetOutOfRange(
        double offsetHz, float peakDb, double referenceHz, double maxDeviationHz) => new(
        CalibrationOutcome.OffsetOutOfRange, offsetHz, peakDb, null,
        $"Measured offset {offsetHz:F1} Hz exceeds ±{maxDeviationHz:F0} Hz at {referenceHz / 1e6:F3} MHz — likely tuned to the wrong reference or a strong interferer.");

    public static CalibrationResult Success(
        double offsetHz, float peakDb, double appliedFactor, double referenceHz) => new(
        CalibrationOutcome.Success, offsetHz, peakDb, appliedFactor,
        $"Calibration applied from {referenceHz / 1e6:F3} MHz: {offsetHz:+0.0;-0.0;0.0} Hz measured ({(appliedFactor - 1.0) * 1e6:+0.000;-0.000;0.000} ppm total correction).");
}

public enum CalibrationOutcome
{
    Success,
    Busy,
    NotConnected,
    CaptureFailed,
    NoSignal,
    OffsetOutOfRange,
}
