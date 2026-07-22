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

using Zeus.Contracts;

namespace Zeus.Server.Tci;

public static class TciStateBroadcast
{
    public static List<string> DiffCommands(StateDto? prev, StateDto next)
    {
        var cmds = new List<string>(12);

        if (Changed(prev, next, static s => s.Rx1Muted))
            cmds.Add(TciProtocol.Command("mute", next.Rx1Muted));
        if (Changed(prev, next, static s => s.Rx1Muted))
            cmds.Add(TciProtocol.Command("rx_mute", 0, next.Rx1Muted));
        if (Changed(prev, next, static s => s.Rx2Muted))
            cmds.Add(TciProtocol.Command("rx_mute", 1, next.Rx2Muted));

        if (Changed(prev, next, static s => s.RitEnabled))
            cmds.Add(TciProtocol.Command("rit_enable", 0, next.RitEnabled));
        if (Changed(prev, next, static s => (int)s.RitHz))
            cmds.Add(TciProtocol.Command("rit_offset", 0, (int)next.RitHz));
        if (Changed(prev, next, static s => s.XitEnabled))
            cmds.Add(TciProtocol.Command("xit_enable", 0, next.XitEnabled));
        if (Changed(prev, next, static s => (int)s.XitHz))
            cmds.Add(TciProtocol.Command("xit_offset", 0, (int)next.XitHz));

        if (Changed(prev, next, static s => s.Agc?.Mode ?? AgcMode.Med))
            cmds.Add(TciProtocol.Command("agc_mode", 0, TciProtocol.AgcModeToTci(next.Agc?.Mode ?? AgcMode.Med)));

        if (Changed(prev, next, static s => (s.Squelch ?? new SquelchConfig()).Enabled))
            cmds.Add(TciProtocol.Command("sql_enable", 0, (next.Squelch ?? new SquelchConfig()).Enabled));
        if (Changed(prev, next, static s => (s.Squelch ?? new SquelchConfig()).Level))
            cmds.Add(TciProtocol.Command("sql_level", 0, (next.Squelch ?? new SquelchConfig()).Level));

        if (Changed(prev, next, static s => s.VfoLocked))
            cmds.Add(TciProtocol.Command("lock", 0, next.VfoLocked));
        if (Changed(prev, next, static s => s.VfoLocked))
            cmds.Add(TciProtocol.Command("vfo_lock", 0, 0, next.VfoLocked));

        return cmds;
    }

    private static bool Changed<T>(StateDto? prev, StateDto next, Func<StateDto, T> selector) =>
        prev is null || !EqualityComparer<T>.Default.Equals(selector(prev), selector(next));
}
