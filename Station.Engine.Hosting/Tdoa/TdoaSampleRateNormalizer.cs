// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace Zeus.Server.Tdoa;

/// <summary>
/// Places independently measured station streams on one deterministic sample-time grid.
/// The lowest input rate is used so normalization never expands the bounded IQ budget.
/// </summary>
internal static class TdoaSampleRateNormalizer
{
    private const int HalfTaps = 16;
    private const double RelativeRateTolerance = 1e-12;

    public static IReadOnlyList<ValidatedTdoaCapture> ToCommonGrid(
        IReadOnlyList<ValidatedTdoaCapture> captures,
        CancellationToken cancellationToken)
    {
        if (captures.Count == 0) return captures;
        double targetRate = captures.Min(capture => capture.SampleRateHz);
        var normalized = new ValidatedTdoaCapture[captures.Count];
        for (int i = 0; i < captures.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatedTdoaCapture capture = captures[i];
            double relativeDelta = Math.Abs(capture.SampleRateHz - targetRate) / targetRate;
            if (relativeDelta <= RelativeRateTolerance)
            {
                normalized[i] = capture with { SampleRateHz = targetRate };
                continue;
            }

            Complex[] samples = Resample(capture.Samples, capture.SampleRateHz, targetRate,
                cancellationToken);
            if (samples.Length < TdoaLimits.MinComplexSamplesPerStation)
                throw new TdoaValidationException(
                    $"Station '{capture.Id}' has fewer than {TdoaLimits.MinComplexSamplesPerStation} common-grid samples after measured-rate normalization.");
            double correctionPpm = (targetRate / capture.SampleRateHz - 1) * 1_000_000;
            // A 33-tap Blackman-windowed sinc has substantially sub-sample interpolation
            // error for the admitted band. Retain a conservative deterministic residual
            // in the timing budget instead of treating resampling as exact.
            double residualNs = 0.02 * 1e9 / targetRate;
            normalized[i] = capture with
            {
                SampleRateHz = targetRate,
                SampleRateCorrectionPpm = correctionPpm,
                ResamplingUncertaintyNanoseconds = residualNs,
                Samples = samples,
            };
        }
        return normalized;
    }

    private static Complex[] Resample(Complex[] source, double sourceRate, double targetRate,
        CancellationToken cancellationToken)
    {
        int outputCount = Math.Max(1,
            (int)Math.Floor((source.Length - 1) * targetRate / sourceRate) + 1);
        var output = new Complex[outputCount];
        double cutoff = 0.47 * Math.Min(1, targetRate / sourceRate);
        double sourcePerOutput = sourceRate / targetRate;

        for (int outputIndex = 0; outputIndex < output.Length; outputIndex++)
        {
            if ((outputIndex & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            double sourcePosition = outputIndex * sourcePerOutput;
            int center = (int)Math.Floor(sourcePosition);
            int first = Math.Max(0, center - HalfTaps + 1);
            int last = Math.Min(source.Length - 1, center + HalfTaps);
            Complex sum = Complex.Zero;
            double weightSum = 0;
            for (int sampleIndex = first; sampleIndex <= last; sampleIndex++)
            {
                double delta = sourcePosition - sampleIndex;
                double normalizedDistance = Math.Abs(delta) / HalfTaps;
                if (normalizedDistance >= 1) continue;
                double window = 0.42 + 0.5 * Math.Cos(Math.PI * normalizedDistance)
                    + 0.08 * Math.Cos(2 * Math.PI * normalizedDistance);
                double argument = 2 * cutoff * delta;
                double sinc = Math.Abs(argument) < 1e-12
                    ? 1
                    : Math.Sin(Math.PI * argument) / (Math.PI * argument);
                double weight = 2 * cutoff * sinc * window;
                sum += source[sampleIndex] * weight;
                weightSum += weight;
            }
            output[outputIndex] = Math.Abs(weightSum) > 1e-12
                ? sum / weightSum
                : source[Math.Clamp(center, 0, source.Length - 1)];
        }
        return output;
    }
}
