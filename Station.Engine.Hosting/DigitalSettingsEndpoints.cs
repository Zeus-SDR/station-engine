// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Globalization;
using System.Text.Json.Serialization;

namespace Zeus.Server;

// Digital workspace settings endpoints for the standalone engine. The desktop
// host kept these in core when the FT8/FT4/WSPR suite was extracted into the
// digital plugin (see Zeus.Server.Hosting/ZeusEndpoints.cs): the per-mode
// workspace settings store, the shared operator identity, and the Auto-CQ
// acknowledgement stamp are UI-shell prefs, not DSP. In Zeus Link attach mode
// the SPA calls the same relative paths against THIS host, and the product
// bundle's digital feature consumes the values the UI forwards (decode passes
// ride the ft8/enable request body), so without these routes the Zeus Digital
// settings tab had no backend in attach.
public static class DigitalSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDigitalSettingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // FT8/FT4/WSPR workspace behaviour + display preferences, persisted
        // server-side PER MODE. Pure behaviour/UI — none transmit; TX still
        // requires an explicit arm product-side.
        endpoints.MapGet("/api/ft8/settings",
            (string? mode, Ft8SettingsStore store) =>
                Results.Ok(store.Get(Ft8SettingsStore.NormalizeMode(mode))));

        endpoints.MapPost("/api/ft8/settings",
            (Zeus.Contracts.Ft8Settings body, string? mode, Ft8SettingsStore store) =>
            {
                var m = Ft8SettingsStore.NormalizeMode(mode);
                var saved = store.Set(m, body);
                log.LogInformation(
                    "api.ft8.settings mode={Mode} autoseq={Auto} passes={Passes} autolog={Log}",
                    m, saved.AutoSequence, saved.DecodePasses, saved.AutoLog);
                return Results.Ok(saved);
            });

        // Auto-CQ control-operator acknowledgement: per-process session flag
        // plus the persisted audit stamp.
        endpoints.MapGet("/api/ft8/autocq-ack",
            (OperatorAckStore store) =>
                Results.Ok(BuildAutoCqAckResponse(store)));

        endpoints.MapPost("/api/ft8/autocq-ack",
            (OperatorAckStore store) =>
            {
                var ackUtc = store.RecordAutoCqAck();
                log.LogInformation("api.ft8.autocq_ack recorded at {AckUtc:O}", ackUtc);
                return Results.Ok(BuildAutoCqAckResponse(store));
            });

        return endpoints;
    }

    private static AutoCqAckResponse BuildAutoCqAckResponse(OperatorAckStore store) =>
        new(
            store.AutoCqAcknowledgedThisSession,
            store.AutoCqLastAckUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private sealed record AutoCqAckResponse(
        [property: JsonPropertyName("acknowledgedThisSession")]
        bool AcknowledgedThisSession,
        [property: JsonPropertyName("lastAckUtc")]
        string? LastAckUtc);
}
