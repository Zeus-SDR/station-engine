// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps receiver noise-reduction and manual-notch engine routes.</summary>
public static class ReceiverDspEndpoints
{
    public static IEndpointRouteBuilder MapReceiverDspEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/rx/nr", (NrSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.nr nr={Nr} anf={Anf} snb={Snb} notches={Notches} nb={Nb} thr={Thr:F2}",
                req.Nr.NrMode, req.Nr.AnfEnabled, req.Nr.SnbEnabled,
                req.Nr.NbpNotchesEnabled, req.Nr.NbMode, req.Nr.NbThreshold);
            if (!Enum.IsDefined(req.Nr.NrMode))
                return Results.BadRequest(new { error = $"unknown NrMode {req.Nr.NrMode}" });
            if (!Enum.IsDefined(req.Nr.NbMode))
                return Results.BadRequest(new { error = $"unknown NbMode {req.Nr.NbMode}" });
            return Results.Ok(r.SetNr(req.Nr));
        });

        // Per-popover PATCH endpoints for the right-click NR settings panels (issue
        // #79). Each merges nullable fields onto the persisted NrConfig so the
        // operator can edit one knob without resending the whole NR block. Skipping
        // fields (or sending null) is a no-op for that field.
        endpoints.MapPost("/api/rx/nr2/post2", (Nr2Post2ConfigSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.nr2.post2 run={Run} factor={Factor} nlevel={Nlevel} rate={Rate} taper={Taper}",
                req.Post2Run, req.Post2Factor, req.Post2Nlevel, req.Post2Rate, req.Post2Taper);
            return Results.Ok(r.SetNr2Post2(req));
        });

        endpoints.MapPost("/api/rx/nr2/core", (Nr2CoreConfigSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.nr2.core gainMethod={Gm} npeMethod={Npm} aeRun={Ae} trainT1={T1} trainT2={T2}",
                req.GainMethod, req.NpeMethod, req.AeRun, req.TrainT1, req.TrainT2);
            try
            {
                return Results.Ok(r.SetNr2Core(req));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        endpoints.MapPost("/api/rx/nr4", (Nr4ConfigSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.nr4 reduction={Red} smoothing={Smo} whitening={Whi} noiseRescale={Nr} postThr={Pft} scaling={Sc} pos={Pos}",
                req.ReductionAmount, req.SmoothingFactor, req.WhiteningFactor,
                req.NoiseRescale, req.PostFilterThreshold, req.NoiseScalingType, req.Position);
            return Results.Ok(r.SetNr4(req));
        });

        // ---- NR3 (RNNoise) model management (issue #79 follow-up) ----
        // Zeus ships no model; NR3 stays hidden in the UI until the operator
        // installs an RNNoise weights file here — either a multipart upload or a
        // server-side fetch from a URL. Native availability + the installed
        // model name also ride StateDto, so the GET is a convenience for the
        // install panel.
        endpoints.MapGet("/api/rx/nr3/model", (RadioService r) =>
        {
            var s = r.Snapshot();
            return Results.Ok(new { available = s.WdspNr3RnnrAvailable, modelName = s.Nr3ModelName });
        });

        endpoints.MapPost("/api/rx/nr3/model", async (HttpRequest http, RadioService r) =>
        {
            if (!http.HasFormContentType || http.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "expected multipart/form-data with a 'file' field" });
            var file = http.Form.Files["file"] ?? http.Form.Files[0];
            if (file.Length == 0)
                return Results.BadRequest(new { error = "uploaded model file is empty" });
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            try
            {
                var state = r.InstallNr3Model(ms.ToArray(), file.FileName);
                log.LogInformation("api.rx.nr3.model.install name=\"{Name}\" bytes={Bytes}", file.FileName, ms.Length);
                return Results.Ok(state);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message); }
        });

        endpoints.MapPost("/api/rx/nr3/model/download", async (Nr3ModelDownloadRequest req, RadioService r, IHttpClientFactory httpFactory) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url) ||
                !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Results.BadRequest(new { error = "url must be an absolute http(s) URL" });
            try
            {
                var client = httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(60);
                // Cap the buffered response so a wrong (huge) URL can't OOM the
                // host; the model store enforces its own 64 MiB ceiling too.
                client.MaxResponseContentBufferSize = 80L * 1024 * 1024;
                var bytes = await client.GetByteArrayAsync(uri);
                var name = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(name)) name = "model.rnnn";
                var state = r.InstallNr3Model(bytes, name);
                log.LogInformation("api.rx.nr3.model.download url=\"{Url}\" bytes={Bytes}", req.Url, bytes.Length);
                return Results.Ok(state);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (HttpRequestException ex) { return Results.BadRequest(new { error = $"download failed: {ex.Message}" }); }
            catch (TaskCanceledException) { return Results.BadRequest(new { error = "download timed out" }); }
        });

        endpoints.MapDelete("/api/rx/nr3/model", (RadioService r) =>
        {
            log.LogInformation("api.rx.nr3.model.remove");
            return Results.Ok(r.RemoveNr3Model());
        });

        // Manual notch filters (MNF) — the client posts the full notch list on
        // every change (and on connect). GET returns the current set so a fresh
        // client (or a reconnect) can hydrate. Notches kill EMF/birdies in the
        // RX audio via WDSP's notch database.
        endpoints.MapGet("/api/rx/notches", (RadioService r) => Results.Ok(r.Notches));
        endpoints.MapPost("/api/rx/notches", (NotchListRequest req, RadioService r) =>
        {
            var notches = req?.Notches ?? Array.Empty<NotchDto>();
            log.LogInformation("api.rx.notches count={Count}", notches.Count);
            r.SetNotches(notches);
            return Results.Ok(r.Notches);
        });

        return endpoints;
    }

}
