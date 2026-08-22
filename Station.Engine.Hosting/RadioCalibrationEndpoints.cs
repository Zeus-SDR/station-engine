// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

using System.Text.Json;

/// <summary>Maps radio frequency and S-meter calibration routes.</summary>
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

        endpoints.MapGet("/api/radio/smeter-calibration", (
            RadioService radio,
            SMeterCalibrationStore store) =>
            Results.Ok(SMeterCalibrationSnapshot(radio, store)));

        endpoints.MapPost("/api/radio/smeter-calibration", (
            JsonElement request,
            RadioService radio,
            SMeterCalibrationStore store) =>
        {
            if (request.ValueKind != JsonValueKind.Object
                || !request.TryGetProperty("offsetDb", out JsonElement value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out double requested)
                || !double.IsFinite(requested))
            {
                return Results.BadRequest(new
                {
                    error = "offsetDb must be a finite real number",
                });
            }

            var board = radio.EffectiveBoardKind;
            var variant = radio.EffectiveOrionMkIIVariant;
            double applied = store.Set(board, variant, requested);
            return Results.Ok(SMeterCalibrationSnapshot(
                board,
                variant,
                applied));
        });

        endpoints.MapPost("/api/radio/smeter-calibration/reset", (
            RadioService radio,
            SMeterCalibrationStore store) =>
        {
            var board = radio.EffectiveBoardKind;
            var variant = radio.EffectiveOrionMkIIVariant;
            double applied = store.Set(board, variant, 0.0);
            return Results.Ok(SMeterCalibrationSnapshot(
                board,
                variant,
                applied));
        });

        return endpoints;
    }

    private static SMeterCalibrationDto SMeterCalibrationSnapshot(
        RadioService radio,
        SMeterCalibrationStore store)
    {
        var board = radio.EffectiveBoardKind;
        var variant = radio.EffectiveOrionMkIIVariant;
        return SMeterCalibrationSnapshot(
            board,
            variant,
            store.Get(board, variant));
    }

    private static SMeterCalibrationDto SMeterCalibrationSnapshot(
        Zeus.Contracts.HpsdrBoardKind board,
        Zeus.Contracts.OrionMkIIVariant variant,
        double offsetDb)
    {
        return new SMeterCalibrationDto(
            offsetDb,
            board,
            variant,
            SMeterCalibrationStore.MinOffsetDb,
            SMeterCalibrationStore.MaxOffsetDb,
            SMeterCalibrationStore.StepDb);
    }
}

internal sealed record FrequencyCalibrationSetRequest(double Ppm);

internal sealed record SMeterCalibrationDto(
    double OffsetDb,
    Zeus.Contracts.HpsdrBoardKind BoardKind,
    Zeus.Contracts.OrionMkIIVariant Variant,
    double MinDb,
    double MaxDb,
    double StepDb);
