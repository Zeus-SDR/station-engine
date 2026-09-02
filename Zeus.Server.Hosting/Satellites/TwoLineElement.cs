// SPDX-License-Identifier: GPL-2.0-or-later
//
// Strict CelesTrak 2LE/3LE parsing and the Zeus-facing immutable orbit model.

using System.Globalization;
#if ZEUS_PRODUCT_HOST
using Zeus.Product.Hosting.Satellites.Vallado;
namespace Zeus.Product.Hosting.Satellites;
#else
using Zeus.Server.Satellites.Vallado;
namespace Zeus.Server.Satellites;
#endif

public sealed class TwoLineElement
{
    private readonly TLE _tle;

    public TwoLineElement(string line1, string line2, string? name = null)
    {
        ValidateLine(line1, '1');
        ValidateLine(line2, '2');
        if (!line1.AsSpan(2, 5).SequenceEqual(line2.AsSpan(2, 5)))
            throw new FormatException("TLE catalog identifiers do not match.");
        _tle = new TLE(line1, line2);
        if (!string.IsNullOrEmpty(_tle.getParseErrors()))
            throw new FormatException(_tle.getParseErrors());
        Line1 = line1;
        Line2 = line2;
        Name = NormalizeName(name) ?? $"NORAD {CatalogId}";
        var year2 = int.Parse(line1.AsSpan(18, 2), CultureInfo.InvariantCulture);
        var year = year2 >= 57 ? 1900 + year2 : 2000 + year2;
        var day = double.Parse(line1.AsSpan(20, 12), CultureInfo.InvariantCulture);
        EpochUtc = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day - 1);
    }

    public string Name { get; }
    public string Line1 { get; }
    public string Line2 { get; }
    public int CatalogId => int.Parse(Line1.AsSpan(2, 5), CultureInfo.InvariantCulture);
    public DateTimeOffset EpochUtc { get; }
    public double MeanMotionFirstDerivative => _tle.getNDot();
    public double MeanMotionSecondDerivative => _tle.getNDDot();
    public double BStar => _tle.getBstar();
    public double InclinationDeg => _tle.getIncDeg();
    public double RightAscensionDeg => _tle.getRaanDeg();
    public double Eccentricity => _tle.getEcc();
    public double ArgumentPerigeeDeg => _tle.getArgpDeg();
    public double MeanAnomalyDeg => _tle.getMaDeg();
    public double MeanMotionRevolutionsPerDay => _tle.getN();
    public int RevolutionNumber => _tle.getRevNum();
    internal TLE Vallado => _tle;

    public static IReadOnlyList<TwoLineElement> ParseMany(string text) => ParseMany(text, out _);

    public static IReadOnlyList<TwoLineElement> ParseMany(string text, out int skippedCount)
    {
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<TwoLineElement>();
        skippedCount = 0;
        for (var i = 0; i < lines.Length;)
        {
            string? name = null;
            if (!lines[i].StartsWith("1 ", StringComparison.Ordinal)) name = lines[i++];
            if (i + 1 >= lines.Length)
            {
                skippedCount++;
                break;
            }
            try { result.Add(new TwoLineElement(lines[i], lines[i + 1], name)); }
            catch (FormatException) { skippedCount++; }
            i += 2;
        }
        return result;
    }

    private static string? NormalizeName(string? value)
    {
        var name = value?.Trim();
        if (name?.StartsWith("0 ", StringComparison.Ordinal) == true) name = name[2..].Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static void ValidateLine(string line, char number)
    {
        if (line is null || line.Length != 69) throw new FormatException($"TLE line {number} must contain exactly 69 characters.");
        if (line[0] != number || line[1] != ' ') throw new FormatException($"Invalid TLE line {number} prefix.");
        if (!char.IsAsciiDigit(line[68])) throw new FormatException($"Invalid TLE line {number} checksum.");
        var checksum = 0;
        for (var i = 0; i < 68; i++)
        {
            if (char.IsAsciiDigit(line[i])) checksum += line[i] - '0';
            else if (line[i] == '-') checksum++;
        }
        if (checksum % 10 != line[68] - '0') throw new FormatException($"TLE line {number} checksum mismatch.");
    }
}
public readonly record struct TemeState(double XKm, double YKm, double ZKm, double VXKmS, double VYKmS, double VZKmS)
{
    public double SpeedKmS => Math.Sqrt(VXKmS * VXKmS + VYKmS * VYKmS + VZKmS * VZKmS);
}
