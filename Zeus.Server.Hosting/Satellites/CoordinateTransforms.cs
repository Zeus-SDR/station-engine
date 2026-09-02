// SPDX-License-Identifier: GPL-2.0-or-later

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.Satellites;
#else
namespace Zeus.Server.Satellites;
#endif

public readonly record struct GeodeticPoint(double LatitudeDeg, double LongitudeDeg, double AltitudeKm);
public readonly record struct LookAngles(double AzimuthDeg, double ElevationDeg, double SlantRangeKm);

public static class CoordinateTransforms
{
    private const double EquatorialRadiusKm = 6378.137;
    private const double Flattening = 1d / 298.257223563;

    public static (double X, double Y, double Z) TemeToEcef(TemeState state, DateTimeOffset utc)
    {
        var theta = Gstime(JulianDate(utc));
        var c = Math.Cos(theta); var s = Math.Sin(theta);
        return (c * state.XKm + s * state.YKm, -s * state.XKm + c * state.YKm, state.ZKm);
    }

    public static GeodeticPoint EcefToGeodetic(double x, double y, double z)
    {
        var e2 = Flattening * (2 - Flattening);
        var lon = Math.Atan2(y, x); var p = Math.Sqrt(x * x + y * y);
        if (p < 1e-9)
        {
            var polarRadius = EquatorialRadiusKm * (1 - Flattening);
            var latitude = z < 0 ? -90d : 90d;
            var polarAltitude = Math.Abs(z) - polarRadius;
            return new GeodeticPoint(latitude, 0, double.IsFinite(polarAltitude) ? polarAltitude : 0);
        }
        var lat = Math.Atan2(z, p * (1 - e2));
        double altitude = 0;
        for (var i = 0; i < 10; i++)
        {
            var sin = Math.Sin(lat); var n = EquatorialRadiusKm / Math.Sqrt(1 - e2 * sin * sin);
            altitude = p / Math.Cos(lat) - n;
            lat = Math.Atan2(z, p * (1 - e2 * n / (n + altitude)));
        }
        var latitudeDeg = lat * 180 / Math.PI;
        var longitudeDeg = NormalizeLongitude(lon * 180 / Math.PI);
        if (!double.IsFinite(latitudeDeg) || !double.IsFinite(longitudeDeg) || !double.IsFinite(altitude))
            return new GeodeticPoint(0, 0, 0);
        return new GeodeticPoint(latitudeDeg, longitudeDeg, altitude);
    }

    public static (double X, double Y, double Z) GeodeticToEcef(GeodeticPoint p)
    {
        var lat = p.LatitudeDeg * Math.PI / 180; var lon = p.LongitudeDeg * Math.PI / 180;
        var e2 = Flattening * (2 - Flattening); var sin = Math.Sin(lat);
        var n = EquatorialRadiusKm / Math.Sqrt(1 - e2 * sin * sin);
        return ((n + p.AltitudeKm) * Math.Cos(lat) * Math.Cos(lon), (n + p.AltitudeKm) * Math.Cos(lat) * Math.Sin(lon), (n * (1 - e2) + p.AltitudeKm) * sin);
    }

    public static LookAngles LookAngle(GeodeticPoint observer, (double X, double Y, double Z) satellite)
    {
        var o = GeodeticToEcef(observer); var dx = satellite.X - o.X; var dy = satellite.Y - o.Y; var dz = satellite.Z - o.Z;
        var lat = observer.LatitudeDeg * Math.PI / 180; var lon = observer.LongitudeDeg * Math.PI / 180;
        var east = -Math.Sin(lon) * dx + Math.Cos(lon) * dy;
        var north = -Math.Sin(lat) * Math.Cos(lon) * dx - Math.Sin(lat) * Math.Sin(lon) * dy + Math.Cos(lat) * dz;
        var up = Math.Cos(lat) * Math.Cos(lon) * dx + Math.Cos(lat) * Math.Sin(lon) * dy + Math.Sin(lat) * dz;
        var range = Math.Sqrt(east * east + north * north + up * up);
        var az = Math.Atan2(east, north) * 180 / Math.PI; if (az < 0) az += 360;
        return new LookAngles(az, Math.Asin(up / range) * 180 / Math.PI, range);
    }

    public static double FootprintRadiusKm(double altitudeKm) => EquatorialRadiusKm * Math.Acos(EquatorialRadiusKm / (EquatorialRadiusKm + Math.Max(0, altitudeKm)));
    public static double JulianDate(DateTimeOffset utc) => utc.ToUniversalTime().ToUnixTimeMilliseconds() / 86400000d + 2440587.5;
    public static double Gstime(double jdut1) { var t = (jdut1 - 2451545.0) / 36525.0; var seconds = -6.2e-6 * t * t * t + 0.093104 * t * t + (876600d * 3600d + 8640184.812866) * t + 67310.54841; var r = seconds * Math.PI / 43200.0 % (2 * Math.PI); return r < 0 ? r + 2 * Math.PI : r; }
    private static double NormalizeLongitude(double lon) => (lon + 540) % 360 - 180;
}
