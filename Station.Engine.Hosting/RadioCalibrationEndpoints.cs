// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>Maps radio frequency-calibration routes.</summary>
public static class RadioCalibrationEndpoints
{
    public static IEndpointRouteBuilder MapRadioCalibrationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // Per-radio frequency calibration (issue #325). GET returns the
        // persisted correction factor + its ppm representation. POST
        // /calibrate runs the one-button auto-cal procedure (snapshot
        // state, tune WWV 10 MHz, find peak, apply factor, restore).
        // POST /reset clears the factor back to 1.0.
        endpoints.MapGet("/api/radio/frequency-calibration", (RadioService radio) =>
        {
            double factor = radio.GetFrequencyCorrectionFactor();
            double ppm = (factor - 1.0) * 1e6;
            double offsetAt10MHz = ppm * 10.0; // Hz offset at 10 MHz
            return Results.Ok(new
            {
                factor,
                ppm,
                offsetHzAt10MHz = offsetAt10MHz,
            });
        });

        // referenceHz picks the reference station (issue #47 follow-up): WWV
        // radiates a continuous carrier on 10 and 15 MHz, and operators who
        // cannot hear one can usually hear the other. Optional query parameter
        // so the original bodyless POST still means "10 MHz".
        endpoints.MapPost("/api/radio/frequency-calibration/calibrate", async (
            FrequencyCalibrationService cal, HttpContext ctx, double? referenceHz) =>
        {
            double reference = referenceHz ?? FrequencyCalibrationService.DefaultReferenceFrequencyHz;
            if (!double.IsFinite(reference) ||
                reference < FrequencyCalibrationService.MinReferenceFrequencyHz ||
                reference > FrequencyCalibrationService.MaxReferenceFrequencyHz)
            {
                return Results.BadRequest(new
                {
                    error = $"referenceHz must be between {FrequencyCalibrationService.MinReferenceFrequencyHz} and {FrequencyCalibrationService.MaxReferenceFrequencyHz}",
                });
            }

            log.LogInformation("api.freqcal.calibrate begin ref={Ref}", reference);
            var result = await cal.CalibrateAsync(reference, ctx.RequestAborted).ConfigureAwait(false);
            log.LogInformation("api.freqcal.calibrate result={Outcome} offset={Off} factor={Factor}",
                result.Outcome, result.OffsetHz, result.AppliedFactor);
            return Results.Ok(result);
        });

        endpoints.MapPost("/api/radio/frequency-calibration/reset", (FrequencyCalibrationService cal) =>
        {
            log.LogInformation("api.freqcal.reset");
            cal.Reset();
            return Results.Ok(new { factor = 1.0, ppm = 0.0, offsetHzAt10MHz = 0.0 });
        });

        // Manual ppm entry (issue #47). Netherlands / Europe / anywhere WWV
        // 10 MHz is inaudible has no auto-cal fallback; this lets the
        // operator type a known offset (e.g. from a GPSDO or RWM measurement)
        // and route it through the same SetFrequencyCorrectionFactor path
        // the auto-cal uses. RadioService clamps to ±100 ppm.
        endpoints.MapPost("/api/radio/frequency-calibration/set", (
            FrequencyCalibrationSetRequest req, RadioService radio) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "body required" });
            if (double.IsNaN(req.Ppm) || double.IsInfinity(req.Ppm))
                return Results.BadRequest(new { error = "ppm must be a finite real number" });

            double factor = 1.0 + req.Ppm * 1e-6;
            double applied = radio.SetFrequencyCorrectionFactor(factor);
            double appliedPpm = (applied - 1.0) * 1e6;
            log.LogInformation("api.freqcal.set requested={Req} applied={AppliedPpm}", req.Ppm, appliedPpm);
            return Results.Ok(new
            {
                factor = applied,
                ppm = appliedPpm,
                offsetHzAt10MHz = appliedPpm * 10.0,
            });
        });

        return endpoints;
    }
}

internal sealed record FrequencyCalibrationSetRequest(double Ppm);
