// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Contracts;

// Serial PTT switch input (Thetis "Bit Bang PTT" parity). The operator assigns
// a COM/serial port whose modem status pins are wired to a footswitch/hand
// switch; Zeus asserts RTS+DTR while the port is open (that supplies the +V
// the switch pulls the sensed pin to) and polls CTS/DSR for edges. Baud is
// irrelevant — no data channel is used — so the config carries none.
// Additive only: no new MsgType, no new WS frames (edges reuse the shared
// PttStatusFrame external-key lamp through ExternalPttService).

/// <summary>Persisted serial-PTT config. Defaults are a fresh install: no port
/// configured, feature OFF (Thetis also defaults its bit-bang port to None);
/// both sense pins selected so a switch on either line works once a port is
/// assigned. <paramref name="PortName"/> is a free-form device path (COM5,
/// /dev/cu.usbserial-1) — virtual adapters are not always enumerable, so the
/// UI never gates assignment on the detected list.</summary>
public sealed record SerialPttConfig(
    bool Enabled = false,
    string PortName = "",
    bool SenseCts = true,
    bool SenseDsr = true)
{
    /// <summary>Fresh-install defaults; also the effective config when the
    /// store has no row yet.</summary>
    public static readonly SerialPttConfig Defaults = new();
}

/// <summary>Serial-PTT status response: the persisted config plus live port
/// state (open / last error / last asserted level) and the host's enumerable
/// serial devices for the UI's free-form port suggestions. Mirrors
/// <see cref="CatSerialStatus"/>.</summary>
public sealed record SerialPttStatus(
    SerialPttConfig Config,
    bool PortOpen,
    string? Error,
    bool Keyed,
    IReadOnlyList<string> AvailablePorts,
    DateTimeOffset GeneratedUtc,
    bool SharedWithCat = false,
    bool CtsHolding = false,
    bool DsrHolding = false,
    bool RtsAsserted = false,
    bool DtrAsserted = false);
