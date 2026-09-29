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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server.PublicListen;

/// <summary>
/// Turns one float dB display row into the u8 codes of a
/// <see cref="PublicSpectrumFrame"/> (docs/designs/public-listening.md):
/// max-decimation to the listener bin count, so narrow carriers survive, then
/// a per-frame floor and a fixed <see cref="PublicSpectrumFrame.DefaultStepDb"/>
/// step. Pure functions — safe on any producer thread.
/// </summary>
public static class PublicSpectrumEncoder
{
    /// <summary>Listener bin count for a source row: never upsampled, never above the wire cap.</summary>
    public static int ResolveBins(int sourceWidth, int targetBins) =>
        Math.Clamp(Math.Min(sourceWidth, targetBins), 0, PublicSpectrumFrame.MaxBins);

    /// <summary>
    /// Max-decimate <paramref name="source"/> into <paramref name="destination"/>.
    /// Destination bin i covers source samples [i·W/N, (i+1)·W/N). Non-finite
    /// source values are ignored; a group with no finite value yields NaN.
    /// </summary>
    public static void DecimateMax(ReadOnlySpan<float> source, Span<float> destination)
    {
        int width = source.Length;
        int bins = destination.Length;
        if (bins == 0) return;
        if (width == 0)
        {
            destination.Fill(float.NaN);
            return;
        }

        for (int i = 0; i < bins; i++)
        {
            int start = (int)((long)i * width / bins);
            int end = (int)((long)(i + 1) * width / bins);
            if (end <= start) end = Math.Min(start + 1, width);
            float max = float.NegativeInfinity;
            bool any = false;
            for (int j = start; j < end; j++)
            {
                float v = source[j];
                if (!float.IsFinite(v)) continue;
                if (!any || v > max) max = v;
                any = true;
            }
            destination[i] = any ? max : float.NaN;
        }
    }

    /// <summary>
    /// Quantize dB values to u8 codes. <c>floorDb</c> is the frame minimum
    /// rounded down to a multiple of <paramref name="stepDb"/>, raised when
    /// needed so the frame maximum still fits in code 255. Non-finite values
    /// encode as code 0. Returns NaN (and writes nothing useful) when the row
    /// holds no finite value.
    /// </summary>
    public static float Quantize(ReadOnlySpan<float> db, Span<byte> codes, float stepDb = PublicSpectrumFrame.DefaultStepDb)
    {
        if (codes.Length < db.Length) throw new ArgumentException("codes shorter than db row", nameof(codes));
        if (!(stepDb > 0f)) throw new ArgumentOutOfRangeException(nameof(stepDb));

        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        foreach (var v in db)
        {
            if (!float.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (min > max)
        {
            codes[..db.Length].Clear();
            return float.NaN;
        }

        float floor = MathF.Floor(min / stepDb) * stepDb;
        if ((max - floor) / stepDb > 255f)
            floor = MathF.Ceiling((max - 255f * stepDb) / stepDb) * stepDb;

        for (int i = 0; i < db.Length; i++)
        {
            float v = db[i];
            if (!float.IsFinite(v))
            {
                codes[i] = 0;
                continue;
            }
            float code = MathF.Round((v - floor) / stepDb, MidpointRounding.AwayFromZero);
            codes[i] = (byte)Math.Clamp(code, 0f, 255f);
        }
        return floor;
    }

    /// <summary>
    /// Build a waterfall-row-only frame for one source row. Returns false when
    /// the row carries no finite value (nothing worth sending).
    /// </summary>
    public static bool TryEncodeRow(
        ReadOnlySpan<float> rowDb,
        int targetBins,
        uint seq,
        double tsUnixMs,
        byte streamId,
        long centerHz,
        float sourceHzPerPixel,
        out PublicSpectrumFrame frame)
    {
        frame = default;
        int bins = ResolveBins(rowDb.Length, targetBins);
        if (bins <= 0) return false;

        var decimated = bins == rowDb.Length ? rowDb : Decimated(rowDb, bins);
        var codes = new byte[bins];
        float floor = Quantize(decimated, codes);
        if (float.IsNaN(floor)) return false;

        float hzPerBin = (float)((double)sourceHzPerPixel * rowDb.Length / bins);
        frame = new PublicSpectrumFrame(
            seq, tsUnixMs, streamId, PublicSpectrumFlags.WfPresent,
            (ushort)bins, centerHz, hzPerBin, floor, PublicSpectrumFrame.DefaultStepDb,
            ReadOnlyMemory<byte>.Empty, codes);
        return true;
    }

    /// <summary>A data-free frame telling listeners the station is transmitting.</summary>
    public static PublicSpectrumFrame TransmitHold(
        uint seq, double tsUnixMs, byte streamId, long centerHz, float hzPerBin) =>
        new(seq, tsUnixMs, streamId, PublicSpectrumFlags.TransmitHold, 0, centerHz, hzPerBin,
            0f, PublicSpectrumFrame.DefaultStepDb, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

    private static float[] Decimated(ReadOnlySpan<float> rowDb, int bins)
    {
        var buffer = new float[bins];
        DecimateMax(rowDb, buffer);
        return buffer;
    }
}
