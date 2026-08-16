// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server.Tdoa;

internal static class TdoaGeodesy
{
    private const double A = 6_378_137.0;
    private const double F = 1 / 298.257223563;
    private const double B = (1 - F) * A;

    public static double Distance3dMeters(ValidatedTdoaCapture a, ValidatedTdoaCapture b) =>
        Math.Sqrt(Math.Pow(SurfaceDistanceMeters(a.LatitudeDeg, a.LongitudeDeg, b.LatitudeDeg, b.LongitudeDeg), 2)
            + Math.Pow(b.AltitudeMeters - a.AltitudeMeters, 2));

    public static double SurfaceDistanceMeters(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        if (Math.Abs(lat1Deg - lat2Deg) < 1e-14 && Math.Abs(lon1Deg - lon2Deg) < 1e-14) return 0;
        double phi1 = DegreesToRadians(lat1Deg), phi2 = DegreesToRadians(lat2Deg);
        double l = DegreesToRadians(WrapLongitude(lon2Deg - lon1Deg));
        double u1 = Math.Atan((1 - F) * Math.Tan(phi1));
        double u2 = Math.Atan((1 - F) * Math.Tan(phi2));
        double sinU1 = Math.Sin(u1), cosU1 = Math.Cos(u1), sinU2 = Math.Sin(u2), cosU2 = Math.Cos(u2);
        double lambda = l, sinSigma = 0, cosSigma = 0, sigma = 0, cosSqAlpha = 0, cos2SigmaM = 0;
        bool converged = false;
        for (int iteration = 0; iteration < 100; iteration++)
        {
            double sinLambda = Math.Sin(lambda), cosLambda = Math.Cos(lambda);
            sinSigma = Math.Sqrt(Math.Pow(cosU2 * sinLambda, 2)
                + Math.Pow(cosU1 * sinU2 - sinU1 * cosU2 * cosLambda, 2));
            if (sinSigma == 0) return 0;
            cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
            sigma = Math.Atan2(sinSigma, cosSigma);
            double sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cosSqAlpha = 1 - sinAlpha * sinAlpha;
            cos2SigmaM = cosSqAlpha < 1e-15 ? 0 : cosSigma - 2 * sinU1 * sinU2 / cosSqAlpha;
            double c = F / 16 * cosSqAlpha * (4 + F * (4 - 3 * cosSqAlpha));
            double previous = lambda;
            lambda = l + (1 - c) * F * sinAlpha
                * (sigma + c * sinSigma * (cos2SigmaM + c * cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)));
            if (Math.Abs(lambda - previous) < 1e-12) { converged = true; break; }
        }
        if (!converged)
        {
            // Vincenty's antipodal singularity: deterministic spherical fallback.
            double dPhi = phi2 - phi1;
            double h = Math.Pow(Math.Sin(dPhi / 2), 2) + Math.Cos(phi1) * Math.Cos(phi2) * Math.Pow(Math.Sin(l / 2), 2);
            return 6_371_008.8 * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(Math.Max(0, 1 - h)));
        }
        double uSq = cosSqAlpha * (A * A - B * B) / (B * B);
        double bigA = 1 + uSq / 16384 * (4096 + uSq * (-768 + uSq * (320 - 175 * uSq)));
        double bigB = uSq / 1024 * (256 + uSq * (-128 + uSq * (74 - 47 * uSq)));
        double deltaSigma = bigB * sinSigma * (cos2SigmaM + bigB / 4
            * (cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)
               - bigB / 6 * cos2SigmaM * (-3 + 4 * sinSigma * sinSigma) * (-3 + 4 * cos2SigmaM * cos2SigmaM)));
        return B * bigA * (sigma - deltaSigma);
    }

    public static double WrapLongitude(double longitude)
    {
        double result = (longitude + 180) % 360;
        if (result < 0) result += 360;
        return result - 180;
    }

    public static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}
