// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>Maps sample-rate, gain, AGC, squelch, and TX-leveling routes.</summary>
public static class RadioDspControlEndpoints
{
    public static IEndpointRouteBuilder MapRadioDspControlEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/sampleRate", (SampleRateSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.sampleRate rate={Rate}", req.Rate);
            if (!TryValidateSampleRate(req.Rate, out var err))
                return Results.BadRequest(new { error = err });
            return Results.Ok(r.SetSampleRate(MapHpsdrSampleRate(req.Rate)));
        });

        endpoints.MapPost("/api/preamp", (PreampSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.preamp on={On}", req.On);
            return r.SetPreamp(req.On);
        });

        endpoints.MapPost("/api/agcGain", (AgcGainSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.agcGain topDb={TopDb:F1}", req.TopDb);
            return r.SetAgcTop(req.TopDb);
        });

        // (Removed /api/agc/threshold + /disengage with the AGC knee — AGC-T is
        // the single manual AGC control now.)

        endpoints.MapPost("/api/rx/agc", (AgcSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.agc mode={Mode} slope={Slope} decayMs={Decay} hangMs={Hang} hangThr={Thr} fixedDb={Fixed}",
                req.Agc.Mode, req.Agc.Slope, req.Agc.DecayMs, req.Agc.HangMs,
                req.Agc.HangThreshold, req.Agc.FixedGainDb);
            if (!Enum.IsDefined(req.Agc.Mode))
                return Results.BadRequest(new { error = $"unknown AgcMode {req.Agc.Mode}" });
            return Results.Ok(r.SetAgc(req.Agc));
        });

        endpoints.MapPost("/api/rx/squelch", (SquelchSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.squelch enabled={Enabled} level={Level} adaptive={Adaptive} fixedSensitivity={FixedSensitivity}",
                req.Squelch.Enabled, req.Squelch.Level, req.Squelch.Adaptive, req.Squelch.FixedSensitivity);
            if (req.Squelch.Level < 0 || req.Squelch.Level > 100)
                return Results.BadRequest(new { error = $"Squelch Level {req.Squelch.Level} out of range 0..100" });
            if (req.Squelch.FixedSensitivity < SquelchConfig.MinFixedSensitivity ||
                req.Squelch.FixedSensitivity > SquelchConfig.MaxFixedSensitivity)
                return Results.BadRequest(new { error = $"Squelch FixedSensitivity {req.Squelch.FixedSensitivity} out of range 0..100" });
            return Results.Ok(r.SetSquelch(req.Squelch));
        });

        endpoints.MapPost("/api/tx/leveling", (TxLevelingSetRequest req, RadioService r) =>
        {
            var cfg = req.TxLeveling;
            log.LogInformation(
                "api.tx.leveling alcMaxGainDb={Alc:F1} alcDecayMs={AlcDecay} levelerEnabled={Lvlr} levelerDecayMs={LvlrDecay} compEnabled={Comp} compGainDb={CompGain:F1}",
                cfg.AlcMaxGainDb, cfg.AlcDecayMs, cfg.LevelerEnabled, cfg.LevelerDecayMs,
                cfg.CompressorEnabled, cfg.CompressorGainDb);
            // Range validation (Thetis parity §6.1-6.3). RadioService also clamps,
            // but a 400 lets a misbehaving client know its value was rejected.
            if (double.IsNaN(cfg.AlcMaxGainDb) || cfg.AlcMaxGainDb < 0.0 || cfg.AlcMaxGainDb > 120.0)
                return Results.BadRequest(new { error = "alcMaxGainDb must be 0..120 dB" });
            if (cfg.AlcDecayMs < 1 || cfg.AlcDecayMs > 50)
                return Results.BadRequest(new { error = "alcDecayMs must be 1..50" });
            if (cfg.LevelerDecayMs < 1 || cfg.LevelerDecayMs > 5000)
                return Results.BadRequest(new { error = "levelerDecayMs must be 1..5000" });
            if (double.IsNaN(cfg.CompressorGainDb) || cfg.CompressorGainDb < 0.0 || cfg.CompressorGainDb > 20.0)
                return Results.BadRequest(new { error = "compressorGainDb must be 0..20 dB" });
            return Results.Ok(r.SetTxLeveling(cfg));
        });

        endpoints.MapPost("/api/tx/phase-rotator", (TxPhaseRotatorSetRequest req, RadioService r) =>
        {
            if (req.TxPhaseRotator is not { } cfg)
                return Results.BadRequest(new { error = "txPhaseRotator required" });
            log.LogInformation(
                "api.tx.phaseRotator enabled={Enabled} cornerHz={Corner} stages={Stages} reverse={Reverse} autoMode={AutoMode}",
                cfg.Enabled, cfg.CornerHz, cfg.Stages, cfg.Reverse, cfg.AutoMode);
            if (cfg.CornerHz < TxPhaseRotatorConfig.MinCornerHz || cfg.CornerHz > TxPhaseRotatorConfig.MaxCornerHz)
                return Results.BadRequest(new { error = $"cornerHz must be {TxPhaseRotatorConfig.MinCornerHz}..{TxPhaseRotatorConfig.MaxCornerHz} Hz" });
            if (cfg.Stages < TxPhaseRotatorConfig.MinStages || cfg.Stages > TxPhaseRotatorConfig.MaxStages)
                return Results.BadRequest(new { error = $"stages must be {TxPhaseRotatorConfig.MinStages}..{TxPhaseRotatorConfig.MaxStages}" });
            return Results.Ok(r.SetTxPhaseRotator(cfg));
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapReceiverGainProtectionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/rx/afGain", (RxAfGainSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.rx.afGain db={Db:F1}", req.Db);
            return r.SetRxAfGain(req.Db);
        });

        endpoints.MapPost("/api/attenuator", (AttenuatorSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.attenuator db={Db}", req.Db);
            if (!TryValidateAttenDb(req.Db, out var err))
                return Results.BadRequest(new { error = err });
            return Results.Ok(r.SetAttenuator(new HpsdrAtten(req.Db)));
        });

        endpoints.MapPost("/api/auto-att", (AutoAttSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.auto-att enabled={Enabled}", req.Enabled);
            return r.SetAutoAtt(req.Enabled);
        });

        endpoints.MapGet("/api/rx/adc-protection", (RadioService r) =>
        {
            return Results.Ok(r.GetAdcProtectionStatus());
        });

        endpoints.MapPut("/api/rx/adc-protection", (AdcProtectionSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.adcProtection enabled={Enabled} attackMs={AttackMs} releaseMs={ReleaseMs} maxOffset={MaxOffset} magLimit={MagLimit}",
                req.Enabled, req.AttackMs, req.ReleaseMs, req.MaxOffsetDb, req.MagnitudeSoftLimit);
            return Results.Ok(r.SetAdcProtection(req));
        });

        endpoints.MapPost("/api/auto-agc", (AutoAgcSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.auto-agc enabled={Enabled}", req.Enabled);
            return r.SetAutoAgc(req.Enabled);
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapCfcEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // CFC (Continuous Frequency Compressor) — issue #123. POSTs the full 10-band
        // CFC profile + master flags. Defaults to OFF so existing operators see no
        // behavior change. Validation is done by RadioService.SetCfc — bad shapes
        // throw ArgumentException which the framework returns as 400.
        endpoints.MapPost("/api/tx/cfc", (CfcSetRequest req, RadioService r) =>
        {
            if (req?.Config is null)
                return Results.BadRequest(new { error = "Config required" });
            if (req.Config.Bands is null || req.Config.Bands.Length != 10)
                return Results.BadRequest(new { error = $"Bands must have exactly 10 entries; got {req.Config.Bands?.Length ?? 0}" });
            log.LogInformation(
                "api.tx.cfc enabled={Enabled} peq={Peq} preComp={Pre:F1}dB prePeq={PrePeq:F1}dB",
                req.Config.Enabled, req.Config.PostEqEnabled, req.Config.PreCompDb, req.Config.PrePeqDb);
            return Results.Ok(r.SetCfc(req));
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapTxPhaseRotatorUtilityEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/tx/phrot/auto-reset", ResetTxPhaseRotatorAutoEndpoint);
        endpoints.MapGet("/api/tx/phrot/asymmetry", GetTxPhaseRotatorAsymmetryEndpoint);
        return endpoints;
    }

    internal static IResult ResetTxPhaseRotatorAutoEndpoint(DspPipelineService dsp)
    {
        dsp.ResetTxPhaseRotatorAuto();
        return Results.Ok();
    }

    internal static IResult GetTxPhaseRotatorAsymmetryEndpoint(DspPipelineService dsp)
    {
        var asymmetry = dsp.GetTxPhaseRotatorAsymmetry();
        return asymmetry is null ? Results.NoContent() : Results.Ok(asymmetry);
    }

    private static bool TryValidateAttenDb(int db, out string error)
    {
        if (db >= HpsdrAtten.MinDb && db <= HpsdrAtten.MaxDb) { error = ""; return true; }
        error = $"atten must be in {HpsdrAtten.MinDb}..{HpsdrAtten.MaxDb} dB, got {db}.";
        return false;
    }

    static bool TryValidateSampleRate(int rate, out string error)
    {
        // 768/1536 kHz are Protocol-2 only; the P1 connect path
        // (RadioService.ConnectAsync) rejects them, and SetSampleRate clamps the
        // live P1 path, so it's safe to accept them here for the P2 flow.
        if (rate is 48_000 or 96_000 or 192_000 or 384_000 or 768_000 or 1_536_000) { error = ""; return true; }
        error = $"sampleRate must be one of {{48000, 96000, 192000, 384000, 768000, 1536000}}, got {rate}.";
        return false;
    }

    static HpsdrSampleRate MapHpsdrSampleRate(int hz) => hz switch
    {
        48_000 => HpsdrSampleRate.Rate48k,
        96_000 => HpsdrSampleRate.Rate96k,
        192_000 => HpsdrSampleRate.Rate192k,
        384_000 => HpsdrSampleRate.Rate384k,
        768_000 => HpsdrSampleRate.Rate768k,     // P2 only (RadioService clamps P1)
        1_536_000 => HpsdrSampleRate.Rate1536k,  // P2 only
        _ => throw new ArgumentOutOfRangeException(nameof(hz), hz, "validate before calling"),
    };
}
