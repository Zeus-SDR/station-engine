// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Text.Json;
using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists the small operator-facing UI families that previously existed
/// only in browser localStorage. Each family is routed to an intentionally
/// named LiteDB collection so the unified zeus-prefs.db export is complete.
/// </summary>
public sealed class OperatorSettingsStore : IDisposable
{
    private const int MaximumJsonBytes = 8 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> Collections =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hotkeys"] = "hotkey_bindings",
            ["smart-nr"] = "smart_nr_settings",
            ["panadapter-render"] = "panadapter_render_settings",
            ["waterfall-render"] = "panadapter_render_settings",
            ["multi-rx"] = "multi_rx_settings",
            ["analog-meter"] = "meter_ui_settings",
            ["s-meter-reading"] = "meter_ui_settings",
            ["connect"] = "connect_settings",
            ["chat"] = "chat_settings",
            ["lightning"] = "lightning_alert_settings",
            ["notepad"] = "notepad_content",
            ["rx-wf-windows"] = "display_aux_settings",
            ["spectrum-view-scope"] = "display_aux_settings",
        };

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly object _gate = new();

    public OperatorSettingsStore(string? dbPathOverride = null)
    {
        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPathOverride ?? PrefsDbPath.Get());
        _db = _dbLease.Database;
    }

    public static bool IsKnownFamily(string family) => Collections.ContainsKey(family);

    public OperatorSettingsDto Get(string family)
    {
        var collection = Collection(family);
        lock (_gate)
        {
            var entry = collection.FindById(family);
            if (entry is null)
                return new OperatorSettingsDto(family, null, 0);
            using var document = JsonDocument.Parse(entry.Json);
            return new OperatorSettingsDto(
                family,
                document.RootElement.Clone(),
                entry.UpdatedUtcMs);
        }
    }

    public OperatorSettingsDto Save(
        string family,
        JsonElement value,
        long? updatedUtcMs = null)
    {
        var collection = Collection(family);
        var json = value.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new ArgumentException("operator settings payload is too large", nameof(value));
        var timestamp = updatedUtcMs.GetValueOrDefault(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (timestamp <= 0)
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var current = collection.FindById(family);
            // Saved connect endpoints hydrate before a radio connection and
            // retain a local offline cache. Its client supplies the cache write
            // timestamp, so a stale browser cannot overwrite a newer server
            // snapshot during last-writer-wins reconciliation.
            if (current is not null
                && family == "connect"
                && updatedUtcMs.HasValue
                && timestamp < current.UpdatedUtcMs)
                return GetLocked(family, current);

            collection.Upsert(new OperatorSettingsEntry
            {
                Id = family,
                Json = json,
                UpdatedUtcMs = timestamp,
            });
            using var document = JsonDocument.Parse(json);
            return new OperatorSettingsDto(family, document.RootElement.Clone(), timestamp);
        }
    }

    private OperatorSettingsDto GetLocked(string family, OperatorSettingsEntry entry)
    {
        using var document = JsonDocument.Parse(entry.Json);
        return new OperatorSettingsDto(family, document.RootElement.Clone(), entry.UpdatedUtcMs);
    }

    private ILiteCollection<OperatorSettingsEntry> Collection(string family)
    {
        if (!Collections.TryGetValue(family, out var collection))
            throw new ArgumentOutOfRangeException(nameof(family), "unknown operator settings family");
        return _db.GetCollection<OperatorSettingsEntry>(collection);
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class OperatorSettingsEntry
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string Json { get; set; } = "{}";
    public long UpdatedUtcMs { get; set; }
}

public sealed record OperatorSettingsDto(
    string Family,
    JsonElement? Value,
    long UpdatedUtcMs);

public sealed record OperatorSettingsSetRequest(
    JsonElement Value,
    long? UpdatedUtcMs = null);
