// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>
/// Estimates in-passband SNR from a one-hertz-normalized, average-power
/// spectrum. Input pixels must be ordered low frequency to high frequency and
/// expressed as dB power density per hertz.
/// </summary>
internal sealed class InPassbandSnrEstimator
{
    internal readonly record struct MeasurementKey(
        int RxId,
        int SampleRateHz,
        int PixelCount,
        long AnalyzerGeneration,
        long AnalyzerCenterHz,
        long EffectiveVfoHz,
        int FilterLowHz,
        int FilterHighHz);

    internal readonly record struct Result(
        double SnrDb,
        double SignalOnlyDb,
        double IntegratedNoiseDb,
        double Confidence)
    {
        public bool IsValid =>
            double.IsFinite(SnrDb) &&
            double.IsFinite(SignalOnlyDb) &&
            double.IsFinite(IntegratedNoiseDb) &&
            double.IsFinite(Confidence) && Confidence > 0.0;

        public static Result Invalid { get; } = new(
            double.NaN, double.NaN, double.NaN, 0.0);
    }

    private const int MinimumReferenceBinsPerSide = 4;
    internal const long SettleMs = 2_250;
    private const double MinimumMeasurementSigmaDb = 0.08;
    private const double DetectionSigma = 3.5;

    private MeasurementKey? _key;
    private long _keyChangedMs = long.MinValue;
    private int _freshFramesForKey;
    private bool _wasKeyed;

    public Result Update(
        ReadOnlySpan<float> psdDbPerHz,
        double hzPerPixel,
        double passbandLowOffsetHz,
        double passbandHighOffsetHz,
        MeasurementKey key,
        long nowMs,
        bool fresh,
        bool keyed)
    {
        if (_key is null || _key.Value != key)
        {
            _key = key;
            _keyChangedMs = nowMs;
            _freshFramesForKey = 0;
        }

        if (keyed)
        {
            // RX analyzer state can be starved or contaminated while the radio
            // is transmitting. Restart acquisition on every keyed interval so
            // the first post-MOX frame can never reuse a pre-TX noise estimate.
            _wasKeyed = true;
            _keyChangedMs = nowMs;
            _freshFramesForKey = 0;
            return Result.Invalid;
        }
        if (_wasKeyed)
        {
            _wasKeyed = false;
            _keyChangedMs = nowMs;
            _freshFramesForKey = 0;
        }
        if (!fresh) return Result.Invalid;

        _freshFramesForKey++;
        if (_freshFramesForKey < 2 || nowMs - _keyChangedMs < SettleMs)
            return Result.Invalid;

        return Estimate(
            psdDbPerHz,
            hzPerPixel,
            passbandLowOffsetHz,
            passbandHighOffsetHz);
    }

