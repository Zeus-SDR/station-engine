// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace Zeus.Server.Tdoa;

internal sealed record PairPeak(double DelayNanoseconds, double Likelihood);

internal sealed record PairAnalysis(
    ValidatedTdoaCapture A,
    ValidatedTdoaCapture B,
    TdoaPairResult Result,
    double MinDelayNanoseconds,
    double DelayStepNanoseconds,
    double[] Likelihood,
    IReadOnlyList<PairPeak> Peaks)
{
    public double LikelihoodAt(double delayNanoseconds)
    {
        double index = (delayNanoseconds - MinDelayNanoseconds) / DelayStepNanoseconds;
        if (index < 0 || index > Likelihood.Length - 1) return 1e-9;
        int lo = (int)Math.Floor(index);
        int hi = Math.Min(lo + 1, Likelihood.Length - 1);
        double f = index - lo;
        return Math.Max(1e-9, Likelihood[lo] * (1 - f) + Likelihood[hi] * f);
    }
}

internal static class TdoaPairAnalyzer
{
    private const double LightSpeedMetersPerSecond = 299_792_458.0;

    public static PairAnalysis Analyze(ValidatedTdoaCapture a, ValidatedTdoaCapture b, CancellationToken cancellationToken)
    {
        int count = Math.Min(a.Samples.Length, b.Samples.Length);
        var x = Prepare(a.Samples, count, cancellationToken);
        var y = Prepare(b.Samples, count, cancellationToken);
        double sampleNs = 1e9 / a.SampleRateHz;
        // Subtract in the integer TAI domain first. Converting the two ~1.9e18 ns
        // epochs separately to double discards tens or hundreds of nanoseconds.
        double startDeltaNs = b.ReferenceTimeTaiNanoseconds >= a.ReferenceTimeTaiNanoseconds
            ? b.ReferenceTimeTaiNanoseconds - a.ReferenceTimeTaiNanoseconds
            : -(double)(a.ReferenceTimeTaiNanoseconds - b.ReferenceTimeTaiNanoseconds);
        double groupDeltaNs = b.GroupDelayNanoseconds - a.GroupDelayNanoseconds;
        double baselineMeters = TdoaGeodesy.Distance3dMeters(a, b);
        double physicalNs = baselineMeters / LightSpeedMetersPerSecond * 1e9;
        double clockMarginNs = 4 * Math.Sqrt(
            a.ClockUncertaintyNanoseconds * a.ClockUncertaintyNanoseconds
            + b.ClockUncertaintyNanoseconds * b.ClockUncertaintyNanoseconds);
        double allowedNs = physicalNs + clockMarginNs + sampleNs;
        int minLag = Math.Max(-count + 64, (int)Math.Ceiling((-allowedNs - startDeltaNs + groupDeltaNs) / sampleNs));
        int maxLag = Math.Min(count - 64, (int)Math.Floor((allowedNs - startDeltaNs + groupDeltaNs) / sampleNs));
        if (minLag > maxLag)
            throw new TdoaValidationException($"Captures '{a.Id}' and '{b.Id}' have no physically possible overlap at their GNSS/TAI sample epochs.");

        var initial = Correlate(x, y, minLag, maxLag, cancellationToken);
        int initialLag = minLag + ArgMax(initial.Likelihood);
        // Align fractionally before estimating carrier drift. Estimating CFO at an integer-only
        // lag aliases a wideband chirp's residual delay slope into a fictitious frequency offset.
        double initialFractionalLag = initialLag + RefineFractionalLag(x, y, initialLag, cancellationToken);
        double cfo = EstimateDifferentialCfo(x, y, initialFractionalLag, a.SampleRateHz, cancellationToken);
        CorrelationResult correlation = initial;
        if (double.IsFinite(cfo) && Math.Abs(cfo) >= 0.02 && Math.Abs(cfo) < a.SampleRateHz * 0.1)
        {
            for (int i = 0; i < y.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                y[i] *= Complex.FromPolarCoordinates(1, -2 * Math.PI * cfo * i / a.SampleRateHz);
            }
            correlation = Correlate(x, y, minLag, maxLag, cancellationToken);
        }
        else cfo = 0;

        int bestIndex = ArgMax(correlation.Likelihood);
        double fractional = RefineFractionalLag(x, y, minLag + bestIndex, cancellationToken);
        double lagSamples = minLag + bestIndex + fractional;
        double delayNs = lagSamples * sampleNs + startDeltaNs - groupDeltaNs;
        var peaks = FindPeaks(correlation.Likelihood, minLag, sampleNs, startDeltaNs - groupDeltaNs).ToList();
        int primaryPeak = peaks.FindIndex(candidate => Math.Abs(candidate.DelayNanoseconds - delayNs) <= sampleNs);
        if (primaryPeak >= 0) peaks[primaryPeak] = new PairPeak(delayNs, 1);
        else peaks.Insert(0, new PairPeak(delayNs, 1));
        if (peaks.Count > 6) peaks.RemoveRange(6, peaks.Count - 6);
        double peak = correlation.Likelihood[bestIndex];
        int exclusion = Math.Max(2, (int)Math.Ceiling(2.0));
        double sidelobe = 1e-12;
        for (int i = 0; i < correlation.Likelihood.Length; i++)
            if (Math.Abs(i - bestIndex) > exclusion) sidelobe = Math.Max(sidelobe, correlation.Likelihood[i]);
        double psr = peak / sidelobe;
        double curvature = PeakCurvature(correlation.Likelihood, bestIndex);
        double correlationSigmaNs = sampleNs * Math.Clamp(0.15 + 1 / Math.Sqrt(Math.Max(curvature, 1e-3) * 20), 0.1, 2.0);
        double uncertaintyNs = Math.Sqrt(correlationSigmaNs * correlationSigmaNs
            + a.ClockUncertaintyNanoseconds * a.ClockUncertaintyNanoseconds
            + b.ClockUncertaintyNanoseconds * b.ClockUncertaintyNanoseconds
            + a.ResamplingUncertaintyNanoseconds * a.ResamplingUncertaintyNanoseconds
            + b.ResamplingUncertaintyNanoseconds * b.ResamplingUncertaintyNanoseconds);
        double quality = Math.Clamp(0.45 * correlation.Coherence
            + 0.35 * Math.Clamp((psr - 1) / 4, 0, 1)
            + 0.20 * Math.Clamp(curvature * 5, 0, 1), 0, 1);
        var warnings = new List<string>();
        if (Math.Abs(a.SampleRateCorrectionPpm) > 0.001 || Math.Abs(b.SampleRateCorrectionPpm) > 0.001)
            warnings.Add(FormattableString.Invariant(
                $"Measured sample rates were normalized to a common grid (A {a.SampleRateCorrectionPpm:F3} ppm, B {b.SampleRateCorrectionPpm:F3} ppm correction)."));
        if (psr < 1.35) warnings.Add("Ambiguous correlation peak; another delay mode has similar support.");
        if (correlation.Coherence < 0.2) warnings.Add("Low cross-station spectral coherence.");
        if (Math.Abs(delayNs) > physicalNs + clockMarginNs)
            warnings.Add("Peak lies at the physical baseline timing boundary.");
        bool usable = quality >= 0.12 && Math.Abs(delayNs) <= physicalNs + clockMarginNs + sampleNs;

        var result = new TdoaPairResult(a.Id, b.Id, delayNs, lagSamples, cfo, psr,
            correlation.Coherence, uncertaintyNs, quality, usable, warnings);
        double minDelay = minLag * sampleNs + startDeltaNs - groupDeltaNs;
        return new PairAnalysis(a, b, result, minDelay, sampleNs, correlation.Likelihood, peaks);
    }

