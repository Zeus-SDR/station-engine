// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Upstreams:
// https://api.algotraffic.com/v4.0/Cameras
// https://cwwp2.dot.ca.gov/data/d4/cctv/cctvStatusD04.json
// https://511wi.gov/List/GetData/Cameras

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
#if ZEUS_PRODUCT_HOST
using Zeus.Product.Hosting;
namespace Zeus.Product.Hosting.GodsEye;
#else
using Zeus.Server;
namespace Zeus.Server.GodsEye;
#endif

public sealed class CameraFeedService : BackgroundService
{
    public const string HttpClientName = "GodsEyeCameras";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    public const long MaxResponseBytes = 8 * 1024 * 1024;
    internal const int DeadAfterFailures = 3;
    internal const int MaximumHealthEntries = 10_000;
    // One stream per source is probed for DRM after each metadata refresh (up to this many
    // candidates until one answers). A DOT that licenses its video to its own player encrypts
    // every camera the same way, so the answer is applied source-wide.
    internal const int EncryptionProbeCandidates = 3;
    internal const long MaxPlaylistBytes = 256 * 1024;

    private static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(1);
    private static readonly CameraSource[] Sources =
    [
        new(CameraSourceNames.AlabamaDot, "Alabama DOT ALGO Traffic", "Alabama Department of Transportation", "https://api.algotraffic.com/v4.0/Cameras", TimeSpan.FromMinutes(15), CameraAdapters.ParseAlabama),
        new(CameraSourceNames.Caltrans, "Caltrans District 4", "California Department of Transportation", "https://cwwp2.dot.ca.gov/data/d4/cctv/cctvStatusD04.json", TimeSpan.FromMinutes(15), CameraAdapters.ParseCaltrans),
        new(CameraSourceNames.WisconsinDot, "Wisconsin 511", "Wisconsin Department of Transportation", CameraAdapters.WisconsinUrl, TimeSpan.FromMinutes(10), CameraAdapters.ParseWisconsin, CameraAdapters.FetchWisconsinPagesAsync),
    ];

    private readonly IHttpClientFactory _clients;
    private readonly GodsEyeSettingsStore _settings;
    private readonly GodsEyeViewerRegistry _viewers;
    private readonly bool _ownsViewers;
    private readonly ILogger<CameraFeedService> _log;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Dictionary<string, SourceCache> _caches = Sources.ToDictionary(x => x.Id, x => new SourceCache(), StringComparer.Ordinal);
    private readonly Dictionary<string, CameraHealth> _health = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _nextSourceRefresh = new(StringComparer.Ordinal);
    private CancellationTokenSource _wake = new();
    private Func<GodsEyeObserver?> _observerResolver = static () => null;
    private GodsEyeObserver? _lastResolvedObserver;
    private bool _disposed;
    private readonly TaskCompletionSource _noViewerWaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _noObserverWaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task NoViewerWaitEnteredForTesting => _noViewerWaitEntered.Task;
    internal Task NoObserverWaitEnteredForTesting => _noObserverWaitEntered.Task;

    public CameraFeedService(IHttpClientFactory clients, GodsEyeSettingsStore settings, GodsEyeViewerRegistry viewers,
        ILogger<CameraFeedService> log, TimeProvider? timeProvider = null)
        : this(clients, settings, viewers, false, log, timeProvider)
    {
    }

    private CameraFeedService(IHttpClientFactory clients, GodsEyeSettingsStore settings, GodsEyeViewerRegistry viewers,
        bool ownsViewers, ILogger<CameraFeedService> log, TimeProvider? timeProvider)
    {
        _clients = clients; _settings = settings; _viewers = viewers; _ownsViewers = ownsViewers; _log = log; _time = timeProvider ?? TimeProvider.System;
    }

    internal CameraFeedService(IHttpClientFactory clients, GodsEyeSettingsStore settings,
        ILogger<CameraFeedService> log, TimeProvider? timeProvider = null)
        : this(clients, settings, new GodsEyeViewerRegistry(timeProvider), true, log, timeProvider) { }

