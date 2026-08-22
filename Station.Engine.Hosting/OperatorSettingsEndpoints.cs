// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

public static class OperatorSettingsEndpoints
{
    public static IEndpointRouteBuilder MapOperatorSettingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/operator-settings/{family}", (
            string family,
            OperatorSettingsStore store) =>
            OperatorSettingsStore.IsKnownFamily(family)
                ? Results.Ok(store.Get(family))
                : Results.NotFound(new { error = "unknown operator settings family" }));

        endpoints.MapPut("/api/operator-settings/{family}", (
            string family,
            OperatorSettingsSetRequest request,
            OperatorSettingsStore store) =>
        {
            if (!OperatorSettingsStore.IsKnownFamily(family))
                return Results.NotFound(new { error = "unknown operator settings family" });
            try
            {
                return Results.Ok(store.Save(family, request.Value, request.UpdatedUtcMs));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return endpoints;
    }
}
