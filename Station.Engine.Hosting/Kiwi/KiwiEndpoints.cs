// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps the KiwiSDR configuration and public-directory API shared by
/// the monolithic Zeus host and the standalone station engine.</summary>
public static class KiwiEndpoints
{
    // Upper bound on an absolute zoom request. The service clamps to whatever
    // cap the remote actually negotiated (typically z14); this only rejects
    // obviously bogus values before they reach it.
    private const int MaxKiwiZoomLevel = 30;

    public static IEndpointRouteBuilder MapKiwiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // GET returns current status but never the stored password (only
        // HasPassword). POST patches enable/url/password and reconnects.
        endpoints.MapGet("/api/kiwi", (KiwiSdrService kiwi) => Results.Ok(kiwi.GetConfig()));
        endpoints.MapPost("/api/kiwi", async (
            KiwiSetRequest req,
            KiwiSdrService kiwi,
            ILogger<KiwiSdrService> log) =>
        {
            log.LogInformation(
                "api.kiwi enabled={Enabled} url={Url} pwSet={PwSet}",
                req.Enabled,
                req.Url,
                req.Password is not null);
            return Results.Ok(await kiwi.SetConfigAsync(
                req.Enabled,
                req.Url,
                req.Password,
                default));
        });

        // Kiwi-native waterfall zoom is independent of the radio-wide DDC
        // zoom. Delta is one wheel/pinch step (-1 out, +1 in); Level jumps
        // straight to an absolute z-level (what the slice window's zoom slider
        // sends). Anchor is the normalized pointer/passband location retained
        // across the change. Level wins when both are present.
        endpoints.MapPost("/api/kiwi/zoom", (
            KiwiZoomStepRequest req,
            KiwiSdrService kiwi) =>
        {
            if (req.Level is null && req.Delta is not (-1 or 1))
                return Results.BadRequest(new { error = "delta must be -1 or 1" });
            if (req.Level is < 0 or > MaxKiwiZoomLevel)
                return Results.BadRequest(new { error = $"level must be in [0,{MaxKiwiZoomLevel}]" });
            if (!double.IsFinite(req.Anchor) || req.Anchor < 0 || req.Anchor > 1)
                return Results.BadRequest(new { error = "anchor must be in [0,1]" });
            if (req.CenterHz is < 0 or > 60_000_000)
                return Results.BadRequest(new { error = "centerHz must be in [0,60000000]" });

            var result = req.Level is int level
                ? kiwi.SetZoomLevel(level, req.Anchor, req.CenterHz)
                : kiwi.StepZoom(req.Delta, req.Anchor, req.CenterHz);
            return Results.Ok(new
            {
                level = result.Level,
                changed = result.Changed,
                centerHz = result.CenterHz,
            });
        });

        // The upstream directory is plain HTTP, so browsers cannot fetch it
        // directly from an HTTPS app. Both hosts proxy and cache it here.
        endpoints.MapGet("/api/kiwi/directory", async (
            KiwiDirectoryService directory,
            HttpContext context) =>
            Results.Ok(await directory.GetAsync(context.RequestAborted)));

        return endpoints;
    }
}

/// <summary>Body of <c>POST /api/kiwi/zoom</c>. Send <c>Delta</c> (-1/+1) for a
/// single native step, or <c>Level</c> for an absolute z-level; <c>Level</c>
/// takes precedence when both are supplied.</summary>
public sealed record KiwiZoomStepRequest(
    int Delta = 0,
    double Anchor = 0.5,
    long? CenterHz = null,
    int? Level = null);
