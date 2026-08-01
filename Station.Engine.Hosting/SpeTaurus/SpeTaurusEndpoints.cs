// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// SPE Expert 1.5K Taurus amplifier support. This file is GPL-3.0-or-later
// (see Station.Engine.Hosting/SpeTaurus/SOURCE.md); the rest of the engine is
// GPL-2.0-or-later, whose "or later" option permits the combination. The
// resulting engine binary is distributed as GPL-3.0-or-later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.SpeTaurus;

internal sealed record SpeOperateRequest(bool Operate);

internal sealed class SpeTaurusWorker(SpeTaurusService service) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        service.RunAsync(stoppingToken);
}

public static class SpeTaurusEndpoints
{
    public static IEndpointRouteBuilder MapSpeTaurusEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/amp/spe-taurus");
        group.MapGet("/status", (SpeTaurusService service) => Results.Ok(service.Status()));
        group.MapGet("/config", (SpeTaurusService service) => Results.Ok(service.Config));
        group.MapPost("/config", async Task<IResult> (
            SpeTaurusConfig? config,
            SpeTaurusService service,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(
                    await service.SetConfigAsync(config, ct).ConfigureAwait(false));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        group.MapPost("/ports/refresh", (SpeTaurusService service) =>
            Results.Ok(service.RefreshDevices()));
        group.MapPost("/devices/refresh", (SpeTaurusService service) =>
            Results.Ok(service.RefreshDevices()));
        group.MapPost("/operate", async (
            SpeOperateRequest request,
            SpeTaurusService service,
            CancellationToken ct) => Results.Ok(
                await service.SetOperateAsync(request.Operate, ct).ConfigureAwait(false)));
        group.MapPost("/power-level", async (SpeTaurusService service, CancellationToken ct) =>
            Results.Ok(await service.CycleAsync(SpeCommand.PowerLevel, ct).ConfigureAwait(false)));
        group.MapPost("/antenna", async (SpeTaurusService service, CancellationToken ct) =>
            Results.Ok(await service.CycleAsync(SpeCommand.Antenna, ct).ConfigureAwait(false)));
        group.MapPost("/input", async (SpeTaurusService service, CancellationToken ct) =>
            Results.Ok(await service.CycleAsync(SpeCommand.Input, ct).ConfigureAwait(false)));
        group.MapPost("/atu/tune", async (SpeTaurusService service, CancellationToken ct) =>
            Results.Ok(await service.TuneAsync(ct).ConfigureAwait(false)));
        return endpoints;
    }
}
