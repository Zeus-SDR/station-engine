// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Diagnostics;
using System.Net;

namespace Zeus.StationEngine;

internal static class ReceiveOnlyRuntime
{
    internal static async Task RestrictRequests(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var path = request.Path;
        bool local = context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
        bool receiveMutation = HttpMethods.IsPost(request.Method)
            && (path.Equals("/api/vfo") || path.Equals("/api/connect") || path.Equals("/api/disconnect"));
        bool privateFeed = path.StartsWithSegments("/api/station/public-listen")
            || (path.Equals("/ws") && request.Query["feed"] == "listener");
        bool read = HttpMethods.IsGet(request.Method) && path.StartsWithSegments("/api");
        // The private engine accepts only the receive controls needed by its owner.
        // Generic operator sockets and settings cannot enable alternate radio transports.
        if (!local || request.Headers.ContainsKey("Origin") || !(read || receiveMutation || privateFeed))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This station engine is receive-only." });
            return;
        }
        await next(context);
    }
}

internal sealed class ReceiveOnlyOwnerMonitor(int ownerPid, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Process owner;
        try { owner = Process.GetProcessById(ownerPid); }
        catch (ArgumentException) { lifetime.StopApplication(); return; }
        using (owner)
        {
            try
            {
                await owner.WaitForExitAsync(stoppingToken);
                lifetime.StopApplication();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (InvalidOperationException) { lifetime.StopApplication(); }
        }
    }
}
