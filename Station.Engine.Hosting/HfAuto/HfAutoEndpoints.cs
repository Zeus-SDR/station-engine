// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA),
//                    Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

public static class HfAutoEndpoints
{
    public static IEndpointRouteBuilder MapHfAutoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/hf-auto/status", (HfAutoService service) => service.GetStatus());

        endpoints.MapPost("/api/hf-auto/config", (HfAutoRuntimeConfig request, HfAutoService service) =>
        {
            try
            {
                return Results.Ok(service.SetConfig(request));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return endpoints;
    }
}
