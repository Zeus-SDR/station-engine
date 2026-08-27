// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace Zeus.Server;

public static class VnaMath
{
    private const double ReferenceOhms = 50.0;
    private const double Epsilon = 1e-12;

    public static Complex ApplyThru(Complex measured, Complex thru) =>
        thru.Magnitude <= Epsilon ? Complex.Zero : measured / thru;

    public static Complex ApplyOsl(
        Complex measured,
        Complex open,
        Complex @short,
        Complex load)
    {
        // One-port three-term error model:
        // m = Ed + Er*gamma/(1 - Es*gamma).  An ideal load has gamma=0,
        // open=+1 and short=-1, which makes the three coefficients solvable
        // independently at every frequency point.
        Complex directivity = load;
        Complex openDelta = open - directivity;
        Complex shortDelta = @short - directivity;
        Complex denominator = openDelta - shortDelta;
        if (denominator.Magnitude <= Epsilon) return Complex.Zero;

        Complex sourceMatch = (openDelta + shortDelta) / denominator;
        Complex reflectionTracking = openDelta * (Complex.One - sourceMatch);
        Complex observed = measured - directivity;
        Complex solve = reflectionTracking + observed * sourceMatch;
        if (solve.Magnitude <= Epsilon) return Complex.Zero;
        return ClampGamma(observed / solve);
    }

    public static VnaPointDto ToPoint(
        VnaComplexSample raw,
        Complex value,
        bool reflection,
        bool includeImpedance = true)
    {
        double magnitude = value.Magnitude;
        double magnitudeDb = 20.0 * Math.Log10(Math.Max(magnitude, Epsilon));
        double phase = Math.Atan2(value.Imaginary, value.Real) * 180.0 / Math.PI;

        if (!reflection)
        {
            return new VnaPointDto(
                raw.FrequencyHz, raw.Real, raw.Imaginary,
                Round(magnitudeDb), Round(phase), null, null, null, null);
        }

        Complex gamma = ClampGamma(value);
        double rho = Math.Min(gamma.Magnitude, 0.999999);
        double swr = (1.0 + rho) / Math.Max(1.0 - rho, Epsilon);
        double returnLoss = -20.0 * Math.Log10(Math.Max(rho, Epsilon));
        Complex impedance = ReferenceOhms * (Complex.One + gamma) / (Complex.One - gamma);
        return new VnaPointDto(
            raw.FrequencyHz, raw.Real, raw.Imaginary,
            Round(20.0 * Math.Log10(Math.Max(rho, Epsilon))), Round(phase),
            Round(swr), Round(returnLoss),
            includeImpedance ? Round(impedance.Real) : null,
            includeImpedance ? Round(impedance.Imaginary) : null);
    }

    public static VnaSweepMetricsDto Metrics(IReadOnlyList<VnaPointDto> points)
    {
        if (points.Count == 0)
            return new VnaSweepMetricsDto(0, null, null, null, null, null, null, null, null);

        var calibrated = points.Where(p => p.Swr.HasValue).ToArray();
        VnaPointDto resonance = calibrated.Length > 0
            ? calibrated.OrderBy(p => Math.Abs(p.ReactanceOhms ?? double.MaxValue))
                .ThenBy(p => p.Swr).First()
            : points.OrderByDescending(p => p.MagnitudeDb).First();

        double? minSwr = calibrated.Length == 0 ? null : calibrated.Min(p => p.Swr!.Value);
        double? maxReturnLoss = calibrated.Length == 0 ? null : calibrated.Max(p => p.ReturnLossDb!.Value);
        long? bw15 = Bandwidth(calibrated, resonance.FrequencyHz, 1.5);
        long? bw20 = Bandwidth(calibrated, resonance.FrequencyHz, 2.0);
        long? bw30 = Bandwidth(calibrated, resonance.FrequencyHz, 3.0);
        double? q = bw20 is > 0 ? resonance.FrequencyHz / (double)bw20.Value : null;

        return new VnaSweepMetricsDto(
            resonance.FrequencyHz,
            minSwr is null ? null : Round(minSwr.Value),
            maxReturnLoss is null ? null : Round(maxReturnLoss.Value),
            resonance.ResistanceOhms,
            resonance.ReactanceOhms,
            bw15,
            bw20,
            bw30,
            q is null ? null : Round(q.Value));
    }

    private static long? Bandwidth(IReadOnlyList<VnaPointDto> points, long resonanceHz, double limit)
    {
        if (points.Count == 0) return null;
        int center = 0;
        long nearest = long.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            long delta = Math.Abs(points[i].FrequencyHz - resonanceHz);
            if (delta < nearest) { nearest = delta; center = i; }
        }
        if (points[center].Swr is not { } centerSwr || centerSwr > limit) return null;

        int left = center;
        int right = center;
        while (left > 0 && points[left - 1].Swr is { } l && l <= limit) left--;
        while (right + 1 < points.Count && points[right + 1].Swr is { } r && r <= limit) right++;
        return points[right].FrequencyHz - points[left].FrequencyHz;
    }

    private static Complex ClampGamma(Complex gamma)
    {
        if (!double.IsFinite(gamma.Real) || !double.IsFinite(gamma.Imaginary)) return Complex.Zero;
        double magnitude = gamma.Magnitude;
        return magnitude >= 0.999999 && magnitude > 0
            ? gamma * (0.999999 / magnitude)
            : gamma;
    }

    private static double Round(double value) => Math.Round(value, 6);
}
