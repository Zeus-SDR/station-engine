// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Net;
using System.Text.Json;
using Station.AudioRing;

namespace Zeus.Server;

/// <summary>Maps the versioned SPA-to-station protocol discovery surface.</summary>
public static class StationProtocolEndpoints
{
    public const int CurrentProtocolVersion = 1;

    public static IEndpointRouteBuilder MapStationProtocolEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/station/version", () => Results.Ok(new
        {
            protocol = CurrentProtocolVersion,
            engine = EngineVersion,
        }));

        endpoints.MapPost("/api/station/product-audio/attach", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var port = context.RequestServices.GetService<ProductAudioRingPort>();
            if (port is null)
                return Results.NotFound();

            ProductAudioAttachRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ProductAudioAttachRequest>(
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                return Results.BadRequest(new { error = "invalid JSON attachment request" });
            }

            if (request is null)
                return Results.BadRequest(new { error = "attachment request is required" });
            return port.TryCreateAttachment(request, out var response, out var error)
                ? Results.Ok(response)
                : Results.Conflict(new { error });
        });

        endpoints.MapGet("/api/station/product-audio/lease/{leaseId}", async (
            HttpContext context,
            string leaseId) =>
        {
            if (!IsLoopback(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var port = context.RequestServices.GetService<ProductAudioRingPort>();
            if (port is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await port.HoldLeaseAsync(leaseId, context).ConfigureAwait(false);
        });

        return endpoints;
    }

    internal static string EngineVersion =>
        typeof(StreamingHub).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0-unknown";

    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
}