    private static Complex[] Prepare(Complex[] source, int count, CancellationToken token)
    {
        var result = new Complex[count];
        Complex mean = Complex.Zero;
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            mean += source[i];
        }
        mean /= count;
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            // Hann suppresses finite-capture correlation sidelobes; DC removal prevents a false zero-lag peak.
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (count - 1));
            result[i] = (source[i] - mean) * window;
        }
        return result;
    }

    private sealed record CorrelationResult(double[] Likelihood, double Coherence);

    private static CorrelationResult Correlate(Complex[] x, Complex[] y, int minLag, int maxLag, CancellationToken token)
    {
        int fftSize = 1;
        while (fftSize < x.Length + y.Length) fftSize <<= 1;
        var fx = new Complex[fftSize];
        var fy = new Complex[fftSize];
        Array.Copy(x, fx, x.Length);
        Array.Copy(y, fy, y.Length);
        Fft(fx, inverse: false, token);
        Fft(fy, inverse: false, token);

        var cross = new Complex[fftSize];
        var crossRaw = new Complex[fftSize];
        double meanMagnitude = 0;
        for (int k = 0; k < fftSize; k++)
        {
            if ((k & 4095) == 0) token.ThrowIfCancellationRequested();
            crossRaw[k] = Complex.Conjugate(fx[k]) * fy[k];
            meanMagnitude += crossRaw[k].Magnitude;
        }
        meanMagnitude = Math.Max(meanMagnitude / fftSize, 1e-18);
        double coherenceSum = 0;
        int coherenceCount = 0;
        for (int k = 1; k < fftSize - 1; k++)
        {
            if ((k & 4095) == 0) token.ThrowIfCancellationRequested();
            Complex smoothCross = Complex.Zero;
            double smoothX = 0, smoothY = 0;
            for (int d = -2; d <= 2; d++)
            {
                int q = (k + d + fftSize) % fftSize;
                smoothCross += crossRaw[q];
                smoothX += fx[q].Magnitude * fx[q].Magnitude;
                smoothY += fy[q].Magnitude * fy[q].Magnitude;
            }
            double coherence = Math.Clamp(smoothCross.Magnitude / Math.Sqrt(Math.Max(smoothX * smoothY, 1e-30)), 0, 1);
            double magnitude = crossRaw[k].Magnitude;
            // Preserve broadband amplitude evidence while PHAT sharpens delay and the smoothed
            // coherence term rejects bins whose phase is inconsistent with their neighbours.
            double rawWeight = 0.35 / meanMagnitude;
            double phatWeight = 0.65 * coherence / Math.Max(magnitude, meanMagnitude * 1e-6);
            cross[k] = crossRaw[k] * (rawWeight + phatWeight);
            if (magnitude > meanMagnitude * 0.05) { coherenceSum += coherence; coherenceCount++; }
        }
        cross[0] = Complex.Zero;
        cross[^1] = Complex.Zero;
        Fft(cross, inverse: true, token);

        var likelihood = new double[maxLag - minLag + 1];
        double max = 1e-30;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            token.ThrowIfCancellationRequested();
            int index = lag >= 0 ? lag : fftSize + lag;
            double value = cross[index].Magnitude;
            likelihood[lag - minLag] = value;
            max = Math.Max(max, value);
        }
        for (int i = 0; i < likelihood.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            likelihood[i] = Math.Max(1e-9, likelihood[i] / max);
        }
        return new CorrelationResult(likelihood, coherenceCount == 0 ? 0 : coherenceSum / coherenceCount);
    }

    private static double EstimateDifferentialCfo(Complex[] x, Complex[] y, double lag, double sampleRate,
        CancellationToken token)
    {
        int xStart = Math.Max(0, (int)Math.Ceiling(1 - lag));
        int xEnd = Math.Min(x.Length - 1, (int)Math.Ceiling(y.Length - 2 - lag) - 1);
        Complex sum = Complex.Zero;
        Complex? previous = null;
        for (int n = xStart; n <= xEnd; n++)
        {
            if ((n & 4095) == 0) token.ThrowIfCancellationRequested();
            Complex z = Complex.Conjugate(x[n]) * Interpolate(y, n + lag);
            if (previous is { } p)
            {
                double weight = Math.Sqrt(Math.Max(p.Magnitude * z.Magnitude, 0));
                if (weight > 1e-15) sum += Complex.Conjugate(p) * z / Math.Max(p.Magnitude * z.Magnitude, 1e-30) * weight;
            }
            previous = z;
        }
        return sum.Magnitude < 1e-12 ? 0 : Math.Atan2(sum.Imaginary, sum.Real) * sampleRate / (2 * Math.PI);
    }

    private static double RefineFractionalLag(Complex[] x, Complex[] y, int integerLag, CancellationToken token)
    {
        const double step = 0.05;
        const int halfSteps = 15;
        var scores = new double[halfSteps * 2 + 1];
        for (int i = -halfSteps; i <= halfSteps; i++)
        {
            token.ThrowIfCancellationRequested();
            scores[i + halfSteps] = FractionalCorrelation(x, y, integerLag + i * step, token);
        }
        int best = ArgMax(scores);
        double correction = ParabolicOffset(scores, best) * step;
        return (best - halfSteps) * step + correction;
    }

    private static double FractionalCorrelation(Complex[] x, Complex[] y, double lag, CancellationToken token)
    {
        int start = Math.Max(0, (int)Math.Ceiling(1 - lag));
        int end = Math.Min(x.Length - 1, (int)Math.Ceiling(y.Length - 2 - lag) - 1);
        int stride = Math.Max(1, (end - start) / 8192);
        Complex sum = Complex.Zero;
        double energyX = 0, energyY = 0;
        for (int n = start; n <= end; n += stride)
        {
            if ((n & 4095) == 0) token.ThrowIfCancellationRequested();
            double position = n + lag;
            Complex interpolated = Interpolate(y, position);
            sum += Complex.Conjugate(x[n]) * interpolated;
            energyX += x[n].Magnitude * x[n].Magnitude;
            energyY += interpolated.Magnitude * interpolated.Magnitude;
        }
        return sum.Magnitude / Math.Sqrt(Math.Max(energyX * energyY, 1e-30));
    }

    private static Complex Interpolate(Complex[] values, double position)
    {
        int index = (int)Math.Floor(position);
        double f = position - index;
        Complex ym1 = values[index - 1], y0 = values[index], y1 = values[index + 1], y2 = values[index + 2];
        return 0.5 * ((2 * y0)
            + (-ym1 + y1) * f
            + (2 * ym1 - 5 * y0 + 4 * y1 - y2) * f * f
            + (-ym1 + 3 * y0 - 3 * y1 + y2) * f * f * f);
    }

    private static IReadOnlyList<PairPeak> FindPeaks(double[] values, int minLag, double sampleNs, double offsetNs)
    {
        var peaks = new List<PairPeak>();
        for (int i = 1; i < values.Length - 1; i++)
            if (values[i] >= values[i - 1] && values[i] > values[i + 1] && values[i] >= 0.2)
                peaks.Add(new PairPeak((minLag + i + ParabolicOffset(values, i)) * sampleNs + offsetNs, values[i]));
        return peaks.OrderByDescending(p => p.Likelihood).Take(6).ToArray();
    }

    private static int ArgMax(double[] values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++) if (values[i] > values[best]) best = i;
        return best;
    }

    private static double ParabolicOffset(double[] values, int index)
    {
        if (index <= 0 || index >= values.Length - 1) return 0;
        double denominator = values[index - 1] - 2 * values[index] + values[index + 1];
        if (Math.Abs(denominator) < 1e-15) return 0;
        return Math.Clamp(0.5 * (values[index - 1] - values[index + 1]) / denominator, -0.5, 0.5);
    }

    private static double PeakCurvature(double[] values, int index)
    {
        if (index <= 0 || index >= values.Length - 1) return 0;
        return Math.Max(0, (2 * values[index] - values[index - 1] - values[index + 1]) / Math.Max(values[index], 1e-12));
    }

    private static void Fft(Complex[] values, bool inverse, CancellationToken token)
    {
        int n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            token.ThrowIfCancellationRequested();
            double angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            Complex root = Complex.FromPolarCoordinates(1, angle);
            for (int i = 0; i < n; i += length)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                Complex w = Complex.One;
                for (int j = 0; j < length / 2; j++)
                {
                    Complex even = values[i + j];
                    Complex odd = values[i + j + length / 2] * w;
                    values[i + j] = even + odd;
                    values[i + j + length / 2] = even - odd;
                    w *= root;
                }
            }
        }
        if (inverse)
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                values[i] /= n;
            }
    }
}
