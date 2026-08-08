// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Maps the operator UI preference routes (theme, display, toolbar, NR
/// disclosure, operator identity, bottom-row pins, pan/waterfall split) on the
/// standalone station engine. These families were previously product-host-only,
/// so an attached SPA lost its saved theme / dB ranges / toolbar favorites /
/// operator profile on every launch — localStorage is only a fast-paint cache
/// and the loopback origin changes per launch. The handlers mirror the product
/// host's routes one-for-one so both hosts serve the same contract.
/// </summary>
public static class OperatorUiSettingsEndpoints
{
    public static IEndpointRouteBuilder MapOperatorUiSettingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // Panadapter background + dB windows + display performance. The PUT
        // pushes the validated, merged config into the running DSP pipeline so
        // the change is live without a reconnect — same as the product host.
        endpoints.MapGet("/api/display-settings", (DisplaySettingsStore store) => Results.Ok(store.Get()));

        endpoints.MapPut("/api/display-settings", (DisplaySettingsSetRequest req, DisplaySettingsStore store, DspPipelineService dsp) =>
        {
            if (string.IsNullOrWhiteSpace(req.Mode) || string.IsNullOrWhiteSpace(req.Fit))
                return Results.BadRequest(new { error = "mode and fit required" });
            store.SaveMode(req.Mode, req.Fit, req.RxTraceColor,
                req.DbMin, req.DbMax, req.TxDbMin, req.TxDbMax,
                req.WfDbMin, req.WfDbMax, req.WfTxDbMin, req.WfTxDbMax,
                req.TxDisplayCalOffsetDb, req.TxDisplayFftSize,
                req.TxDisplayWindow, req.TxDisplayAvgTauMs,
                req.WidebandDisplayEnabled,
                req.DisplayMaxFrameRateHz,
                req.DisplayDecimation,
                req.WaterfallUpdatePeriod,
                req.WidebandSignalMarkersEnabled);
            var saved = store.Get();
            dsp.ApplyDisplaySettings(saved);
            return Results.Ok(saved);
        });

        // Wideband signal markers snapshot (display-only, additive wire
        // surface — it never rides inside the binary DisplayFrame stream).
        // Polled by display clients while the markers overlay is enabled.
        endpoints.MapGet("/api/radio/wideband/signals", (DspPipelineService dsp) =>
            Results.Ok(dsp.GetWidebandSignalsSnapshot()));

        endpoints.MapGet("/api/display-settings/image", (DisplaySettingsStore store) =>
        {
            var img = store.GetImage();
            if (img is null) return Results.NotFound();
            return Results.File(img.Value.Bytes, img.Value.Mime);
        });

        // Multipart upload — single field "file", any image/* mime type. Capped
        // at 8 MB so a stray giant TIFF can't fill the prefs DB.
        endpoints.MapPut("/api/display-settings/image", async (HttpContext ctx, DisplaySettingsStore store) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "file field required" });
            const long MaxBytes = 8 * 1024 * 1024;
            if (file.Length > MaxBytes)
                return Results.BadRequest(new { error = $"file too large (max {MaxBytes} bytes)" });
            var mime = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType;
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "image/* content-type required" });
            using var ms = new MemoryStream(capacity: (int)file.Length);
            await file.CopyToAsync(ms);
            store.SaveImage(ms.ToArray(), mime);
            return Results.Ok(store.Get());
        });

        endpoints.MapDelete("/api/display-settings/image", (DisplaySettingsStore store) =>
        {
            store.DeleteImage();
            return Results.Ok(store.Get());
        });

        // Classic-layout bottom-row pin state — Logbook + TX Stage Meters.
        endpoints.MapGet("/api/bottom-pin", (BottomPinStore store) => Results.Ok(store.Get()));

        endpoints.MapPut("/api/bottom-pin", (BottomPinSetRequest req, BottomPinStore store) =>
        {
            store.Save(req.Logbook, req.TxMeters);
            return Results.Ok(store.Get());
        });

        // Vertical split between the panadapter and the waterfall in the Hero
        // panel. The store clamps PanPercent into 10..90.
        endpoints.MapGet("/api/pan-wf-split", (PanWfSplitStore store) => Results.Ok(store.Get()));

        endpoints.MapPut("/api/pan-wf-split", (PanWfSplitSetRequest req, PanWfSplitStore store) =>
        {
            var saved = store.Save(req.PanPercent);
            return Results.Ok(saved);
        });

        // Toolbar Mode/Band/Step favorite-slot pins + the live tuning step
        // (StepHz). POST patches only the fields supplied — null fields leave
        // the stored value untouched.
        endpoints.MapGet("/api/toolbar-settings", (ToolbarSettingsStore store) => Results.Ok(store.Get()));

        endpoints.MapPost("/api/toolbar-settings", (ToolbarSettingsSetRequest req, ToolbarSettingsStore store) =>
        {
            store.Save(req.Mode, req.Band, req.Step, req.StepHz);
            return Results.Ok(store.Get());
        });

        // Inline NR settings accordion disclosure state (NR1 / NR2 / NR4).
        endpoints.MapGet("/api/nr-ui-prefs", (NrUiPrefsStore store) => Results.Ok(store.Get()));

        endpoints.MapPut("/api/nr-ui-prefs", (NrUiPrefsSetRequest req, NrUiPrefsStore store) =>
        {
            store.Set(req.Nr1Expanded, req.Nr2Expanded, req.Nr4Expanded);
            return Results.Ok(store.Get());
        });

        // Operator UI theme ("dark" | "light") + per-CSS-variable colour
        // overrides. PUT replaces both atomically.
        endpoints.MapGet("/api/theme-settings", (ThemeSettingsStore store) => Results.Ok(store.Get()));

        endpoints.MapPut("/api/theme-settings", (ThemeSettingsSetRequest req, ThemeSettingsStore store) =>
        {
            store.Set(req.Theme, req.Overrides);
            return Results.Ok(store.Get());
        });

        // Shared operator identity (callsign + Maidenhead grid). The engine has
        // no QRZ session, so unlike the product host there is no QRZ home-
        // station fallback: the resolved values equal the saved override and
        // the FromQrz flags are always false. Same wire shape either way, so
        // the SPA's Settings page renders identically against both hosts.
        endpoints.MapGet("/api/operator",
            (OperatorIdentityStore store) => Results.Ok(Status(store)));

        endpoints.MapPost("/api/operator",
            (OperatorIdentity body, OperatorIdentityStore store) =>
            {
                store.Set(body);
                log.LogInformation("api.operator.set call={Call} grid={Grid}",
                    body.Callsign, body.Grid);
                return Results.Ok(Status(store));
            });

        return endpoints;
    }

    /// <summary>
    /// /api/operator status without a QRZ fallback source: the effective
    /// identity is exactly the saved override.
    /// </summary>
    internal static OperatorIdentityStatus Status(OperatorIdentityStore store)
    {
        var saved = store.Get();
        return new OperatorIdentityStatus(
            Callsign: saved.Callsign,
            Grid: saved.Grid,
            ResolvedCallsign: saved.Callsign,
            ResolvedGrid: saved.Grid,
            CallsignFromQrz: false,
            GridFromQrz: false);
    }
}
