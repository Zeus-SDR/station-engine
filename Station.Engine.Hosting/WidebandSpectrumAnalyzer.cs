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
    public const int AnalysisFftSize = Protocol2Client.WidebandFrameSamples * 64;

    private const int InputSampleCount = Protocol2Client.WidebandFrameSamples;
    private const int FftSize = AnalysisFftSize;
    private const double MinAmplitude = 1e-12;
    private const double SpectrumEmaAlpha = 0.38;
    private const double MaxZoomSpectrumEmaAlpha = 0.62;
    private const double MinSpatialSideWeight = 0.06;
    private const double MaxSpatialSideWeight = 0.18;

    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imag = new double[FftSize];
    private readonly double[] _window = new double[InputSampleCount];
    private readonly int[] _bitReverse = new int[FftSize];
    private readonly double[] _stageCos;
    private readonly double[] _stageSin;
    private readonly double[] _binPower = new double[(FftSize / 2) + 1];
    private readonly float[] _smoothedDb = new float[DisplayWidth];
    private readonly double _windowSum;
    private int _sampleRateHz;
    private long _viewportCenterHz;
    private float _viewportHzPerPixel;
    private bool _smoothedValid;

    public WidebandSpectrumAnalyzer()
    {
        double windowSum = 0.0;
        for (int i = 0; i < InputSampleCount; i++)
        {
            double phase = 2.0 * Math.PI * i / (InputSampleCount - 1);
            double w =
                0.35875 -
                0.48829 * Math.Cos(phase) +
                0.14128 * Math.Cos(2.0 * phase) -
                0.01168 * Math.Cos(3.0 * phase);
            _window[i] = w;
            windowSum += w;
        }
        _windowSum = windowSum;

        int bits = 0;
        for (int n = FftSize; n > 1; n >>= 1) bits++;
        for (int i = 0; i < FftSize; i++)
            _bitReverse[i] = ReverseBits(i, bits);

        _stageCos = new double[bits];
        _stageSin = new double[bits];
        for (int stage = 0, len = 2; len <= FftSize; stage++, len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            _stageCos[stage] = Math.Cos(angle);
            _stageSin[stage] = Math.Sin(angle);
        }
    }

    public WidebandSpectrumViewport Analyze(
        ReadOnlySpan<short> samples,
        int sampleRateHz,
        Span<float> panDb,
        Span<float> wfDb,
        int zoomLevel,
        long targetCenterHz)
    {
        if (panDb.Length < DisplayWidth || wfDb.Length < DisplayWidth)
            throw new ArgumentException("Output spans must be at least DisplayWidth samples long.");

        if (sampleRateHz <= 0) sampleRateHz = Protocol2Client.WidebandAdcSampleRateHz;
        var viewport = ResolveViewport(zoomLevel, targetCenterHz);
        if (sampleRateHz != _sampleRateHz ||
            viewport.CenterHz != _viewportCenterHz ||
            Math.Abs(viewport.HzPerPixel - _viewportHzPerPixel) > Math.Max(1e-6f, viewport.HzPerPixel * 1e-6f))
        {
            _sampleRateHz = sampleRateHz;
            _viewportCenterHz = viewport.CenterHz;
            _viewportHzPerPixel = viewport.HzPerPixel;
            _smoothedValid = false;
        }

        int copy = Math.Min(samples.Length, InputSampleCount);
        for (int i = 0; i < copy; i++)
            _real[i] = samples[i] * _window[i];
        if (copy < FftSize) Array.Clear(_real, copy, FftSize - copy);
        Array.Clear(_imag, 0, _imag.Length);

        FftInPlace();

        double baseScale = 1.0 / (_windowSum * 32768.0);
        int maxPositiveBin = FftSize / 2;
        for (int bin = 0; bin <= maxPositiveBin; bin++)
        {
            double scale = bin == 0 ? baseScale : 2.0 * baseScale;
            double re = _real[bin] * scale;
            double im = _imag[bin] * scale;
            _binPower[bin] = re * re + im * im;
        }

        double binHz = sampleRateHz / (double)FftSize;
        double nyquistHz = sampleRateHz / 2.0;
        double startHz = viewport.CenterHz - (viewport.SpanHz / 2.0);
        double emaAlpha = EmaAlphaForZoom(viewport.ZoomLevel);
        for (int pixel = 0; pixel < DisplayWidth; pixel++)
        {
            double loHz = startHz + pixel * viewport.HzPerPixel;
            double hiHz = loHz + viewport.HzPerPixel;
            double power = 0.0;
            if (hiHz > 0.0 && loHz < nyquistHz)
            {
                double startBin = Math.Max(0.0, loHz / binHz);
                double endBin = Math.Min(maxPositiveBin, hiHz / binHz);
                power = IntegratePower(startBin, endBin);
            }

            double amplitude = Math.Sqrt(Math.Max(power, 0.0));
            float db = (float)(20.0 * Math.Log10(Math.Max(amplitude, MinAmplitude)));
            if (!_smoothedValid)
            {
                _smoothedDb[pixel] = db;
            }
            else
            {
                _smoothedDb[pixel] = (float)(_smoothedDb[pixel] * (1.0 - emaAlpha) + db * emaAlpha);
            }
        }

        _smoothedValid = true;
        double sideWeight = SpatialSideWeightForZoom(viewport.ZoomLevel);
        double centerWeight = 1.0 - (2.0 * sideWeight);
        for (int pixel = 0; pixel < DisplayWidth; pixel++)
        {
            float db = _smoothedDb[pixel];
            float smoothedDb =
                pixel == 0 || pixel == DisplayWidth - 1
                    ? db
                    : (float)(_smoothedDb[pixel - 1] * sideWeight + db * centerWeight + _smoothedDb[pixel + 1] * sideWeight);
            panDb[pixel] = smoothedDb;
            wfDb[pixel] = smoothedDb;
        }

        return viewport;
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

    private static double EmaAlphaForZoom(int zoomLevel)
    {
        if (zoomLevel <= SyntheticDspEngine.MaxZoomLevel) return SpectrumEmaAlpha;
        double t = Math.Clamp(
            (zoomLevel - SyntheticDspEngine.MaxZoomLevel) /
            (double)(MaxZoomLevel - SyntheticDspEngine.MaxZoomLevel),
            0.0,
            1.0);
        return SpectrumEmaAlpha + (MaxZoomSpectrumEmaAlpha - SpectrumEmaAlpha) * t;
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
            if ((uint)bin >= (uint)_binPower.Length) continue;
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
        if (lo >= _binPower.Length - 1) return _binPower[^1];
        double frac = binPosition - lo;
        return _binPower[lo] * (1.0 - frac) + _binPower[lo + 1] * frac;
    }

    private void FftInPlace()
    {
        for (int i = 0; i < FftSize; i++)
        {
            int j = _bitReverse[i];
            if (i >= j) continue;
            (_real[i], _real[j]) = (_real[j], _real[i]);
            (_imag[i], _imag[j]) = (_imag[j], _imag[i]);
        }

        for (int stage = 0, len = 2; len <= FftSize; stage++, len <<= 1)
        {
            int half = len >> 1;
            double wLenRe = _stageCos[stage];
            double wLenIm = _stageSin[stage];
            for (int i = 0; i < FftSize; i += len)
            {
                double wRe = 1.0;
                double wIm = 0.0;
                for (int j = 0; j < half; j++)
                {
                    int even = i + j;
                    int odd = even + half;
                    double oddRe = _real[odd] * wRe - _imag[odd] * wIm;
                    double oddIm = _real[odd] * wIm + _imag[odd] * wRe;
                    double evenRe = _real[even];
                    double evenIm = _imag[even];
                    _real[even] = evenRe + oddRe;
                    _imag[even] = evenIm + oddIm;
                    _real[odd] = evenRe - oddRe;
                    _imag[odd] = evenIm - oddIm;

                    double nextRe = wRe * wLenRe - wIm * wLenIm;
                    wIm = wRe * wLenIm + wIm * wLenRe;
                    wRe = nextRe;
                }
            }
        }
    }

    private static int ReverseBits(int value, int bits)
    {
        int result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
}

internal readonly record struct WidebandSpectrumViewport(
    long CenterHz,
    float HzPerPixel,
    double SpanHz,
    int ZoomLevel);
