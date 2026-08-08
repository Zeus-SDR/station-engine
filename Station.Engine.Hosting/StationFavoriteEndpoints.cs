// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps the station-wide favorite-slot persistence routes.</summary>
public static class StationFavoriteEndpoints
{
    public static IEndpointRouteBuilder MapStationFavoriteEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/station/favorites", (StationFavoriteStore store) =>
            Results.Ok(store.GetAll()));

        endpoints.MapPut("/api/station/favorites/{slot:int}", (
            int slot,
            StationFavoriteSetRequest request,
            StationFavoriteStore store,
            IServiceProvider services) =>
        {
            if (!StationFavoriteStore.IsValidSlot(slot))
                return Results.BadRequest(new { error = "slot must be from 1 through 5" });
            var radio = services.GetService<RadioService>();
            bool frequencyAvailable = radio?.IsExternalFrequencyAvailable(request.FrequencyHz)
                ?? request.FrequencyHz is >= TransverterFrequencyConverter.MinimumRadioFrequencyHz
                    and <= TransverterFrequencyConverter.MaximumRadioFrequencyHz;
            if (!frequencyAvailable)
                return Results.BadRequest(new { error = "frequencyHz is outside the native radio range and enabled transverter profiles" });
            if (!Enum.IsDefined(request.Mode))
                return Results.BadRequest(new { error = "mode is invalid" });
            if (request.FilterLowHz >= request.FilterHighHz)
                return Results.BadRequest(new { error = "filterLowHz must be less than filterHighHz" });

            return Results.Ok(store.Upsert(
                slot,
                request.FrequencyHz,
                request.Mode,
                request.FilterLowHz,
                request.FilterHighHz));
        });

        endpoints.MapDelete("/api/station/favorites/{slot:int}", (
            int slot,
            StationFavoriteStore store) =>
        {
            if (!StationFavoriteStore.IsValidSlot(slot))
                return Results.BadRequest(new { error = "slot must be from 1 through 5" });
            store.Clear(slot);
            return Results.NoContent();
        });

        return endpoints;
    }
}
