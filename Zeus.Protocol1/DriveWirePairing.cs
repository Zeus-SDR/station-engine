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
/// Pairs a USB frame's TX IQ scale with the DriveFilter byte actually sent.
/// Owned by <see cref="Protocol1Client"/> and called only on the TX-loop
/// thread. A packet is the even USB frame, then the odd one.
///
/// Hermes-Lite 2 steps its TX attenuator only when a DriveFilter frame goes
/// out, while the IQ scale is applied on the very next frame. Until that
/// register write, an HL2 MOX frame keeps the scale that was paired with the
/// byte already written, whether or not PureSignal is armed. The first MOX
/// frames stay silent until a DriveFilter frame has been sent. Every other
/// board and every non-MOX frame uses the snapshot scale. DriveFilter frames
/// still record the pair in those cases, so a later byte change does not
/// resume a scale that belonged to an older register byte.
///
/// <see cref="BeginPacket"/> starts from the committed pair and drops whatever
/// the previous packet left pending. Frames in the open packet see that
/// committed pair plus DriveFilter writes and same-byte scale updates from
/// earlier frames of the same packet, and the result stays pending until
/// <see cref="Commit"/>. The TX loop commits only after the datagram is sent.
/// <see cref="Reset"/> returns the pairing to unknown at stream start. The
/// non-MOX rotation emits DriveFilter within 7 packets, so a reset only
/// silences gated MOX frames in the first rotation after a start.
/// </summary>
internal sealed class DriveWirePairing
{
    private byte? _wireByte;
    private double _wireScale;
    private byte? _pendingByte;
    private double _pendingScale;
    private bool _packetOpen;

    /// <summary>
    /// Start one packet from the committed pair. An uncommitted pending pair
    /// from the previous packet is discarded.
    /// </summary>
    internal void BeginPacket()
    {
        _pendingByte = _wireByte;
        _pendingScale = _wireScale;
        _packetOpen = true;
    }

    /// <summary>
    /// IQ amplitude for one USB frame. <paramref name="register"/> is the
    /// register this frame carries. Inside an open packet the pair this frame
    /// produces stays pending; a call with no packet open commits its own pair.
    /// </summary>
    internal double ScaleForFrame(ControlFrame.CcRegister register, in ControlFrame.CcState state)
    {
        if (_packetOpen)
            return Apply(register, in state, ref _pendingByte, ref _pendingScale);
        return Apply(register, in state, ref _wireByte, ref _wireScale);
    }

    /// <summary>Promote the open packet's pair. No open packet is a no-op.</summary>
    internal void Commit()
    {
        if (!_packetOpen) return;
        _wireByte = _pendingByte;
        _wireScale = _pendingScale;
        _packetOpen = false;
    }

    /// <summary>Forget the wire pair. Gated frames stay silent until the next sent DriveFilter.</summary>
    internal void Reset()
    {
        _wireByte = null;
        _wireScale = 0;
        _pendingByte = null;
        _pendingScale = 0;
        _packetOpen = false;
    }

    private static double Apply(
        ControlFrame.CcRegister register,
        in ControlFrame.CcState state,
        ref byte? wireByte,
        ref double wireScale)
    {
        bool gating = state.Board == HpsdrBoardKind.HermesLite2
            && state.Mox;
        double target = state.TxIqScale ?? (state.DriveLevel == 0 ? 0.0 : 1.0);

        if (register == ControlFrame.CcRegister.DriveFilter)
        {
            wireByte = state.DriveLevel;
            wireScale = target;
            return target;
        }

        if (!gating)
            return target;

        // The byte already written is still the live attenuator, so this
        // target scale is what the radio is applying. Remember it: the next
        // frame may change the byte before another DriveFilter goes out.
        if (wireByte is byte known && known == state.DriveLevel)
        {
            wireScale = target;
            return target;
        }

        if (wireByte is not null)
            return wireScale;

        return 0;
    }
}
