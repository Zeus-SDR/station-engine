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

    private static int DefaultPort(string scheme)
        => string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
            ? 443
            : string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
                ? 80
                : -1;
}
