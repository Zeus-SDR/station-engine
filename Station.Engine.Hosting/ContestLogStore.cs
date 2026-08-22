// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
using System.Text.Json;
using LiteDB;

namespace Zeus.Server;

public sealed class ContestLogStore : IDisposable
{
    private const int SingletonId = 1;
    private readonly Zeus.Data.SharedLiteDatabase.Lease _lease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<ContestLogSessionEntry> _sessions;
    private readonly ILiteCollection<ContestLogActiveEntry> _active;
    private readonly ILiteCollection<ContestLogQsoEntry> _qsos;
    private readonly ILogger<ContestLogStore> _log;
    private readonly object _sync = new();

    public ContestLogStore(ILogger<ContestLogStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var path = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _lease = Zeus.Data.SharedLiteDatabase.Acquire(path);
        _db = _lease.Database;
        _sessions = _db.GetCollection<ContestLogSessionEntry>("contest_log_sessions");
        _active = _db.GetCollection<ContestLogActiveEntry>("contest_log_active");
        _qsos = _db.GetCollection<ContestLogQsoEntry>("contest_log_qsos");
        _sessions.EnsureIndex(x => x.StartedUtc);
        _qsos.EnsureIndex(x => x.SessionId);
        _qsos.EnsureIndex(x => x.Order);
        MigrateV1();
    }

    public ContestLogSnapshotDto? Get()
    {
        lock (_sync)
        {
            var id = ActiveId();
            return id is null ? null : Detail(id);
        }
    }

    public ContestLogSnapshotDto? GetSession(string id)
    {
        lock (_sync) return Detail(id);
    }

    public IReadOnlyList<ContestLogSummaryDto> List()
    {
        lock (_sync)
            return _sessions.Query().Where(x => x.FinishedUtc != null)
                .OrderByDescending(x => x.StartedUtc).ToEnumerable()
                .Select(Summary).ToList();
    }

    // Migration/recovery merge. Omitted QSO ids are never deletions.
    public ContestLogSnapshotDto Put(ContestLogSnapshotDto snapshot)
    {
        var id = ContestLogSnapshotDto.SessionId(snapshot.Session);
        var incoming = BuildQsos(snapshot.Qsos, id);
        lock (_sync)
        {
            var active = ActiveId();
            if (active is not null && active != id) throw Conflict("Snapshot belongs to another active contest.");
            var session = _sessions.FindById(id);
            if (session?.FinishedUtc is not null) throw Conflict("Archived contests cannot be mutated.");
            InTransaction(() =>
            {
                var now = DateTime.UtcNow;
                var sessionRow = new ContestLogSessionEntry
                {
                    Id = id,
                    SessionJson = snapshot.Session.GetRawText(),
                    StartedUtc = session?.StartedUtc ?? Started(snapshot.Session, now),
                    UpdatedUtc = now,
                    NextQsoOrder = session is null ? 0 : NextOrder(session),
                    OrderCounterInitialized = true,
                };
                _sessions.Upsert(sessionRow);
                SetActive(id);
                foreach (var row in incoming)
                {
                    var old = _qsos.FindById(row.Id);
                    if (old is not null && old.SessionId != id) throw Conflict("QSO id belongs to another contest.");
                    row.Order = old?.Order ?? sessionRow.NextQsoOrder++;
                    _qsos.Upsert(row);
                }
                _sessions.Update(sessionRow);
            });
            return Detail(id)!;
        }
    }

    public ContestLogSnapshotDto PutSession(JsonElement value)
    {
        var id = ContestLogSnapshotDto.SessionId(value);
        lock (_sync)
        {
            var active = ActiveId();
            if (active is not null && active != id) throw Conflict("Finish the active contest before starting another.");
            var old = _sessions.FindById(id);
            if (old?.FinishedUtc is not null) throw Conflict("An archived contest cannot be restarted.");
            InTransaction(() =>
            {
                var now = DateTime.UtcNow;
                _sessions.Upsert(new ContestLogSessionEntry
                {
                    Id = id,
                    SessionJson = value.GetRawText(),
                    StartedUtc = old?.StartedUtc ?? Started(value, now),
                    UpdatedUtc = now,
                    NextQsoOrder = old is null ? 0 : NextOrder(old),
                    OrderCounterInitialized = true,
                });
                SetActive(id);
            });
            return Detail(id)!;
        }
    }

