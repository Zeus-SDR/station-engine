// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps core VFO and radio-tuning routes.</summary>
public static class RadioTuningEndpoints
{
    public static IEndpointRouteBuilder MapVfoEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/vfo", (VfoSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.vfo receiver={Receiver} hz={Hz}", req.Receiver, req.Hz);
            return r.SetReceiverVfo(req.Receiver, req.Hz);
        });

        endpoints.MapPost("/api/vfo/swap", (RadioService r) =>
        {
            log.LogInformation("api.vfo.swap");
            return r.SwapVfos();
        });

        endpoints.MapPost("/api/rx2", (Rx2SetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx2 enabled={Enabled} vfoBHz={VfoBHz} audioMode={AudioMode} afGainDb={AfGainDb}",
                req.Enabled, req.VfoBHz, req.AudioMode, req.AfGainDb);
            return r.SetRx2(req);
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapRadioTuningEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // VFO lock (Thetis chkVFOLock): blocks operator dial tuning; CAT/TCI
        // still tune. Pure software guard, no hardware effect.
        endpoints.MapPost("/api/radio/vfo-lock", (VfoLockSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.radio.vfo-lock locked={Locked}", req.Locked);
            return Results.Ok(r.SetVfoLock(req.Locked));
        });

        // RIT (Receiver Incremental Tuning). Both fields optional; Hz clamped to
        // ±99999. RX1 only.
        endpoints.MapPost("/api/rx/rit", (RitSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.rx.rit enabled={Enabled} hz={Hz}", req.Enabled, req.Hz);
            return Results.Ok(r.SetRit(req.Enabled, req.Hz));
        });

        // XIT (Transmit Incremental Tuning). Both fields optional; Hz clamped to
        // ±99999. Moves only the transmitted carrier.
        endpoints.MapPost("/api/tx/xit", (XitSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.xit enabled={Enabled} hz={Hz}", req.Enabled, req.Hz);
            return Results.Ok(r.SetXit(req.Enabled, req.Hz));
        });

        // Diversity combiner (Thetis DiversityForm). Every field optional.
        // P2/ANAN-class only (needs two phase-synchronous ADCs).
        endpoints.MapPost("/api/rx/diversity", (DiversitySetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.diversity enabled={Enabled} gain={Gain} phaseDeg={PhaseDeg} sourceRx={SourceRx}",
                req.Enabled, req.Gain, req.PhaseDeg, req.SourceRx);
            return Results.Ok(r.SetDiversity(req.Enabled, req.Gain, req.PhaseDeg, req.SourceRx));
        });

        // ATU tune request (Thetis ATUTune). Holds the Apollo/Alex auto-tune bit
        // (Protocol-1 C0=0x12 C2[4]) for DurationMs then auto-clears.
        // Bench-verification pending — see RadioService.RequestAtuTune.
        endpoints.MapPost("/api/radio/atu/tune", (AtuTuneRequest req, RadioService r) =>
        {
            log.LogInformation("api.radio.atu.tune durationMs={DurationMs}", req.DurationMs);
            return Results.Ok(r.RequestAtuTune(req.DurationMs));
        });

        endpoints.MapPost("/api/tx/vfo", (TxVfoSetRequest req, RadioService r) =>
        {
            if (!Enum.IsDefined(req.TxVfo))
                return Results.BadRequest(new { error = $"unknown txVfo {req.TxVfo}" });
            log.LogInformation("api.tx.vfo txVfo={TxVfo}", req.TxVfo);
            return Results.Ok(r.SetTxVfo(req.TxVfo));
        });

        // Select the transmit target by receiver index (0=RX1, 1=RX2, >=2 extra
        // DDC). Generalises /api/tx/vfo beyond the A/B pair; an out-of-range or
        // not-exposed index clamps to RX1 server-side.
        endpoints.MapPost("/api/tx/receiver", (TxReceiverSetRequest req, RadioService r) =>
        {
            if (req.Index < 0 || req.Index >= Zeus.Contracts.WireContract.MaxReceivers)
                return Results.BadRequest(new { error = $"tx receiver index out of range (0..{Zeus.Contracts.WireContract.MaxReceivers - 1})" });
            log.LogInformation("api.tx.receiver index={Index}", req.Index);
            return Results.Ok(r.SetTxReceiver(req.Index));
        });

        // Set the radio's hardware NCO (LO) frequency directly. Does not
        // move VfoHz — used by the panadapter pure-pan gesture when a drag
        // would carry the viewport outside the IQ capture window.
        // See docs/prd/panfall_behavior.md.
        endpoints.MapPost("/api/radio/lo", (RadioLoSetRequest req, RadioService r) =>
        {
            if (req.Hz < 0 || req.Hz > 60_000_000)
            {
                log.LogInformation("api.radio.lo rejected hz={Hz}", req.Hz);
                return Results.BadRequest(new { error = "hz out of range [0, 60000000]" });
            }
            log.LogInformation("api.radio.lo hz={Hz}", req.Hz);
            return Results.Ok(r.SetRadioLo(req.Hz));
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapCtunEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // Toggle CTUN (click-tune / centred tuning). When enabled, panadapter
        // clicks move only the dial and leave the hardware NCO frozen so the
        // operator can tune off-centre; TX retunes to the dial on key-down.
        // See docs/prd/panfall_behavior.md and StateDto.CtunEnabled.
        endpoints.MapPost("/api/radio/ctun", (CtunSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.radio.ctun enabled={Enabled}", req.Enabled);
            return r.SetCtunEnabled(req.Enabled);
        });

        return endpoints;
    }
}