    public void SetObserver(GodsEyeObserver? observer) => SetObserverResolver(() => observer);
    public void SetObserverResolver(Func<GodsEyeObserver?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_sync) _observerResolver = resolver;
    }
    public void SettingsChanged() { lock (_sync) { if (_disposed) return; _wake.Cancel(); _wake.Dispose(); _wake = new CancellationTokenSource(); } }

    public CameraFeedSnapshot GetSnapshot(GodsEyeObserver? observer = null)
    {
        var settings = _settings.GetInternal()[GodsEyeLayerNames.Cameras];
        var point = observer ?? (settings.Enabled ? ResolveObserver() : null);
        List<CameraCandidate> candidates; DateTimeOffset? fetched; long requestCount; DateTimeOffset? lastFetch;
        lock (_sync)
        {
            candidates = _caches.Values.SelectMany(cache => cache.Cameras).ToList();
            fetched = _caches.Values.Where(cache => cache.FetchedUtc is not null).Select(cache => cache.FetchedUtc).Max();
            requestCount = _caches.Values.Sum(cache => cache.RequestCount);
            lastFetch = _caches.Values.Where(cache => cache.LastFetchUtc is not null).Select(cache => cache.LastFetchUtc).Max();
        }
        if (!settings.Enabled) return Empty(GodsEyeFreshness.Unavailable, "Weather and incident cameras are off.");
        if (point is null) return Empty(GodsEyeFreshness.Unavailable, "Choose a point of interest or configure a station QTH.");

        var bounded = candidates.Where(camera => SourceEnabled(settings, camera.Source)
            && GodsEyeFeedsService.DistanceKm(point.Value.LatitudeDeg, point.Value.LongitudeDeg, camera.LatitudeDeg, camera.LongitudeDeg) <= settings.RadiusKm).ToList();
        var insecure = bounded.Count(camera => camera.Exclusion == CameraExclusion.Insecure);
        var unsupported = bounded.Count(camera => camera.Exclusion == CameraExclusion.Unsupported);
        var usable = bounded.Where(camera => camera.Exclusion == CameraExclusion.None)
            .OrderBy(camera => GodsEyeFeedsService.DistanceKm(point.Value.LatitudeDeg, point.Value.LongitudeDeg, camera.LatitudeDeg, camera.LongitudeDeg))
            .ToList();
        var items = usable.Take(settings.MaxCount).Select(ToDto).ToArray();
        var sourceCounts = bounded.GroupBy(camera => camera.Source).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var excluded = new CameraExclusionCounts(insecure, unsupported);
        var reason = excluded.Total > 0 ? $"{excluded.Total} camera streams excluded: {insecure} insecure and {unsupported} unsupported." : null;
        var state = fetched is null || items.Length == 0 ? GodsEyeFreshness.Unavailable : GodsEyeFreshness.Live;
        var effectiveCadence = Sources.Where(source => SourceEnabled(settings, source.Id))
            .Select(source => source.Cadence)
            .Append(TimeSpan.FromSeconds(settings.CadenceSeconds))
            .Max();
        if (fetched is not null && _time.GetUtcNow() - fetched > effectiveCadence * 2) state = GodsEyeFreshness.Stale;
        return new(GodsEyeLayerNames.Cameras, state, fetched, items, bounded.Count, items.Length,
            usable.Count > items.Length, reason ?? (items.Length == 0 ? "No secure cameras are available within the selected radius." : null), excluded, sourceCounts, requestCount, lastFetch);

        CameraFeedSnapshot Empty(string state, string reasonText) => new(GodsEyeLayerNames.Cameras, state, null, [], 0, 0, false,
            reasonText, new(0, 0), new Dictionary<string, int>(), requestCount, lastFetch);
    }

    internal async Task<bool> RefreshSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        var source = Sources.SingleOrDefault(item => item.Id == sourceId);
        if (source is null) return false;
        var settings = _settings.GetInternal()[GodsEyeLayerNames.Cameras];
        if (!settings.Enabled || !SourceEnabled(settings, sourceId)) return false;
        var observer = ResolveObserver();
        if (observer is null) return false;
        try
        {
            lock (_sync) { _caches[sourceId].RequestCount++; _caches[sourceId].LastFetchUtc = _time.GetUtcNow(); }
            var client = _clients.CreateClient(HttpClientName);
            IReadOnlyList<string> payloads;
            if (source.Fetcher is not null) payloads = await source.Fetcher(client, cancellationToken).ConfigureAwait(false);
            else
            {
                using var response = await client.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                payloads = [await ReadBoundedStringAsync(response.Content, MaxResponseBytes, cancellationToken).ConfigureAwait(false)];
            }
            var parsedPayloads = payloads.Select(payload => source.Parser(payload, source)).ToArray();
            var parsed = parsedPayloads.SelectMany(payload => payload.Cameras).ToArray();
            var skippedRows = parsedPayloads.Sum(payload => payload.SkippedRows);
            if (skippedRows > 0)
                _log.LogWarning("Skipped {Count} malformed camera rows from {Source}", skippedRows, source.DisplayName);
            var encrypted = await CameraStreamProbe.SourceStreamsEncryptedAsync(client, parsed, EncryptionProbeCandidates, MaxPlaylistBytes, cancellationToken).ConfigureAwait(false);
            bool previouslyEncrypted; lock (_sync) previouslyEncrypted = _caches[sourceId].StreamsEncrypted;
            // An inconclusive probe (offline camera, non-playlist answer) keeps the last verdict.
            var streamsEncrypted = encrypted ?? previouslyEncrypted;
            if (streamsEncrypted) parsed = parsed.Select(CameraAdapters.WithoutEncryptedStream).ToArray();
            if (streamsEncrypted != previouslyEncrypted)
                _log.LogInformation("Weather and incident camera source {Source} live streams are {State}", source.DisplayName, streamsEncrypted ? "DRM-encrypted; publishing stills only" : "playable again");
            lock (_sync)
            {
                _caches[sourceId].StreamsEncrypted = streamsEncrypted;
                foreach (var camera in parsed.Where(camera => camera.Exclusion == CameraExclusion.None))
                    if (camera.SourceHealthy || MayRetry(camera.Id)) RecordResult(camera.Id, camera.SourceHealthy);
                _caches[sourceId].Cameras = parsed;
                _caches[sourceId].FetchedUtc = _time.GetUtcNow();
                _caches[sourceId].Failed = false;
                PruneHealthLocked();
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                _caches[sourceId].Failed = true;
                foreach (var camera in _caches[sourceId].Cameras.Where(camera => camera.Exclusion == CameraExclusion.None && MayRetry(camera.Id)))
                    RecordResult(camera.Id, false);
            }
            _log.LogWarning(ex, "Weather and incident camera source {Source} refresh failed", source.DisplayName);
            return false;
        }
    }

    internal void RecordCameraResult(string id, bool succeeded) { lock (_sync) { if (succeeded || MayRetry(id)) RecordResult(id, succeeded); } }

    internal bool CameraMayRetry(string id)
    {
        lock (_sync) return MayRetry(id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var viewerChanged = _viewers.ChangedToken;
            if (!_viewers.HasViewers)
            {
                _noViewerWaitEntered.TrySetResult();
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, viewerChanged);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, waiting.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
                continue;
            }
            var settings = _settings.GetInternal()[GodsEyeLayerNames.Cameras];
            var observer = settings.Enabled ? ResolveObserver() : null;
            CancellationToken wake; lock (_sync) wake = _wake.Token;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, wake, viewerChanged);
            var now = _time.GetUtcNow();
            if (_viewers.HasViewers && settings.Enabled && observer is not null)
                foreach (var source in Sources.Where(source => SourceEnabled(settings, source.Id)))
                {
                    bool due; lock (_sync) due = !_nextSourceRefresh.TryGetValue(source.Id, out var next) || next <= now;
                    if (!due) continue;
                    try { await RefreshSourceAsync(source.Id, linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
                    var operatorCadence = TimeSpan.FromSeconds(settings.CadenceSeconds);
                    lock (_sync) _nextSourceRefresh[source.Id] = _time.GetUtcNow() + (operatorCadence > source.Cadence ? operatorCadence : source.Cadence);
                }
            if (settings.Enabled && observer is null) _noObserverWaitEntered.TrySetResult();
            try { await Task.Delay(TimeSpan.FromMinutes(1), linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
        }
    }

    public override void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _wake.Cancel();
            _wake.Dispose();
        }
        if (_ownsViewers) _viewers.Dispose();
        base.Dispose();
    }

    private CameraDto ToDto(CameraCandidate camera)
    {
        CameraHealth health; lock (_sync) health = _health.GetValueOrDefault(camera.Id) ?? new CameraHealth();
        var available = camera.SourceHealthy && health.ConsecutiveFailures < DeadAfterFailures;
        return new(camera.Id, camera.Name, camera.LatitudeDeg, camera.LongitudeDeg, camera.TimestampUtc,
            camera.HeadingDeg, camera.FieldOfViewDeg, camera.SnapshotUrl, camera.SnapshotRefreshSeconds,
            camera.StreamUrl, camera.StreamType.ToString().ToLowerInvariant(), camera.Source, camera.Attribution,
            available, available ? null : "Camera has not responded after repeated attempts.", health.ConsecutiveFailures, health.LastSuccessUtc);
    }

    private void RecordResult(string id, bool succeeded)
    {
        var health = _health.GetValueOrDefault(id) ?? new CameraHealth();
        health.LastObservedUtc = _time.GetUtcNow();
        if (succeeded) { health.ConsecutiveFailures = 0; health.LastSuccessUtc = _time.GetUtcNow(); health.NextRetryUtc = null; }
        else { health.ConsecutiveFailures++; if (health.ConsecutiveFailures >= DeadAfterFailures) health.NextRetryUtc = _time.GetUtcNow() + FailureBackoff; }
        _health[id] = health;
        if (_health.Count > MaximumHealthEntries)
            foreach (var key in _health.OrderBy(pair => pair.Value.LastObservedUtc).Take(_health.Count - MaximumHealthEntries).Select(pair => pair.Key).ToArray())
                _health.Remove(key);
    }

    private void PruneHealthLocked()
    {
        var currentIds = _caches.Values.SelectMany(cache => cache.Cameras).Select(camera => camera.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _health.Keys.Where(id => !currentIds.Contains(id)).ToArray()) _health.Remove(staleId);
    }

    internal static bool SourceEnabled(GodsEyeLayerSettings settings, string sourceId) =>
        settings.Sources is null || !settings.Sources.TryGetValue(sourceId, out var enabled) || enabled;

    internal static async Task<string> ReadBoundedStringAsync(HttpContent content, long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes < 0 || content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("Camera metadata response exceeds byte cap.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > maximumBytes)
                throw new InvalidDataException("Camera metadata response exceeds byte cap.");
            destination.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
    }

    private bool MayRetry(string id) => !_health.TryGetValue(id, out var health)
        || health.NextRetryUtc is null
        || health.NextRetryUtc <= _time.GetUtcNow();

    // The resolver reads the settings store and the host's station identity on every call. A
    // transient store fault must not escape into the background loop (that would stop the host),
    // so a failed resolution keeps the last observer that resolved successfully.
    private GodsEyeObserver? ResolveObserver()
    {
        Func<GodsEyeObserver?> resolver;
        lock (_sync) resolver = _observerResolver;
        try
        {
            var observer = resolver();
            lock (_sync) _lastResolvedObserver = observer;
            return observer;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Weather and incident camera observer resolution failed; keeping the last known observer");
            lock (_sync) return _lastResolvedObserver;
        }
    }

    private sealed class SourceCache { public IReadOnlyList<CameraCandidate> Cameras { get; set; } = []; public DateTimeOffset? FetchedUtc { get; set; } public bool Failed { get; set; } public bool StreamsEncrypted { get; set; } public long RequestCount { get; set; } public DateTimeOffset? LastFetchUtc { get; set; } }
    private sealed class CameraHealth { public int ConsecutiveFailures { get; set; } public DateTimeOffset? LastSuccessUtc { get; set; } public DateTimeOffset? NextRetryUtc { get; set; } public DateTimeOffset LastObservedUtc { get; set; } }
    internal int HealthCountForTesting { get { lock (_sync) return _health.Count; } }
}

