// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Dsp;
using Zeus.Protocol2;

namespace Zeus.Server;

internal sealed class WidebandSpectrumAnalyzer
{
    public const int DisplayWidth = 4096;
    public const double DisplaySpanHz = 60_000_000.0;
    public const long DisplayCenterHz = 30_000_000;
    public const float HzPerPixel = (float)(DisplaySpanHz / DisplayWidth);
    public const int MaxZoomLevel = 256;
    // One native radix-2 transform for the 32,736-sample Saturn capture. The
    // remaining 32 slots are padding for the FFT algorithm, not a 64x
    // interpolated spectrum presented as additional RF resolution.
    public const int AnalysisFftSize = 32_768;

    private const int FftSize = AnalysisFftSize;
    private const int MinSegmentLength = 8_192;
    // Segmentation is allowed while the coarser FFT bin stays at or below
    // this fraction of a display pixel, so reprojection always keeps at
    // least ~1.7 real bins per pixel. Overview zooms (where one 3.75 kHz
    // bin is already far finer than one 14.6 kHz pixel) qualify; deep
    // zooms keep the full-length transform and its native resolution.
    private const double MaxSegmentBinToPixelRatio = 0.6;
    private const int MaxSegments = 8;
    private const double MinAmplitude = 1e-12;
    private const double MinPower = MinAmplitude * MinAmplitude;
    // Temporal smoothing is expressed as time constants and converted to a
    // per-frame EMA alpha from the actual snapshot cadence, so the display
    // feels identical at the Saturn 50 ms rate and the classic 100 ms rate.
    private const double PanTauOverviewMs = 210.0;
    private const double PanTauMaxZoomMs = 100.0;
    // Attack is deliberately ~2.5x faster than the slowest decay and only
    // engages on rises well above the averaged level, so signal onsets
    // render promptly while ordinary noise wiggle never trips the fast
    // path and the floor keeps the full averaging depth.
    private const double PanAttackTauMs = 80.0;
    private const double PanAttackTriggerRatio = 1.6;
    private const double WfTauMs = 70.0;
    private const double MinFrameIntervalMs = 10.0;
    private const double MaxFrameIntervalMs = 1_000.0;
    private const double MinSpatialSideWeight = 0.06;
    private const double MaxSpatialSideWeight = 0.18;

    private readonly double[] _real = new double[FftSize];
    private readonly double[] _binPower = new double[(FftSize / 2) + 1];
    private readonly double[] _avgPanPower = new double[DisplayWidth];
    private readonly double[] _avgWfPower = new double[DisplayWidth];
    private readonly Dictionary<int, FftPlan> _fftPlans = new();
    private readonly Dictionary<int, WindowPlan> _windowPlans = new();
    private int _activeBinCount;
    private int _segmentLength = FftSize;
    private int[] _segmentOffsets = [0];
    private int _sampleRateHz;
    private long _viewportCenterHz;
    private float _viewportHzPerPixel;
    private int _windowSampleCount;
    private bool _smoothedValid;

    public WidebandSpectrumAnalyzer()
    {
        // The full-length plan is always needed (deep zooms and classic
        // captures run unsegmented), so build it up front.
        _ = PlanFor(FftSize);
    }

    public WidebandSpectrumViewport Analyze(
        ReadOnlySpan<short> samples,
        int sampleRateHz,
        Span<float> panDb,
        Span<float> wfDb,
        int zoomLevel,
        long targetCenterHz,
        double frameIntervalMs = 100.0) =>
        Analyze(samples, sampleRateHz, panDb, wfDb, zoomLevel, targetCenterHz,
            frameIntervalMs, signalDetector: null, markers: default, out _);

