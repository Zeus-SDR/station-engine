// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Globalization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps the standalone station engine's HTTP and WebSocket surface.</summary>
public static class StationEngineEndpoints
{
    internal static readonly HashSet<string> AllowedBrowserOrigins = new(StringComparer.Ordinal)
    {
        "https://app.zeussdr.com",
        // Staging Pages alias for the develop-branch SPA. Production stays
        // app.zeussdr.com, which deploys only from main; this alias lets a
        // staging-channel engine install serve the develop web app without a
        // local dev server.
        "https://staging.zeus-web-app-7ag.pages.dev",
        "http://localhost:5173",
        "http://127.0.0.1:5173",
    };

    public static void ConfigureCors(CorsPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy
            .SetIsOriginAllowed(IsBrowserOriginAllowed)
            .AllowAnyHeader()
            .AllowAnyMethod()
            // navigator.sendBeacon (workspace-layout unload flush) always sends
            // a credentialed request, and its application/json Blob forces a
            // CORS preflight. Without Access-Control-Allow-Credentials the
            // browser rejects that preflight and the beacon never reaches the
            // engine. Safe here: the origin check is the loopback-only
            // predicate plus the pinned origins above — never a wildcard (and
            // ASP.NET would throw on AllowAnyOrigin + AllowCredentials).
            .AllowCredentials();
    }

    /// <summary>
    /// Allows the pinned remote origins plus any plain-HTTP loopback origin.
    /// In the Zeus Link topology the proprietary bundle serves the web app
    /// from 127.0.0.1 on a dynamic port (WebKit refuses https-page-to-
    /// http-loopback mixed content, so the app cannot be remote-hosted). The
    /// engine itself binds loopback only, so a loopback browser origin has
    /// the same trust as any local process.
    /// </summary>
    internal static bool IsBrowserOriginAllowed(string origin)
    {
        if (AllowedBrowserOrigins.Contains(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp) return false;
        return uri.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1";
    }

    public static IEndpointRouteBuilder MapStationEngineEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Keep the same registration order used by the product host while
        // limiting this surface to the modules extracted into the engine.
        endpoints.MapStationProtocolEndpoints();
        // Field gap: ImdMeasureService is registered by AddStationEngine, but
        // the route used to be mapped only by the product host, so the IMD
        // tool 404'd against a standalone engine (attach mode).
        endpoints.MapImdMeasure();
        endpoints.MapStationEngineCapabilitiesEndpoint();
        endpoints.MapNativeAudioEndpoints();
        endpoints.MapRadioStateEndpoint();
        endpoints.MapRadioConnectionEndpoints();
        endpoints.MapVfoEndpoints();
        endpoints.MapReceiverConfigurationEndpoints();
        endpoints.MapRadioTuningEndpoints();
        endpoints.MapReceiverLoEndpoint();
        endpoints.MapCtunEndpoint();
        endpoints.MapModeEndpoint();
        endpoints.MapFilterEndpoints();
        endpoints.MapRadioDspControlEndpoints();
        endpoints.MapTxPhaseRotatorUtilityEndpoints();
        endpoints.MapTxFidelityPolicyEndpoints();
        endpoints.MapReceiverGainProtectionEndpoints();
        endpoints.MapTxControlEndpoints();
        endpoints.MapTxTimingAndTestEndpoints();
        endpoints.MapPureSignalEndpoints();
        endpoints.MapTxMonitorEndpoint();
        endpoints.MapReceiverDspEndpoints();
        endpoints.MapCfcEndpoint();
        endpoints.MapCfcPresetEndpoints();
        endpoints.MapSpectralZoomEndpoint();
        endpoints.MapWorkspaceZoomEndpoint();
        endpoints.MapOperatorUiSettingsEndpoints();
        endpoints.MapWorkspaceLayoutEndpoints();
        endpoints.MapBandPlanEndpoints();
        endpoints.MapPaSettingsEndpoints();
        endpoints.MapRadioSelectionEndpoints();
        endpoints.MapRadioCapabilitiesEndpoint();
        endpoints.MapExternalPttEndpoint();
        endpoints.MapPttStatusEndpoints();
        endpoints.MapRadioAudioEndpoints();
        endpoints.MapPaThermalEndpoint();
        endpoints.MapRadioHardwareEndpoints();
        endpoints.MapRadioCalibrationEndpoints();
        endpoints.MapTciEndpoints();
        endpoints.MapCatEndpoints();
        // Client-error beacon fallback transport (the /ws diagnostic frame is
        // preferred); without this route uncaught SPA errors vanish in local
        // attach whenever the websocket is closed or reconnecting.
        endpoints.MapClientDiagnosticLogEndpoint();

        endpoints.Map("/ws", AttachWebSocketAsync);
        return endpoints;
    }

    private static async Task AttachWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var origins = context.Request.Headers.Origin;
        if (origins.Count > 1
            || (origins.Count == 1
                && (origins[0] is not { } origin
                    // Must match the CORS policy exactly: pinned remote origins
                    // plus any plain-http loopback origin (the bundle serves
                    // the app from a dynamic loopback port). Field defect:
                    // this check still used the raw list after #501 moved the
                    // policy to the predicate, so the panadapter WebSocket got
                    // a 403 from the bundle-served app.
                    || !IsBrowserOriginAllowed(origin))))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!TryParseSessionOptions(
                context.Request.Query,
                out var displayRxId,
                out var suppressAudio))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(
                $"displayRxId must be an integer from 1 through {WireContract.MaxReceivers - 1}",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var hub = context.RequestServices.GetRequiredService<StreamingHub>();
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await hub.AttachClientAsync(
            socket,
            context.RequestAborted,
            displayRxId: displayRxId,
            suppressAudio: suppressAudio).ConfigureAwait(false);
    }

    private static bool TryParseSessionOptions(
        IQueryCollection query,
        out int? displayRxId,
        out bool suppressAudio)
    {
        displayRxId = null;
        suppressAudio = query.TryGetValue("audio", out var audioValues)
            && audioValues.Count == 1
            && string.Equals(audioValues[0], "0", StringComparison.Ordinal);

        if (!query.TryGetValue("displayRxId", out var displayRxValues))
            return true;

        if (displayRxValues.Count != 1
            || !int.TryParse(
                displayRxValues[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < 1
            || parsed >= WireContract.MaxReceivers)
            return false;

        displayRxId = parsed;
        return true;
    }
}
