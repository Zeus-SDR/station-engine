// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for provenance.

using LiteDB;
#if ZEUS_PRODUCT_HOST
using SharedDatabase = Zeus.Product.Hosting.Data.SharedLiteDatabase;
#else
using SharedDatabase = Zeus.Data.SharedLiteDatabase;
#endif

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting;
#else
namespace Zeus.Server;
#endif

public sealed record SatelliteSettings(
    bool Enabled = true,
    IReadOnlyList<string>? CatalogGroups = null,
    double MinimumPassElevationDeg = 15,
    int PassHorizonHours = 48,
    string CustomTleUrl = "",
    bool ShowFootprints = true,
    bool ShowGroundTracks = true)
{
    public static readonly string[] DefaultGroups = ["amateur", "stations"];

    public SatelliteSettings Normalized()
    {
        var groups = (CatalogGroups ?? DefaultGroups)
            .Select(x => (x ?? "").Trim().ToLowerInvariant())
            .Where(x => x.Length is > 0 and <= 40 && x.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (groups.Length == 0) groups = DefaultGroups;
        var url = (CustomTleUrl ?? "").Trim();
        if (url.Length > 2048 || (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))) url = "";
        return this with
        {
            CatalogGroups = groups,
            MinimumPassElevationDeg = Math.Clamp(MinimumPassElevationDeg, 0, 90),
            PassHorizonHours = Math.Clamp(PassHorizonHours, 1, 168),
            CustomTleUrl = url,
        };
    }
}

public sealed class SatelliteSettingsStore : IDisposable
{
    private readonly SharedDatabase.Lease? _lease;
    private readonly ILiteCollection<SatelliteSettingsEntry> _settings;
    private readonly ILiteCollection<SatelliteTleCacheEntry> _tleCache;
    private readonly object _sync = new();
    internal bool OwnsDatabaseLeaseForTesting => _lease is not null;

    public SatelliteSettingsStore(ILogger<SatelliteSettingsStore> log, string? dbPathOverride = null)
    {
        var path = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
#if ZEUS_PRODUCT_HOST
        _lease = SharedDatabase.Acquire(path, warning: (message, exception) => log.LogWarning(exception, "{Message}", message));
#else
        _lease = SharedDatabase.Acquire(path);
#endif
        _settings = _lease.Database.GetCollection<SatelliteSettingsEntry>("satellite_settings");
        _tleCache = _lease.Database.GetCollection<SatelliteTleCacheEntry>("satellite_tle_cache");
        log.LogInformation("SatelliteSettingsStore initialized at {Path}", path);
    }

    internal SatelliteSettingsStore(ILogger<SatelliteSettingsStore> log, LiteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _settings = database.GetCollection<SatelliteSettingsEntry>("satellite_settings");
        _tleCache = database.GetCollection<SatelliteTleCacheEntry>("satellite_tle_cache");
        log.LogInformation("SatelliteSettingsStore initialized with the shared Gods Eye database lease");
    }

    public SatelliteSettings Get()
    {
        lock (_sync)
        {
            var row = _settings.FindById(1);
            return row is null ? new SatelliteSettings().Normalized() : new SatelliteSettings(row.Enabled, row.CatalogGroups, row.MinimumPassElevationDeg, row.PassHorizonHours, row.CustomTleUrl ?? "", row.ShowFootprints, row.ShowGroundTracks).Normalized();
        }
    }

    public SatelliteSettings Set(SatelliteSettings value)
    {
        var normalized = value.Normalized();
        lock (_sync)
        {
            _settings.Upsert(new SatelliteSettingsEntry { Id = 1, Enabled = normalized.Enabled, CatalogGroups = normalized.CatalogGroups!.ToList(), MinimumPassElevationDeg = normalized.MinimumPassElevationDeg, PassHorizonHours = normalized.PassHorizonHours, CustomTleUrl = normalized.CustomTleUrl, ShowFootprints = normalized.ShowFootprints, ShowGroundTracks = normalized.ShowGroundTracks, UpdatedUtc = DateTime.UtcNow });
        }
        return normalized;
    }

    public IReadOnlyList<string> LoadLastGoodTles()
    {
        lock (_sync) return _tleCache.FindAll().OrderBy(x => x.Id).Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
    }

    public void SaveLastGoodTles(IReadOnlyList<string> sets)
    {
        lock (_sync)
        {
            _tleCache.DeleteAll();
            for (var i = 0; i < sets.Count; i++) _tleCache.Insert(new SatelliteTleCacheEntry { Id = i + 1, Text = sets[i], UpdatedUtc = DateTime.UtcNow });
        }
    }

    public void Dispose() => _lease?.Dispose();
}

public sealed class SatelliteSettingsEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string>? CatalogGroups { get; set; }
    public double MinimumPassElevationDeg { get; set; } = 15;
    public int PassHorizonHours { get; set; } = 48;
    public string? CustomTleUrl { get; set; }
    public bool ShowFootprints { get; set; } = true;
    public bool ShowGroundTracks { get; set; } = true;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class SatelliteTleCacheEntry
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}
