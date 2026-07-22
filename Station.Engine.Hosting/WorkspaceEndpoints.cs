// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps engine-backed operator workspace routes.</summary>
public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceZoomEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // Workspace UI zoom — scales the panel-grid cell pitch (see
        // StateDto.WorkspaceZoomPct). Distinct from /api/rx/zoom (spectral). The
        // server clamps Pct into range rather than 400-ing, so a slider step can
        // never get stuck on an out-of-range value; the echoed state carries the
        // accepted percent back for the optimistic-send reconcile.
        endpoints.MapPost("/api/ui/workspace-zoom", (WorkspaceZoomSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.ui.workspaceZoom pct={Pct}", req.Pct);
            return Results.Ok(r.SetWorkspaceZoom(req.Pct));
        });

        return endpoints;
    }
}
