// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Text.Json;

namespace Zeus.Server;

/// <summary>
/// Routes the Zeus Link product bundle's settings mirror on the standalone
/// station engine. The bundle (ZeusProduct) reads its operator settings back
/// from here on startup (restoring feature toggles and amplifier configs
/// after an update, a profile switch, or a move to a new machine) and writes
/// through on every change, so the operator's bundle settings live in the
/// exportable zeus-prefs.db instead of only in a machine-local side file the
/// splash Database row cannot see. The payload is opaque product JSON; the
/// engine validates only that it is a JSON object and caps its size.
/// </summary>
public static class ProductBundleSettingsEndpoints
{
    public static IEndpointRouteBuilder MapProductBundleSettingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/product/bundle-settings", (ProductBundleSettingsStore store) =>
        {
            var entry = store.Get();
            return entry is null
                ? Results.NotFound(new { error = "No bundle settings synced yet." })
                : Results.Ok(new ProductBundleSettingsDto(entry.Json, entry.UpdatedUtcMs));
        });

        endpoints.MapPut("/api/product/bundle-settings",
            (ProductBundleSettingsPutRequest req, ProductBundleSettingsStore store) =>
        {
            if (string.IsNullOrWhiteSpace(req.Json))
                return Results.BadRequest(new { error = "json required" });
            try
            {
                using var document = JsonDocument.Parse(req.Json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest(new { error = "json must be an object" });
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"invalid json: {ex.Message}" });
            }

            try
            {
                var entry = store.Save(req.Json);
                return Results.Ok(new ProductBundleSettingsDto(entry.Json, entry.UpdatedUtcMs));
            }
            catch (ArgumentException ex)
            {
                // Validation failures (blank, oversized) are client errors;
                // anything else (disk full, DB fault) must surface as a 500 so
                // the bundle's sync loop treats it as a transient server side
                // problem instead of a malformed payload.
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return endpoints;
    }
}

public sealed record ProductBundleSettingsPutRequest(string? Json);
public sealed record ProductBundleSettingsDto(string Json, long UpdatedUtcMs);
