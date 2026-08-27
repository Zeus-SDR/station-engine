// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

public static class VnaEndpoints
{
    public static IEndpointRouteBuilder MapVnaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/vna/capability", (VnaService service) => Results.Ok(service.Capability()));
        endpoints.MapGet("/api/vna/status", (VnaService service) => Results.Ok(service.Status()));
        endpoints.MapGet("/api/vna/sweeps", (VnaService service) => Results.Ok(service.Sweeps()));
        endpoints.MapGet("/api/vna/calibrations", (VnaService service) => Results.Ok(service.Calibrations()));

        endpoints.MapPost("/api/vna/sweeps", async (VnaSweepRequest request, VnaService service,
            HttpContext context) =>
        {
            try { return Results.Ok(await service.SweepAsync(request, context.RequestAborted)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        endpoints.MapPost("/api/vna/calibrations/capture", async (
            VnaCalibrationCaptureRequest request, VnaService service, HttpContext context) =>
        {
            try { return Results.Ok(await service.CaptureCalibrationAsync(request, context.RequestAborted)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        endpoints.MapPost("/api/vna/cancel", (VnaService service) => { service.Cancel(); return Results.NoContent(); });
        endpoints.MapDelete("/api/vna/sweeps/{id}", (string id, VnaService service) =>
            service.DeleteSweep(id) ? Results.NoContent() : Results.NotFound());
        endpoints.MapDelete("/api/vna/calibrations/{id}", (string id, VnaService service) =>
            service.DeleteCalibration(id) ? Results.NoContent() : Results.NotFound());
        return endpoints;
    }
}
