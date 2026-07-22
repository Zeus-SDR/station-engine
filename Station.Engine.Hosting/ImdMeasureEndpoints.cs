// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>Maps the engine-owned two-tone IMD measurement route.</summary>
public static class ImdMeasureEndpoints
{
    public static IEndpointRouteBuilder MapImdMeasure(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/dsp/imd-measure", (
            ImdMeasureRequest request,
            ImdMeasureService service) => Results.Ok(service.Measure(request)));

        return endpoints;
    }
}