    /// <summary>Pure single-spectrum calculation, exposed to deterministic tests.</summary>
    internal static Result Estimate(
        ReadOnlySpan<float> psdDbPerHz,
        double hzPerPixel,
        double passbandLowOffsetHz,
        double passbandHighOffsetHz)
    {
        int count = psdDbPerHz.Length;
        if (count < 16 || !double.IsFinite(hzPerPixel) || hzPerPixel <= 0.0 ||
            !double.IsFinite(passbandLowOffsetHz) || !double.IsFinite(passbandHighOffsetHz))
            return Result.Invalid;

        double passLow = Math.Min(passbandLowOffsetHz, passbandHighOffsetHz);
        double passHigh = Math.Max(passbandLowOffsetHz, passbandHighOffsetHz);
        double passWidth = passHigh - passLow;
        if (passWidth <= 0.0) return Result.Invalid;

        double spectrumLow = -0.5 * count * hzPerPixel;
        double spectrumHigh = spectrumLow + count * hzPerPixel;
        // A report is only meaningful when the complete active filter and both
        // guarded reference regions are represented by the current analyzer.
        if (passLow < spectrumLow || passHigh > spectrumHigh)
            return Result.Invalid;

        double guardHz = Math.Max(2.0 * hzPerPixel, 0.25 * passWidth);
        double referenceHz = Math.Max(passWidth, 8.0 * hzPerPixel);
        double leftLow = Math.Max(spectrumLow, passLow - guardHz - referenceHz);
        double leftHigh = passLow - guardHz;
        double rightLow = passHigh + guardHz;
        double rightHigh = Math.Min(spectrumHigh, passHigh + guardHz + referenceHz);

        var left = RobustSide(psdDbPerHz, hzPerPixel, spectrumLow, leftLow, leftHigh);
        var right = RobustSide(psdDbPerHz, hzPerPixel, spectrumLow, rightLow, rightHigh);
        if (!left.Valid || !right.Valid)
            return Result.Invalid;

        double totalPower = IntegrateMeasured(
            psdDbPerHz, hzPerPixel, spectrumLow, passLow, passHigh, out double coveredHz);
        if (!double.IsFinite(totalPower) || totalPower <= 0.0 ||
            coveredHz < passWidth - Math.Max(1e-6, hzPerPixel * 1e-6))
            return Result.Invalid;

        // Interpolate the local noise slope in dB/Hz between robust medians on
        // the two guarded sides, then integrate that prediction over the exact
        // same fractional pixel overlaps as P(S+N).
        double slope = (right.LevelDb - left.LevelDb) /
            Math.Max(hzPerPixel, right.CenterHz - left.CenterHz);
        // A >30 dB/reference-span tilt is almost certainly an occupied sideband
        // rather than a local receiver-noise slope; refuse to manufacture a report.
        if (!double.IsFinite(slope) || Math.Abs(right.LevelDb - left.LevelDb) > 30.0)
            return Result.Invalid;

        double noisePower = IntegratePredictedNoise(
            hzPerPixel, spectrumLow, count, passLow, passHigh,
            left.LevelDb, left.CenterHz, slope);
        if (!double.IsFinite(noisePower) || noisePower <= 0.0)
            return Result.Invalid;

        double signalPower = totalPower - noisePower;
        // Sub-noise reports are allowed only when the excess clears uncertainty
        // in both the guarded reference estimate and the in-passband noise mean.
        if (!double.IsFinite(signalPower) || signalPower <= 0.0)
            return Result.Invalid;

        double passbandBinSupport = Math.Max(1.0, passWidth / hzPerPixel);
        double leftSeDb = left.SigmaDb / Math.Sqrt(left.UsedBins);
        double rightSeDb = right.SigmaDb / Math.Sqrt(right.UsedBins);
        double referenceSeDb = Math.Sqrt(leftSeDb * leftSeDb + rightSeDb * rightSeDb) / 2.0;
        double pooledScatterDb = Math.Max(left.SigmaDb, right.SigmaDb);
        double passbandSeDb = pooledScatterDb / Math.Sqrt(passbandBinSupport);
        double combinedSigmaDb = Math.Sqrt(
            MinimumMeasurementSigmaDb * MinimumMeasurementSigmaDb +
            referenceSeDb * referenceSeDb + passbandSeDb * passbandSeDb);
        double relativeNoiseSigma = Math.Exp(Math.Log(10.0) / 10.0 * combinedSigmaDb) - 1.0;
        double requiredExcess = DetectionSigma * noisePower * relativeNoiseSigma;
        if (!double.IsFinite(requiredExcess) || signalPower <= requiredExcess)
            return Result.Invalid;

        double snrDb = 10.0 * Math.Log10(signalPower / noisePower);
        double signalDb = 10.0 * Math.Log10(signalPower);
        double noiseDb = 10.0 * Math.Log10(noisePower);

        // Confidence reflects reference support, side estimator dispersion and
        // subtraction conditioning. It is metadata, not a gate on negative SNR:
        // a resolved 0.1*N excess (-10 dB) remains reportable at lower confidence.
        double support = Math.Min(1.0,
            Math.Min(left.UsedBins, right.UsedBins) / 12.0);
        double dispersion = Math.Exp(-Math.Max(left.MadDb, right.MadDb) / 6.0);
        double conditioning = Math.Clamp(signalPower / requiredExcess - 1.0, 0.0, 1.0);
        double agreement = Math.Exp(-Math.Abs(left.LevelDb - right.LevelDb) / 20.0);
        double confidence = Math.Clamp(support * dispersion * agreement *
            (0.15 + 0.85 * conditioning), 0.01, 1.0);

        return new Result(snrDb, signalDb, noiseDb, confidence);
    }

