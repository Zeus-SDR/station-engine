// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

/// <summary>Immutable process policy for an isolated receiver engine.</summary>
public sealed record StationReceiveOnlyPolicy(bool Enabled = false)
{
    public const long MaximumFrequencyHz = 1_766_000_000;
    public const string TransmitDenied = "This station engine is receive-only.";
}
