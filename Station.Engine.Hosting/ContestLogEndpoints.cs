// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Text.Json;

namespace Zeus.Server;

public static class ContestLogEndpoints
{
    public static IEndpointRouteBuilder MapContestLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/contest-log", (ContestLogStore store) =>
        {
            var snapshot = store.Get();
            return snapshot is null ? Results.NoContent() : Results.Ok(snapshot);
        });

        endpoints.MapGet("/api/contest-logs", (ContestLogStore store) =>
            Results.Ok(store.List()));

        endpoints.MapGet("/api/contest-logs/{id}", (string id, ContestLogStore store) =>
            store.GetSession(id) is { } detail ? Results.Ok(detail) : Results.NotFound());

        endpoints.MapPut("/api/contest-log", (JsonElement request, ContestLogStore store) =>
        {
            try
            {
                var snapshot = ContestLogSnapshotDto.FromValidated(request);
                return Results.Ok(store.Put(snapshot));
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ContestLogConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Starts the first contest, or idempotently retries the same stable id.
        // A different active id conflicts until the operator finishes it.
        endpoints.MapPut("/api/contest-log/session", (JsonElement request, ContestLogStore store) =>
        {
            try
            {
                return Results.Ok(store.PutSession(request));
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ContestLogConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        endpoints.MapPut("/api/contest-log/qsos/{id}", (string id, JsonElement request, ContestLogStore store) =>
        {
            try
            {
                return Results.Ok(store.PutQso(id, request));
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ContestLogConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        endpoints.MapDelete("/api/contest-log/qsos/{id}",
            (string id, string? sessionId, ContestLogStore store) =>
            {
                try
                {
                    return store.DeleteQso(id, sessionId) ? Results.NoContent() : Results.NotFound();
                }
                catch (ContestLogConflictException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }
            });

        endpoints.MapDelete("/api/contest-log/qsos", (string? sessionId, ContestLogStore store) =>
        {
            try
            {
                store.DeleteQsos(sessionId);
                return Results.NoContent();
            }
            catch (ContestLogConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        endpoints.MapPost("/api/contest-log/finish", (JsonElement request, ContestLogStore store) =>
        {
            try
            {
                var finish = ContestLogFinishRequest.FromValidated(request);
                return Results.Ok(store.Finish(finish.SessionId, finish.FinishedUtc));
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ContestLogConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // End-session alias for clients using the original DELETE route. It
        // accepts a stable timestamp when available and otherwise uses the server clock.
        endpoints.MapDelete("/api/contest-log", (string? sessionId, string? finishedUtc, ContestLogStore store) =>
        {
            try
            {
                var timestamp = finishedUtc is null
                    ? DateTime.UtcNow
                    : ContestLogFinishRequest.ParseUtc(finishedUtc);
                store.Finish(sessionId, timestamp);
                return Results.NoContent();
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ContestLogConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        return endpoints;
    }
}

internal sealed record ContestLogFinishRequest(string SessionId, DateTime FinishedUtc)
{
    internal static ContestLogFinishRequest FromValidated(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("sessionId", out var sessionId) ||
            sessionId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(sessionId.GetString()))
            throw new JsonException("Finish request must have a non-empty sessionId.");
        if (!value.TryGetProperty("finishedUtc", out var finishedUtc) ||
            finishedUtc.ValueKind != JsonValueKind.String)
            throw new JsonException("Finish request must have a finishedUtc UTC timestamp.");
        return new ContestLogFinishRequest(sessionId.GetString()!, ParseUtc(finishedUtc.GetString()));
    }

    internal static DateTime ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.EndsWith('Z') ||
            !DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ||
            parsed.Offset != TimeSpan.Zero ||
            parsed.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new JsonException("finishedUtc must be a valid UTC timestamp with millisecond precision.");
        return parsed.UtcDateTime;
    }
}
