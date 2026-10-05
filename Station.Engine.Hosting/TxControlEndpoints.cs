// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Zeus.Contracts;
using Zeus.Dsp;
namespace Zeus.Server;

/// <summary>Maps core CW and TX-control routes.</summary>
public static class TxControlEndpoints
{
    // Serialises a CW settings PUT with the live keyer push that follows it.
    // The store drops a stale revision; this keeps that revision from
    // pushing the keyer after a newer one already has.
    private static readonly object CwSettingsPutGate = new();

    /// <summary>Combine engine FM readouts with the effective repeater shift:
    /// active only while the TX mode is FM, split is off and the signed offset
    /// is non-zero; EffectiveShift is the direction actually applied
    /// (Reverse folded in).</summary>
    internal static FmStatusDto BuildFmStatus(FmDspStatus dsp, StateDto state)
    {
        var rx = RadioFrequencyResolver.TxReceiver(state);
        long offset = rx.SplitEnabled
            ? 0
            : RadioFrequencyResolver.FmRepeaterOffsetHz(rx.Mode, rx.VfoHz, state.Fm);
        var shift = offset > 0 ? FmRepeaterShift.Plus
            : offset < 0 ? FmRepeaterShift.Minus
            : FmRepeaterShift.Simplex;
        return new FmStatusDto(
            dsp.ToneDecodeAvailable,
            dsp.DcsAvailable,
            dsp.DeviationMeterAvailable,
            dsp.CtcssLevelAvailable,
            dsp.DeEmphasisBypassAvailable,
            dsp.DetectedCtcssHz,
            dsp.DetectedDcsCode,
            dsp.DetectedDcsInverted,
            dsp.ToneSquelchOpen,
            dsp.TxPeakDeviationHz,
            RepeaterShiftActive: offset != 0,
            TxFrequencyHz: RadioFrequencyResolver.TxFrequencyHz(state),
            EffectiveShift: shift);
    }

