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

using System.Diagnostics;

namespace Zeus.Protocol2;

/// <summary>
/// Pure predicates for the Protocol-2 TX-IQ pacing-collapse WARN (issue #3401).
/// A window is degraded when the pacer slept the default Windows quantum
/// several times in one second: at least 5 gaps over 10 ms and a pacing-phase
/// maximum of at least 10 ms.
/// </summary>
internal static class TxPacingHealth
{
    internal const int DegradedGapCount = 5;
    internal const long DegradedPacingUs = 10_000;
    internal const int WarnIntervalSeconds = 30;

    internal static bool IsDegraded(int gapsOver10Ms, long pacingUsMax) =>
        gapsOver10Ms >= DegradedGapCount && pacingUsMax >= DegradedPacingUs;

    /// <summary>
    /// Rate-limit helper. <paramref name="lastWarnTicks"/> of 0 means never
    /// warned; the first degraded window always qualifies.
    /// </summary>
    internal static bool ShouldWarn(long nowTicks, long lastWarnTicks) =>
        lastWarnTicks == 0
        || nowTicks - lastWarnTicks >= (long)WarnIntervalSeconds * Stopwatch.Frequency;
}
