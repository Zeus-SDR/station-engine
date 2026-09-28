// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Zeus.Server;

/// <summary>
/// Loopback-only WSPR decoder routes (docs/designs/wspr-return.md, "Engine
/// contract"). The station-access token middleware applies as it does to every
/// station route; these routes additionally refuse any non-loopback caller,
/// like the product-plugin audio routes.
/// </summary>
public static class WsprDecoderEndpoints
{
    public static IEndpointRouteBuilder MapWsprDecoderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/wspr/decoder", (HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var service = context.RequestServices.GetService<WsprDecodeService>();
            return service is null ? Results.NotFound() : Results.Ok(service.Status());
        });

        endpoints.MapPut("/api/wspr/decoder", async (HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var service = context.RequestServices.GetService<WsprDecodeService>();
            if (service is null) return Results.NotFound();
            WsprDecoderConfig? config;
            try
            {
                config = await context.Request.ReadFromJsonAsync<WsprDecoderConfig>(context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                config = null;
            }
            if (config is null) return Results.BadRequest(new { error = "decoder configuration is required" });
            if (config.Depth is { } depth
                && !string.Equals(depth, WsprDecoderWire.DepthNormal, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(depth, WsprDecoderWire.DepthDeep, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "depth must be \"normal\" or \"deep\"" });
            return Results.Ok(service.Configure(config));
        });

        endpoints.MapGet("/api/wspr/decoder/slots", (HttpContext context) =>
        {
            if (!IsLoopback(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var service = context.RequestServices.GetService<WsprDecodeService>();
            if (service is null) return Results.NotFound();
            long after = 0;
            if (context.Request.Query.TryGetValue("after", out var raw)
                && !long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out after))
                return Results.BadRequest(new { error = "after must be an integer sequence number" });
            return Results.Ok(service.SlotsAfter(after));
        });

        return endpoints;
    }

    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
}