    /// <summary>
    /// Merge one CW settings PUT, push an applied write to the live keyer,
    /// and hand every connected client the settings on record. A field whose
    /// revision the store has already passed is not written. When no field
    /// in the patch is newer, the keyer is left alone. The broadcast is
    /// still the stored snapshot, so a console that re-read before this PUT
    /// landed replaces the stale copy. The broadcast runs inside
    /// <see cref="CwSettingsPutGate"/> immediately after the save, so two
    /// PUTs publish in the order they were saved.
    /// </summary>
    internal static CwSettingsDto ApplyCwSettingsPut(
        CwSettingsSetRequest req,
        CwSettingsStore store,
        CwSidetoneSource sidetone,
        Action<CwSettingsDto> pushKeyer,
        Action<CwSettingsDto> publish)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sidetone);
        ArgumentNullException.ThrowIfNull(pushKeyer);
        ArgumentNullException.ThrowIfNull(publish);

        // Save first so the persisted view is the source of truth even
        // if the live generator update races somehow. Then push the
        // (post-clamp) values to the live generator so a slider drag
        // updates pitch/gain without a restart.
        CwSettingsWrite write;
        lock (CwSettingsPutGate)
        {
            write = store.Write(req);
            // Same lock as the save, and before the lock is released, so a
            // second PUT cannot publish ahead of this one.
            publish(write.Settings);
            if (write.Applied)
            {
                var snapshot = write.Settings;
                sidetone.SetPitchHz(snapshot.SidetoneHz);
                sidetone.SetGainDb(snapshot.SidetoneGainDb);
                // Forward keyer speed (WPM) + mode + sidetone to the radio's
                // on-board iambic keyer so a paddle keys at the panel speed:
                // P1 → C&C 0x0B; P2 → TxSpecific internal-keyer arm (#1032). No-op
                // when no radio is connected (cached + re-pushed on the next
                // connect). See zeus-bks.
                pushKeyer(snapshot);
            }
        }

        return write.Settings;
    }

    public static IEndpointRouteBuilder MapTxControlEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/tx/mox", (MoxSetRequest req, TxService tx, HttpContext context) =>
        {
            log.LogInformation("api.tx.mox on={On}", req.On);
            var ok = RemoteTxLease.TryGet(context, out var leaseId)
                ? tx.TrySetRemoteMox(req.On, leaseId, MoxSource.UI, out var err)
                : tx.TrySetMox(req.On, out err);
            if (!ok) return Results.Conflict(new { error = err });
            return Results.Ok(new { moxOn = tx.IsMoxOn });
        });

        // CW keyer (zeus-drf). Body: { text, wpm?, expectedAbortSeq?, receiver? }.
        // Returns 202 when the text is queued or when HALT already superseded
        // it. An explicit receiver that is not the current local CW TX target
        // is 409 and is not queued. WPM null = engine default (currently 20);
        // the engine clamps to 5..50. Empty text is allowed — produces no
        // symbols and resolves to Idle without keying. The engine does not
        // select the TX receiver.
        endpoints.MapPost("/api/cw/send", (CwSendRequest req, CwEngine cw, HttpContext context) =>
        {
            var leaseId = RemoteTxLease.TryGet(context, out var remoteLease)
                ? remoteLease
                : null;
            return EnqueueCwSend(req, cw, leaseId);
        });

        // Hard abort. An empty body is the historical global abort: it drops
        // the queue, cancels the in-flight playback, and returns the new
        // counter. A body that names job ids cancels only those jobs and
        // does not move the counter. An invalid id is 400 and cancels nothing.
        endpoints.MapPost("/api/cw/abort", async (CwEngine cw, HttpContext context) =>
        {
            var result = await AbortCw(cw, context.Request);
            if (result.StatusCode == StatusCodes.Status400BadRequest)
                return Results.BadRequest(new { error = result.Error });
            return result.Scoped
                ? Results.Ok(new { abortSeq = result.AbortSeq, scoped = true })
                : Results.Ok(new { abortSeq = result.AbortSeq });
        });

        // The abort counter for a page that has not seen a status frame yet:
        // a reload, a reconnect, or a CW+ pop-out. Sending 0 in that case
        // is refused once HALT has moved the engine on.
        endpoints.MapGet("/api/cw/status", (CwEngine cw) =>
            Results.Ok(new { abortSeq = cw.AbortSeq }));

        // Present only on an engine that can select the CW decoder's receiver
        // and can bind a CW send to that receiver. An older engine 404s.
        // A client that wants RX3 must see this first and must not send an
        // RX1 enable in its place. receiverBoundSend is the CW-send flag;
        // receiverSelection alone is the decoder flag.
        endpoints.MapGet("/api/cw/decoder/capabilities", () =>
            Results.Ok(CwDecoderCapabilities()));

        // Persisted CW operator settings (WPM, Farnsworth, 6 macros,
        // sidetone gain/pitch). PATCH-shaped PUT: every field nullable so
        // the UI can save one slider or one macro without round-tripping
        // the whole record. Returns the post-merge snapshot so the client
        // can reconcile its store with what the server actually stored
        // (e.g. clamped values).
        // CW station ID timer. The ID is mixed into the operator's own voice
        // transmission (TxAudioIngest); none of these routes key the radio.
        endpoints.MapGet("/api/cwid", (CwIdService cwId) => Results.Ok(cwId.Status()));

        endpoints.MapPost("/api/cwid/start", (CwIdStartRequest req, CwIdService cwId) =>
            cwId.Start(req.Callsign, out var error)
                ? Results.Ok(cwId.Status())
                : Results.BadRequest(new { error }));

        endpoints.MapPost("/api/cwid/stop", (CwIdStopRequest? req, CwIdService cwId) =>
        {
            cwId.Stop(req?.SkipFinalId ?? false);
            return Results.Ok(cwId.Status());
        });

        endpoints.MapPut("/api/cwid/settings", (CwIdSettingsSetRequest req, CwIdSettingsStore store, CwIdService cwId) =>
        {
            cwId.ApplySettings(store.Save(req));
            return Results.Ok(cwId.Status());
        });

        endpoints.MapGet("/api/cw/settings", (CwSettingsStore store) =>
            Results.Ok(store.Get()));

        endpoints.MapPut("/api/cw/settings", (
            CwSettingsSetRequest req,
            CwSettingsStore store,
            CwSidetoneSource sidetone,
            RadioService radio,
            StreamingHub hub) =>
        {
            var saved = ApplyCwSettingsPut(
                req,
                store,
                sidetone,
                snapshot => radio.SetCwKeyerConfig(
                    snapshot.Wpm,
                    snapshot.KeyerMode,
                    snapshot.SidetoneHz,
                    snapshot.SidetoneGainDb,
                    snapshot.BreakIn,
                    snapshot.HangMs,
                    snapshot.Weight,
                    snapshot.PaddleReverse),
                hub.BroadcastCwSettings);
            return Results.Ok(saved);
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

        // Legacy symmetric AM carrier level (0..125 %, clamped), used by AM/SAM
        // when broadcast AM is disabled. Part of the single live TX audio
        // config (StateDto.AmCarrierLevelPercent); TX audio profiles carry it.
        endpoints.MapPost("/api/tx/am-carrier-level", (AmCarrierLevelSetRequest req, RadioService r) =>
        {
            var snap = r.SetAmCarrierLevel(req.CarrierLevelPercent);
            return Results.Ok(new { carrierLevelPercent = snap.AmCarrierLevelPercent });
        });

        // TX audio profile selection mirror for a host that owns the profiles
        // itself (the product host). It reports the operator's profile and the
        // auto-applied one so StateDto.TxAudioProfileId / TxAudioAutoProfileId
        // reach every client, as the engine's own TxAudioProfileService does.
        endpoints.MapPut("/api/tx/audio-profile-selection", (TxAudioProfileSelectionRequest req, RadioService r) =>
        {
            static string? Clean(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
            r.SetTxAudioProfileSelection(Clean(req.ProfileId), Clean(req.AutoProfileId));
            return Results.NoContent();
        });

        // Broadcast AM (AM/SAM TX modulator): asymmetric +pos/-neg modulation,
        // clipper + brickwall LPF at the AM high cut, optional pre-emphasis and
        // polarity invert, PEP-preserving carrier. Body/response is
        // AmBroadcastConfig; out-of-range percentages are clamped (pos 100..150,
        // neg 80..100) and the sanitized config is returned. Also mirrored on
        // StateDto.AmBroadcast.
        endpoints.MapGet("/api/tx/am-broadcast", (RadioService r) =>
            Results.Ok(r.GetAmBroadcast()));

        endpoints.MapPost("/api/tx/am-broadcast", (AmBroadcastConfig? req, RadioService r) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "amBroadcast body required" });
            return Results.Ok(r.SetAmBroadcast(req));
        });

        // supported: true when the live WDSP engine carries the broadcast-AM
        // native stage, false when the engine lacks it (stale libwdsp or a
        // non-WDSP engine), null when no DSP engine is running.
        endpoints.MapGet("/api/tx/am-broadcast/status", (DspPipelineService pipe) =>
            Results.Ok(new { supported = pipe.CurrentEngine?.TxAmBroadcastSupported }));
        // FM configuration (Thetis FM console panel + Setup -> DSP -> FM):
        // deviation, FM audio cuts, pre-emphasis position, FM mic, CTCSS
        // encode + RX tone notch, detector limiter, repeater shift/reverse and
        // per-band offsets. RadioService normalizes (clamps / snaps) and
        // persists; the pipeline pushes it to TX and every RX channel.
        endpoints.MapGet("/api/fm", (RadioService r) =>
            Results.Ok(r.GetFmConfig()));

        endpoints.MapPost("/api/fm", (FmConfigSetRequest req, RadioService r) =>
        {
            if (req?.Fm is null)
                return Results.BadRequest(new { error = "fm required" });
            r.SetFmConfig(req.Fm);
            return Results.Ok(r.Snapshot());
        });

        // Live FM readouts for the operator strip (polled ~5 Hz): engine tone
        // decode / deviation meter + the effective repeater shift and TX carrier.
        endpoints.MapGet("/api/fm/status", (RadioService r, DspPipelineService pipeline) =>
            Results.Ok(BuildFmStatus(pipeline.GetFmDspStatus(), r.Snapshot())));

        // Access tone burst on demand. Only while keyed voice TX in FM; it
        // never keys the transmitter (409 otherwise).
        endpoints.MapPost("/api/fm/burst", (TxService tx) =>
            tx.TryStartFmToneBurst(out var error)
                ? Results.Ok(new { ok = true })
                : Results.Conflict(new { error }));

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
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var remoteLeaseId = RemoteTxLease.TryGet(context, out var leaseId)
                ? leaseId
                : null;
            var result = await coordinator.SetAsync(
                    req.On,
                    tx,
                    cancellationToken,
                    remoteLeaseId)
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
                volumeDb = pipe.TxMonitorVolumeDb,
            });
        }

        static IResult SetAudioSuitePreview(
            PreviewSetRequest body,
            TxService tx,
            DspPipelineService pipe)
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
                volumeDb = pipe.TxMonitorVolumeDb,
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
        endpoints.MapPut("/api/tx-audio-suite/monitor-volume", (
            MonitorVolumeSetRequest body,
            DspPipelineService pipe) => Results.Ok(new
            {
                volumeDb = pipe.SetTxMonitorVolumeDb(body.VolumeDb),
            }));

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

    /// <summary>
    /// Queue one CW send. <paramref name="remoteTxLeaseId"/> is the lease
    /// already resolved by the route. A <c>receiver-*</c> refusal is HTTP 409;
    /// a queued send and an abort-seq mismatch (<c>halted</c>) stay HTTP 202.
    /// </summary>
    internal static IResult EnqueueCwSend(CwSendRequest req, CwEngine cw, string? remoteTxLeaseId)
    {
        var refusal = cw.TryEnqueueSend(
            req.Text ?? string.Empty,
            req.Wpm,
            default,
            remoteTxLeaseId,
            req.ExpectedAbortSeq,
            req.Receiver,
            req.JobId);
        return CwSendHttpResult(refusal);
    }

    /// <summary>HTTP mapping for <see cref="CwEngine.TryEnqueueSend"/>.</summary>
    internal static IResult CwSendHttpResult(string? refusal) =>
        refusal == CwEngine.InvalidJobIdReason
            ? Results.BadRequest(new { error = refusal })
            : refusal is not null && refusal.StartsWith("receiver-", StringComparison.Ordinal)
                ? Results.Conflict(new { error = refusal })
                : Results.Accepted();

    /// <summary>
    /// Empty body and a JSON object with no job id are the global abort.
    /// <c>jobIds: []</c> cancels nothing. Any invalid id rejects the whole
    /// request before a job is cancelled.
    /// </summary>
    internal static async Task<CwAbortHttp> AbortCw(CwEngine cw, HttpRequest request)
    {
        if (!HasAbortBody(request))
            return new CwAbortHttp(StatusCodes.Status200OK, cw.Abort("api.cw.abort"), false, null);
        CwAbortRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<CwAbortRequest>();
        }
        catch (JsonException)
        {
            return new CwAbortHttp(StatusCodes.Status400BadRequest, cw.AbortSeq, false, CwEngine.InvalidJobIdReason);
        }
        if (body is null || (body.JobId is null && body.JobIds is null))
            return new CwAbortHttp(StatusCodes.Status200OK, cw.Abort("api.cw.abort"), false, null);
        var ids = new List<string>();
        if (body.JobId is not null) ids.Add(body.JobId);
        if (body.JobIds is not null) ids.AddRange(body.JobIds);
        if (ids.Count == 0)
            return new CwAbortHttp(StatusCodes.Status200OK, cw.AbortSeq, true, null);
        foreach (var id in ids)
        {
            if (!CwJobIds.IsValid(id))
                return new CwAbortHttp(StatusCodes.Status400BadRequest, cw.AbortSeq, false, CwEngine.InvalidJobIdReason);
        }
        return new CwAbortHttp(StatusCodes.Status200OK, cw.TryCancelJobs(ids), true, null);
    }

    private static bool HasAbortBody(HttpRequest request)
    {
        if (request.ContentLength is long length) return length > 0;
        var type = request.ContentType;
        return type is not null && type.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Body of <c>GET /api/cw/decoder/capabilities</c>.
    /// <c>maxReceivers</c> is <see cref="WireContract.MaxReceivers"/>.
    /// </summary>
    internal static object CwDecoderCapabilities() => new
    {
        receiverSelection = true,
        maxReceivers = WireContract.MaxReceivers,
        receiverBoundSend = true,
        cwJobIds = true,
        txReceiverIdleGuard = true,
    };

}

internal readonly record struct CwAbortHttp(int StatusCode, int AbortSeq, bool Scoped, string? Error);

internal sealed record CwAbortRequest(string? JobId = null, string[]? JobIds = null);

internal sealed record PreviewSetRequest(
    bool Enabled,
    bool? MeterOnly = null,
    bool? MonitorOnTransmit = null);

internal sealed record MonitorVolumeSetRequest(double VolumeDb);

/// <summary>PUT body for /api/tx/audio-profile-selection.</summary>
internal sealed record TxAudioProfileSelectionRequest(string? ProfileId, string? AutoProfileId);