    public ContestLogSnapshotDto Finish(string? expectedId, DateTime finishedUtc)
    {
        if (finishedUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Finish timestamp must be UTC.", nameof(finishedUtc));
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(expectedId))
                throw Conflict("A contest session id is required.");
            var requested = _sessions.FindById(expectedId);
            if (requested?.FinishedUtc is not null)
                return Detail(expectedId)!; // idempotent even after another contest starts
            RequireActive(expectedId);
            InTransaction(() =>
            {
                var row = _sessions.FindById(expectedId!)!;
                row.FinishedUtc = finishedUtc;
                row.UpdatedUtc = DateTime.UtcNow;
                _sessions.Update(row);
                _active.Delete(SingletonId);
            });
            return Detail(expectedId!)!;
        }
    }

    public JsonElement PutQso(string id, JsonElement value)
    {
        var sessionId = ContestLogSnapshotDto.QsoSessionId(value, id);
        lock (_sync)
        {
            JsonElement result = default;
            InTransaction(() =>
            {
                RequireActive(sessionId);
                var old = _qsos.FindById(id);
                if (old is not null && old.SessionId != sessionId) throw Conflict("QSO id belongs to another contest.");
                var order = old?.Order;
                if (order is null)
                {
                    var session = _sessions.FindById(sessionId)!;
                    order = NextOrder(session);
                    session.NextQsoOrder = checked(order.Value + 1);
                    session.OrderCounterInitialized = true;
                    session.UpdatedUtc = DateTime.UtcNow;
                    _sessions.Update(session);
                }
                _qsos.Upsert(new ContestLogQsoEntry
                {
                    Id = id, SessionId = sessionId, Order = order.Value,
                    QsoJson = value.GetRawText(), UpdatedUtc = DateTime.UtcNow,
                });
                result = value.Clone();
            });
            return result;
        }
    }

    public bool DeleteQso(string id, string? sessionId)
    {
        lock (_sync)
        {
            RequireActive(sessionId);
            var row = _qsos.FindById(id);
            if (row is null) return false;
            if (row.SessionId != sessionId) throw Conflict("QSO belongs to another contest.");
            return _qsos.Delete(id);
        }
    }

    public void DeleteQsos(string? sessionId)
    {
        lock (_sync)
        {
            RequireActive(sessionId);
            _qsos.DeleteMany(x => x.SessionId == sessionId);
        }
    }

    public void Dispose() => _lease.Dispose();

    private ContestLogSnapshotDto? Detail(string id)
    {
        var row = _sessions.FindById(id);
        return row is null ? null : new ContestLogSnapshotDto(
            Parse(row.SessionJson), QsoRows(id).Select(x => Parse(x.QsoJson)).ToList(),
            Utc(row.StartedUtc), row.FinishedUtc is null ? null : Utc(row.FinishedUtc.Value));
    }

    private ContestLogSummaryDto Summary(ContestLogSessionEntry row)
    {
        var session = Parse(row.SessionJson);
        var qsos = QsoRows(row.Id).Select(x => Parse(x.QsoJson)).ToList();
        var stamps = qsos.Select(Timestamp).Where(x => x.HasValue).Select(x => x!.Value).Order().ToArray();
        var startedUtc = Utc(row.StartedUtc);
        var finishedUtc = Utc(row.FinishedUtc!.Value);
        var hours = Math.Max(0, (finishedUtc - startedUtc).TotalHours);
        return new ContestLogSummaryDto(
            row.Id, session, startedUtc, finishedUtc, qsos.Count,
            qsos.Count(x => Bool(x, "dupe")),
            qsos.Count(x => !string.IsNullOrWhiteSpace(Str(x, "pushedAt"))),
            qsos.Select(x => Str(x, "call")).Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase).Count,
            hours > 0 ? qsos.Count / hours : 0, Peak(stamps),
            Distinct(qsos, "band"), Distinct(qsos, "mode"));
    }

    private IEnumerable<ContestLogQsoEntry> QsoRows(string id) =>
        _qsos.Query().Where(x => x.SessionId == id).OrderBy(x => x.Order).ToEnumerable();

    private long NextOrder(ContestLogSessionEntry session)
    {
        if (session.OrderCounterInitialized) return session.NextQsoOrder;
        var row = _qsos.Query().Where(x => x.SessionId == session.Id)
            .OrderByDescending(x => x.Order).Limit(1).FirstOrDefault();
        return row is null ? 0 : checked(row.Order + 1);
    }

    private string? ActiveId() => _active.FindById(SingletonId)?.SessionId;
    private void SetActive(string id) => _active.Upsert(new ContestLogActiveEntry { Id = SingletonId, SessionId = id });
    private void RequireActive(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || ActiveId() != id)
            throw Conflict("Mutation belongs to another or missing active contest.");
    }

    private void MigrateV1()
    {
        lock (_sync)
        {
            if (_sessions.Count() != 0 || ActiveId() is not null) return;
            var old = _db.GetCollection<LegacyContestLogSessionEntry>("contest_log_session").FindById(SingletonId);
            if (old is null) return;
            try
            {
                var json = Parse(old.SessionJson);
                var id = ContestLogSnapshotDto.SessionId(json);
                var start = Started(json, old.UpdatedUtc == default ? DateTime.UtcNow : old.UpdatedUtc);
                InTransaction(() =>
                {
                    _sessions.Insert(new ContestLogSessionEntry
                    {
                        Id = id, SessionJson = old.SessionJson, StartedUtc = start,
                        UpdatedUtc = old.UpdatedUtc == default ? start : old.UpdatedUtc,
                        NextQsoOrder = NextOrderForMigration(id),
                        OrderCounterInitialized = true,
                    });
                    SetActive(id);
                });
                _log.LogInformation("Migrated active contest {SessionId} into contest history", id);
            }
            catch (JsonException ex) { _log.LogWarning(ex, "Could not migrate legacy contest"); }
        }
    }

    private long NextOrderForMigration(string id)
    {
        var row = _qsos.Query().Where(x => x.SessionId == id)
            .OrderByDescending(x => x.Order).Limit(1).FirstOrDefault();
        return row is null ? 0 : checked(row.Order + 1);
    }

    private void InTransaction(Action action)
    {
        _db.BeginTrans();
        try { action(); _db.Commit(); }
        catch { _db.Rollback(); throw; }
    }

    private static List<ContestLogQsoEntry> BuildQsos(IReadOnlyList<JsonElement> values, string sessionId)
    {
        var rows = new List<ContestLogQsoEntry>(values.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var id = value.GetProperty("id").GetString()!;
            if (ContestLogSnapshotDto.QsoSessionId(value) != sessionId)
                throw new JsonException("QSO sessionId must match contest session id.");
            if (!ids.Add(id)) throw new JsonException($"Duplicate QSO id '{id}'.");
            rows.Add(new ContestLogQsoEntry
            {
                Id = id, SessionId = sessionId, QsoJson = value.GetRawText(), UpdatedUtc = DateTime.UtcNow,
            });
        }
        return rows;
    }

    private static DateTime Started(JsonElement value, DateTime fallback) =>
        DateTime.TryParse(Str(value, "createdUtc"), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToUniversalTime() : fallback.ToUniversalTime();
    private static DateTime? Timestamp(JsonElement value) =>
        DateTime.TryParse(Str(value, "timestampUtc"), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToUniversalTime() : null;
    private static double Peak(DateTime[] values)
    {
        var peak = 0;
        var left = 0;
        for (var right = 0; right < values.Length; right++)
        {
            while (left < right && values[right] - values[left] >= TimeSpan.FromMinutes(10)) left++;
            peak = Math.Max(peak, right - left + 1);
        }
        return peak * 6d;
    }
    private static IReadOnlyList<string> Distinct(IEnumerable<JsonElement> values, string key) =>
        values.Select(x => Str(x, key)).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    private static string? Str(JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static bool Bool(JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;
    private static JsonElement Parse(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : value.ToUniversalTime();
    private static ContestLogConflictException Conflict(string message) => new(message);
}

public sealed record ContestLogSnapshotDto(
    JsonElement Session, IReadOnlyList<JsonElement> Qsos,
    DateTime? StartedUtc = null, DateTime? FinishedUtc = null)
{
    public static ContestLogSnapshotDto FromValidated(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("session", out var session))
            throw new JsonException("Snapshot object with session is required.");
        var id = SessionId(session);
        if (!root.TryGetProperty("qsos", out var qsos) || qsos.ValueKind != JsonValueKind.Array)
            throw new JsonException("qsos must be an array.");
        var list = new List<JsonElement>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var qso in qsos.EnumerateArray())
        {
            if (QsoSessionId(qso) != id) throw new JsonException("QSO sessionId must match snapshot session id.");
            var qsoId = qso.GetProperty("id").GetString()!;
            if (!ids.Add(qsoId)) throw new JsonException($"Duplicate QSO id '{qsoId}'.");
            list.Add(qso.Clone());
        }
        return new ContestLogSnapshotDto(session.Clone(), list);
    }

    internal static string SessionId(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new JsonException("Contest session must have a non-empty string id.");
        return id.GetString()!;
    }

    internal static string QsoSessionId(JsonElement value, string? expectedId = null)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new JsonException("QSO must have a non-empty string id.");
        if (expectedId is not null && id.GetString() != expectedId) throw new JsonException("Route id must match QSO id.");
        if (!value.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(session.GetString())) throw new JsonException("QSO must have a sessionId.");
        return session.GetString()!;
    }
}

public sealed record ContestLogSummaryDto(
    string Id, JsonElement Session, DateTime StartedUtc, DateTime FinishedUtc,
    int QsoCount, int DupeCount, int PushedCount, int UniqueCallCount,
    double AverageRatePerHour, double PeakRatePerHour,
    IReadOnlyList<string> Bands, IReadOnlyList<string> Modes);

public sealed class ContestLogSessionEntry
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string SessionJson { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public long NextQsoOrder { get; set; }
    public bool OrderCounterInitialized { get; set; }
}
public sealed class ContestLogActiveEntry { public int Id { get; set; } public string SessionId { get; set; } = string.Empty; }
public sealed class ContestLogQsoEntry
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public long Order { get; set; }
    public string QsoJson { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
public sealed class LegacyContestLogSessionEntry
{
    public int Id { get; set; }
    public string SessionJson { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
public sealed class ContestLogConflictException(string message) : InvalidOperationException(message);
