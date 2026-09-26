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

            // FM slots carry a repeater memory: the client's, or captured from
            // the live FmConfig (offset for this frequency's band). Non-FM → none.
            FmMemory? fm = request.Mode == RxMode.FM
                ? request.Fm is { } supplied
                    ? FmMemories.Normalize(supplied, request.FrequencyHz)
                    : radio?.CaptureFmMemory(request.FrequencyHz)
                : null;

            return Results.Ok(store.Upsert(
                slot,
                request.FrequencyHz,
                request.Mode,
                request.FilterLowHz,
                request.FilterHighHz,
                fm));
        });

        // Restore a favorite's FM repeater memory into the live FmConfig
        // without moving the dial. The client recalls VFO / mode / filter
        // through the ordinary routes first, then calls this when the
        // favorite carries an FM memory.
        endpoints.MapPost("/api/station/favorites/{slot:int}/recall-fm", (
            int slot,
            StationFavoriteStore store,
            RadioService radio) =>
        {
            if (!StationFavoriteStore.IsValidSlot(slot))
                return Results.BadRequest(new { error = "slot must be from 1 through 5" });
            var favorite = store.Get(slot);
            if (favorite is not { Fm: { } memory, FrequencyHz: long frequencyHz })
                return Results.NotFound(new { error = "favorite has no FM memory" });
            return Results.Ok(radio.RecallFmMemory(memory, frequencyHz));
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
