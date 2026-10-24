// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeus.Server;

public sealed partial class ContestLogStore
{
    public ContestLogSnapshotDto CreateArchive(JsonElement request)
    {
        var session = ArchiveSession(ArchiveObject(request, "session"));
        var id = ContestLogSnapshotDto.SessionId(session);
        if (request.TryGetProperty("qsos", out var contacts) && (contacts.ValueKind != JsonValueKind.Array || contacts.GetArrayLength() != 0))
            throw new JsonException("Create an empty archive, then add contacts individually.");
        var (started, finished) = ArchiveTimes(request);
        lock (_sync)
        {
            if (_deletedArchives.Exists(x => x.Id == id)) throw Conflict("This contest id was deleted and cannot be reused.");
            if (_sessions.FindById(id) is not null) throw Conflict("This contest id already exists. Reload history before retrying.");
            InTransaction(() => _sessions.Insert(new ContestLogSessionEntry
            {
                Id = id, SessionJson = session.GetRawText(), StartedUtc = started, FinishedUtc = finished,
                UpdatedUtc = DateTime.UtcNow, ArchiveRevision = 1, OrderCounterInitialized = true,
            }));
            return Detail(id)!;
        }
    }

    public ContestLogSnapshotDto UpdateArchive(string id, JsonElement request)
    {
        var session = ArchiveSession(ArchiveObject(request, "session"));
        if (ContestLogSnapshotDto.SessionId(session) != id) throw new JsonException("Contest id cannot be changed.");
        var (started, finished) = ArchiveTimes(request);
        lock (_sync)
        {
            var row = RequireArchive(id, Revision(request));
            var old = Parse(row.SessionJson);
            if (Str(session, "createdUtc") != Str(old, "createdUtc")) throw new JsonException("Contest creation timestamp cannot be changed.");
            InTransaction(() =>
            {
                row.SessionJson = session.GetRawText();
                row.StartedUtc = started;
                row.FinishedUtc = finished;
                TouchArchive(row);
            });
            return Detail(id)!;
        }
    }

    public ContestLogSnapshotDto SaveArchiveQso(string id, string qsoId, JsonElement request, bool create)
    {
        var input = ArchiveObject(request, "qso");
        if (ContestLogSnapshotDto.QsoSessionId(input, qsoId) != id) throw new JsonException("QSO must belong to this contest.");
        ValidateArchiveQso(input);
        lock (_sync)
        {
            var session = RequireArchive(id, Revision(request));
            var old = _qsos.FindById(qsoId);
            if (old is not null && old.SessionId != id) throw Conflict("QSO id belongs to another contest.");
            if (create && old is not null) throw Conflict("QSO id already exists. Reload history before retrying.");
            if (!create && old is null) throw new KeyNotFoundException("QSO no longer exists. Reload history.");
            var value = JsonNode.Parse(input.GetRawText())!.AsObject();
            if (old is not null)
            {
                var previous = Parse(old.QsoJson);
                value["pushedAt"] = previous.TryGetProperty("pushedAt", out var pushed) ? JsonNode.Parse(pushed.GetRawText()) : null;
                value["mainLogId"] = previous.TryGetProperty("mainLogId", out var mainId) ? JsonNode.Parse(mainId.GetRawText()) : null;
            }
            else { value["pushedAt"] = null; value["mainLogId"] = null; }
            value["dupe"] = false;
            value["call"] = value["call"]!.GetValue<string>().Trim().ToUpperInvariant();
            InTransaction(() =>
            {
                var order = old?.Order ?? NextOrder(session);
                if (old is null) { session.NextQsoOrder = checked(order + 1); session.OrderCounterInitialized = true; }
                _qsos.Upsert(new ContestLogQsoEntry
                {
                    Id = qsoId, SessionId = id, Order = order,
                    QsoJson = value.ToJsonString(), UpdatedUtc = DateTime.UtcNow,
                });
                RecalculateArchiveDupes(id);
                TouchArchive(session);
            });
            return Detail(id)!;
        }
    }

    public ContestLogSnapshotDto DeleteArchiveQso(string id, string qsoId, long revision)
    {
        lock (_sync)
        {
            var session = RequireArchive(id, revision);
            var qso = _qsos.FindById(qsoId) ?? throw new KeyNotFoundException("QSO no longer exists. Reload history.");
            if (qso.SessionId != id) throw Conflict("QSO belongs to another contest.");
            InTransaction(() => { _qsos.Delete(qsoId); RecalculateArchiveDupes(id); TouchArchive(session); });
            return Detail(id)!;
        }
    }

    public void DeleteArchive(string id, long revision)
    {
        lock (_sync)
        {
            if (_deletedArchives.Exists(x => x.Id == id)) return;
            RequireArchive(id, revision);
            InTransaction(() => { _deletedArchives.Insert(new ContestLogDeletedArchive { Id = id }); _qsos.DeleteMany(x => x.SessionId == id); _sessions.Delete(id); });
        }
    }

    private ContestLogSessionEntry RequireArchive(string id, long revision)
    {
        var row = _sessions.FindById(id) ?? throw new KeyNotFoundException("Contest no longer exists. Reload history.");
        if (row.FinishedUtc is null || ActiveId() == id) throw Conflict("Only archived contests can be managed here.");
        if (revision < 0 || row.ArchiveRevision != revision) throw Conflict("This archive changed in another window. Reload history before saving again.");
        return row;
    }