    /// <summary>
    /// Full analysis pass plus optional signal detection. When
    /// <paramref name="signalDetector"/> is provided, markers for the
    /// detected signals are written to <paramref name="markers"/> (strongest
    /// SNR first) and <paramref name="markerCount"/> reports how many were
    /// written. Detection runs on the same DC-suppressed, Welch-averaged
    /// spectrum the display is drawn from, so markers always agree with the
    /// rendered trace.
    /// </summary>
    public WidebandSpectrumViewport Analyze(
        ReadOnlySpan<short> samples,
        int sampleRateHz,
        Span<float> panDb,
        Span<float> wfDb,
        int zoomLevel,
        long targetCenterHz,
        double frameIntervalMs,
        WidebandSignalDetector? signalDetector,
        Span<WidebandSignalMarker> markers,
        out int markerCount)
    {
        if (panDb.Length < DisplayWidth || wfDb.Length < DisplayWidth)
            throw new ArgumentException("Output spans must be at least DisplayWidth samples long.");
        if (samples.Length < 2 || samples.Length > Protocol2Client.WidebandMaxFrameSamples)
            throw new ArgumentException(
                $"Input must contain between 2 and {Protocol2Client.WidebandMaxFrameSamples} samples.",
                nameof(samples));

        if (sampleRateHz <= 0) sampleRateHz = Protocol2Client.WidebandAdcSampleRateHz;
        frameIntervalMs = Math.Clamp(frameIntervalMs, MinFrameIntervalMs, MaxFrameIntervalMs);
        var viewport = ResolveViewport(zoomLevel, targetCenterHz);
        if (sampleRateHz != _sampleRateHz ||
            samples.Length != _windowSampleCount ||
            viewport.CenterHz != _viewportCenterHz ||
            Math.Abs(viewport.HzPerPixel - _viewportHzPerPixel) > Math.Max(1e-6f, viewport.HzPerPixel * 1e-6f))
        {
            _sampleRateHz = sampleRateHz;
            _windowSampleCount = samples.Length;
            _viewportCenterHz = viewport.CenterHz;
            _viewportHzPerPixel = viewport.HzPerPixel;
            _smoothedValid = false;
            PlanSegments(samples.Length, sampleRateHz, viewport.HzPerPixel);
        }

        // Welch step: average the per-segment periodograms in linear power.
        // Overlapped Blackman-Harris segments are correlated, but even the
        // conservative ~1.8x variance reduction at the 3-segment overview is
        // noise the single-periodogram estimator used to show as grass.
        int bins = (_segmentLength / 2) + 1;
        Array.Clear(_binPower, 0, bins);
        foreach (int offset in _segmentOffsets)
        {
            // The slice is only ever short for the unsegmented full-frame
            // case (e.g. 32,736 Saturn samples padded to the 32,768 FFT);
            // AccumulateSegmentPower zero-pads up to the transform size.
            AccumulateSegmentPower(
                samples.Slice(offset, Math.Min(_segmentLength, samples.Length - offset)),
                bins);
        }
        if (_segmentOffsets.Length > 1)
        {
            double inv = 1.0 / _segmentOffsets.Length;
            for (int bin = 0; bin < bins; bin++)
                _binPower[bin] *= inv;
        }
        _activeBinCount = bins;

        // DC / LO-feedthrough suppression: bin 0 collects ADC offset and
        // residual DC, and the window mainlobe spreads it across the next
        // few bins, which otherwise paints a false hot line into the
        // leftmost display pixels. Replace the DC skirt with the mean of
        // bins past the mainlobe, matching how high-end analyzers blank the
        // DC region. Real RF under ~40 kHz on HF is the ADC's own offset,
        // never signal.
        if (bins > 19)
        {
            double dcFloor = 0.0;
            for (int bin = 6; bin <= 18; bin++)
                dcFloor += _binPower[bin];
            dcFloor /= 13.0;
            for (int bin = 0; bin <= 4; bin++)
                _binPower[bin] = dcFloor;
        }

        double binHz = sampleRateHz / (double)_segmentLength;

        // Signal detection rides the same spectrum the display renders:
        // DC-suppressed, Welch-averaged, pre-smoothing bin power.
        if (signalDetector != null)
        {
            markerCount = signalDetector.Detect(
                _binPower.AsSpan(0, bins), binHz, frameIntervalMs, markers);
        }
        else
        {
            markerCount = 0;
        }

        double nyquistHz = sampleRateHz / 2.0;
        double startHz = viewport.CenterHz - (viewport.SpanHz / 2.0);
        double panDecayAlpha = EmaAlpha(frameIntervalMs, PanTauMsForZoom(viewport.ZoomLevel));
        double panAttackAlpha = EmaAlpha(frameIntervalMs, PanAttackTauMs);
        double wfAlpha = EmaAlpha(frameIntervalMs, WfTauMs);
        for (int pixel = 0; pixel < DisplayWidth; pixel++)
        {
            double loHz = startHz + pixel * viewport.HzPerPixel;
            double hiHz = loHz + viewport.HzPerPixel;
            double power = 0.0;
            if (hiHz > 0.0 && loHz < nyquistHz)
            {
                double startBin = Math.Max(0.0, loHz / binHz);
                double endBin = Math.Min((_segmentLength / 2), hiHz / binHz);
                power = IntegratePower(startBin, endBin);
            }

            // Average in linear power, convert to dB once at display time.
            // Averaging in dB (the old path) biases the noise floor ~2.5 dB
            // low for Rayleigh-distributed noise and does not reduce its
            // variance; power averaging does, which is what gives the trace
            // the same tight floor as the WDSP single-receiver display.
            if (!_smoothedValid)
            {
                _avgPanPower[pixel] = power;
                _avgWfPower[pixel] = power;
            }
            else
            {
                // Fast attack / slow decay on the trace, like a lab
                // analyzer: a genuine signal onset (>2 dB above the
                // averaged level) pops on within a frame or two while the
                // noise floor keeps the full averaging depth. The waterfall
                // stays symmetric and fast to preserve time texture.
                double panAlpha =
                    power > _avgPanPower[pixel] * PanAttackTriggerRatio
                        ? panAttackAlpha
                        : panDecayAlpha;
                _avgPanPower[pixel] = _avgPanPower[pixel] * (1.0 - panAlpha) + power * panAlpha;
                _avgWfPower[pixel] = _avgWfPower[pixel] * (1.0 - wfAlpha) + power * wfAlpha;
            }
        }

        _smoothedValid = true;
        double sideWeight = SpatialSideWeightForZoom(viewport.ZoomLevel);
        double centerWeight = 1.0 - (2.0 * sideWeight);
        for (int pixel = 0; pixel < DisplayWidth; pixel++)
        {
            panDb[pixel] = SpatiallySmoothedDb(_avgPanPower, pixel, sideWeight, centerWeight);
            wfDb[pixel] = SpatiallySmoothedDb(_avgWfPower, pixel, sideWeight, centerWeight);
        }

        return viewport;
    }

