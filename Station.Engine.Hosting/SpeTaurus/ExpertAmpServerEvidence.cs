// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Zeus.Server.SpeTaurus;

internal static class ExpertAmpServerEvidence
{
    internal static readonly TimeSpan MaximumContactAge = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumFutureContactSkew = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The model banner the Taurus paints on row 0 of its standby LCD screen.
    /// This is the only place the amplifier ever spells its own name.
    /// </summary>
    internal const string TaurusDisplayBanner = "EXPERT 1.5K TAURUS";

    /// <summary>True when a status or display string names a Taurus outright.</summary>
    internal static bool MentionsTaurus([NotNullWhen(true)] string? value) =>
        value?.Contains("TAURUS", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>True when a display frame carries the Taurus model banner.</summary>
    internal static bool HasTaurusDisplayBanner(string? screenText) =>
        screenText?.Contains(TaurusDisplayBanner, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A Taurus answers the serial status poll with the 1.5K-FA model code, so
    /// <c>modelName</c> alone can never identify one. Only a blank or 1.5K-FA
    /// model may be upgraded to "Taurus" by display evidence — any other model
    /// is a different amplifier and must fail closed.
    /// </summary>
    internal static bool CanUseDisplayIdentityFallback(string? modelName) =>
        string.IsNullOrWhiteSpace(modelName)
        || modelName.Contains("1.5K-FA", StringComparison.OrdinalIgnoreCase);

    internal static bool HasFreshProtocolStatus(
        string? source,
        string? confidence,
        string? provenance,
        string? lastContactAt) => HasFreshProtocolStatus(
            source,
            confidence,
            provenance,
            lastContactAt,
            DateTimeOffset.UtcNow);

    internal static bool HasFreshProtocolStatus(
        string? source,
        string? confidence,
        string? provenance,
        string? lastContactAt,
        DateTimeOffset now)
    {
        if (!string.Equals(source, "serial", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(confidence, "protocol-native", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(provenance, "status-poll", StringComparison.OrdinalIgnoreCase)
            || !TryParseContact(lastContactAt, out var contact))
            return false;

        var age = now.ToUniversalTime() - contact.ToUniversalTime();
        return age >= -MaximumFutureContactSkew && age <= MaximumContactAge;
    }

    internal static bool TryParseContact(string? value, out DateTimeOffset contact) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out contact);
}
