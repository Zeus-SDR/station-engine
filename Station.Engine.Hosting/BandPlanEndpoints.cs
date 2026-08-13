// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;
namespace Zeus.Server;

/// <summary>Maps band memory, band stack, and regional band-plan routes.</summary>
public static class BandPlanEndpoints
{
    public static IEndpointRouteBuilder MapBandPlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Band memory: last-used (hz, mode) per HF band. GET returns the full map so
        // the BandButtons UI can restore on load with one round-trip. PUT upserts one
        // entry — the web debounces writes so tuning doesn't hammer LiteDB.
        endpoints.MapGet("/api/bands/memory", (BandMemoryStore store) => Results.Ok(store.GetAll()));

        endpoints.MapPut("/api/bands/memory/{band}", (string band, BandMemorySetRequest req, BandMemoryStore store) =>
        {
            if (string.IsNullOrWhiteSpace(band))
                return Results.BadRequest(new { error = "band name required" });
            if (req.Hz <= 0)
                return Results.BadRequest(new { error = "hz must be positive" });
            store.Upsert(band, req.Hz, req.Mode, req.FilterLowHz, req.FilterHighHz, req.FilterMode);
            return Results.Ok(store.Get(band) ?? new BandMemoryDto(band, req.Hz, req.Mode));
        });

        // RX and TX waterfall intensity are universal band settings: every
        // display surface reads and writes the same backend-owned map.
        endpoints.MapGet("/api/bands/waterfall-settings", (BandMemoryStore store) =>
            Results.Ok(store.GetAllWaterfallSettings()));

        endpoints.MapPut("/api/bands/waterfall-settings/{band}",
            PutWaterfallSettings);

        // Band stack (issue #179): named per-band presets that snapshot
        // (hz, mode, filter). Distinct from the single automatic last-used slot
        // above — operators push/delete these explicitly. GET returns the full
        // set so the panel can hydrate once; POST appends a new entry; DELETE
        // removes one by id scoped to its band (a stale id from another band
        // won't collide).
        endpoints.MapGet("/api/bands/stack", (BandStackStore store) => Results.Ok(store.GetAll()));

        endpoints.MapPost("/api/bands/stack/{band}", (string band, BandStackAddRequest req, BandStackStore store) =>
        {
            if (string.IsNullOrWhiteSpace(band))
                return Results.BadRequest(new { error = "band name required" });
            if (req.Hz <= 0)
                return Results.BadRequest(new { error = "hz must be positive" });
            var label = string.IsNullOrWhiteSpace(req.Label) ? band : req.Label.Trim();
            if (label.Length > 64) label = label.Substring(0, 64);
            var entry = store.Add(band, label, req.Hz, req.Mode, req.FilterLowHz, req.FilterHighHz);
            return Results.Ok(entry);
        });

        endpoints.MapDelete("/api/bands/stack/{band}/{id:int}", (string band, int id, BandStackStore store) =>
        {
            if (string.IsNullOrWhiteSpace(band))
                return Results.BadRequest(new { error = "band name required" });
            var removed = store.Delete(band, id);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // Regional band plan (issue #65). Shipped JSON under BandPlans/ defines
        // baseline regions (IARU R1/R2/R3) and country overrides (EI, G, US FCC
        // General/Extra). Operator can edit per-region segments (PUT) and reset
        // back to shipped defaults (DELETE). Active region is persisted in
        // BandPrefsStore; switches fire BandPlanChanged (0x1B) so other tabs
        // refetch.
        endpoints.MapGet("/api/bands/regions", (BandPlanStore store) =>
            Results.Ok(store.Regions));

        endpoints.MapGet("/api/bands/plan", (string? region, BandPlanService svc) =>
        {
            var regionId = region ?? svc.CurrentRegion.Id;
            var plan = svc.ResolvePlan(regionId);
            return Results.Ok(new BandPlanDto(regionId, plan));
        });

        endpoints.MapGet("/api/bands/current", (BandPlanService svc) =>
            Results.Ok(new
            {
                regionId = svc.CurrentRegion.Id,
                region = svc.CurrentRegion,
                segments = svc.CurrentPlan,
                txGuardIgnore = svc.TxGuardIgnore,
            }));

        endpoints.MapPost("/api/bands/current", (BandPlanCurrentSetRequest req, BandPlanService svc) =>
        {
            svc.SetRegion(req.RegionId);
            return Results.Ok(new { regionId = svc.CurrentRegion.Id });
        });

        endpoints.MapPut("/api/bands/plan", (BandPlanSaveRequest req, BandPlanService svc) =>
        {
            try
            {
                svc.SavePlan(req.RegionId, req.Segments);
                return Results.Ok(new { regionId = req.RegionId, saved = req.Segments.Count });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        endpoints.MapDelete("/api/bands/plan/{regionId}", (string regionId, BandPlanService svc) =>
        {
            svc.ResetPlan(regionId);
            return Results.Ok(new { regionId, reset = true });
        });

        endpoints.MapPost("/api/bands/guard", (BandGuardSetRequest req, BandPlanService svc) =>
        {
            svc.SetTxGuardIgnore(req.Ignore);
            return Results.Ok(new { txGuardIgnore = req.Ignore });
        });

        return endpoints;
    }

    internal static IResult PutWaterfallSettings(
        string band,
        BandWaterfallSettingsSetRequest req,
        BandMemoryStore store)
    {
        if (string.IsNullOrWhiteSpace(band))
            return Results.BadRequest(new { error = "band name required" });

        try
        {
            store.SetWaterfallSettings(
                band,
                req.WfDbMin,
                req.WfDbMax,
                req.WfTxDbMin,
                req.WfTxDbMax);
            return Results.Ok(store.GetWaterfallSettings(band));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}

internal sealed record BandWaterfallSettingsSetRequest(
    double? WfDbMin,
    double? WfDbMax,
    double? WfTxDbMin,
    double? WfTxDbMax);
