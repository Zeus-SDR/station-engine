// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server;

/// <summary>
/// App-lifecycle routes for the standalone station engine. Only restart is
/// mapped: the Zeus Link launcher supervises the engine process and respawns
/// it after ANY exit (see the launcher engine supervisor), so an engine
/// "restart" is a graceful shutdown — the launcher brings the replacement up
/// with the same arguments on the same port. This deliberately does NOT reuse
/// the desktop host's AppRestartService, which self-relaunches a detached
/// replacement process; under the launcher that would race the supervisor's
/// own respawn and put two engines on one port.
///
/// Quit is NOT mapped on the engine: exiting the engine alone is meaningless
/// under a supervisor that immediately respawns it. Closing the Zeus Link
/// window is the launcher's concern.
/// </summary>
public static class EngineAppControlEndpoints
{
    // Let the in-flight HTTP response flush before the host stops. Mirrors the
    // desktop AppRestartService's flush delay.
    private const int RestartFlushDelayMs = 400;

    public static IEndpointRouteBuilder MapEngineAppControlEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/app/restart", (HttpContext ctx, IHostApplicationLifetime lifetime) =>
        {
            // Guard: loopback requests only, and when an Origin header is
            // present it must be an allowed browser origin (pinned app origins
            // or plain-http loopback). The desktop host's same-origin guard
            // (LocalRequestGuard.RejectIfNotLocalSameOrigin) would reject the
            // legitimate Zeus Link flow, where the product bundle serves the
            // SPA from a DIFFERENT loopback port than the engine — so the
            // engine uses the same origin allowlist as its /ws WebSocket.
            if (!LocalRequestGuard.IsLocalRequest(ctx))
            {
                return Results.Json(
                    new { error = "Open Zeus on the machine running the engine to restart Zeus." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var origins = ctx.Request.Headers.Origin;
            if (origins.Count > 1
                || (origins.Count == 1
                    && (origins[0] is not { } origin
                        || !StationEngineEndpoints.IsBrowserOriginAllowed(origin))))
            {
                return Results.Json(
                    new { error = "Zeus can only restart from an allowed local app origin." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(RestartFlushDelayMs).ConfigureAwait(false);
                try
                {
                    lifetime.StopApplication();
                }
                catch
                {
                    // The host may already be stopping (test hosts, shutdown
                    // race) — the launcher respawn decision does not depend on
                    // this call landing.
                }
            });

            return Results.Ok(new { restarting = true });
        });

        return endpoints;
    }
}
