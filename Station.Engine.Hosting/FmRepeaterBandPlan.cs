// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Automatic Repeater Shift (ARS) table: the repeater OUTPUT segments of each
/// IARU region, mapped to the shift that reaches the repeater input. Beyond
/// Thetis (which has no ARS). A frequency outside every segment returns null
/// (the caller treats that as Simplex).
///
/// Segments are inclusive on both edges and matched first-hit, so the 2 m
/// Region 2 147.000 boundary resolves to Plus (147.000–147.400 is listed
/// ahead of the 146.610–147.000 Minus segment, making 147.000 exclusive there).
/// Region 3 has no single harmonised repeater plan; it falls back to the
/// Region 2 table.
/// </summary>
public static class FmRepeaterBandPlan
{
    private readonly record struct Segment(long LowHz, long HighHz, FmRepeaterShift Shift);

    private static Segment S(double lowMHz, double highMHz, FmRepeaterShift shift) =>
        new((long)Math.Round(lowMHz * 1_000_000), (long)Math.Round(highMHz * 1_000_000), shift);

    private static readonly Segment[] Region2 =
    {
        S(29.610, 29.700, FmRepeaterShift.Minus),
        S(51.620, 51.980, FmRepeaterShift.Minus),
        S(52.500, 52.980, FmRepeaterShift.Minus),
        S(53.000, 54.000, FmRepeaterShift.Minus),
        S(145.100, 145.500, FmRepeaterShift.Minus),
        S(147.000, 147.400, FmRepeaterShift.Plus),
        S(146.610, 147.000, FmRepeaterShift.Minus),
        S(223.850, 225.000, FmRepeaterShift.Minus),
        S(442.000, 445.000, FmRepeaterShift.Plus),
        S(447.000, 450.000, FmRepeaterShift.Minus),
        S(927.000, 928.000, FmRepeaterShift.Minus),
        S(1282.000, 1288.000, FmRepeaterShift.Minus),
    };

    private static readonly Segment[] Region1 =
    {
        S(29.620, 29.700, FmRepeaterShift.Minus),
        S(51.810, 52.000, FmRepeaterShift.Minus),
        // Outputs RV48–RV63 are 145.600–145.7875; 145.575 (RV46) is included.
        S(145.575, 145.800, FmRepeaterShift.Minus),
        S(438.650, 439.425, FmRepeaterShift.Minus),
        S(1297.000, 1297.500, FmRepeaterShift.Minus),
    };

    /// <summary>The shift for a repeater output at <paramref name="hz"/> in
    /// IARU <paramref name="region"/> ("R1" | "R2" | "R3"), or null when the
    /// frequency is not in a repeater output segment.</summary>
    public static FmRepeaterShift? ShiftFor(long hz, string? region)
    {
        var table = region == "R1" ? Region1 : Region2;
        foreach (var seg in table)
        {
            if (hz >= seg.LowHz && hz <= seg.HighHz)
                return seg.Shift;
        }
        return null;
    }

    /// <summary>IARU region code ("R1" | "R2" | "R3") for a band-plan region,
    /// or null when it cannot be derived from this region alone. Uses the
    /// short code, then the id / parent id (IARU_Rn), then national prefixes
    /// (US FCC plans are Region 2).</summary>
    public static string? IaruRegionCodeFor(BandRegion? region)
    {
        if (region is null) return null;
        return CodeFrom(region.ShortCode)
            ?? CodeFrom(region.Id)
            ?? CodeFrom(region.ParentId)
            ?? (region.Id.StartsWith("US_", StringComparison.OrdinalIgnoreCase) ? "R2" : null);
    }

    private static string? CodeFrom(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToUpperInvariant();
        if (v.StartsWith("IARU_", StringComparison.Ordinal)) v = v[5..];
        return v is "R1" or "R2" or "R3" ? v : null;
    }
}
