// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
using System.Globalization;
using System.Text.RegularExpressions;
#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.Aprs;
#else
namespace Zeus.Server.Aprs;
#endif

public sealed record AprsPosition(string Id, string Callsign, string Name, string Kind,
    double LatitudeDeg, double LongitudeDeg, string Symbol, int Ambiguity,
    DateTimeOffset ReceivedUtc, DateTimeOffset? ReportedUtc, double? HeadingDeg,
    double? SpeedKnots, double? AltitudeM, string Comment, string Raw, bool Killed = false);

/// <summary>APRS 1.0.1 position reports. Unsupported payloads never become guessed positions.</summary>
public static class AprsParser
{
    public static AprsPosition? Parse(string raw, DateTimeOffset received)
    {
        if (raw.Length is < 10 or > 510 || raw[0] == '#' || raw.Contains('\r') || raw.Contains('\n')) return null;
        var packet = raw;
        // Unwrap one third-party packet; keep its original source and payload.
        var colon = packet.IndexOf(':');
        if (colon < 1) return null;
        if (packet[colon..].StartsWith(":}", StringComparison.Ordinal)) packet = packet[(colon + 2)..];
        colon = packet.IndexOf(':');
        var arrow = packet.IndexOf('>');
        if (arrow < 1 || colon <= arrow + 1 || colon == packet.Length - 1) return null;
        var source = packet[..arrow];
        if (!Regex.IsMatch(source, "^[A-Za-z0-9]{1,9}(-[A-Za-z0-9]{1,2})?$", RegexOptions.CultureInvariant)) return null;
        source = source.ToUpperInvariant();
        var destination = packet[(arrow + 1)..colon].Split(',')[0].Split('-')[0];
        var payload = packet[(colon + 1)..];
        var name = source; var kind = "station"; var killed = false;
        DateTimeOffset? reported = null;
        var offset = 1;
        if (payload[0] == ';')
        {
            if (payload.Length < 18 || payload[10] is not ('*' or '_')) return null;
            name = payload.Substring(1, 9).TrimEnd(); kind = "object"; killed = payload[10] == '_';
            if (!Timestamp(payload.AsSpan(11, 7), received, out reported)) return null;
            offset = 18;
        }
        else if (payload[0] == ')')
        {
            var end = payload.IndexOfAny(['!', '_'], 1);
            if (end is < 4 or > 10) return null;
            name = payload[1..end]; kind = "item"; killed = payload[end] == '_'; offset = end + 1;
        }
        else if (payload[0] is '/' or '@')
        {
            if (payload.Length < 8 || !Timestamp(payload.AsSpan(1, 7), received, out reported)) return null;
            offset = 8;
        }
        else if (payload[0] is not ('!' or '=' or '`' or '\'')) return null;
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) return null;
        var id = kind == "station" ? source : $"{source}/{kind}/{name}";
        if (killed) return new(id, source, name, kind, 0, 0, "", 0, received, reported, null, null, null, "", raw, true);
        double lat, lon; double? heading = null, speed = null, altitude = null;
        string symbol; int ambiguity = 0, used;
        if (payload[0] is '`' or '\'')
        {
            if (!MicE(destination, payload, out lat, out lon, out symbol, out heading, out speed, out ambiguity)) return null;
            offset = 0; used = 9;
        }
        else
        {
            var pos = payload[offset..];
            if (pos.Length > 0 && char.IsAsciiDigit(pos[0]))
            {
                if (pos.Length < 19 || !Coordinate(pos[..8], 2, out lat, out ambiguity)
                    || !Coordinate(pos.Substring(9, 9), 3, out lon, out var lonAmbiguity)
                    || ambiguity != lonAmbiguity || !Table(pos[8]) || !Symbol(pos[18])) return null;
                symbol = $"{pos[8]}{pos[18]}"; used = 19;
                // Weather uses this extension for wind, not station motion.
                if (symbol[1] != '_' && pos.Length >= 26 && pos[22] == '/'
                    && Digits(pos.AsSpan(19, 3)) && Digits(pos.AsSpan(23, 3)))
                {
                    var course = int.Parse(pos.AsSpan(19, 3), CultureInfo.InvariantCulture);
                    if (course <= 360) { heading = course == 0 ? null : course % 360; speed = int.Parse(pos.AsSpan(23, 3), CultureInfo.InvariantCulture); used = 26; }
                }
            }
            else
            {
                if (pos.Length < 13 || !Table(pos[0]) || !Symbol(pos[9]) || !Base91(pos.AsSpan(1, 8))) return null;
                lat = 90 - Decode91(pos.AsSpan(1, 4)) / 380926d;
                lon = -180 + Decode91(pos.AsSpan(5, 4)) / 190463d;
                symbol = $"{pos[0]}{pos[9]}"; used = 13;
                if (pos[10] != ' ')
                {
                    if (!Base91(pos.AsSpan(10, 3))) return null;
                    int c = pos[10] - 33, s = pos[11] - 33, type = pos[12] - 33;
                    if ((type & 0x18) == 0x10) altitude = Math.Pow(1.002, c * 91 + s) * 0.3048;
                    else if (c < 90 && symbol[1] != '_') { heading = c * 4; speed = Math.Pow(1.08, s) - 1; }
                }
            }
        }
        if (!double.IsFinite(lat) || !double.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180) return null;
        var comment = payload[(offset + used)..];
        var alt = Regex.Match(comment, @"/A=(-?\d{5,6})(?!\d)", RegexOptions.CultureInvariant);
        if (alt.Success && int.TryParse(alt.Groups[1].Value, CultureInfo.InvariantCulture, out var feet)) altitude = feet * 0.3048;
        // Never represent an arrival time as the time at which a remote GPS fix was taken.
        return new(id, source, name, kind, lat, lon, symbol, ambiguity, received, reported,
            heading, speed, altitude, new string(comment.Where(c => !char.IsControl(c)).ToArray()), raw);
    }

    private static bool Coordinate(string value, int degrees, out double result, out int ambiguity)
    {
        result = 0; ambiguity = 0;
        if (value.Length != degrees + 6 || value[degrees + 2] != '.' || !Digits(value.AsSpan(0, degrees))) return false;
        char hemisphere = char.ToUpperInvariant(value[^1]);
        if (degrees == 2 ? hemisphere is not ('N' or 'S') : hemisphere is not ('E' or 'W')) return false;
        var digits = value.Substring(degrees, 2) + value.Substring(degrees + 3, 2);
        var firstSpace = digits.IndexOf(' ');
        if (firstSpace >= 0)
        {
            if (digits[firstSpace..].Any(c => c != ' ') || !Digits(digits.AsSpan(0, firstSpace))) return false;
            ambiguity = 4 - firstSpace;
            digits = digits[..firstSpace] + (firstSpace == 0 ? "3000" : "5" + new string('0', 3 - firstSpace));
        }
        if (!Digits(digits.AsSpan())) return false;
        var mins = int.Parse(digits, CultureInfo.InvariantCulture) / 100d;
        if (mins >= 60) return false;
        result = int.Parse(value.AsSpan(0, degrees), CultureInfo.InvariantCulture) + mins / 60;
        if (hemisphere is 'S' or 'W') result = -result;
        return Math.Abs(result) <= (degrees == 2 ? 90 : 180);
    }

    private static bool MicE(string dest, string p, out double lat, out double lon, out string symbol,
        out double? heading, out double? speed, out int ambiguity)
    {
        lat = lon = 0; symbol = ""; heading = speed = null; ambiguity = 0;
        if (dest.Length != 6 || p.Length < 9 || !Table(p[8]) || !Symbol(p[7])) return false;
        var digits = new char[6];
        for (var i = 0; i < 6; i++)
        {
            var c = dest[i];
            if (i >= 3 && c is >= 'A' and <= 'K') return false;
            digits[i] = c switch { >= '0' and <= '9' => c, >= 'A' and <= 'J' => (char)(c - 'A' + '0'),
                >= 'P' and <= 'Y' => (char)(c - 'P' + '0'), 'K' or 'L' or 'Z' => ' ', _ => '?' };
        }
        if (!Coordinate(new string(digits, 0, 4) + "." + new string(digits, 4, 2) + (dest[3] >= 'P' ? "N" : "S"), 2, out lat, out ambiguity)) return false;
        var d = p[1] - 28 + (dest[4] >= 'P' ? 100 : 0);
        if (d is >= 180 and <= 189) d -= 80;
        else if (d is >= 190 and <= 199) d -= 190;
        var m = p[2] - 28; if (m >= 60) m -= 60;
        var h = p[3] - 28;
        if (p[1] is < (char)38 or > (char)127 || m is < 0 or >= 60 || h is < 0 or > 99 || d is < 0 or > 179) return false;
        var minutes = m + h / 100d;
        minutes = ambiguity switch { 1 => Math.Floor(minutes * 10) / 10 + .05, 2 => Math.Floor(minutes) + .5,
            3 => Math.Floor(minutes / 10) * 10 + 5, 4 => 30, _ => minutes };
        lon = (d + minutes / 60) * (dest[5] >= 'P' ? -1 : 1);
        if (p.AsSpan(4, 3).ContainsAnyInRange((char)0, (char)27)) return false;
        var s = (p[4] - 28) * 10 + (p[5] - 28) / 10; if (s >= 800) s -= 800;
        var cse = ((p[5] - 28) % 10) * 100 + p[6] - 28; if (cse >= 400) cse -= 400;
        if (s is < 0 or > 799 || cse is < 0 or > 360) return false;
        speed = s; heading = cse % 360; symbol = $"{p[8]}{p[7]}"; return true;
    }

    private static bool Timestamp(ReadOnlySpan<char> text, DateTimeOffset now, out DateTimeOffset? value)
    {
        value = null;
        if (text.Length != 7 || !Digits(text[..6])) return false;
        var a = int.Parse(text[..2], CultureInfo.InvariantCulture);
        var b = int.Parse(text.Slice(2, 2), CultureInfo.InvariantCulture);
        var c = int.Parse(text.Slice(4, 2), CultureInfo.InvariantCulture);
        if (text[6] == '/') return a is >= 1 and <= 31 && b < 24 && c < 60; // Station-local timezone is unknown.
        var candidates = new List<DateTimeOffset>();
        if (text[6] == 'h' && a < 24 && b < 60 && c < 60)
            for (var day = -1; day <= 1; day++) candidates.Add(new DateTimeOffset(now.UtcDateTime.Date.AddDays(day).AddHours(a).AddMinutes(b).AddSeconds(c), TimeSpan.Zero));
        else if (text[6] == 'z' && a is >= 1 and <= 31 && b < 24 && c < 60)
            for (var month = -1; month <= 1; month++)
            {
                var basis = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(month);
                if (a <= DateTime.DaysInMonth(basis.Year, basis.Month)) candidates.Add(basis.AddDays(a - 1).AddHours(b).AddMinutes(c));
            }
        if (candidates.Count == 0) return false;
        value = candidates.MinBy(date => Math.Abs((date - now).TotalSeconds)); return true;
    }
    private static bool Digits(ReadOnlySpan<char> s) { foreach (var c in s) if (c is < '0' or > '9') return false; return true; }
    private static bool Table(char c) => c is '/' or '\\' or >= 'A' and <= 'Z' or >= '0' and <= '9' or >= 'a' and <= 'j';
    private static bool Symbol(char c) => c is >= '!' and <= '~';
    private static bool Base91(ReadOnlySpan<char> s) { foreach (var c in s) if (c is < '!' or > '{') return false; return true; }
    private static int Decode91(ReadOnlySpan<char> s) { var v = 0; foreach (var c in s) v = v * 91 + c - 33; return v; }
}
