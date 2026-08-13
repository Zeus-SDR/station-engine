// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;
namespace Zeus.Server;

/// <summary>Maps core CW and TX-control routes.</summary>
public static class TxControlEndpoints
{
    public static IEndpointRouteBuilder MapTxControlEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/tx/mox", (MoxSetRequest req, TxService tx) =>
        {
            log.LogInformation("api.tx.mox on={On}", req.On);
            if (!tx.TrySetMox(req.On, out var err)) return Results.Conflict(new { error = err });
            return Results.Ok(new { moxOn = tx.IsMoxOn });
        });

        // CW keyer (zeus-drf). Body: { text, wpm? }. Returns 202 immediately;
        // playback happens on the engine's worker. WPM null = engine default
        // (currently 20); the engine clamps to 5..50. Empty text is allowed —
        // produces no symbols and resolves to Idle without keying.
        endpoints.MapPost("/api/cw/send", async (CwSendRequest req, CwEngine cw) =>
        {
            await cw.SendAsync(req.Text ?? string.Empty, req.Wpm, default).ConfigureAwait(false);
            return Results.Accepted();
        });

        // Hard abort. Drops the queue and signals the in-flight playback to
        // cancel. MOX falls on the next playback tick (≤ ChunkSamples / SR ≈
        // 10 ms). Returns 200 unconditionally — abort is best-effort.
        endpoints.MapPost("/api/cw/abort", (CwEngine cw) =>
        {
            cw.Abort("api.cw.abort");
            return Results.Ok();
        });

        // Persisted CW operator settings (WPM, Farnsworth, 6 macros,
        // sidetone gain/pitch). PATCH-shaped PUT: every field nullable so
        // the UI can save one slider or one macro without round-tripping
        // the whole record. Returns the post-merge snapshot so the client
        // can reconcile its store with what the server actually stored
        // (e.g. clamped values).
        endpoints.MapGet("/api/cw/settings", (CwSettingsStore store) =>
            Results.Ok(store.Get()));

        endpoints.MapPut("/api/cw/settings", (CwSettingsSetRequest req, CwSettingsStore store, CwSidetoneSource sidetone, RadioService radio) =>
        {
            // Save first so the persisted view is the source of truth even
            // if the live generator update races somehow. Then push the
            // (post-clamp) values to the live generator so a slider drag
            // updates pitch/gain without a restart.
            var snapshot = store.Save(req);
            sidetone.SetPitchHz(snapshot.SidetoneHz);
            sidetone.SetGainDb(snapshot.SidetoneGainDb);
            // Forward keyer speed (WPM) + mode + sidetone to the radio's
            // on-board iambic keyer so a paddle keys at the panel speed:
            // P1 → C&C 0x0B; P2 → TxSpecific internal-keyer arm (#1032). No-op
            // when no radio is connected (cached + re-pushed on the next
            // connect). See zeus-bks.
            radio.SetCwKeyerConfig(
                snapshot.Wpm,
                snapshot.KeyerMode,
                snapshot.SidetoneHz,
                snapshot.SidetoneGainDb,
                snapshot.BreakIn,
                snapshot.HangMs,
                snapshot.Weight,
                snapshot.PaddleReverse);
            return Results.Ok(snapshot);
        });

        // Mic-gain: N dB in [-40, +10], scales WDSP TXA panel-gain-1 the same
        // way Thetis does (console.cs:28805 setAudioMicGain → Audio.MicPreamp =
        // 10^(db/20) → cmaster.CMSetTXAPanelGain1). The negative range is the
        // important half: browser getUserMedia mics typically peak around
        // -10..-15 dBFS, which over-drives WDSP TXA + ALC and prints as
        // splatter on the air; without an attenuator the operator has nowhere
        // to back off. Range matches Thetis's MicGainMin/Max defaults
        // (console.cs:19151 = -40, :19163 = +10). RadioService persists the dB
        // value via RadioStateStore; the dB → linear (10^(db/20)) conversion
        // happens at the engine seam in DspPipelineService.
        endpoints.MapPost("/api/mic-gain", (MicGainSetRequest req, RadioService r) =>
        {
            var snap = r.SetTxMicGain(req.Db);
            return Results.Ok(new { micGainDb = snap.MicGainDb });
        });

        // Leveler max-gain ceiling in dB. Operator band is 0..20 dB (Thetis parity,
        // setup.designer.cs): 0 disables the headroom entirely (unity-cap Leveler);
        // Thetis's stock default is 15 (radio.cs:2979). Anything outside is a
        // 400 so a misbehaving client can't hand WDSP a value that'd saturate on
        // the first voiced sample. RadioService persists via RadioStateStore so
        // the operator's ceiling survives backend restart; the frontend no
        // longer needs to re-POST on WS reconnect.
        endpoints.MapPost("/api/tx/leveler-max-gain", (LevelerMaxGainSetRequest req, RadioService r) =>
        {
            if (req.Gain < 0.0 || req.Gain > 20.0 || double.IsNaN(req.Gain))
                return Results.BadRequest(new { error = "gain must be 0..20 dB" });
            log.LogInformation("api.tx.levelerMaxGain dB={Db:F1}", req.Gain);
            var snap = r.SetTxLevelerMaxGain(req.Gain);
            return Results.Ok(new { levelerMaxGainDb = snap.LevelerMaxGainDb });
        });

        // TUN: internal-tune carrier. Flips SetTXAPostGenRun on WDSP; server-side is
        // where the PRD's drive clamp to min(drive, 25) lives, and where we gate
        // mutual exclusion with MOX so the HL2 sees exactly one of them active.
        endpoints.MapPost("/api/tx/tun", async Task<IResult> (
            TunSetRequest req,
            TxService tx,
            [Microsoft.AspNetCore.Mvc.FromServices] TuneCarrierCommandCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            var result = await coordinator.SetAsync(req.On, tx, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                var body = new { error = result.Error };
                return result.ExternalFailure
                    ? Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Conflict(body);
            }
            return Results.Ok(new { tunOn = result.TunOn });
        });

        endpoints.MapPost("/api/tx/drive", (DriveSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.drive percent={Pct}", req.Percent);
            if (req.Percent < 0 || req.Percent > 100)
                return Results.BadRequest(new { error = "percent must be 0..100" });
            r.SetDrive(req.Percent);
            return Results.Ok(new { drivePercent = r.Snapshot().DrivePct });
        });

        endpoints.MapPost("/api/tx/drive-max", (DriveMaxSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.drive-max percent={Pct}", req.Percent);
            if (req.Percent < 1 || req.Percent > 100)
                return Results.BadRequest(new { error = "percent must be 1..100" });
            var state = r.SetDriveMaximum(req.Percent);
            return Results.Ok(new
            {
                driveMaxPercent = state.DriveMaxPct,
                drivePercent = state.DrivePct,
                tunePercent = state.TunePct,
            });
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapTxFidelityPolicyEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // TX fidelity policy and station-profile overrides. Built-in Studio/eSSB/DX
        // defaults live in the frontend; these routes persist only active target
        // selection and operator edits so diagnostics never duplicate profile data.
        endpoints.MapGet("/api/tx/fidelity-policy", (TxFidelityPolicyStore store) =>
            Results.Ok(store.Get()));

        endpoints.MapPut("/api/tx/fidelity-policy", (TxFidelityPolicyDto req, TxFidelityPolicyStore store) =>
        {
            if (!TryValidateTxFidelityPolicy(req, out var err))
                return Results.BadRequest(new { error = err });

            var saved = store.Set(req);
            log.LogInformation(
                "api.tx.fidelityPolicy profile={ProfileId} density={Density}",
                saved.ProfileId, saved.TargetSpectralDensity);
            return Results.Ok(saved);
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapTxMonitorEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        static IResult GetAudioSuitePreview(
            RadioService radio,
            DspPipelineService pipe,
            TxService tx)
        {
            var enabled = radio.Snapshot().TxMonitorEnabled;
            return Results.Ok(new
            {
                supported = true,
                enabled,
                meterOnly = enabled && pipe.TxMonitorMeterOnly,
                monitorOnTransmit = tx.MonitorOnTransmit,
            });
        }

        static IResult SetAudioSuitePreview(
            PreviewSetRequest body,
            TxService tx)
        {
            bool meterOnly = body.Enabled && (body.MeterOnly ?? false);
            var state = tx.ApplyTxMonitorPreview(
                body.Enabled,
                meterOnly,
                body.MonitorOnTransmit);
            return Results.Ok(new
            {
                supported = true,
                enabled = state.Enabled,
                meterOnly = state.MeterOnly,
                monitorOnTransmit = state.MonitorOnTransmit,
            });
        }

        // Audio Suite Preview toggle — operator-facing alias for TX Monitor.
        // It drives the WDSP TX-monitor path, which demodulates post-TXA IQ
        // back to mono audio after the full transmit chain (Audio Suite/VST
        // route, leveler, compressor, CFC, ALC, bandpass, CFIR). This keeps
        // Audio Suite "Preview ON" a 1:1 off-air comparison with what would
        // reach the radio, rather than the older plugin-chain-only preview.
        endpoints.MapGet("/api/audio-suite/preview", GetAudioSuitePreview);
        endpoints.MapPut("/api/audio-suite/preview", SetAudioSuitePreview);
        endpoints.MapGet("/api/tx-audio-suite/preview", GetAudioSuitePreview);
        endpoints.MapPut("/api/tx-audio-suite/preview", SetAudioSuitePreview);

        // Preview-path toggle. The engine call lives in
        // DspPipelineService.UpdateState so it lands beside the rest of the
        // TX-side seam plumbing on the next tick.
        endpoints.MapPost("/api/tx/monitor", (
            TxMonitorSetRequest req,
            TxService tx) =>
        {
            log.LogInformation("api.tx.monitor enabled={Enabled}", req.Enabled);
            var state = tx.ApplyTxMonitorPreview(
                req.Enabled,
                meterOnly: false,
                monitorOnTransmit: null);
            return Results.Ok(state.RadioState);
        });

        return endpoints;
    }

    internal static bool TryValidateTxAudioProfileIdPointer(string? id, out string error)
    {
        // ProfileId now points at a unified TX audio profile. The fixed 3-up
        // station-profile system is retired, and TxFidelityPolicyStore never
        // enforced catalog membership, so validate only the slug shape emitted
        // by TxAudioProfileService rather than restoring the retired whitelist.
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "profile id is required";
            return false;
        }
        if (id.Length > 256)
        {
            error = "profile id must be 256 characters or fewer";
            return false;
        }

        // Uppercase input is accepted intentionally; the storage authority
        // normalizes with ToLowerInvariant(), so input casing cannot collide.
        var previousWasSeparator = true;
        foreach (var ch in id)
        {
            if (char.IsLetterOrDigit(ch))
            {
                previousWasSeparator = false;
                continue;
            }
            if (ch == '-' && !previousWasSeparator)
            {
                previousWasSeparator = true;
                continue;
            }

            error = "profile id must be an alphanumeric slug with single '-' separators";
            return false;
        }
        if (previousWasSeparator)
        {
            error = "profile id must be an alphanumeric slug with single '-' separators";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryValidateTxFidelityPolicy(TxFidelityPolicyDto policy, out string error)
    {
        if (!TryValidateTxAudioProfileIdPointer(policy.ProfileId, out error))
            return false;
        if (policy.TargetSpectralDensity < 0 || policy.TargetSpectralDensity > 100)
        {
            error = "targetSpectralDensity must be 0..100";
            return false;
        }
        error = "";
        return true;
    }

}

internal sealed record PreviewSetRequest(
    bool Enabled,
    bool? MeterOnly = null,
    bool? MonitorOnTransmit = null);
