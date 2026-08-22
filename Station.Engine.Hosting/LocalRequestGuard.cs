// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;

namespace Zeus.Server;

internal static class LocalRequestGuard
{
    public static bool IsLoopbackRequest(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        return IPAddress.IsLoopback(remote);
    }

    public static bool IsLocalRequest(HttpContext ctx)
    {
        static IPAddress Normalize(IPAddress ip)
            => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null) return true;
        remote = Normalize(remote);
        if (IPAddress.IsLoopback(remote)) return true;

        var local = ctx.Connection.LocalIpAddress;
        return local is not null && remote.Equals(Normalize(local));
    }

    public static bool IsSameOriginOrNoOrigin(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
        if (!ctx.Request.Host.HasValue) return false;

        var requestPort = ctx.Request.Host.Port ?? DefaultPort(ctx.Request.Scheme);
        var originPort = originUri.IsDefaultPort
            ? DefaultPort(originUri.Scheme)
            : originUri.Port;

        return string.Equals(originUri.Scheme, ctx.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Host, ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && originPort == requestPort;
    }

    /// <summary>Allow non-browser websocket clients with no Origin, while
    /// constraining browser handshakes to this server or a fixed Zeus-owned
    /// app origin. HTTP CORS middleware does not govern websocket upgrades.</summary>
    public static bool IsTrustedWebSocketOrigin(HttpContext ctx)
    {
        var origins = ctx.Request.Headers.Origin;
        if (origins.Count == 0) return true;
        if (origins.Count != 1 || string.IsNullOrWhiteSpace(origins[0])) return false;
        return IsSameOriginOrNoOrigin(ctx)
            || NativeWrapperCorsPolicy.IsAllowedOrigin(origins[0]!);
    }

    /// <summary>
    /// Strong browser privilege gate: unlike ordinary local app-control calls,
    /// raw microphone access requires an explicit, single same-origin header.
    /// This rejects non-browser/no-Origin callers and cross-site websocket
    /// pages even when they can reach a loopback listener.
    /// </summary>
    public static bool IsLoopbackSameOriginBrowser(HttpContext ctx)
    {
        var origins = ctx.Request.Headers.Origin;
        return IsLoopbackRequest(ctx)
            && IsLoopbackHost(ctx.Request.Host.Host)
            && origins.Count == 1
            && !string.IsNullOrWhiteSpace(origins[0])
            && IsSameOriginOrNoOrigin(ctx);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    public static IResult? RejectIfNotLocalSameOrigin(HttpContext ctx, string action)
    {
        if (!IsLocalRequest(ctx))
        {
            return Results.Json(
                new { error = $"Open Zeus on the machine running the backend to {action}." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!IsSameOriginOrNoOrigin(ctx))
        {
            return Results.Json(
                new { error = $"Zeus can only {action} from the local same-origin app." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    /// <summary>
    /// Local privileged-request gate for the split Zeus Link topology. The
    /// request itself must originate on this machine; a browser Origin, when
    /// present, must be one of the origins accepted by the station engine's
    /// CORS policy. This admits a product SPA on a different loopback port
    /// without admitting a browser running on another LAN host.
    /// </summary>
    public static IResult? RejectIfNotLocalAllowedBrowserOrigin(
        HttpContext ctx,
        string action)
    {
        if (!IsLocalRequest(ctx))
        {
            return Results.Json(
                new { error = $"Open Zeus on the machine running the engine to {action}." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var origins = ctx.Request.Headers.Origin;
        if (origins.Count > 1
            || (origins.Count == 1
                && (origins[0] is not { } origin
                    || !StationEngineEndpoints.IsBrowserOriginAllowed(origin))))
        {
            return Results.Json(
                new { error = $"Zeus can only {action} from an allowed local app origin." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    public static IResult? RejectIfNotLiteralLocalHost(HttpContext ctx, string action)
    {
        var host = ctx.Request.Host.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return null;
        if (IPAddress.TryParse(host, out var address))
        {
            address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (IPAddress.IsLoopback(address))
                return null;
            var local = ctx.Connection.LocalIpAddress;
            if (local is not null)
            {
                local = local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local;
                if (address.Equals(local))
                    return null;
            }
        }

        return Results.Json(
            new { error = $"Zeus can only {action} through a literal local address." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static int DefaultPort(string scheme)
        => string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
            ? 443
            : string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
                ? 80
                : -1;
}
