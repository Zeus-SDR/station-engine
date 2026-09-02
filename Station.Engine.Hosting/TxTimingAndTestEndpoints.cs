// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps TX timing, signal-generator, timeout, drive, and two-tone routes.</summary>
public static class TxTimingAndTestEndpoints
{
    public static IEndpointRouteBuilder MapTxTimingAndTestEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/tx/prekey-delay", (TxPreKeyDelaySetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.prekeyDelay ms={Ms}", req.DelayMs);
            if (req.DelayMs < 0 || req.DelayMs > 500)
                return Results.BadRequest(new { error = "delayMs must be 0..500" });
            var state = r.SetTxMoxPreKeyDelayMs(req.DelayMs);
            return Results.Ok(new { txMoxPreKeyDelayMs = state.TxMoxPreKeyDelayMs });
        });

        // TX tail (MOX hang) delay (issue #1294). Holds the wire MOX bit
        // asserted for N ms after a UI PTT release so audio still in the
        // browser→WDSP→IQ pipeline finishes clocking out before the radio
        // drops off the air. Voice modes only; ignored on CW. Thetis exposes
        // its related PTT Delay up to 5000 ms.
        endpoints.MapPost("/api/tx/tail-delay", (TxTailDelaySetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.tailDelay ms={Ms}", req.DelayMs);
            if (req.DelayMs < 0 || req.DelayMs > RadioService.MaxTailDelayMs)
                return Results.BadRequest(new { error = $"delayMs must be 0..{RadioService.MaxTailDelayMs}" });
            var state = r.SetTxMoxTailDelayMs(req.DelayMs);
            return Results.Ok(new { txMoxTailDelayMs = state.TxMoxTailDelayMs });
        });

        // Post-TX RX resume delay. Keeps RX audio/display muted for N ms after
        // MOX falls, then fades receive audio back in. This is the release-side
        // knob for relay/DSP transition splash.
        endpoints.MapPost("/api/tx/rx-resume-delay", (TxRxResumeDelaySetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.rxResumeDelay ms={Ms}", req.DelayMs);
            if (req.DelayMs < 0 || req.DelayMs > RadioService.MaxPostTxRxMuteDelayMs)
                return Results.BadRequest(new { error = $"delayMs must be 0..{RadioService.MaxPostTxRxMuteDelayMs}" });
            var state = r.SetTxPostTxRxMuteDelayMs(req.DelayMs);
            return Results.Ok(new { txPostTxRxMuteDelayMs = state.TxPostTxRxMuteDelayMs });
        });

        endpoints.MapPost("/api/tx/roger-beep", (RogerBeepSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.rogerBeep enabled={Enabled}", req.Enabled);
            var state = r.SetRogerBeepEnabled(req.Enabled);
            return Results.Ok(new { rogerBeepEnabled = state.RogerBeepEnabled });
        });

        // Hidden QRM test source. This arms a generated audio source into the
        // normal TX ingest path, but deliberately does not key MOX. It reaches
        // RF only while the operator has keyed TX.
        endpoints.MapPost("/api/tx/qrm", (SignalJammerSetRequest req, SignalJammerTxSource qrm) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "body required" });

            var snapshot = qrm.Configure(req);
            log.LogInformation(
                "api.tx.qrm enabled={Enabled} preset={Preset} level={Level} tone={ToneHz} drift={DriftHz} pulse={PulseRateHz:F1}",
                snapshot.Enabled, snapshot.Preset, snapshot.Level, snapshot.ToneHz, snapshot.DriftHz, snapshot.PulseRateHz);
            return Results.Ok(snapshot);
        });

        endpoints.MapPost("/api/tx/qrm/text", (SignalJammerTextRequest req, SignalJammerTxSource qrm, HttpContext context) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.SamplesBase64))
                return Results.BadRequest(new { error = "samplesBase64 required" });
            if (!TryDecodeFloat32Base64(req.SamplesBase64, out var samples, out var error))
                return Results.BadRequest(new { error });

            try
            {
                var remoteLeaseId = RemoteTxLease.TryGet(context, out var leaseId)
                    ? leaseId
                    : null;
                var snapshot = qrm.EnqueueText(
                    samples,
                    req.SampleRate,
                    req.AutoTransmit,
                    remoteLeaseId);
                log.LogInformation(
                    "api.tx.qrm.text samples={Samples} rate={Rate} autoTx={AutoTx} queued={Queued}",
                    samples.Length, req.SampleRate, req.AutoTransmit, snapshot.TextQueued);
                return Results.Ok(snapshot);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // TX timeout (issue #1270). Maximum single-transmission length that
        // TxMetersService allows before it trips MOX/TUN to protect the PA.
        // 0 disables the guard entirely only when the request explicitly
        // acknowledges that a disconnect during transmission may leave the
        // radio keyed. Any other value must be in [30, 600] s. A pre-warning
        // AlertKind.TxTimeoutWarning is emitted ~30 s before the trip fires so
        // the operator gets a heads-up rather than a silent drop.
        endpoints.MapPost("/api/tx/timeout", (TxTimeoutSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.tx.timeout seconds={S} disconnectRiskAcknowledged={Acknowledged}",
                req.Seconds,
                req.AcknowledgeDisconnectRisk);
            if (req.Seconds != 0 && (req.Seconds < RadioService.MinTxTimeoutSec || req.Seconds > RadioService.MaxTxTimeoutSec))
                return Results.BadRequest(new { error = $"seconds must be 0 (disabled) or {RadioService.MinTxTimeoutSec}..{RadioService.MaxTxTimeoutSec}" });
            try
            {
                var state = r.SetTxTimeoutSec(req.Seconds, req.AcknowledgeDisconnectRisk);
                return Results.Ok(new { txTimeoutSec = state.TxTimeoutSec });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // TUN drive %. Symmetric with /api/tx/drive; the same PA-gain math applies,
        // so equal slider positions emit equal watts. Backend selects between the
        // two sources based on whether TUN is keyed (TxService.TrySetTun →
        // RadioService.NotifyTunActive).
        endpoints.MapPost("/api/tx/tune-drive", (TuneDriveSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.tune-drive percent={Pct}", req.Percent);
            if (req.Percent < 0 || req.Percent > 100)
                return Results.BadRequest(new { error = "percent must be 0..100" });
            r.SetTuneDrive(req.Percent);
            return Results.Ok(new { tunePercent = r.Snapshot().TunePct });
        });

        // Two-tone test generator (TXA PostGen mode=1). Protocol-agnostic — works
        // on both P1 and P2 because it only touches WDSP TXA, not the wire format.
        endpoints.MapPost("/api/tx/twotone", (TwoToneSetRequest req, RadioService r, TxService tx, HttpContext context) =>
        {
            log.LogInformation(
                "api.tx.twotone enabled={On} f1={F1} f2={F2} mag={Mag}",
                req.Enabled, req.Freq1, req.Freq2, req.Mag);
            if (req.Mag is double m && (m < 0.0 || m > 1.0 || double.IsNaN(m)))
                return Results.BadRequest(new { error = "mag must be 0..1" });
            if (req.Freq1 is double f1 && (f1 < 50.0 || f1 > 5000.0 || double.IsNaN(f1)))
                return Results.BadRequest(new { error = "freq1 must be 50..5000 Hz" });
            if (req.Freq2 is double f2 && (f2 < 50.0 || f2 > 5000.0 || double.IsNaN(f2)))
                return Results.BadRequest(new { error = "freq2 must be 50..5000 Hz" });
            // TrySetTwoTone owns both the engine state (RadioService.SetTwoTone) and
            // the MOX side-effect — Thetis parity, setup.cs:11162-11165. Returns the
            // post-mutate snapshot via Snapshot(); on a connect-interlock failure
            // the request is rejected with 400.
            var ok = RemoteTxLease.TryGet(context, out var leaseId)
                ? tx.TrySetRemoteTwoTone(req, leaseId, out var err)
                : tx.TrySetTwoTone(req, out err);
            if (!ok)
                return Results.BadRequest(new { error = err });
            return Results.Ok(r.Snapshot());
        });

        return endpoints;
    }

    private static bool TryDecodeFloat32Base64(
        string samplesBase64,
        out float[] samples,
        out string error)
    {
        samples = [];
        error = "";
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(samplesBase64);
        }
        catch (FormatException)
        {
            error = "samplesBase64 must be valid base64";
            return false;
        }

        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
        {
            error = "samplesBase64 must contain float32 little-endian samples";
            return false;
        }

        int count = bytes.Length / sizeof(float);
        if (count > SignalJammerTxSource.MaxTextSamples)
        {
            error = $"text audio may not exceed {SignalJammerTxSource.MaxTextSamples} samples";
            return false;
        }

        samples = new float[count];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
        return true;
    }
}
