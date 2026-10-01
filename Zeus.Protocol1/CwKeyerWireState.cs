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

namespace Zeus.Protocol1;

/// <summary>
/// Per-frame TX I/Q treatment that keeps the legacy P1 gateware's CW keyer
/// inputs from being driven by host audio. Chosen by
/// <see cref="CwKeyerWireState"/> (TX-loop thread) from what the gateware's
/// internal_CW latch has actually been sent, carried into
/// <see cref="ControlFrame"/> on the per-frame <see cref="ControlFrame.CcState"/>.
/// </summary>
internal enum KeyerIqGuard
{
    /// <summary>Legacy encoding: clear only the I/Q LSBs (the HL2 CWX
    /// workaround); every caller that never sets the field gets this.</summary>
    None,
    /// <summary>The gateware internal_CW latch is or may be armed: strip
    /// I[2:0] — the dot/dash/CWX keyer inputs on every legacy P1 gateware
    /// (Hermes.v:1084-1089, Angelia.v:1241, Orion.v:1315, Orion-MkII
    /// Orion.v:1404, HermesC10 Hermes.v:1095, Metis.v:970, HermesII
    /// Hermes.v:912) — so streamed audio can never press them. Q keeps the
    /// legacy LSB mask.</summary>
    MaskKeyerBits,
    /// <summary>The gateware keyer is armed and owns CW (hardware paddle):
    /// write zero for every I/Q sample. Thetis-faithful — while cw_enable is
    /// set Thetis replaces the whole sample with just the keyer bits, which
    /// are always zero from a host that never self-keys
    /// (networkproto1.c:738-741). The IQ source is still drained so ring
    /// pacing is unchanged.</summary>
    Silence,
}

/// <summary>
/// Tracks the gateware's internal_CW latch (C&amp;C 0x0F / wire 0x1E, C1[0]) as
/// last <em>sent</em> on the wire: Unknown / Armed / Disarmed. Owned by
/// <see cref="Protocol1Client"/> and called only on the TX-loop thread. A
/// packet is the even USB frame, then the odd one.
///
/// While the latch is armed, every legacy HPSDR P1 gateware reads the host TX
/// I sample's three low bits as keyer inputs, so a MOX frame of mic audio can
/// key the radio by itself (issue #2644). The latch is written only by a
/// CwControl frame; it becomes known to have changed only when the datagram
/// carrying that frame has been sent. <see cref="BeginPacket"/> starts from
/// the committed latch and drops whatever the previous packet left pending;
/// frames in the open packet see an earlier same-packet CwControl frame's
/// value; <see cref="Commit"/> promotes the pending value after SendTo
/// accepts the datagram. <see cref="Reset"/> returns the latch to Unknown at
/// stream start: a previous session may have left it set, and P1 gateware
/// clears it only at power-up.
///
/// The PS-armed 16-phase rotation never emits CwControl, so a committed-Armed
/// value can go stale for a whole PS-armed period; it keeps resolving to
/// <see cref="KeyerIqGuard.MaskKeyerBits"/>. Unknown (stream start until the
/// first committed CwControl) masks as well: the gateware latch may still be
/// armed from a previous session (it resets only at power-up).
///
/// Disarmed is trusted only after TWO consecutive committed CwControl disarm
/// frames: UDP has no ack, so a single lost disarm datagram would otherwise
/// open the I[2:0] window to a still-armed keyer until the next rotation came
/// around. CwControl rotates every ~18 ms, so the second disarm costs ~36 ms
/// of I[2:0] masking (~ -78 dBFS); without an ack, full certainty is
/// impossible — this bounds the exposure to one lost datagram. An arm commit
/// resets the streak and becomes Armed immediately (the operator's paddle must
/// never wait for a second frame).
/// </summary>
internal sealed class CwKeyerWireState
{
    private enum Latch
    {
        Unknown,
        Armed,
        Disarmed,
    }

    private Latch _committed = Latch.Unknown;
    private int _committedDisarmStreak;
    private Latch _pending = Latch.Unknown;
    private int _pendingDisarmStreak;
    private bool _packetOpen;