    private readonly record struct SideEstimate(
        bool Valid, double LevelDb, double CenterHz, double MadDb, double SigmaDb, int UsedBins)
    {
        public static SideEstimate Invalid { get; } = new(false, 0, 0, 0, 0, 0);
    }

    private static SideEstimate RobustSide(
        ReadOnlySpan<float> pixels,
        double hzPerPixel,
        double spectrumLow,
        double regionLow,
        double regionHigh)
    {
        if (regionHigh <= regionLow) return SideEstimate.Invalid;

        var values = new List<(double Db, double Hz)>();
        for (int i = 0; i < pixels.Length; i++)
        {
            double center = spectrumLow + (i + 0.5) * hzPerPixel;
            if (center < regionLow || center >= regionHigh) continue;
            float db = pixels[i];
            if (float.IsFinite(db) && db > -300f && db < 200f)
                values.Add((db, center));
        }
        if (values.Count < MinimumReferenceBinsPerSide)
            return SideEstimate.Invalid;

        var ordered = values.Select(static x => x.Db).Order().ToArray();
        double median = Median(ordered);
        var deviations = ordered.Select(x => Math.Abs(x - median)).Order().ToArray();
        double mad = Median(deviations);
        double highGate = median + Math.Max(3.0, 3.5 * mad);
        double lowGate = median - Math.Max(6.0, 4.0 * mad);
        var kept = values.Where(x => x.Db >= lowGate && x.Db <= highGate).ToArray();
        if (kept.Length < MinimumReferenceBinsPerSide || kept.Length < values.Count / 2)
            return SideEstimate.Invalid;

        var keptDb = kept.Select(static x => x.Db).Order().ToArray();
        double robustDb = Median(keptDb);
        double centerHz = kept.Average(static x => x.Hz);
        return new SideEstimate(true, robustDb, centerHz, mad, 1.4826 * mad, kept.Length);
    }

    private static double IntegrateMeasured(
        ReadOnlySpan<float> pixels,
        double hzPerPixel,
        double spectrumLow,
        double low,
        double high,
        out double coveredHz)
    {
        double sum = 0.0;
        coveredHz = 0.0;
        for (int i = 0; i < pixels.Length; i++)
        {
            double binLow = spectrumLow + i * hzPerPixel;
            double overlap = Math.Min(high, binLow + hzPerPixel) - Math.Max(low, binLow);
            if (overlap <= 0.0) continue;
            float db = pixels[i];
            if (!float.IsFinite(db) || db <= -300f || db >= 200f)
                return double.NaN;
            sum += DbToLinear(db) * overlap;
            coveredHz += overlap;
        }
        return sum;
    }

    private static double IntegratePredictedNoise(
        double hzPerPixel,
        double spectrumLow,
        int pixelCount,
        double low,
        double high,
        double referenceDb,
        double referenceHz,
        double slopeDbPerHz)
    {
        double sum = 0.0;
        for (int i = 0; i < pixelCount; i++)
        {
            double binLow = spectrumLow + i * hzPerPixel;
            double overlapLow = Math.Max(low, binLow);
            double overlapHigh = Math.Min(high, binLow + hzPerPixel);
            if (overlapHigh <= overlapLow) continue;
            double midpoint = 0.5 * (overlapLow + overlapHigh);
            double predictedDb = referenceDb + slopeDbPerHz * (midpoint - referenceHz);
            sum += DbToLinear(predictedDb) * (overlapHigh - overlapLow);
        }
        return sum;
    }

    private static double DbToLinear(double db) => Math.Pow(10.0, db / 10.0);

    private static double Median(double[] ordered)
    {
        int n = ordered.Length;
        return (n & 1) != 0
            ? ordered[n / 2]
            : 0.5 * (ordered[n / 2 - 1] + ordered[n / 2]);
    }
}
