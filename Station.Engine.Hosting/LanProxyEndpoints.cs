// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

/// <summary>Maps the private-LAN HTTP proxy used by the LAN Browser panel.</summary>
public static class LanProxyEndpoints
{
    public static IEndpointRouteBuilder MapLanProxyEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            LanProxyService.ProxyPath,
            async (string? url,
                   string? inline,
                   LanProxyService proxy,
                   HttpContext context,
                   CancellationToken cancellationToken) =>
            {
                var result = await proxy.FetchAsync(
                    url,
                    inline is "1" or "true",
                    cancellationToken);
                ApplyResponsePolicy(context.Response, result);
                context.Response.StatusCode = result.Status;
                context.Response.ContentType =
                    result.ContentType ?? "application/octet-stream";
                await context.Response.Body.WriteAsync(
                    result.Body,
                    cancellationToken);
            });

        return endpoints;
    }

    /// <summary>
    /// Fonts inside the deliberately opaque LAN Browser sandbox have origin
    /// <c>null</c> and therefore require an explicit CORS grant. Proxied
    /// documents and API payloads remain unreadable cross-origin.
    /// </summary>
    internal static void ApplyResponsePolicy(
        HttpResponse response,
        LanProxyResult result)
    {
        if (LanProxyService.IsFontContentType(result.ContentType))
            response.Headers["Access-Control-Allow-Origin"] = "*";
    }
}
