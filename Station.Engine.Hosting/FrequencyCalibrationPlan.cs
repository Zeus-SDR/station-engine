// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Pure geometry + arithmetic behind the WWV auto-calibration procedure
/// (issue #325). Split out of <see cref="FrequencyCalibrationService"/> so the
/// parts that decide *where to look* and *what the measurement means* are unit
/// testable without a radio, a DSP pipeline, or a 3-second settle.
///
/// Everything here scales with the reference frequency, because Zeus now
/// calibrates against WWV 15 MHz as well as 10 MHz and the two are not
/// interchangeable: a ±100 ppm crystal error is ±1000 Hz at 10 MHz but
/// ±1500 Hz at 15 MHz, so a search window hard-coded for 10 MHz would miss a
/// drifted radio on 15 — or worse, lock onto whatever noise sat inside it.
/// </summary>
internal static class FrequencyCalibrationPlan
{
    /// <summary>
    /// Widest crystal error the procedure is willing to correct, in ppm.
    /// Matches the clamp in <see cref="RadioService.SetFrequencyCorrectionFactor"/>
    /// (factor ∈ [0.9999, 1.0001]) and piHPSDR's range. Any real HPSDR-class
    /// board is one to two orders of magnitude better than this.
    /// </summary>
    public const double MaxCorrectablePpm = 100.0;

    /// <summary>
    /// Keep-out radius around the LO, in Hz. HPSDR-class radios always emit a
    /// DC bias spike at the LO; the reference carrier must stay clear of it or
    /// peak detection cannot tell a perfectly-tuned radio from one that simply
    /// cannot hear the reference station.
    /// </summary>
    public const double DcGuardHz = 1000.0;

    /// <summary>Fraction of extra visible span requested beyond the search
    /// band, so the far edge of the band never lands on the outermost pixel.</summary>
    private const double SpanMargin = 1.10;

    /// <summary>Half-width of the crystal-error window at this reference, in Hz.</summary>
    public static double MaxDeviationHz(double referenceHz) =>
        referenceHz * MaxCorrectablePpm * 1e-6;

    /// <summary>
    /// How far ABOVE the reference the LO is parked, in Hz. Far enough that a
    /// radio drifted to the very edge of <see cref="MaxCorrectablePpm"/> still
    /// puts its carrier outside <see cref="DcGuardHz"/>. Works out to the
    /// historical 2 kHz at 10 MHz, 2.5 kHz at 15 MHz.
    /// </summary>
    public static double LoOffsetHz(double referenceHz) =>
        MaxDeviationHz(referenceHz) + DcGuardHz;

    /// <summary>
    /// Search band relative to the LO, in Hz — always negative, because the
    /// LO is parked above the reference. The near edge is exactly the DC
    /// guard; the far edge is the LO offset plus a full deviation window.
    /// At 10 MHz this is −3000..−1000 Hz, matching the original hard-coded
    /// constants byte for byte.
    /// </summary>
    public static (double MinHz, double MaxHz) SearchBandHz(double referenceHz) =>
        (-(LoOffsetHz(referenceHz) + MaxDeviationHz(referenceHz)), -DcGuardHz);

    /// <summary>Visible half-span the panadapter must cover for the whole
    /// search band to be on screen, with margin.</summary>
    public static double RequiredHalfSpanHz(double referenceHz) =>
        (LoOffsetHz(referenceHz) + MaxDeviationHz(referenceHz)) * SpanMargin;

    /// <summary>
    /// Pick the deepest zoom level whose visible span still contains the whole
    /// search band. The DDC panadapter spans <c>sampleRate / zoom</c>
    /// (DspPipelineService.VisibleDdcHzPerPixel), so deeper zoom = finer
    /// Hz/pixel = a more precise measurement, right up to the point where the
    /// band would fall off screen.
    ///
    /// 10 MHz at 48 kHz → 7 (±3.4 kHz visible, 3.3 Hz/pixel).
    /// 10 MHz at 192 kHz → 29 (±3.3 kHz, 3.2 Hz/pixel — the old fixed zoom 8
    /// left P2 at a coarse 11.7 Hz/pixel, i.e. 1.2 ppm of quantisation).
    /// 15 MHz at 48 kHz → 5 (±4.8 kHz, 4.7 Hz/pixel).
    /// </summary>
    public static int ZoomForReference(double referenceHz, int sampleRateHz, int maxZoomLevel)
    {
        int ceiling = Math.Max(1, maxZoomLevel);
        if (sampleRateHz <= 0) return Math.Min(1, ceiling);
        double half = RequiredHalfSpanHz(referenceHz);
        if (half <= 0) return ceiling;
        int zoom = (int)Math.Floor(sampleRateHz / (2.0 * half));
        return Math.Clamp(zoom, 1, ceiling);
    }

