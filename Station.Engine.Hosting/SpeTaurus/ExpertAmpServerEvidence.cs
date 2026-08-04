// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Globalization;

namespace Zeus.Server.SpeTaurus;

internal static class ExpertAmpServerEvidence
{
    internal static readonly TimeSpan MaximumContactAge = TimeSpan.FromSeconds(5);

    internal static bool HasFreshProtocolStatus(
        string? source,
        string? confidence,
        string? provenance,
        string? lastContactAt)
    {
        if (!string.Equals(source, "serial", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(confidence, "protocol-native", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(provenance, "status-poll", StringComparison.OrdinalIgnoreCase)
            || !TryParseContact(lastContactAt, out var contact))
            return false;

        var age = DateTimeOffset.UtcNow - contact.ToUniversalTime();
        return age >= TimeSpan.Zero && age <= MaximumContactAge;
    }

    internal static bool TryParseContact(string? value, out DateTimeOffset contact) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out contact);
}
