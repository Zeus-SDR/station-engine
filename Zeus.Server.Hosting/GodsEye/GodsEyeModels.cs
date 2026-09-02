// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.GodsEye;
#else
namespace Zeus.Server.GodsEye;
#endif

public static class GodsEyeLayerNames
{
    public const string Earthquakes = "earthquakes";
    public const string Launches = "launches";
    public const string Aircraft = "aircraft";
    public const string Vessels = "vessels";
    public const string Fires = "fires";
    public const string Cameras = "cameras";
    public const string MilitaryFlights = "military-flights";
    public const string Radio = "radio";
    public const string Bikeshare = "bikeshare";
    public const string Traffic = "traffic";
    public const string MappedInstallations = "mapped-installations";

    public static readonly string[] FeedLayers =
    [
        Earthquakes, Launches, Aircraft, Vessels, Fires,
        MilitaryFlights, Radio, Bikeshare, Traffic, MappedInstallations,
    ];
    public static readonly string[] All = [.. FeedLayers, Cameras];
    public static bool IsKnown(string value) => All.Contains(value, StringComparer.OrdinalIgnoreCase);
    public static string Normalize(string value) => All.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) ?? "";
}

public static class GodsEyeFreshness
{
    public const string Live = "live";
    public const string Stale = "stale";
    public const string Unconfigured = "unconfigured";
    public const string Unavailable = "unavailable";
    public const string RateLimited = "rate-limited";
}

public sealed record GodsEyeItemDto(
    string Id,
    string Name,
    double LatitudeDeg,
    double LongitudeDeg,
    DateTimeOffset TimestampUtc,
    double? Magnitude = null,
    double? HeadingDeg = null,
    double? SpeedKnots = null,
    double? AltitudeM = null,
    double? Frp = null,
    string? Confidence = null,
    string? Status = null,
    string? Site = null,
    string? Callsign = null);

public static class CameraSourceNames
{
    public const string AlabamaDot = "alabamaDot";
    public const string Caltrans = "caltrans";
    public const string WisconsinDot = "wisconsinDot";
    public static readonly string[] All = [AlabamaDot, Caltrans, WisconsinDot];
}

public sealed record CameraDto(
    string Id, string Name, double LatitudeDeg, double LongitudeDeg, DateTimeOffset TimestampUtc,
    double? HeadingDeg, double? FieldOfViewDeg, string? SnapshotUrl, int SnapshotRefreshSeconds,
    string? StreamUrl, string StreamType,
    string Source, string SourceAttribution, bool Available, string? UnavailableReason,
    int ConsecutiveFailures, DateTimeOffset? LastSuccessUtc);

public sealed record CameraExclusionCounts(int InsecureScheme, int UnsupportedStreamType)
{
    public int Total => InsecureScheme + UnsupportedStreamType;
}

public sealed record CameraFeedSnapshot(
    string Layer, string State, DateTimeOffset? FetchedUtc, IReadOnlyList<CameraDto> Items,
    int TotalCount, int ReturnedCount, bool Truncated, string? Reason,
    CameraExclusionCounts Excluded, IReadOnlyDictionary<string, int> SourceCounts,
    long RequestCount = 0, DateTimeOffset? LastFetchUtc = null);

public sealed record GodsEyeLayerSnapshot(
    string Layer,
    string State,
    DateTimeOffset? FetchedUtc,
    IReadOnlyList<GodsEyeItemDto> Items,
    int TotalCount,
    int ReturnedCount,
    bool Truncated,
    string? Reason = null,
    long RequestCount = 0,
    DateTimeOffset? LastFetchUtc = null);

public sealed record GodsEyeLayersResponse(IReadOnlyDictionary<string, GodsEyeLayerSnapshot> Layers);

public sealed record GodsEyeLayerSettingsResponse(
    bool Enabled,
    int CadenceSeconds,
    double RadiusKm,
    int MaxCount,
    bool Configured,
    IReadOnlyDictionary<string, bool>? Sources = null);

public sealed record GodsEyeObserverSettings(
    double? LatitudeDeg = null,
    double? LongitudeDeg = null,
    string Grid = "");

public sealed record GodsEyeResolvedObserver(
    double LatitudeDeg,
    double LongitudeDeg,
    string Source);

public sealed record GodsEyeSettingsResponse(
    GodsEyeLayerSettingsResponse Earthquakes,
    GodsEyeLayerSettingsResponse Launches,
    GodsEyeLayerSettingsResponse Aircraft,
    GodsEyeLayerSettingsResponse Vessels,
    GodsEyeLayerSettingsResponse Fires,
    GodsEyeLayerSettingsResponse? Cameras = null,
    GodsEyeLayerSettingsResponse? MilitaryFlights = null,
    GodsEyeLayerSettingsResponse? Radio = null,
    GodsEyeLayerSettingsResponse? Bikeshare = null,
    GodsEyeLayerSettingsResponse? Traffic = null,
    GodsEyeLayerSettingsResponse? MappedInstallations = null,
    GodsEyeLogbookSettings? Logbook = null,
    GodsEyeObserverSettings? Observer = null,
    GodsEyeProviderSettingsResponse? Providers = null,
    GodsEyeResolvedObserver? ResolvedObserver = null);

public sealed record GodsEyeProviderSettingsResponse(
    bool GoogleMapsConfigured = false,
    bool CesiumIonConfigured = false,
    bool TomTomConfigured = false);

public sealed record GodsEyeProviderSettingsWrite(
    string? GoogleMapsApiKey = null,
    string? CesiumIonToken = null,
    string? TomTomApiKey = null,
    bool ClearGoogleMapsApiKey = false,
    bool ClearCesiumIonToken = false,
    bool ClearTomTomApiKey = false);

public sealed record GodsEyeProviderCredentialsResponse(
    string GoogleMapsApiKey,
    string CesiumIonToken);

public sealed record GodsEyeLogbookSettings(
    bool MatchMaritimeMobile = true,
    bool MatchAeronauticalMobile = true,
    bool PinMatchedTracks = true,
    bool StampSatellitesInView = true,
    bool ShowLiveLayers = true);

public sealed record GodsEyeLayerSettingsWrite(
    bool Enabled,
    int CadenceSeconds,
    double RadiusKm,
    int MaxCount,
    string? ApiKey = null,
    IReadOnlyDictionary<string, bool>? Sources = null);

public sealed record GodsEyeSettingsRequest(
    GodsEyeLayerSettingsWrite Earthquakes,
    GodsEyeLayerSettingsWrite Launches,
    GodsEyeLayerSettingsWrite Aircraft,
    GodsEyeLayerSettingsWrite Vessels,
    GodsEyeLayerSettingsWrite Fires,
    GodsEyeLayerSettingsWrite? Cameras = null,
    GodsEyeLayerSettingsWrite? MilitaryFlights = null,
    GodsEyeLayerSettingsWrite? Radio = null,
    GodsEyeLayerSettingsWrite? Bikeshare = null,
    GodsEyeLayerSettingsWrite? Traffic = null,
    GodsEyeLayerSettingsWrite? MappedInstallations = null,
    GodsEyeLogbookSettings? Logbook = null,
    GodsEyeObserverSettings? Observer = null,
    GodsEyeProviderSettingsWrite? Providers = null);

public readonly record struct GodsEyeObserver(double LatitudeDeg, double LongitudeDeg);
public readonly record struct GodsEyeBounds(double South, double West, double North, double East);