    /// <summary>
    /// Sub-pixel peak position by parabolic interpolation over the three
    /// log-magnitude bins straddling the peak — the standard refinement for
    /// windowed FFT peaks, and the difference between quantising the answer to
    /// one pixel (0.33 ppm at 10 MHz / 48 kHz) and resolving a small fraction
    /// of one. Returns the peak index as a double; falls back to the integer
    /// index when the three points are not a downward parabola (flat top,
    /// clipped edge, NaN).
    /// </summary>
    public static double InterpolatePeakIndex(ReadOnlySpan<float> pixels, int peakIndex)
    {
        if (peakIndex <= 0 || peakIndex >= pixels.Length - 1) return peakIndex;
        double y0 = pixels[peakIndex - 1];
        double y1 = pixels[peakIndex];
        double y2 = pixels[peakIndex + 1];
        if (!double.IsFinite(y0) || !double.IsFinite(y1) || !double.IsFinite(y2))
            return peakIndex;
        double denom = y0 - 2.0 * y1 + y2;
        if (denom >= -1e-9) return peakIndex;      // flat or upward — no usable vertex
        double delta = 0.5 * (y0 - y2) / denom;
        if (!double.IsFinite(delta)) return peakIndex;
        return peakIndex + Math.Clamp(delta, -0.5, 0.5);
    }

    /// <summary>
    /// Absolute frequency a pixel represents. Mirrors the frontend's mapping
    /// (<c>centerHz + (x/width − 0.5) × span</c>) exactly, so a peak the
    /// operator can see at a given place on the panadapter resolves to the
    /// same Hz here.
    /// </summary>
    public static double PixelToHz(double pixelIndex, long centerHz, double hzPerPixel, int width) =>
        centerHz + (pixelIndex - width / 2.0) * hzPerPixel;

    /// <summary>
    /// Fold a fresh measurement into the correction factor already in force.
    ///
    /// <para>The radio's NCO is programmed with <c>round(displayHz × factor)</c>
    /// (Protocol1Client.SetVfoAHz), and a clock error <c>d</c> makes the actual
    /// LO <c>displayHz × factor × (1 + d)</c>. So with <c>K = factor × (1 + d)</c>
    /// the reference carrier is displayed at <c>reference − commandedLo × (K − 1)</c>,
    /// which gives <c>K = 1 − deviation / commandedLo</c> for
    /// <c>deviation = measured − reference</c>. The factor that cancels the
    /// clock error outright is <c>1/(1 + d) = factor / K</c>.</para>
    ///
    /// <para>Carrying the existing factor through is the whole point: the
    /// measurement on an already-calibrated radio is the RESIDUAL error, not
    /// the total one. Replacing the factor with <c>1 + deviation/reference</c>
    /// — what this code used to do — threw the previous correction away on
    /// every re-calibration, so a second run on a good radio un-calibrated it
    /// and the third put it back. Composition makes the procedure idempotent:
    /// re-running it on a calibrated radio measures ≈0 Hz and leaves the
    /// factor where it was.</para>
    /// </summary>
    public static double ComposeFactor(double currentFactor, double deviationHz, double commandedLoHz)
    {
        if (commandedLoHz <= 0) return currentFactor;
        double k = 1.0 - deviationHz / commandedLoHz;
        if (k <= 0 || !double.IsFinite(k)) return currentFactor;
        return currentFactor / k;
    }

    /// <summary>Median of a sequence, by value. Sorts a copy — the caller
    /// measures a handful of frames, so allocation is irrelevant.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var buf = values.ToArray();
        Array.Sort(buf);
        int mid = buf.Length / 2;
        return (buf.Length % 2 == 0) ? (buf[mid - 1] + buf[mid]) / 2.0 : buf[mid];
    }

    /// <summary>Median of a span of dB values (float spectrum flavour).</summary>
    public static float Median(ReadOnlySpan<float> values)
    {
        if (values.Length == 0) return float.NaN;
        var buf = values.ToArray();
        Array.Sort(buf);
        int mid = buf.Length / 2;
        return (buf.Length % 2 == 0) ? (buf[mid - 1] + buf[mid]) / 2f : buf[mid];
    }
}
