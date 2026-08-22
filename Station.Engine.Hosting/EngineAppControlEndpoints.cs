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
/// App-lifecycle routes for the standalone station engine. The Zeus Link
/// launcher supervises the engine process and respawns it after any exit, so
/// while the supervisor is live an engine restart or shutdown produces the
/// same supervised replacement. This deliberately does not reuse the desktop
/// host's AppRestartService, which would race that supervisor.
///
/// <c>POST /shutdown</c> is the launcher's graceful engine-stop contract, not
/// an operator quit command. During launcher teardown it lets the engine finish
/// its three-second host shutdown budget instead of being force-killed after
/// the launcher's five-second wait. Keep this contract aligned with
/// <c>zeus-link-launcher/src/engine/process.rs</c>.
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

        endpoints.MapPost(
            "/api/app/restart",
            (HttpContext ctx, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory) =>
            {
                var rejection = LocalRequestGuard.RejectIfNotLocalAllowedBrowserOrigin(
                    ctx, "restart Zeus");
                if (rejection is not null)
                    return rejection;

                StopAfterResponseFlush(lifetime, Log(loggerFactory));
                return Results.Ok(new { restarting = true });
            });

        endpoints.MapPost(
            "/shutdown",
            (HttpContext ctx, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory) =>
            {
                var rejection = LocalRequestGuard.RejectIfNotLocalAllowedBrowserOrigin(
                    ctx, "shut down Zeus");
                if (rejection is not null)
                    return rejection;

                var log = Log(loggerFactory);
                // The operator report behind issue #716 contained no
                // shutdown-time evidence at all — the engine had no way to say
                // it had been asked to stop. Stamp that into zeus-app.log so
                // the next "Zeus will not close" report is diagnosable.
                log.LogInformation("engine.shutdown accepted");
                StopAfterResponseFlush(lifetime, log);
                return Results.Ok(new { shuttingDown = true });
            });

        return endpoints;
    }

    private static ILogger Log(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger(nameof(EngineAppControlEndpoints));

    private static void StopAfterResponseFlush(IHostApplicationLifetime lifetime, ILogger log)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(RestartFlushDelayMs).ConfigureAwait(false);
            try
            {
                lifetime.StopApplication();
            }
            catch (Exception ex)
            {
                // Keep swallowing: the host may already be stopping (test
                // hosts, shutdown race), and the launcher's force-kill bounds
                // the worst case either way. But do NOT swallow it SILENTLY —
                // if StopApplication ever fails for a real reason, this line is
                // the only evidence that the engine was told to stop and did
                // not, which is exactly the gap that made issue #716 hard to
                // diagnose.
                log.LogWarning(ex, "engine.stop request did not reach the host");
            }
        });
    }
}