internal sealed record CameraSource(
    string Id,
    string DisplayName,
    string Attribution,
    string Url,
    TimeSpan Cadence,
    Func<string, CameraSource, CameraParseResult> Parser,
    Func<HttpClient, CancellationToken, Task<IReadOnlyList<string>>>? Fetcher = null);
internal sealed record CameraParseResult(IReadOnlyList<CameraCandidate> Cameras, int SkippedRows);
internal enum CameraExclusion { None, Insecure, Unsupported }
// Encrypted: the operator publishes a stream, but under DRM (FairPlay / Widevine / PlayReady) that
// only their own licensed player can decrypt. The still remains the camera's usable picture.
internal enum CameraStreamType { None, Mjpeg, Hls, Encrypted }
internal sealed record CameraCandidate(string Id, string Name, double LatitudeDeg, double LongitudeDeg, DateTimeOffset TimestampUtc,
    double? HeadingDeg, double? FieldOfViewDeg, string? SnapshotUrl, int SnapshotRefreshSeconds,
    string? StreamUrl, CameraStreamType StreamType, string Source, string Attribution, bool SourceHealthy, CameraExclusion Exclusion);

internal static class CameraAdapters
{
    internal const string WisconsinUrl = "https://511wi.gov/List/GetData/Cameras";
    private static readonly Uri WisconsinBaseUri = new("https://511wi.gov/");

