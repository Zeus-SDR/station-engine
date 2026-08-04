// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Contracts;

/// <summary>
/// Operator-configured additive transverter frequency conversion. Zeus keeps
/// every radio and DSP frequency in physical IF Hz; this setting controls only
/// the RF frequency presented by operator-facing displays and TCI.
/// </summary>
public sealed record TransverterSettingsDto(
    bool Enabled = false,
    long IfFrequencyHz = 28_000_000,
    long RfFrequencyHz = 144_000_000);

/// <summary>Replace the persisted transverter conversion settings.</summary>
public sealed record TransverterSettingsSetRequest(
    bool Enabled,
    long IfFrequencyHz,
    long RfFrequencyHz,
    string RadioKey = "default",
    string LayoutId = "default");
