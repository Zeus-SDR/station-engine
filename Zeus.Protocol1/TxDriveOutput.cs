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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Protocol1;

/// <summary>
/// One drive update: the C1 register byte and the TX IQ amplitude that goes
/// with it. Stored and published as one value so a frame never pairs a new
/// byte with an old scale. PureSignal uses this same pair. On Hermes-Lite 2
/// the DDC3 reference is compensated by <c>1/IqScale</c>, because that tap
/// is the DAC data before the step attenuator.
/// </summary>
/// <param name="DriveByte">DriveFilter C1.</param>
/// <param name="IqScale">TX IQ amplitude in 0..1. 0 silences the payload.</param>
public readonly record struct TxDriveOutput(byte DriveByte, double IqScale)
{
    /// <summary>Drive register 0 and silent IQ. Safety inhibit and a zero power target.</summary>
    public static readonly TxDriveOutput Off = new(0, 0.0);

    /// <summary>
    /// Every board except Hermes-Lite 2: the byte is the whole drive
    /// command, IQ is unity, and a zero byte stays silent.
    /// </summary>
    public static TxDriveOutput FromLegacyByte(byte driveByte) =>
        new(driveByte, driveByte == 0 ? 0.0 : 1.0);
}