    /// <summary>
    /// Start one packet from the committed latch. An uncommitted pending value
    /// from the previous packet is discarded.
    /// </summary>
    internal void BeginPacket()
    {
        _pending = _committed;
        _pendingDisarmStreak = _committedDisarmStreak;
        _packetOpen = true;
    }

    /// <summary>
    /// Keyer-bit guard for one USB frame carrying <paramref name="register"/>.
    /// The decision sees the latch as of the earlier frames of this packet; a
    /// CwControl frame then records this frame's own
    /// <c>state.CwKeyerEnabled</c> as the pending latch (the gateware applies
    /// it only once the datagram arrives, so a frame never trusts its own
    /// C&amp;C payload). The rotation emits at most one CwControl frame per
    /// packet, so each CwControl frame counts as one send attempt. Called with
    /// no packet open, the frame commits its own CwControl write immediately.
    /// </summary>
    internal KeyerIqGuard GuardForFrame(ControlFrame.CcRegister register, in ControlFrame.CcState state)
    {
        Latch latch = _packetOpen ? _pending : _committed;
        int disarmStreak = _packetOpen ? _pendingDisarmStreak : _committedDisarmStreak;
        var guard = Decide(latch, disarmStreak, in state);
        if (register == ControlFrame.CcRegister.CwControl)
        {
            Latch next;
            int nextStreak;
            if (state.CwKeyerEnabled)
            {
                next = Latch.Armed;
                nextStreak = 0;
            }
            else
            {
                next = Latch.Disarmed;
                // Saturate: only "at least two" matters, and an uncapped
                // count would wrap after ~1.2 years of continuous streaming.
                nextStreak = Math.Min(disarmStreak + 1, 2);
            }
            if (_packetOpen)
            {
                _pending = next;
                _pendingDisarmStreak = nextStreak;
            }
            else
            {
                _committed = next;
                _committedDisarmStreak = nextStreak;
            }
        }
        return guard;
    }

    /// <summary>Promote the open packet's latch. No open packet is a no-op.</summary>
    internal void Commit()
    {
        if (!_packetOpen) return;
        _committed = _pending;
        _committedDisarmStreak = _pendingDisarmStreak;
        _packetOpen = false;
    }

    /// <summary>Forget the latch and the disarm streak. Frames mask until two
    /// consecutive committed CwControl disarms.</summary>
    internal void Reset()
    {
        _committed = Latch.Unknown;
        _committedDisarmStreak = 0;
        _pending = Latch.Unknown;
        _pendingDisarmStreak = 0;
        _packetOpen = false;
    }

    private static KeyerIqGuard Decide(Latch latch, int disarmStreak, in ControlFrame.CcState state)
    {
        // HL2's gateware reads only I[0] as CWX (dsopenhpsdr1.v:360) and the
        // legacy LSB mask already covers it — HL2 bytes stay exactly as today.
        if (state.Board == HpsdrBoardKind.HermesLite2)
            return KeyerIqGuard.None;
        // The gateware keyer is armed on the wire AND the host still wants it
        // armed: in production this is only a hardware-paddle transmission, so
        // the FPGA keyer is the only CW source and host IQ must not reach air.
        if (latch == Latch.Armed && state.CwKeyerEnabled)
            return KeyerIqGuard.Silence;
        // The latch is provably disarmed — two consecutive committed disarm
        // frames — and the host is not asking for the keyer: legacy encoding.
        if (latch == Latch.Disarmed && disarmStreak >= 2 && !state.CwKeyerEnabled)
            return KeyerIqGuard.None;
        // Everything else masks: latch armed or unknown (stale-armed under the
        // PS rotation, which never emits CwControl, or left armed by a previous
        // session — P1 gateware resets internal_CW only at power-up), a single
        // committed disarm that one lost UDP datagram could have undone, or an
        // arm/disarm transition in flight. Strip the keyer input bits; host IQ
        // otherwise passes unchanged. Unknown lasts only from TX-loop start
        // until the first CwControl commits (≤ 7 packets, ~18 ms, in the normal
        // rotation; PureSignal is forced off at connect, so the normal rotation
        // runs first), and masking costs at most 3 LSBs of I (~ -78 dBFS).
        return KeyerIqGuard.MaskKeyerBits;
    }
}