    private static float SpatiallySmoothedDb(double[] power, int pixel, double sideWeight, double centerWeight)
    {
        double p = power[pixel];
        double smoothed =
            pixel == 0 || pixel == DisplayWidth - 1
                ? p
                : power[pixel - 1] * sideWeight + p * centerWeight + power[pixel + 1] * sideWeight;
        return (float)(10.0 * Math.Log10(Math.Max(smoothed, MinPower)));
    }

    private void PlanSegments(int sampleCount, int sampleRateHz, double hzPerPixel)
    {
        int segLen = FftSize;
        while (segLen > MinSegmentLength &&
               (segLen >> 1) <= sampleCount &&
               sampleRateHz / (double)(segLen >> 1) <= hzPerPixel * MaxSegmentBinToPixelRatio)
        {
            segLen >>= 1;
        }

        _segmentLength = segLen;
        if (segLen >= sampleCount)
        {
            // Full-frame transform; zero-padding up to the FFT size (the 32
            // missing Saturn samples) happens in AccumulateSegmentPower.
            _segmentOffsets = [0];
            return;
        }

        int nseg = Math.Clamp(
            (int)Math.Round((sampleCount - segLen) / (double)(segLen / 2)) + 1,
            1,
            MaxSegments);
        if (nseg <= 1)
        {
            _segmentOffsets = [0];
            return;
        }

        var offsets = new int[nseg];
        double hop = (sampleCount - segLen) / (double)(nseg - 1);
        for (int i = 0; i < nseg; i++)
            offsets[i] = (int)Math.Round(i * hop);
        _segmentOffsets = offsets;
    }

    private void AccumulateSegmentPower(ReadOnlySpan<short> samples, int bins)
    {
        var window = WindowFor(samples.Length);
        var plan = PlanFor(_segmentLength);
        int n = plan.Size;
        for (int i = 0; i < samples.Length; i++)
            _real[i] = samples[i] * window.Coefficients[i];
        if (samples.Length < n) Array.Clear(_real, samples.Length, n - samples.Length);

        // Real-input transform: the input is real-only, so the packed N/2
        // complex FFT plus split computes the identical single-sided
        // spectrum at roughly half the cost of the old full-complex pass.
        plan.Fft.ForwardPower(_real.AsSpan(0, n), plan.Power);

        double baseScale = 1.0 / (window.Sum * 32768.0);
        int maxPositiveBin = n / 2;
        for (int bin = 0; bin <= maxPositiveBin; bin++)
        {
            double scale = bin == 0 ? baseScale : 2.0 * baseScale;
            _binPower[bin] += scale * scale * plan.Power[bin];
        }
    }

