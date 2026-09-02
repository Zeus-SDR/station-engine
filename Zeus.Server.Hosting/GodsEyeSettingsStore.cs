// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using LiteDB;
#if ZEUS_PRODUCT_HOST
using Zeus.Product.Hosting.GodsEye;
#else
using Zeus.Server.GodsEye;
#endif
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

public sealed record GodsEyeLayerSettings(
    string Layer,
    bool Enabled,
    int CadenceSeconds,
    double RadiusKm,
    int MaxCount,
    string ApiKey,
    IReadOnlyDictionary<string, bool>? Sources = null)
{
    public bool RequiresKey => Layer is GodsEyeLayerNames.Vessels or GodsEyeLayerNames.Fires;
    public bool Configured => !RequiresKey || !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class GodsEyeSettingsStore : IDisposable
{
    private static readonly IReadOnlyDictionary<string, GodsEyeLayerSettings> Defaults =
        new Dictionary<string, GodsEyeLayerSettings>(StringComparer.Ordinal)
        {
            [GodsEyeLayerNames.Earthquakes] = new(GodsEyeLayerNames.Earthquakes, true, 300, 5_000, 500, ""),
            [GodsEyeLayerNames.Launches] = new(GodsEyeLayerNames.Launches, true, 21_600, 20_000, 100, ""),
            [GodsEyeLayerNames.Aircraft] = new(GodsEyeLayerNames.Aircraft, false, 900, 500, 250, ""),
            [GodsEyeLayerNames.Vessels] = new(GodsEyeLayerNames.Vessels, false, 30, 500, 500, ""),
            [GodsEyeLayerNames.Fires] = new(GodsEyeLayerNames.Fires, false, 900, 2_500, 500, ""),
            [GodsEyeLayerNames.Cameras] = new(GodsEyeLayerNames.Cameras, false, 900, 250, 500, "",
                CameraSourceNames.All.ToDictionary(source => source, _ => true, StringComparer.Ordinal)),
            [GodsEyeLayerNames.MilitaryFlights] = new(GodsEyeLayerNames.MilitaryFlights, true, 60, 20_050, 500, ""),
            [GodsEyeLayerNames.Radio] = new(GodsEyeLayerNames.Radio, true, 2_700, 20_050, 750, ""),
            [GodsEyeLayerNames.Bikeshare] = new(GodsEyeLayerNames.Bikeshare, false, 300, 500, 500, ""),
            [GodsEyeLayerNames.Traffic] = new(GodsEyeLayerNames.Traffic, true, 120, 100, 300, ""),
            [GodsEyeLayerNames.MappedInstallations] = new(GodsEyeLayerNames.MappedInstallations, false, 900, 500, 700, ""),
        };

    private readonly SharedDatabase.Lease? _lease;
    private readonly ILiteCollection<GodsEyeLayerSettingsEntry> _settings;
    private readonly ILiteCollection<GodsEyeFeatureSettingsEntry> _features;
    private readonly object _sync = new();
    internal bool OwnsDatabaseLeaseForTesting => _lease is not null;

    public GodsEyeSettingsStore(ILogger<GodsEyeSettingsStore> log, string? dbPathOverride = null)
    {
        var path = dbPathOverride ?? PrefsDbPath.Get();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
#if ZEUS_PRODUCT_HOST
        _lease = SharedDatabase.Acquire(path, warning: (message, exception) => log.LogWarning(exception, "{Message}", message));
#else
        _lease = SharedDatabase.Acquire(path);
#endif
        _settings = _lease.Database.GetCollection<GodsEyeLayerSettingsEntry>("godseye_layer_settings");
        _features = _lease.Database.GetCollection<GodsEyeFeatureSettingsEntry>("godseye_feature_settings");
        log.LogInformation("GodsEyeSettingsStore initialized at {Path}", path);
    }

    internal GodsEyeSettingsStore(ILogger<GodsEyeSettingsStore> log, LiteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _settings = database.GetCollection<GodsEyeLayerSettingsEntry>("godseye_layer_settings");
        _features = database.GetCollection<GodsEyeFeatureSettingsEntry>("godseye_feature_settings");
        log.LogInformation("GodsEyeSettingsStore initialized with the shared Gods Eye database lease");
    }

    public IReadOnlyDictionary<string, GodsEyeLayerSettings> GetInternal()
    {
        lock (_sync)
        {
            return GodsEyeLayerNames.All.ToDictionary(layer => layer, layer =>
            {
                var fallback = Defaults[layer];
                var row = _settings.FindById(layer);
                return row is null ? fallback : Normalize(new GodsEyeLayerSettings(
                    layer, row.Enabled, row.CadenceSeconds, row.RadiusKm, row.MaxCount, row.ApiKey ?? "", row.Sources), fallback);
            }, StringComparer.Ordinal);
        }
    }

    public GodsEyeSettingsResponse GetPublic(string? fallbackGrid = null)
    {
        var values = GetInternal();
        return new GodsEyeSettingsResponse(
            Public(values[GodsEyeLayerNames.Earthquakes]),
            Public(values[GodsEyeLayerNames.Launches]),
            Public(values[GodsEyeLayerNames.Aircraft]),
            Public(values[GodsEyeLayerNames.Vessels]),
            Public(values[GodsEyeLayerNames.Fires]), Public(values[GodsEyeLayerNames.Cameras]),
            Public(values[GodsEyeLayerNames.MilitaryFlights]), Public(values[GodsEyeLayerNames.Radio]),
            Public(values[GodsEyeLayerNames.Bikeshare]), Public(values[GodsEyeLayerNames.Traffic]),
            Public(values[GodsEyeLayerNames.MappedInstallations]),
            GetLogbook(), GetObserverSettings(), GetProviderSettings(), GetResolvedObserver(fallbackGrid));
    }

    public GodsEyeObserverSettings GetObserverSettings()
    {
        lock (_sync)
        {
            var row = _features.FindById(1);
            return NormalizeObserver(row is null
                ? new GodsEyeObserverSettings()
                : new GodsEyeObserverSettings(row.ObserverLatitudeDeg, row.ObserverLongitudeDeg, row.ObserverGrid ?? ""));
        }
    }

    /// <summary>
    /// Resolve the point every distance-bounded layer is measured from: an explicit
    /// Gods Eye point of interest first, then a Gods Eye grid, and finally
    /// <paramref name="fallbackGrid"/> — normally the operator's station grid, so a
    /// station that has already told Zeus where it is does not have to say so twice.
    /// Returns null only when no locator is known anywhere, which is what leaves the
    /// QTH-anchored layers reporting that they have nowhere to look.
    /// </summary>
    public GodsEyeObserver? GetObserver(string? fallbackGrid = null)
    {
        var resolved = GetResolvedObserver(fallbackGrid);
        return resolved is null ? null : new GodsEyeObserver(resolved.LatitudeDeg, resolved.LongitudeDeg);
    }

    public GodsEyeResolvedObserver? GetResolvedObserver(string? fallbackGrid = null)
    {
        var settings = GetObserverSettings();
        if (settings.LatitudeDeg is { } latitude && settings.LongitudeDeg is { } longitude)
            return new GodsEyeResolvedObserver(latitude, longitude, "explicit");
        if (TryMaidenhead(settings.Grid, out var grid))
            return new GodsEyeResolvedObserver(grid.LatitudeDeg, grid.LongitudeDeg, "grid");
        return TryMaidenhead(fallbackGrid, out var stationGrid)
            ? new GodsEyeResolvedObserver(stationGrid.LatitudeDeg, stationGrid.LongitudeDeg, "station-grid")
            : null;
    }

    public GodsEyeLogbookSettings GetLogbook()
    {
        lock (_sync)
        {
            var row = _features.FindById(1);
            return row is null ? new GodsEyeLogbookSettings() : new(
                row.MatchMaritimeMobile, row.MatchAeronauticalMobile, row.PinMatchedTracks,
                row.StampSatellitesInView, row.ShowLiveLayers);
        }
    }

    public GodsEyeProviderSettingsResponse GetProviderSettings()
    {
        lock (_sync)
        {
            var row = _features.FindById(1);
            return new GodsEyeProviderSettingsResponse(
                !string.IsNullOrWhiteSpace(row?.GoogleMapsApiKey),
                !string.IsNullOrWhiteSpace(row?.CesiumIonToken),
                !string.IsNullOrWhiteSpace(row?.TomTomApiKey));
        }
    }

    public (string GoogleMapsApiKey, string CesiumIonToken, string TomTomApiKey) GetProviderKeys()
    {
        lock (_sync)
        {
            var row = _features.FindById(1);
            return (row?.GoogleMapsApiKey ?? "", row?.CesiumIonToken ?? "", row?.TomTomApiKey ?? "");
        }
    }

    public GodsEyeSettingsResponse Set(GodsEyeSettingsRequest request)
    {
        var incoming = new Dictionary<string, GodsEyeLayerSettingsWrite>(StringComparer.Ordinal)
        {
            [GodsEyeLayerNames.Earthquakes] = request.Earthquakes,
            [GodsEyeLayerNames.Launches] = request.Launches,
            [GodsEyeLayerNames.Aircraft] = request.Aircraft,
            [GodsEyeLayerNames.Vessels] = request.Vessels,
            [GodsEyeLayerNames.Fires] = request.Fires,
            [GodsEyeLayerNames.Cameras] = request.Cameras ?? new(false, 900, 250, 500, Sources: CameraSourceNames.All.ToDictionary(source => source, _ => true, StringComparer.Ordinal)),
            [GodsEyeLayerNames.MilitaryFlights] = request.MilitaryFlights ?? PublicWrite(Defaults[GodsEyeLayerNames.MilitaryFlights]),
            [GodsEyeLayerNames.Radio] = request.Radio ?? PublicWrite(Defaults[GodsEyeLayerNames.Radio]),
            [GodsEyeLayerNames.Bikeshare] = request.Bikeshare ?? PublicWrite(Defaults[GodsEyeLayerNames.Bikeshare]),
            [GodsEyeLayerNames.Traffic] = request.Traffic ?? PublicWrite(Defaults[GodsEyeLayerNames.Traffic]),
            [GodsEyeLayerNames.MappedInstallations] = request.MappedInstallations ?? PublicWrite(Defaults[GodsEyeLayerNames.MappedInstallations]),
        };
        foreach (var value in incoming.Values) ValidateCredential(value.ApiKey);
        if (request.Providers is { } providerWrite)
        {
            ValidateCredential(providerWrite.GoogleMapsApiKey);
            ValidateCredential(providerWrite.CesiumIonToken);
            ValidateCredential(providerWrite.TomTomApiKey);
        }
        lock (_sync)
        {
            foreach (var layer in GodsEyeLayerNames.All)
            {
                if ((layer == GodsEyeLayerNames.Cameras && request.Cameras is null)
                    || (layer == GodsEyeLayerNames.MilitaryFlights && request.MilitaryFlights is null)
                    || (layer == GodsEyeLayerNames.Radio && request.Radio is null)
                    || (layer == GodsEyeLayerNames.Bikeshare && request.Bikeshare is null)
                    || (layer == GodsEyeLayerNames.Traffic && request.Traffic is null)
                    || (layer == GodsEyeLayerNames.MappedInstallations && request.MappedInstallations is null)) continue;
                var value = incoming[layer];
                var fallback = Defaults[layer];
                var existing = _settings.FindById(layer);
                var key = value.ApiKey is null ? existing?.ApiKey ?? "" : value.ApiKey.Trim();
                var sources = layer == GodsEyeLayerNames.Cameras
                    ? CameraSourceNames.All.ToDictionary(source => source,
                        source => value.Sources?.GetValueOrDefault(source) ?? existing?.Sources?.GetValueOrDefault(source) ?? true,
                        StringComparer.Ordinal)
                    : null;
                var normalized = Normalize(new GodsEyeLayerSettings(layer, value.Enabled, value.CadenceSeconds, value.RadiusKm, value.MaxCount, key, sources), fallback);
                _settings.Upsert(new GodsEyeLayerSettingsEntry
                {
                    Id = layer, Enabled = normalized.Enabled, CadenceSeconds = normalized.CadenceSeconds,
                    RadiusKm = normalized.RadiusKm, MaxCount = normalized.MaxCount,
                    ApiKey = normalized.ApiKey, Sources = normalized.Sources is null ? null : new Dictionary<string, bool>(normalized.Sources), UpdatedUtc = DateTime.UtcNow,
                });
            }
            if (request.Logbook is not null || request.Observer is not null || request.Providers is not null)
            {
                var row = _features.FindById(1) ?? new GodsEyeFeatureSettingsEntry { Id = 1 };
                if (request.Logbook is { } logbook)
                {
                    row.MatchMaritimeMobile = logbook.MatchMaritimeMobile;
                    row.MatchAeronauticalMobile = logbook.MatchAeronauticalMobile;
                    row.PinMatchedTracks = logbook.PinMatchedTracks;
                    row.StampSatellitesInView = logbook.StampSatellitesInView;
                    row.ShowLiveLayers = logbook.ShowLiveLayers;
                }
                if (request.Observer is { } observer)
                {
                    var normalized = NormalizeObserver(observer);
                    row.ObserverLatitudeDeg = normalized.LatitudeDeg;
                    row.ObserverLongitudeDeg = normalized.LongitudeDeg;
                    row.ObserverGrid = normalized.Grid;
                }
                if (request.Providers is { } providers)
                {
                    row.GoogleMapsApiKey = RetainOrReplace(row.GoogleMapsApiKey, providers.GoogleMapsApiKey, providers.ClearGoogleMapsApiKey);
                    row.CesiumIonToken = RetainOrReplace(row.CesiumIonToken, providers.CesiumIonToken, providers.ClearCesiumIonToken);
                    row.TomTomApiKey = RetainOrReplace(row.TomTomApiKey, providers.TomTomApiKey, providers.ClearTomTomApiKey);
                }
                row.UpdatedUtc = DateTime.UtcNow;
                _features.Upsert(row);
            }
        }
        return GetPublic();
    }

    public void Dispose() => _lease?.Dispose();

    private static GodsEyeLayerSettingsResponse Public(GodsEyeLayerSettings value) =>
        new(value.Enabled, value.CadenceSeconds, value.RadiusKm, value.MaxCount, value.Configured, value.Sources);

    private static GodsEyeLayerSettingsWrite PublicWrite(GodsEyeLayerSettings value) =>
        new(value.Enabled, value.CadenceSeconds, value.RadiusKm, value.MaxCount, Sources: value.Sources);

    private static string RetainOrReplace(string? current, string? incoming, bool clear)
    {
        if (clear) return "";
        if (incoming is null) return current ?? "";
        return incoming.Trim();
    }

    private static void ValidateCredential(string? value)
    {
        if (value?.Trim().Length > 1024)
            throw new ArgumentException("Provider credentials must not exceed 1024 characters.");
    }

    private static GodsEyeLayerSettings Normalize(GodsEyeLayerSettings value, GodsEyeLayerSettings fallback) => value with
    {
        CadenceSeconds = Math.Clamp(value.CadenceSeconds, MinimumCadence(value.Layer), 7 * 24 * 60 * 60),
        RadiusKm = Math.Clamp(double.IsFinite(value.RadiusKm) ? value.RadiusKm : fallback.RadiusKm, 25, 20_050),
        MaxCount = Math.Clamp(value.MaxCount, 1, 2_000),
        ApiKey = value.ApiKey.Length <= 1024 ? value.ApiKey : "",
        Sources = value.Layer == GodsEyeLayerNames.Cameras
            ? fallback.Sources!.ToDictionary(
                pair => pair.Key,
                pair => value.Sources?.GetValueOrDefault(pair.Key) ?? pair.Value,
                StringComparer.Ordinal)
            : value.Sources,
    };

    private static int MinimumCadence(string layer) => layer switch
    {
        GodsEyeLayerNames.Earthquakes => 60,
        GodsEyeLayerNames.Launches => 900,
        GodsEyeLayerNames.Aircraft => 900,
        GodsEyeLayerNames.Vessels => 10,
        GodsEyeLayerNames.Fires => 300,
        GodsEyeLayerNames.Cameras => 300,
        GodsEyeLayerNames.MilitaryFlights => 30,
        GodsEyeLayerNames.Radio => 900,
        GodsEyeLayerNames.Bikeshare => 60,
        GodsEyeLayerNames.Traffic => 60,
        GodsEyeLayerNames.MappedInstallations => 300,
        _ => 60,
    };

    private static GodsEyeObserverSettings NormalizeObserver(GodsEyeObserverSettings value)
    {
        var latitude = value.LatitudeDeg;
        var longitude = value.LongitudeDeg;
        if (latitude is null || longitude is null
            || !double.IsFinite(latitude.Value) || latitude is < -90 or > 90
            || !double.IsFinite(longitude.Value) || longitude is < -180 or > 180)
        {
            latitude = null;
            longitude = null;
        }
        var grid = (value.Grid ?? "").Trim().ToUpperInvariant();
        if (!TryMaidenhead(grid, out _)) grid = "";
        return new GodsEyeObserverSettings(latitude, longitude, grid);
    }

    internal static bool TryMaidenhead(string? grid, out GodsEyeObserver observer)
    {
        observer = default;
        var value = (grid ?? "").Trim().ToUpperInvariant();
        if (value.Length is not (4 or 6)
            || value[0] is < 'A' or > 'R' || value[1] is < 'A' or > 'R'
            || !char.IsAsciiDigit(value[2]) || !char.IsAsciiDigit(value[3]))
            return false;
        var longitude = -180d + (value[0] - 'A') * 20d + (value[2] - '0') * 2d;
        var latitude = -90d + (value[1] - 'A') * 10d + (value[3] - '0');
        var longitudeWidth = 2d;
        var latitudeHeight = 1d;
        if (value.Length == 6)
        {
            if (value[4] is < 'A' or > 'X' || value[5] is < 'A' or > 'X') return false;
            longitudeWidth = 2d / 24;
            latitudeHeight = 1d / 24;
            longitude += (value[4] - 'A') * longitudeWidth;
            latitude += (value[5] - 'A') * latitudeHeight;
        }
        observer = new GodsEyeObserver(latitude + latitudeHeight / 2, longitude + longitudeWidth / 2);
        return true;
    }
}

public sealed class GodsEyeFeatureSettingsEntry
{
    public int Id { get; set; }
    public bool MatchMaritimeMobile { get; set; } = true;
    public bool MatchAeronauticalMobile { get; set; } = true;
    public bool PinMatchedTracks { get; set; } = true;
    public bool StampSatellitesInView { get; set; } = true;
    public bool ShowLiveLayers { get; set; } = true;
    public double? ObserverLatitudeDeg { get; set; }
    public double? ObserverLongitudeDeg { get; set; }
    public string? ObserverGrid { get; set; }
    public string? GoogleMapsApiKey { get; set; }
    public string? CesiumIonToken { get; set; }
    public string? TomTomApiKey { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class GodsEyeLayerSettingsEntry
{
    [BsonId] public string Id { get; set; } = "";
    public bool Enabled { get; set; }
    public int CadenceSeconds { get; set; }
    public double RadiusKm { get; set; }
    public int MaxCount { get; set; }
    public string? ApiKey { get; set; }
    public Dictionary<string, bool>? Sources { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