    internal static CameraParseResult ParseAlabama(string json, CameraSource source)
    {
        using var doc = JsonDocument.Parse(json); var result = new List<CameraCandidate>(); var skipped = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            try
            {
                var location = item.GetProperty("location");
                var id = item.GetProperty("id").ToString();
                var route = String(location, "displayRouteDesignator");
                var crossStreet = String(location, "displayCrossStreet");
                var name = !string.IsNullOrWhiteSpace(route) && !string.IsNullOrWhiteSpace(crossStreet)
                    ? $"{route} at {crossStreet}" : route ?? crossStreet ?? $"Alabama camera {id}";
                var streamUrl = item.TryGetProperty("playbackUrls", out var playback) ? String(playback, "hls") : null;
                result.Add(Build(source, id, name, Number(location, "latitude"), Number(location, "longitude"),
                    String(item, "snapshotImageUrl"), 60, streamUrl, CameraStreamType.Hls,
                    string.Equals(String(item, "accessLevel"), "Public", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception)
            {
                skipped++;
            }
        }
        return new(result, skipped);
    }

    internal static CameraParseResult ParseCaltrans(string json, CameraSource source)
    {
        using var doc = JsonDocument.Parse(json); var result = new List<CameraCandidate>(); var skipped = 0;
        foreach (var row in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            try
            {
                var camera = row.GetProperty("cctv"); var location = camera.GetProperty("location"); var image = camera.GetProperty("imageData");
                var id = $"d4-{String(camera, "index")}";
                var mjpeg = String(image, "mjpegURL");
                var stream = !string.IsNullOrWhiteSpace(mjpeg) ? mjpeg : String(image, "streamingVideoURL");
                var streamType = !string.IsNullOrWhiteSpace(mjpeg) ? CameraStreamType.Mjpeg : CameraStreamType.Hls;
                var snapshot = image.TryGetProperty("static", out var staticImage) ? String(staticImage, "currentImageURL") : null;
                var minutes = staticImage.ValueKind == JsonValueKind.Object
                    && int.TryParse(String(staticImage, "currentImageUpdateFrequency"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMinutes)
                    && parsedMinutes > 0 ? parsedMinutes : 1;
                var refreshSeconds = Math.Clamp(minutes * 60, 15, 300);
                result.Add(Build(source, id, String(location, "locationName") ?? id, Number(location, "latitude"), Number(location, "longitude"),
                    snapshot, refreshSeconds, stream, streamType,
                    bool.TryParse(String(camera, "inService"), out var inService) && inService));
            }
            catch (Exception)
            {
                skipped++;
            }
        }
        return new(result, skipped);
    }

    internal static CameraParseResult ParseWisconsin(string json, CameraSource source)
    {
        using var doc = JsonDocument.Parse(json); var result = new List<CameraCandidate>(); var skipped = 0;
        foreach (var camera in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            try
            {
                if (!camera.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0) continue;
                var image = images[0]; var point = camera.GetProperty("latLng").GetProperty("geography").GetProperty("wellKnownText").GetString() ?? "";
                var coordinates = ParsePoint(point);
                var snapshot = ResolveWisconsinUrl(String(image, "imageUrl"));
                var videoUrl = String(image, "videoUrl");
                var videoType = String(image, "videoType") ?? "";
                var hls = IsHls(videoUrl, videoType) && !Bool(image, "isVideoAuthRequired") && !Bool(image, "videoDisabled") ? videoUrl : null;
                var healthy = !Bool(image, "disabled") && !Bool(image, "blocked");
                result.Add(Build(source, camera.GetProperty("id").ToString(), String(camera, "location") ?? String(camera, "roadway") ?? "Wisconsin road camera",
                    coordinates.Latitude, coordinates.Longitude, snapshot, 15, hls, CameraStreamType.Hls, healthy));
            }
            catch (Exception)
            {
                skipped++;
            }
        }
        return new(result, skipped);
    }

    internal static CameraCandidate Build(CameraSource source, string id, string name, double latitude, double longitude,
        string? snapshotUrl, int snapshotRefreshSeconds, string? streamUrl, CameraStreamType streamType, bool healthy)
    {
        var hadSnapshot = !string.IsNullOrWhiteSpace(snapshotUrl);
        var hadStream = !string.IsNullOrWhiteSpace(streamUrl);
        var secureSnapshot = SecureHttps(snapshotUrl);
        var supportedStream = streamType is CameraStreamType.Mjpeg or CameraStreamType.Hls;
        var secureStream = supportedStream ? SecureHttps(streamUrl) : null;
        var exclusion = secureSnapshot is not null || secureStream is not null
            ? CameraExclusion.None
            : hadSnapshot || hadStream
                ? AllPresentUrlsAreHttp(snapshotUrl, streamUrl) ? CameraExclusion.Insecure : CameraExclusion.Unsupported
                : CameraExclusion.Unsupported;
        return new($"{source.Id}-{id}", name, latitude, longitude, DateTimeOffset.UtcNow, null, null,
            secureSnapshot, Math.Max(10, snapshotRefreshSeconds), secureStream,
            secureStream is null ? CameraStreamType.None : streamType,
            source.Id, source.Attribution, healthy, exclusion);
    }

    internal static CameraCandidate WithoutEncryptedStream(CameraCandidate camera) => camera.StreamUrl is null
        ? camera
        : camera with
        {
            StreamUrl = null,
            StreamType = CameraStreamType.Encrypted,
            Exclusion = camera.SnapshotUrl is null ? CameraExclusion.Unsupported : camera.Exclusion,
        };

    internal static async Task<IReadOnlyList<string>> FetchWisconsinPagesAsync(HttpClient client, CancellationToken cancellationToken) =>
        await FetchWisconsinPagesAsync(client, 100, 20, CameraFeedService.MaxResponseBytes, cancellationToken).ConfigureAwait(false);

    internal static async Task<IReadOnlyList<string>> FetchWisconsinPagesAsync(HttpClient client, int pageSize, int pageCap, long byteCap, CancellationToken cancellationToken)
    {
        var pages = new List<string>();
        long consumed = 0;
        for (var page = 0; page < pageCap; page++)
        {
            var start = page * pageSize;
            var query = Uri.EscapeDataString(JsonSerializer.Serialize(new { columns = Array.Empty<string>(), start, length = pageSize }));
            using var response = await client.GetAsync($"{WisconsinUrl}?query={query}&lang=en", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await CameraFeedService.ReadBoundedStringAsync(response.Content, byteCap - consumed, cancellationToken).ConfigureAwait(false);
            consumed += System.Text.Encoding.UTF8.GetByteCount(payload);
            pages.Add(payload);
            using var doc = JsonDocument.Parse(payload);
            var rowCount = doc.RootElement.GetProperty("data").GetArrayLength();
            var recordsTotal = doc.RootElement.TryGetProperty("recordsTotal", out var total) && total.TryGetInt32(out var parsedTotal)
                ? parsedTotal : start + rowCount;
            if (rowCount < pageSize || start + rowCount >= recordsTotal) break;
        }
        return pages;
    }

    private static string? ResolveWisconsinUrl(string? value) => string.IsNullOrWhiteSpace(value)
        ? null : new Uri(WisconsinBaseUri, value).AbsoluteUri;
    private static string? SecureHttps(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? uri.AbsoluteUri : null;
    private static bool AllPresentUrlsAreHttp(params string?[] values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .All(value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
    private static bool IsHls(string? url, string type) => type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
        || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static double Number(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Number ? value.GetProperty(name).GetDouble() : double.Parse(value.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    private static bool Bool(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static (double Longitude, double Latitude) ParsePoint(string point)
    {
        var values = point.Replace("POINT (", "", StringComparison.Ordinal).TrimEnd(')').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (double.Parse(values[0], CultureInfo.InvariantCulture), double.Parse(values[1], CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Reads an HLS playlist the way a player would, far enough to learn whether the segments are
/// DRM-protected. AES-128 with an identity key is ordinary HLS and plays everywhere; SAMPLE-AES or
/// any non-identity KEYFORMAT (FairPlay, Widevine, PlayReady) needs a license Zeus can never hold.
/// </summary>
internal static class CameraStreamProbe
{
    internal static async Task<bool?> SourceStreamsEncryptedAsync(HttpClient client, IReadOnlyList<CameraCandidate> cameras,
        int maxProbes, long byteCap, CancellationToken cancellationToken)
    {
        var candidates = cameras
            .Where(camera => camera.Exclusion == CameraExclusion.None && camera.SourceHealthy && camera.StreamType == CameraStreamType.Hls && camera.StreamUrl is not null)
            .Select(camera => camera.StreamUrl!)
            .Distinct(StringComparer.Ordinal)
            .Take(maxProbes);
        foreach (var url in candidates)
        {
            var verdict = await StreamIsDrmProtectedAsync(client, url, byteCap, cancellationToken).ConfigureAwait(false);
            if (verdict is not null) return verdict;
        }
        return null;
    }

    internal static async Task<bool?> StreamIsDrmProtectedAsync(HttpClient client, string masterUrl, long byteCap, CancellationToken cancellationToken)
    {
        try
        {
            var master = await FetchPlaylistAsync(client, masterUrl, byteCap, cancellationToken).ConfigureAwait(false);
            if (master is null) return null;
            var verdict = PlaylistIsDrmProtected(master);
            if (verdict is not null) return verdict;
            var variant = FirstVariantUri(master, new Uri(masterUrl, UriKind.Absolute));
            if (variant is null) return false;
            var media = await FetchPlaylistAsync(client, variant, byteCap, cancellationToken).ConfigureAwait(false);
            return media is null ? null : PlaylistIsDrmProtected(media) ?? false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a key line demands DRM, false when a key line is plain AES-128, null when the
    /// playlist carries no key line at all (a master playlist usually does not; look at a variant).
    /// </summary>
    internal static bool? PlaylistIsDrmProtected(string playlist)
    {
        if (!playlist.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)) return null;
        bool? verdict = null;
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) && !line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal)) continue;
            var attributes = line[(line.IndexOf(':') + 1)..];
            var method = Attribute(attributes, "METHOD") ?? "";
            if (string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase)) continue;
            var keyFormat = Attribute(attributes, "KEYFORMAT");
            var plainAes = string.Equals(method, "AES-128", StringComparison.OrdinalIgnoreCase)
                && (keyFormat is null || string.Equals(keyFormat, "identity", StringComparison.OrdinalIgnoreCase));
            if (!plainAes) return true;
            verdict = false;
        }
        return verdict;
    }

    internal static string? FirstVariantUri(string playlist, Uri baseUri)
    {
        var expectUri = false;
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) { expectUri = true; continue; }
            if (line.StartsWith('#')) continue;
            if (!expectUri) continue;
            return Uri.TryCreate(baseUri, line, out var resolved) ? resolved.AbsoluteUri : null;
        }
        return null;
    }

    private static async Task<string?> FetchPlaylistAsync(HttpClient client, string url, long byteCap, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var body = await CameraFeedService.ReadBoundedStringAsync(response.Content, byteCap, cancellationToken).ConfigureAwait(false);
        return body.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal) ? body : null;
    }

    private static string? Attribute(string attributes, string name)
    {
        var index = 0;
        while (index < attributes.Length)
        {
            var equals = attributes.IndexOf('=', index);
            if (equals < 0) return null;
            var key = attributes[index..equals].Trim();
            string value; int next;
            if (equals + 1 < attributes.Length && attributes[equals + 1] == '"')
            {
                var close = attributes.IndexOf('"', equals + 2);
                if (close < 0) return null;
                value = attributes[(equals + 2)..close];
                next = close + 1;
            }
            else
            {
                var comma = attributes.IndexOf(',', equals + 1);
                value = comma < 0 ? attributes[(equals + 1)..] : attributes[(equals + 1)..comma];
                next = comma < 0 ? attributes.Length : comma;
            }
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value.Trim();
            index = next + 1;
        }
        return null;
    }
}
