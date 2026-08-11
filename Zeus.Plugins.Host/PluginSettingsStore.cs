// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;
using JsonSerializerOptions = System.Text.Json.JsonSerializerOptions;
using JsonNamingPolicy = System.Text.Json.JsonNamingPolicy;
using JsonException = System.Text.Json.JsonException;

namespace Zeus.Plugins.Host;

/// <summary>
/// LiteDB-backed key/value store. One collection per plugin id; plugins
/// cannot read one another's settings. Backed by the same
/// <c>zeus-prefs.db</c> as the rest of Zeus when running under the
/// server; tests typically point ZEUS_PREFS_PATH at a temp file.
/// </summary>
public sealed class PluginSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly string _dbPath;
    private readonly ILogger<PluginSettingsStore>? _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, PendingNotification> _pendingNotifications =
        new(StringComparer.Ordinal);
    private bool _disposed;

    // A native plugin persists its complete settings object one key at a time.
    // Coalesce that synchronous burst before telling the audio bridge to recycle
    // the plugin; otherwise one knob gesture produces one recycle per key.
    private const int NotificationDebounceMs = 50;

    public PluginSettingsStore(string dbPath, ILogger<PluginSettingsStore>? log = null)
    {
        _dbPath = dbPath;
        _log = log;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _log?.LogInformation("PluginSettingsStore initialised at {Path}", dbPath);
    }

    public string DataDirectory =>
        Path.GetDirectoryName(Path.GetFullPath(_dbPath)) ?? Directory.GetCurrentDirectory();

    public event Action<string>? PluginSettingsChanged;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Get an IPluginSettings view scoped to one plugin id.</summary>
    public IPluginSettings ForPlugin(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            throw new ArgumentException("pluginId required", nameof(pluginId));
        return new ScopedSettings(this, pluginId);
    }

    private ILiteCollection<SettingDoc> CollectionFor(string pluginId)
        => _db.GetCollection<SettingDoc>(CollectionName(pluginId));

    /// <summary>
    /// Opaque snapshot of one plugin's ENTIRE LiteDB collection (every
    /// <see cref="SettingDoc"/>: knobs + per-plugin bypass). Mirrors how VST
    /// blobs round-trip — key/value in, key/value out, the host never
    /// interprets the values. This is the complete, plugin-agnostic capture of
    /// a NATIVE plugin's state for the TX Audio Profile system, without
    /// touching the <c>IAudioPlugin</c>/<c>IPluginSettings</c> contracts.
    /// </summary>
    public IReadOnlyDictionary<string, string> DumpCollection(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return new Dictionary<string, string>();
        lock (_lock)
        {
            var coll = CollectionFor(pluginId);
            var outp = new Dictionary<string, string>(StringComparer.Ordinal);
            // last-writer-wins on duplicate keys, matching ScopedSettings.GetAsync's
            // OrderByDescending(Id) defensive read: ascending Id means the highest
            // Id (latest write) lands last and overwrites stale duplicates.
            foreach (var d in coll.Query().OrderBy(x => x.Id).ToList())
                outp[d.Key] = d.JsonValue;
            return outp;
        }
    }

    /// <summary>
    /// Restore: replace the collection's contents for the supplied keys with a
    /// saved snapshot. DeleteMany+Insert per key under the same lock — the
    /// identical idempotent pattern as <c>ScopedSettings.SetAsync</c> (the
    /// PR #387 "Id=0 upsert grows unbounded" fix), so a restore can't strand
    /// the live value behind stale duplicates. Keys not present in the snapshot
    /// are left untouched (a partial snapshot never wipes unrelated settings).
    /// </summary>
    public void RestoreCollection(string pluginId, IReadOnlyDictionary<string, string> snapshot)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || snapshot is null) return;
        lock (_lock)
        {
            var coll = CollectionFor(pluginId);
            foreach (var kv in snapshot)
            {
                coll.DeleteMany(x => x.Key == kv.Key);
                coll.Insert(new SettingDoc { Key = kv.Key, JsonValue = kv.Value });
            }
        }
    }

    /// <summary>
    /// Restore a complete native-plugin profile snapshot. Unlike
    /// <see cref="RestoreCollection"/>, profile snapshots are authoritative:
    /// settings absent from the snapshot are removed so profiles cannot bleed
    /// into one another. Host-owned <c>__zeus.*</c> keys are never changed.
    /// </summary>
    public void RestoreProfileCollection(
        string pluginId,
        IReadOnlyDictionary<string, string> snapshot)
        => _ = RestoreProfileCollectionIfChanged(pluginId, snapshot);

    /// <summary>
    /// Restore an authoritative profile snapshot only when its operator-owned
    /// values differ from the current collection. Returns true when the store
    /// changed, allowing profile orchestration to avoid recycling a live native
    /// plug-in whose settings already match.
    /// </summary>
    public bool RestoreProfileCollectionIfChanged(
        string pluginId,
        IReadOnlyDictionary<string, string> snapshot)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || snapshot is null) return false;
        lock (_lock)
        {
            var coll = CollectionFor(pluginId);
            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var doc in coll.Query().OrderBy(x => x.Id).ToList())
            {
                if (doc.Key is not null && !IsInternalKey(doc.Key))
                    existing[doc.Key] = doc.JsonValue;
            }
            var desired = snapshot
                .Where(pair => !IsInternalKey(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            if (existing.Count == desired.Count
                && existing.All(pair => desired.TryGetValue(pair.Key, out var value)
                    && string.Equals(pair.Value, value, StringComparison.Ordinal)))
            {
                return false;
            }

            foreach (var key in existing.Keys)
                coll.DeleteMany(doc => doc.Key == key);

            foreach (var kv in desired)
            {
                coll.DeleteMany(doc => doc.Key == kv.Key);
                coll.Insert(new SettingDoc { Key = kv.Key, JsonValue = kv.Value });
            }
            return true;
        }
    }

    private static bool IsInternalKey(string key) =>
        key.StartsWith("__zeus.", StringComparison.Ordinal);

    public void DropCollection(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return;
        lock (_lock)
        {
            _db.DropCollection(CollectionName(pluginId));
        }
    }

    private void QueuePluginSettingsChanged(string pluginId)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (!_pendingNotifications.TryGetValue(pluginId, out var pending))
            {
                pending = new PendingNotification(pluginId);
                pending.Timer = new Timer(
                    FlushPluginSettingsChanged,
                    pending,
                    Timeout.Infinite,
                    Timeout.Infinite);
                _pendingNotifications.Add(pluginId, pending);
            }

            pending.LastWriteMs = Environment.TickCount64;
            pending.Timer!.Change(NotificationDebounceMs, Timeout.Infinite);
        }
    }

    private void FlushPluginSettingsChanged(object? state)
    {
        var pending = (PendingNotification)state!;
        Action<string>? handlers;
        lock (_lock)
        {
            if (_disposed) return;

            var elapsedMs = Environment.TickCount64 - pending.LastWriteMs;
            if (elapsedMs < NotificationDebounceMs)
            {
                pending.Timer!.Change(
                    Math.Max(1, NotificationDebounceMs - (int)elapsedMs),
                    Timeout.Infinite);
                return;
            }

            pending.Timer!.Change(Timeout.Infinite, Timeout.Infinite);
            handlers = PluginSettingsChanged;
        }

        if (handlers is null) return;

        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(pending.PluginId); }
            catch (Exception ex)
            {
                _log?.LogWarning(ex,
                    "Plugin settings change handler failed for plugin {PluginId}",
                    pending.PluginId);
            }
        }
    }

    internal static string CollectionName(string pluginId)
        => "plugin_" + pluginId.Replace('.', '_').Replace('-', '_');

    public void Dispose()
    {
        Timer[] timers;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            timers = _pendingNotifications.Values
                .Select(pending => pending.Timer!)
                .ToArray();
            _pendingNotifications.Clear();
            PluginSettingsChanged = null;
        }

        foreach (var timer in timers) timer.Dispose();
        _dbLease.Dispose();
    }

    private sealed class ScopedSettings : IPluginSettings
    {
        private readonly PluginSettingsStore _store;
        private readonly string _pluginId;

        public ScopedSettings(PluginSettingsStore store, string pluginId)
        {
            _store = store;
            _pluginId = pluginId;
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            lock (_store._lock)
            {
                // OrderByDescending on Id: defensive against duplicate rows
                // for the same Key that may exist from before the SetAsync
                // dedupe fix below. SetAsync clears them out on next write.
                var doc = _store.CollectionFor(_pluginId)
                    .Query()
                    .Where(x => x.Key == key)
                    .OrderByDescending(x => x.Id)
                    .FirstOrDefault();
                if (doc is null) return Task.FromResult<T?>(default);
                try
                {
                    var value = JsonSerializer.Deserialize<T>(doc.JsonValue, JsonOpts);
                    return Task.FromResult<T?>(value);
                }
                catch (JsonException)
                {
                    return Task.FromResult<T?>(default);
                }
            }
        }

        public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(value, JsonOpts);
            lock (_store._lock)
            {
                // Upsert by Key (not by LiteDB _id). The old code used
                // `coll.Upsert(new SettingDoc { ... })` which left Id at 0
                // and so LiteDB treated every Set as a new row, growing
                // the collection without bound and stranding the latest
                // value behind a wall of stale duplicates. DeleteMany +
                // Insert is atomic under _store._lock and idempotent on
                // the existing duplicate corpus.
                var coll = _store.CollectionFor(_pluginId);
                coll.DeleteMany(x => x.Key == key);
                coll.Insert(new SettingDoc { Key = key, JsonValue = json });

                // Keep persistence and debounce scheduling atomic. Otherwise a
                // delayed timer can acquire the lock after the write completes
                // but before this write resets LastWriteMs, emitting a stale
                // notification followed by a second one for the same burst.
                _store.QueuePluginSettingsChanged(_pluginId);
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            lock (_store._lock)
            {
                _store.CollectionFor(_pluginId).DeleteMany(x => x.Key == key);
                _store.QueuePluginSettingsChanged(_pluginId);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class PendingNotification(string pluginId)
    {
        public string PluginId { get; } = pluginId;
        public Timer? Timer { get; set; }
        public long LastWriteMs { get; set; }
    }

    internal sealed class SettingDoc
    {
        public int Id { get; set; }
        public string Key { get; set; } = "";
        public string JsonValue { get; set; } = "";
    }
}
