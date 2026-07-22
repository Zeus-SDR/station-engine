// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps receiver and transmitter filter-control routes.</summary>
public static class FilterEndpoints
{
    public static IEndpointRouteBuilder MapFilterEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/bandwidth", (BandwidthSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.bandwidth low={L} high={H}", req.Low, req.High);
            return r.SetFilter(req.Low, req.High);
        });

        // TX bandpass filter — signed Hz pair (LSB negative, DSB symmetric). Per-mode
        // family memory is managed in RadioService, identical shape to the RX filter.
        // Operator-editable via Settings → TX Filter panel.
        endpoints.MapPost("/api/tx-filter", (TxFilterSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx-filter low={L} high={H}", req.LowHz, req.HighHz);
            return r.SetTxFilter(req.LowHz, req.HighHz);
        });

        // SSB bandpass "rectangularity" — issue #871. RX and TX are independent
        // selectors; each pushes the chosen WDSP fir.c window code (0 = soft /
        // Blackman-Harris 4-term, 1 = sharp / BH 7-term) into the live engine and
        // persists to DspSettingsStore.
        endpoints.MapPost("/api/rx/filter-window", (BandpassWindowSetRequest req, RadioService r) =>
        {
            if (!Enum.IsDefined(req.Window))
                return Results.BadRequest(new { error = $"unknown BandpassWindow {req.Window}" });
            log.LogInformation("api.rx.filterWindow window={Window}", req.Window);
            return Results.Ok(r.SetRxBandpassWindow(req.Window));
        });

        endpoints.MapPost("/api/tx/filter-window", (BandpassWindowSetRequest req, RadioService r) =>
        {
            if (!Enum.IsDefined(req.Window))
                return Results.BadRequest(new { error = $"unknown BandpassWindow {req.Window}" });
            log.LogInformation("api.tx.filterWindow window={Window}", req.Window);
            return Results.Ok(r.SetTxBandpassWindow(req.Window));
        });

        // Filter preset endpoints (PRD §5.2). These are the preferred filter surface;
        // /api/bandwidth remains for backward compat. POST /api/filter also accepts
        // an optional PresetName to track which chip is active.
        endpoints.MapPost("/api/filter", (FilterSetRequest req, RadioService r) =>
        {
            if (req.Receiver is not (0 or 1))
                return Results.BadRequest(new { error = $"unknown receiver {req.Receiver}" });
            var receiver = req.Receiver == 1 ? TxVfo.B : TxVfo.A;
            // Debug, not Information: a passband drag posts here ~20x/sec, and
            // an enabled console/file sink then does 20 formatted writes/sec on
            // the request path for a gesture the operator can already see.
            log.LogDebug(
                "api.filter low={L} high={H} preset={P} receiver={Receiver}",
                req.LowHz,
                req.HighHz,
                req.PresetName,
                receiver);
            return Results.Ok(r.SetFilter(req.LowHz, req.HighHz, req.PresetName, receiver));
        });

        endpoints.MapGet("/api/filter/presets", (string? mode, RadioService r) =>
        {
            if (mode is null || !Enum.TryParse<RxMode>(mode, ignoreCase: true, out var rxMode))
                return Results.BadRequest(new { error = $"Unknown mode '{mode}'. Expected one of: {string.Join(", ", Enum.GetNames<RxMode>())}" });
            return Results.Ok(r.GetFilterPresets(rxMode));
        });

        endpoints.MapPost("/api/filter/presets", (FilterPresetWriteRequest req, RadioService r) =>
        {
            log.LogInformation("api.filter.presets mode={M} slot={S} low={L} high={H}", req.Mode, req.SlotName, req.LowHz, req.HighHz);
            if (req.SlotName is not ("VAR1" or "VAR2"))
                return Results.Conflict(new { error = "Fixed presets cannot be edited. Only VAR1 and VAR2 slots are writable." });
            if (!Enum.IsDefined(req.Mode))
                return Results.BadRequest(new { error = $"Unknown mode '{req.Mode}'." });
            r.SetFilterPresetOverride(req.Mode, req.SlotName, req.LowHz, req.HighHz);
            return Results.Ok(r.GetFilterPresets(req.Mode));
        });

        // Advanced-ribbon pane visibility. Persisted via FilterPresetStore so the
        // operator's close-the-ribbon choice survives a Zeus.Server restart.
        endpoints.MapPost("/api/filter/advanced-pane", (FilterAdvancedPaneRequest req, RadioService r) =>
        {
            log.LogInformation("api.filter.advancedPane open={Open}", req.Open);
            return r.SetFilterAdvancedPaneOpen(req.Open);
        });

        // Get favorite filter slots for a mode.
        endpoints.MapGet("/api/filter/favorites", (string? mode, RadioService r) =>
        {
            if (mode is null || !Enum.TryParse<RxMode>(mode, ignoreCase: true, out var rxMode))
                return Results.BadRequest(new { error = $"Unknown mode '{mode}'. Expected one of: {string.Join(", ", Enum.GetNames<RxMode>())}" });
            var slotNames = r.GetFavoriteFilterSlots(rxMode);
            return Results.Ok(new FilterFavoriteSlotsResponse(slotNames));
        });

        // Set favorite filter slots for a mode (up to 3).
        endpoints.MapPost("/api/filter/favorites", (FilterFavoriteSlotsRequest req, RadioService r) =>
        {
            log.LogInformation("api.filter.favorites mode={M} slots={S}", req.Mode, string.Join(",", req.SlotNames));
            if (!Enum.IsDefined(req.Mode))
                return Results.BadRequest(new { error = $"Unknown mode '{req.Mode}'." });
            if (req.SlotNames.Length > 3)
                return Results.BadRequest(new { error = "Maximum 3 favorite slots allowed." });
            return Results.Ok(r.SetFavoriteFilterSlots(req.Mode, req.SlotNames));
        });

        return endpoints;
    }
}
