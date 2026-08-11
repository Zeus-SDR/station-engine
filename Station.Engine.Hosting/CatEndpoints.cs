// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;
using Zeus.Server.Cat;

namespace Zeus.Server;

/// <summary>Maps the engine-owned network and serial CAT management routes.</summary>
public static class CatEndpoints
{
    public static IEndpointRouteBuilder MapCatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapGet("/api/cat/status", (CatManagementService cat) => cat.GetStatus());

        endpoints.MapPost("/api/cat/config", (CatRuntimeConfig req, CatManagementService cat, HttpContext ctx) =>
        {
            log.LogInformation("api.cat.config enabled={En} bind={Bind} port={Port} autoReport={AutoReport}",
                req.Enabled, req.BindAddress, req.Port, req.AutoReport);
            var status = cat.SetConfig(req);
            return Results.Ok(status);
        });

        endpoints.MapPost("/api/cat/test", (CatTestRequest req, CatManagementService cat, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.BindAddress) || req.Port is <= 0 or >= 65536)
                return Results.BadRequest(new { error = "bindAddress and port required" });
            var result = cat.TestPort(req.BindAddress.Trim(), req.Port);
            return Results.Ok(result);
        });

        // Serial CAT ports (Thetis CAT1–4). GET returns per-port config + live
        // open/error status plus the host's enumerable serial devices (UI
        // suggestions; pty/com0com pairs aren't enumerable so the field is
        // free-form). PUT replaces all four port configs and hot-reconnects (no
        // restart). POST /test probe-opens one port to verify it's usable.
        endpoints.MapGet("/api/cat/serial/status",
            (CatSerialService svc) => Results.Ok(svc.Snapshot()));

        endpoints.MapPut("/api/cat/serial/config",
            (CatSerialConfig req, CatSerialConfigStore store, CatSerialService svc) =>
        {
            var ports = req?.Ports ?? Array.Empty<CatSerialPortConfig>();
            log.LogInformation("api.cat.serial.config ports=[{Ports}]",
                string.Join(", ", ports.Select((p, i) => $"#{i + 1} {(p.Enabled ? $"{p.PortName}@{p.BaudRate}" : "off")}")));
            store.Set(ports);
            return Results.Ok(svc.Snapshot());
        });

        endpoints.MapPost("/api/cat/serial/test",
            (CatSerialTestRequest req, CatSerialService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req?.PortName))
                return Results.BadRequest(new { error = "portName required" });
            return Results.Ok(svc.TestPort(req));
        });

        return endpoints;
    }
}
