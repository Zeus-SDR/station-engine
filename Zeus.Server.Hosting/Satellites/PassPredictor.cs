// SPDX-License-Identifier: GPL-2.0-or-later

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.Satellites;
#else
namespace Zeus.Server.Satellites;
#endif

public sealed record SatellitePass(
    DateTimeOffset? AosUtc,
    DateTimeOffset MaxElevationUtc,
    DateTimeOffset LosUtc,
    double MaxElevationDeg,
    double? AosAzimuthDeg,
    double LosAzimuthDeg,
    bool InProgressAtStart)
{
    public double? DurationSeconds => AosUtc is { } aos ? (LosUtc - aos).TotalSeconds : null;
}
public static class PassPredictor
{
    private const double EarthRadiusKm = 6378.137;
    private const double EarthMuKm3S2 = 398600.4418;

    public static IReadOnlyList<SatellitePass> Predict(
        Sgp4Propagator propagator,
        GeodeticPoint observer,
        DateTimeOffset startUtc,
        double minimumElevationDeg = 15,
        TimeSpan? horizon = null)
    {
        var step = AdaptiveStep(propagator.MeanMotionRevolutionsPerDay, minimumElevationDeg);
        return PredictCore(
            time => CoordinateTransforms.LookAngle(observer, CoordinateTransforms.TemeToEcef(propagator.Propagate(time), time)),
            startUtc,
            minimumElevationDeg,
            horizon ?? TimeSpan.FromHours(48),
            step);
    }

    internal static IReadOnlyList<SatellitePass> PredictCore(
        Func<DateTimeOffset, LookAngles> lookAngles,
        DateTimeOffset startUtc,
        double minimumElevationDeg,
        TimeSpan horizon,
        TimeSpan step)
    {
        var start = startUtc.ToUniversalTime();
        var end = start + horizon;
        var result = new List<SatellitePass>();
        var previousTime = start;
        var previous = lookAngles(previousTime);
        DateTimeOffset? aos = null;
        double? aosAzimuth = null;
        var active = previous.ElevationDeg >= minimumElevationDeg;
        var inProgressAtStart = active;

        if (active)
        {
            var hi = previousTime;
            var lo = hi - step;
            var belowFound = false;
            for (var i = 0; i < 360; i++)
            {
                if (lookAngles(lo).ElevationDeg < minimumElevationDeg)
                {
                    belowFound = true;
                    break;
                }
                hi = lo;
                lo -= step;
            }
            if (belowFound)
            {
                aos = Refine(lookAngles, lo, hi, minimumElevationDeg, rising: true);
                aosAzimuth = lookAngles(aos.Value).AzimuthDeg;
            }
        }

        for (var time = previousTime + step; time <= end; time += step)
        {
            var current = lookAngles(time);
            if (!active && previous.ElevationDeg < minimumElevationDeg && current.ElevationDeg >= minimumElevationDeg)
            {
                aos = Refine(lookAngles, previousTime, time, minimumElevationDeg, rising: true);
                aosAzimuth = lookAngles(aos.Value).AzimuthDeg;
                active = true;
                inProgressAtStart = false;
            }
            else if (active && previous.ElevationDeg >= minimumElevationDeg && current.ElevationDeg < minimumElevationDeg)
            {
                var los = Refine(lookAngles, previousTime, time, minimumElevationDeg, rising: false);
                var maxTime = Maximize(lookAngles, aos ?? start, los);
                result.Add(new SatellitePass(
                    aos,
                    maxTime,
                    los,
                    lookAngles(maxTime).ElevationDeg,
                    aosAzimuth,
                    lookAngles(los).AzimuthDeg,
                    inProgressAtStart));
                aos = null;
                aosAzimuth = null;
                active = false;
                inProgressAtStart = false;
            }
            previousTime = time;
            previous = current;
        }
        return result;
    }

    internal static TimeSpan AdaptiveStep(double meanMotionRevolutionsPerDay, double minimumElevationDeg)
    {
        if (!double.IsFinite(meanMotionRevolutionsPerDay) || meanMotionRevolutionsPerDay <= 0)
            return TimeSpan.FromSeconds(60);

        var angularRate = meanMotionRevolutionsPerDay * 2 * Math.PI / 86400d;
        var orbitalRadius = Math.Cbrt(EarthMuKm3S2 / (angularRate * angularRate));
        var elevation = Math.Clamp(minimumElevationDeg, 0, 89.99) * Math.PI / 180d;
        var ratio = Math.Clamp(EarthRadiusKm / orbitalRadius, 0, 1);
        var cosine = Math.Cos(elevation);
        var centralAngleCos = ratio * cosine * cosine
            + Math.Sin(elevation) * Math.Sqrt(Math.Max(0, 1 - ratio * ratio * cosine * cosine));
        var centralAngle = Math.Acos(Math.Clamp(centralAngleCos, -1, 1));
        var shortestLobeSeconds = 2 * centralAngle / (angularRate + 2 * Math.PI / 86164.0905d);
        return TimeSpan.FromSeconds(Math.Clamp(shortestLobeSeconds / 4, 2, 60));
    }

    private static DateTimeOffset Refine(
        Func<DateTimeOffset, LookAngles> lookAngles,
        DateTimeOffset lo,
        DateTimeOffset hi,
        double threshold,
        bool rising)
    {
        for (var i = 0; i < 24; i++)
        {
            var mid = lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);
            var above = lookAngles(mid).ElevationDeg >= threshold;
            if (above == rising) hi = mid; else lo = mid;
        }
        return lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);
    }

    private static DateTimeOffset Maximize(
        Func<DateTimeOffset, LookAngles> lookAngles,
        DateTimeOffset lo,
        DateTimeOffset hi)
    {
        for (var i = 0; i < 32; i++)
        {
            var third = TimeSpan.FromTicks((hi - lo).Ticks / 3);
            var a = lo + third;
            var b = hi - third;
            if (lookAngles(a).ElevationDeg < lookAngles(b).ElevationDeg) lo = a; else hi = b;
        }
        return lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);
    }
}