    private WindowPlan WindowFor(int length)
    {
        if (_windowPlans.TryGetValue(length, out var plan)) return plan;

        var coefficients = new double[length];
        double sum = 0.0;
        for (int i = 0; i < length; i++)
        {
            double phase = 2.0 * Math.PI * i / (length - 1);
            double w =
                0.35875 -
                0.48829 * Math.Cos(phase) +
                0.14128 * Math.Cos(2.0 * phase) -
                0.01168 * Math.Cos(3.0 * phase);
            coefficients[i] = w;
            sum += w;
        }

        plan = new WindowPlan(coefficients, sum);
        _windowPlans[length] = plan;
        return plan;
    }

    private FftPlan PlanFor(int size)
    {
        if (_fftPlans.TryGetValue(size, out var plan)) return plan;

        plan = new FftPlan(size, new WidebandRealFft(size), new double[(size / 2) + 1]);
        _fftPlans[size] = plan;
        return plan;
    }

    public static WidebandSpectrumViewport ResolveViewport(int zoomLevel, long targetCenterHz)
    {
        int level = Math.Clamp(zoomLevel, 1, MaxZoomLevel);
        double spanHz = DisplaySpanHz / level;
        long centerHz;
        if (level <= 1 || spanHz >= DisplaySpanHz)
        {
            spanHz = DisplaySpanHz;
            centerHz = DisplayCenterHz;
        }
        else
        {
            double halfSpanHz = spanHz / 2.0;
            double requestedCenterHz = Math.Clamp((double)targetCenterHz, 0.0, DisplaySpanHz);
            centerHz = (long)Math.Round(Math.Clamp(requestedCenterHz, halfSpanHz, DisplaySpanHz - halfSpanHz));
        }

        return new WidebandSpectrumViewport(
            centerHz,
            (float)(spanHz / DisplayWidth),
            spanHz,
            level);
    }

    private static double EmaAlpha(double frameIntervalMs, double tauMs) =>
        1.0 - Math.Exp(-frameIntervalMs / tauMs);

    private static double PanTauMsForZoom(int zoomLevel)
    {
        if (zoomLevel <= SyntheticDspEngine.MaxZoomLevel) return PanTauOverviewMs;
        double t = Math.Clamp(
            (zoomLevel - SyntheticDspEngine.MaxZoomLevel) /
            (double)(MaxZoomLevel - SyntheticDspEngine.MaxZoomLevel),
            0.0,
            1.0);
        return PanTauOverviewMs + (PanTauMaxZoomMs - PanTauOverviewMs) * t;
    }

    private static double SpatialSideWeightForZoom(int zoomLevel)
    {
        if (zoomLevel <= SyntheticDspEngine.MaxZoomLevel) return MaxSpatialSideWeight;
        double t = Math.Clamp(
            (zoomLevel - SyntheticDspEngine.MaxZoomLevel) /
            (double)(MaxZoomLevel - SyntheticDspEngine.MaxZoomLevel),
            0.0,
            1.0);
        return MaxSpatialSideWeight + (MinSpatialSideWeight - MaxSpatialSideWeight) * t;
    }

    private double IntegratePower(double startBin, double endBin)
    {
        if (!double.IsFinite(startBin) || !double.IsFinite(endBin) || endBin <= startBin)
            return 0.0;

        double width = endBin - startBin;
        if (width <= 1.0)
            return InterpolatePower((startBin + endBin) * 0.5);

        int first = (int)Math.Floor(startBin);
        int last = (int)Math.Ceiling(endBin) - 1;
        double weightedPower = 0.0;
        double weightSum = 0.0;
        for (int bin = first; bin <= last; bin++)
        {
            if ((uint)bin >= (uint)_activeBinCount) continue;
            double weight = Math.Min(endBin, bin + 1.0) - Math.Max(startBin, bin);
            if (weight <= 0.0) continue;
            weightedPower += _binPower[bin] * weight;
            weightSum += weight;
        }

        return weightSum > 0.0 ? weightedPower / weightSum : 0.0;
    }

    private double InterpolatePower(double binPosition)
    {
        if (!double.IsFinite(binPosition)) return 0.0;
        if (binPosition <= 0.0) return _binPower[0];
        int lo = (int)Math.Floor(binPosition);
        if (lo >= _activeBinCount - 1) return _binPower[_activeBinCount - 1];
        double frac = binPosition - lo;
        return _binPower[lo] * (1.0 - frac) + _binPower[lo + 1] * frac;
    }

    private sealed record WindowPlan(double[] Coefficients, double Sum);

    private sealed record FftPlan(int Size, WidebandRealFft Fft, double[] Power);
}

internal readonly record struct WidebandSpectrumViewport(
    long CenterHz,
    float HzPerPixel,
    double SpanHz,
    int ZoomLevel);
