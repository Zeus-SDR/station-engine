// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Zeus.Server.Tdoa;

/// <summary>Private native-process adapter for the separate product host.
/// These routes are mapped only by StationEngine, never by the browser/product
/// route map, and sit behind station bearer-token middleware.</summary>
public static class TdoaContributionEndpoints
{
    private const int MaxCommandBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapTdoaContributionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tdoa/contribution/status", (HttpContext context) =>
        {
            if (!IsTrustedServiceRequest(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(context.RequestServices
                .GetRequiredService<TdoaContributionCoordinator>()
                .GetLocalEligibility());
        });

        endpoints.MapPost("/api/tdoa/contribution/capture", async (HttpContext context) =>
        {
            if (!IsTrustedServiceRequest(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (context.Request.ContentLength is > MaxCommandBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            TdoaContributionCaptureCommand? command;
            try
            {
                command = await context.Request.ReadFromJsonAsync<TdoaContributionCaptureCommand>(
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                return Results.BadRequest(new { error = "invalid contribution capture request" });
            }

            if (command?.Request is null)
                return Results.BadRequest(new { error = "contribution capture request is required" });
            try
            {
                var coordinator = context.RequestServices.GetRequiredService<TdoaContributionCoordinator>();
                return Results.Ok(await coordinator.CaptureAsync(
                    command.Request,
                    command.ParticipationEnabled,
                    context.RequestAborted).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(499);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(MaxCommandBytes));

        endpoints.MapPost("/api/tdoa/contribution/capture-public-kiwi", async (HttpContext context) =>
        {
            if (!IsTrustedServiceRequest(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (context.Request.ContentLength is > MaxCommandBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            TdoaPublicKiwiCaptureCommand? command;
            try
            {
                command = await context.Request.ReadFromJsonAsync<TdoaPublicKiwiCaptureCommand>(
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            { return Results.BadRequest(new { error = "invalid public KiwiSDR capture request" }); }
            if (command?.Request is null || string.IsNullOrWhiteSpace(command.Url))
                return Results.BadRequest(new { error = "public KiwiSDR URL and capture request are required" });
            var coordinator = context.RequestServices.GetRequiredService<TdoaContributionCoordinator>();
            return Results.Ok(await coordinator.CapturePublicKiwiAsync(
                command.Url, command.Request, context.RequestAborted).ConfigureAwait(false));
        }).WithMetadata(new RequestSizeLimitAttribute(MaxCommandBytes));

        return endpoints;
    }

    internal static bool IsTrustedServiceRequest(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        return address is not null
            && IPAddress.IsLoopback(address)
            && context.Request.Headers.Origin.Count == 0;
    }
}
