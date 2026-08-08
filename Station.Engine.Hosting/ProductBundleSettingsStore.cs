// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Engine-side home for the Zeus Link product bundle's operator settings
/// (feature toggles and amplifier configs), stored as ONE opaque JSON
/// document in zeus-prefs.db. The bundle (ZeusProduct) mirrors its local
/// product-bundle.json here so the settings live in the same exportable
/// database as every desktop 0.15.x setting: a splash-row profile export now
/// carries them, and a profile import on a fresh machine restores them on the
/// product's next startup sync. The engine treats the payload as opaque — the
/// document schema is the product's own, which keeps the proprietary bundle
/// contract out of the GPL engine. Last writer wins; the product is the only
/// writer and the engine is the system of record once seeded.
/// </summary>
public sealed class ProductBundleSettingsStore : IDisposable
{
    internal const string CollectionName = "product_bundle_settings";
    internal const int MaxDocumentBytes = 1024 * 1024;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<ProductBundleSettingsEntry> _docs;
    private readonly ILogger<ProductBundleSettingsStore> _log;
    private readonly object _sync = new();

    public event Action? Changed;

    public ProductBundleSettingsStore(
        ILogger<ProductBundleSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _docs = _dbLease.Database.GetCollection<ProductBundleSettingsEntry>(CollectionName);

        _log.LogInformation("ProductBundleSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>Returns the stored document, or null when the product has
    /// never synced one (fresh install — the product seeds it on first
    /// startup).</summary>
    public ProductBundleSettingsEntry? Get()
    {
        lock (_sync)
            return _docs.FindAll().FirstOrDefault();
    }

    public ProductBundleSettingsEntry Save(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            throw new ArgumentException(
                $"Bundle settings document exceeds {MaxDocumentBytes} bytes.", nameof(json));

        ProductBundleSettingsEntry entry;
        lock (_sync)
        {
            entry = _docs.FindAll().FirstOrDefault() ?? new ProductBundleSettingsEntry();
            entry.Json = json;
            entry.UpdatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (entry.Id == 0) _docs.Insert(entry);
            else _docs.Update(entry);
        }
        Changed?.Invoke();
        return entry;
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class ProductBundleSettingsEntry
{
    public int Id { get; set; }
    public string Json { get; set; } = string.Empty;
    public long UpdatedUtcMs { get; set; }
}
