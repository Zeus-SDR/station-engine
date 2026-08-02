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
        group.MapGet("/status", async (
            [Microsoft.AspNetCore.Mvc.FromServices] ExpertAmpServerControl control,
            CancellationToken ct) => Results.Ok(
                await control.StatusAsync(ct).ConfigureAwait(false)));
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
        group.MapPost("/discover", async (
            [Microsoft.AspNetCore.Mvc.FromServices] SpeTaurusService service,
            [Microsoft.AspNetCore.Mvc.FromServices] ExpertAmpServerDiscovery discovery,
            [Microsoft.AspNetCore.Mvc.FromServices] RadioService radio,
            CancellationToken ct) =>
        {
            var expectedConfig = service.Config;
            var result = await discovery.DiscoverAsync(
                expectedConfig,
                radio.Snapshot().Endpoint,
                ct).ConfigureAwait(false);
            if (!result.Found || result.Url is null)
                return Results.Ok(new { result.Found, result.Probed });

            var config = await service.TrySetConfigAsync(
                expectedConfig,
                expectedConfig with { ExpertServerUrl = result.Url },
                ct).ConfigureAwait(false);
            if (config is null)
                return Results.Conflict(new
                {
                    error = "Taurus configuration changed while discovery was running; the newer settings were kept."
                });
            return Results.Ok(new
            {
                result.Found,
                result.Url,
                result.ModelName,
                result.Source,
                result.Probed,
                Config = config,
            });
        });
        group.MapPost("/operate", async (
            SpeOperateRequest request,
            [Microsoft.AspNetCore.Mvc.FromServices] ExpertAmpServerControl control,
            CancellationToken ct) => Results.Ok(
                await control.SetOperateAsync(request.Operate, ct).ConfigureAwait(false)));
        group.MapPost("/power-level", async ([Microsoft.AspNetCore.Mvc.FromServices] ExpertAmpServerControl control, CancellationToken ct) =>
            Results.Ok(await control.CycleAsync(SpeCommand.PowerLevel, ct).ConfigureAwait(false)));
        group.MapPost("/antenna", async ([Microsoft.AspNetCore.Mvc.FromServices] ExpertAmpServerControl control, CancellationToken ct) =>
            Results.Ok(await control.CycleAsync(SpeCommand.Antenna, ct).ConfigureAwait(false)));
        group.MapPost("/input", async ([Microsoft.AspNetCore.Mvc.FromServices] ExpertAmpServerControl control, CancellationToken ct) =>
            Results.Ok(await control.CycleAsync(SpeCommand.Input, ct).ConfigureAwait(false)));
        group.MapPost("/atu/tune", async (
            [Microsoft.AspNetCore.Mvc.FromServices] SpeTaurusAutomaticTuneCoordinator coordinator,
            CancellationToken ct) => Results.Ok(
                await coordinator.TuneAsync(ct).ConfigureAwait(false)));
        return endpoints;
    }
}
