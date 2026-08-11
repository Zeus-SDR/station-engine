// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps the engine-owned CFC preset-library routes.</summary>
public static class CfcPresetEndpoints
{
    public static IEndpointRouteBuilder MapCfcPresetEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapGet("/api/tx/cfc/presets", (CfcPresetStore store) =>
            Results.Ok(new { presets = store.List() }));

        endpoints.MapPut("/api/tx/cfc/presets/{name}", (
            string name,
            CfcSetRequest req,
            CfcPresetStore store) =>
        {
            if (!TryValidateName(name, out var cleanName, out var nameError))
                return Results.BadRequest(new { error = nameError });
            if (req?.Config is not { } config)
                return Results.BadRequest(new { error = "Config required" });
            if (!TryValidateConfig(config, out var configError))
                return Results.BadRequest(new { error = configError });

            var saved = store.Save(cleanName, config);
            log.LogInformation("api.tx.cfc.presets.save name={Name}", saved.Name);
            return Results.Ok(saved);
        });

        endpoints.MapDelete("/api/tx/cfc/presets/{name}", (
            string name,
            CfcPresetStore store) =>
        {
            if (!TryValidateName(name, out var cleanName, out var nameError))
                return Results.BadRequest(new { error = nameError });

            if (!store.Delete(cleanName))
                return Results.NotFound(new { error = $"CFC preset '{cleanName}' not found" });

            log.LogInformation("api.tx.cfc.presets.delete name={Name}", cleanName);
            return Results.NoContent();
        });

        return endpoints;
    }

    private static bool TryValidateName(
        string? name,
        out string cleanName,
        out string error)
    {
        cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
        {
            error = "Preset name required";
            return false;
        }
        if (cleanName.Length > 80)
        {
            error = "Preset name must be 80 characters or fewer";
            return false;
        }
        if (cleanName.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#'))
        {
            error = "Preset name contains an invalid character";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryValidateConfig(CfcConfig? cfc, out string error)
    {
        if (cfc is null)
        {
            error = "Config required";
            return false;
        }
        if (!IsFinite(cfc.PreCompDb) || !IsFinite(cfc.PrePeqDb))
        {
            error = "CFC preCompDb and prePeqDb must be finite";
            return false;
        }
        if (cfc.Bands is null || cfc.Bands.Length != 10)
        {
            error = $"Bands must have exactly 10 entries; got {cfc.Bands?.Length ?? 0}";
            return false;
        }
        for (var index = 0; index < cfc.Bands.Length; index++)
        {
            var band = cfc.Bands[index];
            if (!IsFinite(band.FreqHz)
                || !IsFinite(band.CompLevelDb)
                || !IsFinite(band.PostGainDb))
            {
                error = $"CFC band {index + 1} values must be finite";
                return false;
            }
        }

        error = "";
        return true;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