    private void TouchArchive(ContestLogSessionEntry row)
    {
        row.ArchiveRevision = checked(row.ArchiveRevision + 1);
        row.UpdatedUtc = DateTime.UtcNow;
        _sessions.Update(row);
    }

    private void RecalculateArchiveDupes(string id)
    {
        var worked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in QsoRows(id).ToList())
        {
            var value = Parse(row.QsoJson);
            var key = JsonSerializer.Serialize(new[] { Str(value, "call")?.Trim().ToUpperInvariant(), Str(value, "band")?.Trim().ToUpperInvariant(), Str(value, "mode")?.Trim().ToUpperInvariant() });
            var dupe = !worked.Add(key);
            if (Bool(value, "dupe") == dupe) continue;
            var updated = JsonNode.Parse(row.QsoJson)!.AsObject();
            updated["dupe"] = dupe;
            row.QsoJson = updated.ToJsonString();
            row.UpdatedUtc = DateTime.UtcNow;
            _qsos.Update(row);
        }
    }

    internal static JsonElement ArchiveObject(JsonElement request, string key)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new JsonException($"{key} must be an object.");
        return value;
    }

    private static long Revision(JsonElement request)
    {
        if (!request.TryGetProperty("expectedRevision", out var value) || !value.TryGetInt64(out var revision) || revision < 0)
            throw new JsonException("A non-negative expectedRevision is required. Reload history before editing.");
        return revision;
    }

    private static JsonElement ArchiveSession(JsonElement session)
    {
        ContestLogSnapshotDto.SessionId(session);
        foreach (var key in new[] { "name", "contestId", "stationCall", "operator", "sentExchange", "exchangeLabel", "modeCategory" }) RequiredText(session, key);
        ParseArchiveUtc(session, "createdUtc");
        return session.Clone();
    }

    private static (DateTime Started, DateTime Finished) ArchiveTimes(JsonElement request)
    {
        var start = ParseArchiveUtc(request, "startedUtc");
        var finish = ParseArchiveUtc(request, "finishedUtc");
        if (finish < start) throw new JsonException("Contest finish must be at or after its start.");
        return (start, finish);
    }

    private static DateTime ParseArchiveUtc(JsonElement value, string key)
    {
        try { return ContestLogFinishRequest.ParseUtc(RequiredText(value, key)); }
        catch (JsonException) { throw new JsonException($"{key} must be a valid UTC timestamp ending in Z with millisecond precision."); }
    }

    private static string RequiredText(JsonElement value, string key)
    {
        var text = Str(value, key);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256) throw new JsonException($"{key} is required and must be at most 256 characters.");
        return text;
    }

    private static void ValidateArchiveQso(JsonElement value)
    {
        foreach (var key in new[] { "call", "band", "mode", "rstSent", "rstReceived", "sentExchange", "receivedExchange" }) RequiredText(value, key);
        ParseArchiveUtc(value, "timestampUtc");
        if (!value.TryGetProperty("frequencyMhz", out var frequency) || !frequency.TryGetDouble(out var mhz) || !double.IsFinite(mhz) || mhz <= 0)
            throw new JsonException("frequencyMhz must be a positive finite number.");
        if (Str(value, "operatingStyle") is not ("run" or "s&p")) throw new JsonException("Operating style must be run or s&p.");
        if (Str(value, "notes") is not { Length: <= 4096 }) throw new JsonException("notes must be text up to 4096 characters.");
    }
}

public static class ContestHistoryManagementEndpoints
{
    public static IEndpointRouteBuilder MapContestHistoryManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/contest-logs", (JsonElement request, ContestLogStore store) => Run(() => Results.Ok(store.CreateArchive(request))));
        endpoints.MapPut("/api/contest-logs/{id}", (string id, JsonElement request, ContestLogStore store) => Run(() => Results.Ok(store.UpdateArchive(id, request))));
        endpoints.MapDelete("/api/contest-logs/{id}", (string id, long revision, ContestLogStore store) => Run(() => { store.DeleteArchive(id, revision); return Results.NoContent(); }));
        endpoints.MapPost("/api/contest-logs/{id}/qsos", (string id, JsonElement request, ContestLogStore store) => Run(() => Results.Ok(store.SaveArchiveQso(id, ContestLogSnapshotDto.SessionId(ContestLogStore.ArchiveObject(request, "qso")), request, true))));
        endpoints.MapPut("/api/contest-logs/{id}/qsos/{qsoId}", (string id, string qsoId, JsonElement request, ContestLogStore store) => Run(() => Results.Ok(store.SaveArchiveQso(id, qsoId, request, false))));
        endpoints.MapDelete("/api/contest-logs/{id}/qsos/{qsoId}", (string id, string qsoId, long revision, ContestLogStore store) => Run(() => Results.Ok(store.DeleteArchiveQso(id, qsoId, revision))));
        return endpoints;
    }

    private static IResult Run(Func<IResult> action)
    {
        try { return action(); }
        catch (ContestLogConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { return Results.BadRequest(new { error = "Invalid archive request: " + ex.Message }); }
    }
}
