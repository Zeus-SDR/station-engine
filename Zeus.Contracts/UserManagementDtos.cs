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

namespace Zeus.Contracts;

public sealed record ZeusPluginEntitlement(
    string PluginId,
    bool AccessAllowed,
    string SubscriptionStatus,
    DateTime? SubscriptionExpiresUtc,
    string? DenialReason = null);

public sealed record ZeusManagedPluginRecord(
    string PluginId,
    string DisplayName,
    bool SubscriptionRequired,
    int MonthlyPriceCents,
    string Currency,
    bool Active,
    string? CheckoutUrl,
    string? Notes,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record ZeusUserRecord(
    string Callsign,
    string DisplayName,
    bool AccessAllowed,
    bool IsAdmin,
    string SubscriptionStatus,
    DateTime? SubscriptionExpiresUtc,
    string PluginAccessMode,
    IReadOnlyList<ZeusPluginEntitlement> PluginEntitlements,
    bool HasQrzXmlSubscription,
    string? Grid,
    string? Notes,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? LastLoginUtc);

public sealed record ZeusUserAnalytics(
    int TotalUsers,
    int AllowedUsers,
    int BlockedUsers,
    int AdminUsers,
    int ActiveLast24Hours,
    int NewLast30Days);

public sealed record ZeusUserAuditEvent(
    long Id,
    DateTime TimestampUtc,
    string Actor,
    string Action,
    string Target,
    string? Detail = null);

public sealed record ZeusUserSession(
    bool QrzConnected,
    string? Callsign,
    string? DisplayName,
    bool AccessAllowed,
    bool IsAdmin,
    bool HasQrzXmlSubscription,
    string SubscriptionStatus,
    DateTime? SubscriptionExpiresUtc,
    string PluginAccessMode,
    IReadOnlyList<ZeusPluginEntitlement> PluginEntitlements,
    IReadOnlyList<ZeusManagedPluginRecord> ManagedPlugins,
    string? DenialReason,
    ZeusUserRecord? User);

public sealed record ZeusUsersAdminResponse(
    ZeusUserSession Session,
    IReadOnlyList<ZeusUserRecord> Users,
    IReadOnlyList<ZeusManagedPluginRecord> ManagedPlugins,
    ZeusUserAnalytics? Analytics = null,
    IReadOnlyList<ZeusUserAuditEvent>? RecentAudit = null,
    DateTime? GeneratedUtc = null,
    string ManagementMode = "local",
    string? ManagementUrl = null);

public sealed record ZeusUserUpsertRequest(
    string Callsign,
    bool? AccessAllowed = null,
    bool? IsAdmin = null,
    string? SubscriptionStatus = null,
    DateTime? SubscriptionExpiresUtc = null,
    string? PluginAccessMode = null,
    IReadOnlyList<ZeusPluginEntitlement>? PluginEntitlements = null,
    string? Notes = null,
    string? DisplayName = null);

public sealed record ZeusManagedPluginUpdateRequest(
    string? DisplayName = null,
    bool? SubscriptionRequired = null,
    int? MonthlyPriceCents = null,
    string? Currency = null,
    bool? Active = null,
    string? CheckoutUrl = null,
    string? Notes = null);

public sealed record ZeusUserUpdateRequest(
    bool? AccessAllowed = null,
    bool? IsAdmin = null,
    string? SubscriptionStatus = null,
    DateTime? SubscriptionExpiresUtc = null,
    string? PluginAccessMode = null,
    IReadOnlyList<ZeusPluginEntitlement>? PluginEntitlements = null,
    string? Notes = null,
    string? DisplayName = null);
