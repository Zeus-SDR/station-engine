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

using System.Text.Json.Serialization;

namespace Zeus.Contracts;

/// <summary>Stream identifiers carried in PublicSpectrum (0x41) and PublicAudioPcm (0x43).</summary>
public static class PublicStreamId
{
    /// <summary>Read-only mirror of the operator's RX1 view / listen-along audio.</summary>
    public const byte OperatorView = 0x00;

    /// <summary>Protocol-2 raw-ADC wideband overview (Phase 2).</summary>
    public const byte WidebandOverview = 0xF0;

    /// <summary>First guest receiver slot (Phase 3); slot n is GuestBase + n.</summary>
    public const byte GuestBase = 0xE0;

    public const int MaxGuestSlots = 8;

    /// <summary>First virtual receiver slot (Phase 4); slot n is VrxBase + n.</summary>
    public const byte VrxBase = 0xC0;

    public const int MaxVrxSlots = 8;

    public static bool IsGuest(byte streamId) => streamId >= GuestBase && streamId < GuestBase + MaxGuestSlots;

    public static bool IsVrx(byte streamId) => streamId >= VrxBase && streamId < VrxBase + MaxVrxSlots;
}

/// <summary>Union of listener demand, sent host → engine (MsgType 0x28).</summary>
[Flags]
public enum ListenerFeedDemandFlags : byte
{
    None = 0,
    OperatorView = 1,
    ListenAlong = 2,
    Wideband = 4,
}

/// <summary>
/// Operator dial summary shown to listeners. <see cref="Dbm"/> (Phase 4) is the
/// operator RX1 S-meter; it is omitted from the JSON when unknown or while the
/// station transmits, so the pre-Phase-4 shape stays byte-identical.
/// </summary>
public sealed record PublicOperatorDto(
    long Hz,
    string Mode,
    int FilterLo,
    int FilterHi,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Dbm = null);

/// <summary>
/// Station status for listeners (MsgType 0x42, camelCase JSON). The engine
/// fills tx/protocol/op/wideband; the host fills listeners/maxListeners and,
/// while it offers virtual receivers (Phase 4), vrxFree before forwarding.
/// <see cref="VrxFree"/> is omitted from the JSON when null.
/// </summary>
public sealed record PublicStationStatusDto(
    int V,
    bool Tx,
    string Protocol,
    PublicOperatorDto? Op,
    bool Wideband,
    int Listeners,
    int MaxListeners,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? VrxFree = null);
