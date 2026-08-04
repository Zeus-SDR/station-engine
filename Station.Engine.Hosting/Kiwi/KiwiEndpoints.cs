// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps the KiwiSDR configuration and public-directory API shared by
/// the monolithic Zeus host and the standalone station engine.</summary>
public static class KiwiEndpoints
{
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

        // The upstream directory is plain HTTP, so browsers cannot fetch it
        // directly from an HTTPS app. Both hosts proxy and cache it here.
        endpoints.MapGet("/api/kiwi/directory", async (
            KiwiDirectoryService directory,
            HttpContext context) =>
            Results.Ok(await directory.GetAsync(context.RequestAborted)));

        return endpoints;
    }
}
