// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Zeus.Server;

/// <summary>
/// Cross-origin policy for the installed Android and iOS wrappers. These two
/// origins are fixed by the native WebViews; LAN and public web origins are
/// intentionally outside this policy.
/// </summary>
public static class NativeWrapperCorsPolicy
{
    public const string Name = "ZeusNativeWrapper";
    public const string AndroidOrigin = "http://localhost";
    public const string IosOrigin = "capacitor://localhost";

    public static bool IsAllowedOrigin(string origin) =>
        origin is AndroidOrigin or IosOrigin;

    public static void Configure(CorsPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy
            .SetIsOriginAllowed(IsAllowedOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    }
}
