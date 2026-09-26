// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Conversion between the live <see cref="FmConfig"/> and a favorite's
/// <see cref="FmMemory"/> (Thetis memory recall restores RPTR / offset /
/// CTCSS / deviation for FM, console.cs:40534). The offset in a memory is
/// the magnitude for the memory's own band; on recall it is written into
/// <c>RepeaterOffsetsHz[FmConfig.RepeaterBandKey(frequency)]</c>.
/// </summary>
public static class FmMemories
{
    public static FmMemory Capture(FmConfig fm, long frequencyHz) => new(
        fm.RepeaterShift,
        fm.RepeaterReverse,
        fm.RepeaterOffsetHzFor(frequencyHz),
        fm.DeviationHz,
        fm.CtcssEnabled,
        fm.CtcssToneHz,
        fm.RxCtcssToneHz,
        fm.DcsTxEnabled,
        fm.DcsCode,
        fm.RxDcsCode,
        fm.DcsInverted,
        fm.RxToneSquelch);

    /// <summary>Merge <paramref name="memory"/> into <paramref name="live"/>;
    /// station-wide audio, limiter, burst and ARS settings are untouched.</summary>
    public static FmConfig Merge(FmConfig live, FmMemory memory, long frequencyHz)
    {
        var offsets = live.RepeaterOffsetsHz is { } map
            ? new Dictionary<string, long>(map, StringComparer.Ordinal)
            : new Dictionary<string, long>(StringComparer.Ordinal);
        offsets[FmConfig.RepeaterBandKey(frequencyHz)] =
            Math.Clamp(memory.RepeaterOffsetHz, 0, FmConfig.MaxRepeaterOffsetHz);
        return (live with
        {
            RepeaterShift = memory.RepeaterShift,
            RepeaterReverse = memory.RepeaterReverse,
            RepeaterOffsetsHz = offsets,
            DeviationHz = memory.DeviationHz,
            CtcssEnabled = memory.CtcssEnabled,
            CtcssToneHz = memory.CtcssToneHz,
            RxCtcssToneHz = memory.RxCtcssToneHz,
            DcsTxEnabled = memory.DcsTxEnabled,
            DcsCode = memory.DcsCode,
            RxDcsCode = memory.RxDcsCode,
            DcsInverted = memory.DcsInverted,
            RxToneSquelch = memory.RxToneSquelch,
        }).Normalized();
    }

    /// <summary>Clamp / snap a client-supplied memory with the same rules as
    /// <see cref="FmConfig.Normalized"/>.</summary>
    public static FmMemory Normalize(FmMemory memory, long frequencyHz) =>
        Capture(Merge(FmConfig.Default, memory, frequencyHz), frequencyHz);
}
