// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Net;
using System.Text.Json;
using Station.AudioRing;
using Zeus.Dsp.Wdsp;

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

        // The closed product bundle owns hardware-diagnostics interpretation.
        // This is its sole diagnostics read seam into the published engine:
        // return the snapshot DspPipelineService already computes, unchanged.
        endpoints.MapGet("/api/station/dsp-diagnostics", (HttpContext context) =>
        {
            var services = context.RequestServices;
            var dsp = services.GetRequiredService<DspPipelineService>();
            var wisdom = services.GetRequiredService<WdspWisdomInitializer>();
            return Results.Ok(dsp.SnapshotDiagnostics(wisdom));
        });

        endpoints.MapPost("/api/station/product-audio/attach", async (HttpContext context) =>
        {
            // Product attachment is an originless native-process seam. A web
            // page must never be able to manufacture the endpoint trust used
            // by the native microphone bridge, even if it can reach loopback
            // and happens to possess a station token.
            if (!IsLoopback(context) || context.Request.Headers.Origin.Count != 0)
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

        endpoints.MapGet("/api/station/product-endpoint", (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<ProductAudioRingPort>();
            return port is not null && port.TryGetProductEndpoint(out var productPort)
                ? Results.Ok(new { port = productPort })
                : Results.NotFound();
        });

        endpoints.MapGet("/api/station/product-audio/lease/{leaseId}", async (
            HttpContext context,
            string leaseId) =>
        {
            if (!IsLoopback(context) || context.Request.Headers.Origin.Count != 0)
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

        endpoints.MapPost("/api/station/rx-audio/attach", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<ProductPluginAudioPort>();
            if (port is null) return Results.NotFound();
            ProductPluginCaptureAttachRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ProductPluginCaptureAttachRequest>(
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                return Results.BadRequest(new { error = "invalid JSON capture attachment request" });
            }
            if (request is null) return Results.BadRequest(new { error = "attachment request is required" });
            return port.TryCreateCaptureAttachment(request, out var response, out var error)
                ? Results.Ok(response)
                : Results.Conflict(new { error });
        });

        endpoints.MapGet("/api/station/rx-audio/lease/{leaseId}", HoldProductPluginLeaseAsync);

        endpoints.MapPost("/api/station/tx-audio/attach", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<ProductPluginAudioPort>();
            if (port is null) return Results.NotFound();
            ProductPluginInjectionAttachRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<ProductPluginInjectionAttachRequest>(
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                return Results.BadRequest(new { error = "invalid JSON injection attachment request" });
            }
            if (request is null) return Results.BadRequest(new { error = "attachment request is required" });
            return port.TryCreateInjectionAttachment(request, out var response, out var error)
                ? Results.Ok(response)
                : Results.Conflict(new { error });
        });

        endpoints.MapGet("/api/station/tx-audio/lease/{leaseId}", HoldProductPluginLeaseAsync);

        endpoints.MapPost("/api/station/mode-modem/attach", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<ModeModemLeasePort>();
            if (port is null) return Results.NotFound();
            var request = await ReadRequestAsync<ModeModemAttachRequest>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "attachment request is required" });
            return port.TryCreateAttachment(request, out var response, out var error)
                ? Results.Ok(response)
                : Results.Conflict(new { error });
        });

        endpoints.MapGet("/api/station/mode-modem/lease/{leaseId}", HoldModeModemLeaseAsync);

        endpoints.MapPost("/api/station/mode-modem/event", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<ModeModemLeasePort>();
            if (port is null) return Results.NotFound();
            var request = await ReadRequestAsync<ModeModemEventRequest>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "mode-modem event is required" });
            return port.PostEvent(request, out var error)
                ? Results.Ok()
                : Results.Conflict(new { error });
        });

        endpoints.MapGet("/api/station/rade/status", (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<CompositeAudioModemPort>();
            return port is null ? Results.NotFound() : Results.Ok(port.RadeStatus());
        });

        endpoints.MapPost("/api/station/rade/select", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<CompositeAudioModemPort>();
            if (port is null) return Results.NotFound();
            var request = await ReadRequestAsync<RadeSelectRequest>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "RADE selection is required" });
            return port.SelectRade(request.Active, request.TxText)
                ? Results.Ok(port.RadeStatus())
                : Results.Conflict(new { error = "RADE is unavailable for this station-engine RID/build" });
        });

        endpoints.MapPost("/api/station/rade/mic", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<CompositeAudioModemPort>();
            if (port is null) return Results.NotFound();
            var request = await ReadRequestAsync<RadeMicConfig>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "RADE mic config is required" });
            port.SetRadeMicConfig(request);
            return Results.Ok(port.RadeStatus());
        });

        endpoints.MapPost("/api/station/key/arm", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<ProductPluginAudioPort>();
            var tx = context.RequestServices.GetService<TxService>();
            if (port is null || tx is null) return Results.NotFound();
            var request = await ReadRequestAsync<ProductPluginArmRequest>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "arm request is required" });
            return port.TrySetArm(request, tx, out var response, out var error)
                ? Results.Ok(response)
                : Results.Conflict(new { error });
        });

        endpoints.MapPost("/api/station/key/request", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<ProductPluginAudioPort>();
            var tx = context.RequestServices.GetService<TxService>();
            if (port is null || tx is null) return Results.NotFound();
            var request = await ReadRequestAsync<ProductPluginKeyRequest>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "key request is required" });
            return port.TryRequestKey(request, tx, out var response, out var error)
                ? Results.Ok(response)
                : Results.Conflict(new { error });
        });

        endpoints.MapPost("/api/station/key/release", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<ProductPluginAudioPort>();
            var tx = context.RequestServices.GetService<TxService>();
            if (port is null || tx is null) return Results.NotFound();
            var request = await ReadRequestAsync<ProductPluginKeyRequest>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "key release request is required" });
            return port.TryReleaseKey(request, tx, out var response, out var error)
                ? Results.Ok(response)
                : Results.Conflict(new { error });
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

    private static async Task HoldProductPluginLeaseAsync(HttpContext context, string leaseId)
    {
        if (!IsLoopback(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var port = context.RequestServices.GetService<ProductPluginAudioPort>();
        if (port is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await port.HoldLeaseAsync(leaseId, context).ConfigureAwait(false);
    }

    private static async Task HoldModeModemLeaseAsync(HttpContext context, string leaseId)
    {
        if (!IsLoopback(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var port = context.RequestServices.GetService<ModeModemLeasePort>();
        if (port is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await port.HoldLeaseAsync(leaseId, context).ConfigureAwait(false);
    }

    private static async Task<T?> ReadRequestAsync<T>(HttpContext context) where T : class
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
