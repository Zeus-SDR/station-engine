// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>Maps the waterfall render-state beacon used by every SPA host.</summary>
public static class WaterfallDiagnosticEndpoints
{
    public static IEndpointRouteBuilder MapWaterfallDiagnosticEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // The desktop app has no readily reachable DevTools, so the frontend
        // posts its runtime waterfall state here after connect. This route is
        // deliberately shared by the full host and StationEngine: Zeus Link's
        // attach topology serves the same SPA against the standalone engine.
        endpoints.MapPost("/api/diag/wf", (
            JsonElement body,
            ILoggerFactory loggerFactory) =>
        {
            loggerFactory
                .CreateLogger("Zeus.Server.WaterfallDiagnostics")
                .LogInformation("diag.wf {Report}", body.ToString());
            return Results.Ok();
        });

        return endpoints;
    }
}
