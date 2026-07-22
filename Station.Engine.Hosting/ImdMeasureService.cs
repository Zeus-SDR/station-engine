// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// The two-tone IMD measurement is ported from Thetis display.cs
// two_tone_readings + findImd (MW0LGE). Thetis is the authoritative reference;
// see ATTRIBUTIONS.md for the full provenance statement.

using System.Text.Json.Serialization;

namespace Zeus.Server;

public sealed record ImdMeasureRequest
{
    public double?[]? Db { get; init; } = [];
    public double? Width { get; init; }
    public double? CenterHz { get; init; }
    public double? HzPerPixel { get; init; }
    public double? ExpectedToneSpacingHz { get; init; }
}

public sealed record ImdProduct(
    double LowerDbm,
    double UpperDbm,
    double Dbc,
    double LowerHz,
    double UpperHz);

public sealed record ImdMeasureResult
{
    public bool Ok { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? F0LowerDbm { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? F0UpperDbm { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? F0LowerHz { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? F0UpperHz { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ToneSpacingHz { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImdProduct? Imd3 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImdProduct? Imd5 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Oip3 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Oip5 { get; init; }
}

/// <summary>
/// Locates two fundamentals and their third/fifth-order products in a
/// panadapter spectrum, preserving the Thetis-derived measurement behavior.
/// </summary>
public sealed class ImdMeasureService
{
    // Thetis requires pixel_diff > 10 before placing the IMD products.
    private const int MinToneSeparationPixels = 11;
    private const int MaxExpectedPairCandidates = 24;

    public ImdMeasureResult Measure(ImdMeasureRequest input)
    {
        var db = input.Db ?? [];
        var width = ValidInputWidth(input);
        if (width == 0)
            return Miss("no spectrum");

        var peaks = FindPeaks(db, width);
        if (peaks.Count < 2)
            return Miss("no signal");

        var expectedPixels = ExpectedSpacingPixels(input);
        var centerHz = input.CenterHz ?? double.NaN;
        var hzPerPixel = input.HzPerPixel ?? double.NaN;
        if (expectedPixels is not null)
        {
            var candidates = ExpectedFundamentalPairs(peaks, expectedPixels.Value);
            if (candidates.Count == 0)
                return Miss("two-tone spacing not found — adjust span");

            var best = candidates
                .Select(pair => MeasurePair(peaks, pair, centerHz, hzPerPixel, width))
                .Where(candidate => candidate is not null)
                .OrderByDescending(candidate => candidate!.Score)
                .FirstOrDefault();

            return best?.Readout ?? Miss("IMD peaks off-screen — widen span");
        }

        var pair = DefaultFundamentalPair(peaks);
        if (pair is null)
            return Miss("tones merged — increase zoom");

        return MeasurePair(peaks, pair, centerHz, hzPerPixel, width)?.Readout
            ?? Miss("IMD peaks off-screen — widen span");
    }

    private static int ValidInputWidth(ImdMeasureRequest input)
    {
        var width = input.Width ?? double.NaN;
        var centerHz = input.CenterHz ?? double.NaN;
        var hzPerPixel = input.HzPerPixel ?? double.NaN;
        if (!double.IsFinite(width) ||
            Math.Truncate(width) != width ||
            width < 16 ||
            width > (input.Db?.Length ?? 0) ||
            !double.IsFinite(centerHz) ||
            !double.IsFinite(hzPerPixel) ||
            hzPerPixel <= 0)
        {
            return 0;
        }

        return (int)width;
    }

    private static List<Peak> FindPeaks(IReadOnlyList<double?> db, int width)
    {
        var peaks = new List<Peak>();
        for (var i = 1; i < width - 1; i++)
        {
            var value = db[i] ?? double.NaN;
            var previous = db[i - 1] ?? double.NaN;
            var next = db[i + 1] ?? double.NaN;
            if (value > previous && value >= next && double.IsFinite(value))
                peaks.Add(new Peak(i, value));
        }

        return peaks.OrderByDescending(peak => peak.Dbm).ToList();
    }

    private static Peak? FindImd(
        IReadOnlyList<Peak> group,
        int imd,
        double pixelJump,
        double offset,
        bool low)
    {
        var jump = (imd - 1) / 2.0;
        var estimate = low ? offset - jump * pixelJump : offset + jump * pixelJump;
        var searchRange = pixelJump / 4.0;
        Peak? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var peak in group)
        {
            var distance = Math.Abs(peak.X - estimate);
            if (distance <= searchRange &&
                (best is null || peak.Dbm > best.Dbm ||
                 (peak.Dbm == best.Dbm && distance < bestDistance)))
            {
                best = peak;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static FundamentalPair PairFromPeaks(Peak a, Peak b, double spacingPenaltyPixels = 0) =>
        a.X <= b.X
            ? new FundamentalPair(a, b, spacingPenaltyPixels)
            : new FundamentalPair(b, a, spacingPenaltyPixels);

    private static FundamentalPair? DefaultFundamentalPair(IReadOnlyList<Peak> peaks)
    {
        var first = peaks[0];
        for (var i = 1; i < peaks.Count; i++)
        {
            var peak = peaks[i];
            if (Math.Abs(peak.X - first.X) >= MinToneSeparationPixels)
                return PairFromPeaks(first, peak);
        }

        return null;
    }

    private static double? ExpectedSpacingPixels(ImdMeasureRequest input)
    {
        var expectedHz = input.ExpectedToneSpacingHz;
        if (expectedHz is null || !double.IsFinite(expectedHz.Value) || expectedHz <= 0)
            return null;

        var pixels = Math.Abs(expectedHz.Value / (input.HzPerPixel ?? double.NaN));
        return pixels >= MinToneSeparationPixels ? pixels : null;
    }

    private static List<FundamentalPair> ExpectedFundamentalPairs(
        IReadOnlyList<Peak> peaks,
        double expectedPixels)
    {
        var tolerancePixels = Math.Max(3, expectedPixels * 0.08);
        var searchCount = Math.Min(peaks.Count, MaxExpectedPairCandidates);
        var pairs = new List<FundamentalPair>();
        for (var i = 0; i < searchCount; i++)
        {
            var a = peaks[i];
            for (var j = i + 1; j < searchCount; j++)
            {
                var b = peaks[j];
                var distancePixels = Math.Abs(a.X - b.X);
                var spacingPenaltyPixels = Math.Abs(distancePixels - expectedPixels);
                if (distancePixels >= MinToneSeparationPixels && spacingPenaltyPixels <= tolerancePixels)
                    pairs.Add(PairFromPeaks(a, b, spacingPenaltyPixels));
            }
        }

        return pairs
            .OrderBy(pair => pair.SpacingPenaltyPixels)
            .ThenByDescending(pair => Math.Min(pair.Low.Dbm, pair.High.Dbm))
            .ToList();
    }

    private static CandidateReadout? MeasurePair(
        IReadOnlyList<Peak> peaks,
        FundamentalPair pair,
        double centerHz,
        double hzPerPixel,
        int width)
    {
        var lowX = pair.Low.X;
        var highX = pair.High.X;
        var pixelDifference = highX - lowX;
        if (pixelDifference <= 10)
            return null;

        var middleX = lowX + pixelDifference / 2.0;
        var lowGroup = peaks.Where(peak => peak.X < middleX).ToList();
        var highGroup = peaks.Where(peak => peak.X > middleX).ToList();

        var fundamentalLow = FindImd(lowGroup, 1, pixelDifference, lowX, true);
        var fundamentalHigh = FindImd(highGroup, 1, pixelDifference, highX, false);
        var imd3Low = FindImd(lowGroup, 3, pixelDifference, lowX, true);
        var imd3High = FindImd(highGroup, 3, pixelDifference, highX, false);
        var imd5Low = FindImd(lowGroup, 5, pixelDifference, lowX, true);
        var imd5High = FindImd(highGroup, 5, pixelDifference, highX, false);
        if (fundamentalLow is null || fundamentalHigh is null ||
            imd3Low is null || imd3High is null || imd5Low is null || imd5High is null)
        {
            return null;
        }

        var weakerFundamental = Math.Min(fundamentalLow.Dbm, fundamentalHigh.Dbm);
        var imd3Maximum = Math.Max(imd3Low.Dbm, imd3High.Dbm);
        var imd5Maximum = Math.Max(imd5Low.Dbm, imd5High.Dbm);
        var imd3Dbc = weakerFundamental - imd3Maximum;
        var imd5Dbc = weakerFundamental - imd5Maximum;
        var oip3 = weakerFundamental + imd3Dbc / 2;
        var oip5 = weakerFundamental + imd5Dbc / 2;
        double Hz(int x) => centerHz + (x - width / 2.0) * hzPerPixel;

        var balancePenalty = Math.Abs(fundamentalLow.Dbm - fundamentalHigh.Dbm) * 0.2;
        var productScore = Math.Max(-20, Math.Min(60, imd3Dbc)) * 0.25
            + Math.Max(-20, Math.Min(80, imd5Dbc)) * 0.1;
        var readout = new ImdMeasureResult
        {
            Ok = true,
            F0LowerDbm = fundamentalLow.Dbm,
            F0UpperDbm = fundamentalHigh.Dbm,
            F0LowerHz = Hz(fundamentalLow.X),
            F0UpperHz = Hz(fundamentalHigh.X),
            ToneSpacingHz = Math.Abs(Hz(fundamentalHigh.X) - Hz(fundamentalLow.X)),
            Imd3 = new ImdProduct(imd3Low.Dbm, imd3High.Dbm, imd3Dbc, Hz(imd3Low.X), Hz(imd3High.X)),
            Imd5 = new ImdProduct(imd5Low.Dbm, imd5High.Dbm, imd5Dbc, Hz(imd5Low.X), Hz(imd5High.X)),
            Oip3 = oip3,
            Oip5 = oip5,
        };

        return new CandidateReadout(
            readout,
            weakerFundamental + productScore - balancePenalty - pair.SpacingPenaltyPixels);
    }

    private static ImdMeasureResult Miss(string reason) => new() { Ok = false, Reason = reason };

    private sealed record Peak(int X, double Dbm);
    private sealed record FundamentalPair(Peak Low, Peak High, double SpacingPenaltyPixels);
    private sealed record CandidateReadout(ImdMeasureResult Readout, double Score);
}
