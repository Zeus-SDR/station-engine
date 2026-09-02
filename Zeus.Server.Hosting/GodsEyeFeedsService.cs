// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// USGS GeoJSON, Launch Library 2, OpenSky, AISStream, and NASA FIRMS are
// operator-selected display feeds. See ATTRIBUTIONS.md for provenance.

using System.Globalization;
using System.Net;
using Microsoft.Extensions.Hosting;
#if ZEUS_PRODUCT_HOST
using Zeus.Product.Hosting.GodsEye;
#else
using Zeus.Server.GodsEye;
#endif

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting;
#else
namespace Zeus.Server;
#endif

public sealed class GodsEyeFeedsService : BackgroundService
{
    public const string HttpClientName = "GodsEyeFeeds";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan OpenSkyAnonymousMinimumInterval = TimeSpan.FromMinutes(15);
    internal const double OpenSkyMaximumBoundsAreaSquareDegrees = 399;
    internal static readonly TimeSpan AisHealthyConnectionThreshold = TimeSpan.FromMinutes(5);
    public const long MaxResponseBytes = 8 * 1024 * 1024;
    internal const long MilitaryFlightsMaxResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];
    private readonly IHttpClientFactory _httpClients;
    private readonly GodsEyeSettingsStore _settings;
    private readonly IAisStreamClient _ais;
    private readonly GodsEyeViewerRegistry _viewers;
    private readonly bool _ownsViewers;
    private readonly ILogger<GodsEyeFeedsService> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _initialDelay;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CancellationToken, Task<GodsEyeObserver?>>? _resolveObserver;
    private readonly SemaphoreSlim _observerResolutionGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, LayerCache> _caches = GodsEyeLayerNames.FeedLayers.ToDictionary(x => x, x => new LayerCache(x), StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _nextAllowed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _nextRefresh = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _backoffAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GodsEyeItemDto> _vessels = new(StringComparer.Ordinal);
    private CancellationTokenSource _configurationChanged = new();
    private GodsEyeObserver? _observer;
    private DateTimeOffset _nextObserverResolution;
    private int _aisReconnectAttempt;
    private bool _disposed;
    private readonly TaskCompletionSource _noViewerWaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task NoViewerWaitEnteredForTesting => _noViewerWaitEntered.Task;

    public GodsEyeFeedsService(IHttpClientFactory httpClients, GodsEyeSettingsStore settings, IAisStreamClient ais,
        GodsEyeViewerRegistry viewers, Func<CancellationToken, Task<GodsEyeObserver?>> observerResolver, ILogger<GodsEyeFeedsService> log)
        : this(httpClients, settings, ais, viewers, false, log, Task.Delay,
            TimeSpan.FromSeconds(Random.Shared.Next(5, 121)), TimeProvider.System, observerResolver) { }

    public GodsEyeFeedsService(IHttpClientFactory httpClients, GodsEyeSettingsStore settings, IAisStreamClient ais,
        ILogger<GodsEyeFeedsService> log, Func<TimeSpan, CancellationToken, Task> delay, TimeProvider? timeProvider = null,
        GodsEyeViewerRegistry? viewers = null, Func<GodsEyeObserver?>? observerResolver = null)
        : this(httpClients, settings, ais, viewers ?? new GodsEyeViewerRegistry(timeProvider), viewers is null, log, delay,
            TimeSpan.Zero, timeProvider ?? TimeProvider.System,
            observerResolver is null ? null : _ => Task.FromResult(observerResolver())) { }

    private GodsEyeFeedsService(IHttpClientFactory httpClients, GodsEyeSettingsStore settings, IAisStreamClient ais,
        GodsEyeViewerRegistry viewers, bool ownsViewers, ILogger<GodsEyeFeedsService> log, Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan initialDelay, TimeProvider timeProvider, Func<CancellationToken, Task<GodsEyeObserver?>>? observerResolver)
    {
        _httpClients = httpClients; _settings = settings; _ais = ais; _viewers = viewers; _ownsViewers = ownsViewers; _log = log; _delay = delay; _initialDelay = initialDelay; _timeProvider = timeProvider; _resolveObserver = observerResolver;
        ApplyConfigurationStates();
    }

    public void SetObserver(GodsEyeObserver? observer)
    {
        lock (_sync)
        {
            if (Nullable.Equals(_observer, observer)) return;
            var becameAvailable = _observer is null && observer is not null;
            _observer = observer;
            if (becameAvailable) WakeRefreshLoopLocked();
        }
    }

    private void WakeRefreshLoopLocked()
    {
        _configurationChanged.Cancel();
        _configurationChanged.Dispose();
        _configurationChanged = new CancellationTokenSource();
    }

    public GodsEyeLogbookSettings GetLogbookSettings() => _settings.GetLogbook();

    public void SettingsChanged()
    {
        var settings = _settings.GetInternal();
        lock (_sync)
        {
            ApplyConfigurationStatesLocked(settings);
            _observer = null;
            _nextObserverResolution = default;
            _nextRefresh.Clear();
            _aisReconnectAttempt = 0;
            WakeRefreshLoopLocked();
        }
    }

    public GodsEyeLayersResponse GetSnapshot(GodsEyeObserver? observer = null)
    {
        if (observer is not null) SetObserver(observer);
        var settings = _settings.GetInternal();
        lock (_sync)
            return new GodsEyeLayersResponse(_caches.ToDictionary(
                x => x.Key, x => CurrentSnapshot(x.Value, settings[x.Key]), StringComparer.Ordinal));
    }

    public GodsEyeLayerSnapshot? GetLayer(string layer, GodsEyeObserver? observer = null)
    {
        var normalized = GodsEyeLayerNames.Normalize(layer);
        if (normalized.Length == 0) return null;
        if (observer is not null) SetObserver(observer);
        var settings = _settings.GetInternal()[normalized];
        lock (_sync) return CurrentSnapshot(_caches[normalized], settings);
    }

    internal void PublishForTesting(string layer, IReadOnlyList<GodsEyeItemDto> items, DateTimeOffset fetchedUtc)
    {
        var normalized = GodsEyeLayerNames.Normalize(layer);
        if (normalized.Length == 0) throw new ArgumentOutOfRangeException(nameof(layer));
        lock (_sync) PublishLocked(normalized, items, _settings.GetInternal()[normalized], fetchedUtc);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_initialDelay > TimeSpan.Zero && !_viewers.HasViewers)
        {
            var viewerChanged = _viewers.ChangedToken;
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, viewerChanged);
            try { await _delay(_initialDelay, startup.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (viewerChanged.IsCancellationRequested) { }
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            var viewerChanged = _viewers.ChangedToken;
            if (!_viewers.HasViewers)
            {
                lock (_sync) _nextRefresh.Clear();
                _noViewerWaitEntered.TrySetResult();
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, viewerChanged);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, waiting.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
                continue;
            }
            await EnsureObserverAsync(stoppingToken).ConfigureAwait(false);
            CancellationToken changed;
            lock (_sync) changed = _configurationChanged.Token;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, changed, viewerChanged);
            try
            {
                await Task.WhenAll(
                    RunHttpLayerAsync(GodsEyeLayerNames.Earthquakes, linked.Token),
                    RunHttpLayerAsync(GodsEyeLayerNames.Launches, linked.Token),
                    RunHttpLayerAsync(GodsEyeLayerNames.Aircraft, linked.Token),
                    RunAisLayerAsync(linked.Token),
                    RunHttpLayerAsync(GodsEyeLayerNames.Fires, linked.Token),
                    RunHttpLayerAsync(GodsEyeLayerNames.MilitaryFlights, linked.Token),
                    RunHttpLayerAsync(GodsEyeLayerNames.Radio, linked.Token),
                    RunHttpLayerAsync(GodsEyeLayerNames.Bikeshare, linked.Token),
                    RunHttpLayerAsync(GodsEyeLayerNames.Traffic, linked.Token),
                    RunHttpLayerAsync(GodsEyeLayerNames.MappedInstallations, linked.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (viewerChanged.IsCancellationRequested && !stoppingToken.IsCancellationRequested) { continue; }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task RunHttpLayerAsync(string layer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await EnsureObserverAsync(cancellationToken).ConfigureAwait(false);
            var settings = _settings.GetInternal()[layer];
            var now = _timeProvider.GetUtcNow();
            var due = false;
            if (_viewers.HasViewers && settings.Enabled && settings.Configured)
            {
                lock (_sync)
                {
                    due = !_nextRefresh.TryGetValue(layer, out var next) || next <= now;
                    if (due) _nextRefresh[layer] = now + EffectiveCadence(settings);
                }
                if (due) await RefreshLayerAsync(layer, cancellationToken).ConfigureAwait(false);
            }
            GodsEyeObserver? observer; lock (_sync) observer = _observer;
            var delay = observer is null ? TimeSpan.FromSeconds(Math.Min(5, settings.CadenceSeconds)) : TimeSpan.FromSeconds(settings.CadenceSeconds);
            await _delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAisLayerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await EnsureObserverAsync(cancellationToken).ConfigureAwait(false);
            var settings = _settings.GetInternal()[GodsEyeLayerNames.Vessels];
            if (!_viewers.HasViewers || !settings.Enabled || !settings.Configured)
            {
                lock (_sync) _aisReconnectAttempt = 0;
                await _delay(TimeSpan.FromSeconds(settings.CadenceSeconds), cancellationToken).ConfigureAwait(false);
                continue;
            }
            GodsEyeObserver? observer;
            lock (_sync) observer = _observer;
            if (observer is null)
            {
                MarkFailure(GodsEyeLayerNames.Vessels, "Operator QTH is unavailable.", null);
                await _delay(TimeSpan.FromSeconds(settings.CadenceSeconds), cancellationToken).ConfigureAwait(false);
                continue;
            }
            var connectedUtc = _timeProvider.GetUtcNow();
            try
            {
                var bounds = BoundsAround(observer.Value, settings.RadiusKm);
                RecordRequest(GodsEyeLayerNames.Vessels);
                await _ais.RunAsync(settings.ApiKey, bounds, OnAisMessageAsync, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                MarkFailure(GodsEyeLayerNames.Vessels, "AISStream connection closed; reconnecting.", null);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) return;
                MarkFailure(GodsEyeLayerNames.Vessels, "AISStream connection is unavailable.", null);
            }
            catch (Exception ex)
            {
                MarkFailure(GodsEyeLayerNames.Vessels, "AISStream connection is unavailable.", ex);
            }
            TimeSpan reconnectDelay;
            lock (_sync)
            {
                if (_timeProvider.GetUtcNow() - connectedUtc >= AisHealthyConnectionThreshold) _aisReconnectAttempt = 0;
                reconnectDelay = Backoff[Math.Min(_aisReconnectAttempt, Backoff.Length - 1)];
                _aisReconnectAttempt++;
            }
            await _delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureObserverAsync(CancellationToken cancellationToken)
    {
        if (_resolveObserver is null) return;
        lock (_sync)
            if (_observer is not null || _nextObserverResolution > _timeProvider.GetUtcNow()) return;
        await _observerResolutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
                if (_observer is not null || _nextObserverResolution > _timeProvider.GetUtcNow()) return;
            try { SetObserver(await _resolveObserver(cancellationToken).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogDebug(ex, "God's Eye observer refresh failed; retaining the prior QTH"); }
            lock (_sync) if (_observer is null) _nextObserverResolution = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(5);
        }
        finally { _observerResolutionGate.Release(); }
    }

    internal Task ResolveObserverForTestingAsync(CancellationToken cancellationToken = default) =>
        EnsureObserverAsync(cancellationToken);

    private Task OnAisMessageAsync(string json)
    {
        try
        {
            var item = GodsEyeParsers.ParseAisPosition(json);
            if (item is null) return Task.CompletedTask;
            var settings = _settings.GetInternal()[GodsEyeLayerNames.Vessels];
            lock (_sync)
            {
                _vessels[item.Id] = item;
                var now = _timeProvider.GetUtcNow();
                var cutoff = now.AddMinutes(-15);
                foreach (var id in _vessels.Where(x => x.Value.TimestampUtc < cutoff).Select(x => x.Key).ToArray()) _vessels.Remove(id);
                PublishLocked(GodsEyeLayerNames.Vessels, _vessels.Values.ToArray(), settings, now);
            }
        }
        catch (Exception ex) { MarkFailure(GodsEyeLayerNames.Vessels, "AISStream returned malformed data.", ex); }
        return Task.CompletedTask;
    }

    public async Task<bool> RefreshLayerAsync(string layer, CancellationToken cancellationToken = default)
    {
        layer = GodsEyeLayerNames.Normalize(layer);
        if (layer.Length == 0 || layer == GodsEyeLayerNames.Vessels) return false;
        var settings = _settings.GetInternal()[layer];
        if (!settings.Enabled || !settings.Configured) { ApplyConfigurationStates(); return false; }
        DateTimeOffset allowed;
        lock (_sync) _nextAllowed.TryGetValue(layer, out allowed);
        var now = _timeProvider.GetUtcNow();
        if (allowed > now) await _delay(allowed - now, cancellationToken).ConfigureAwait(false);
        try
        {
            GodsEyeObserver? observer;
            lock (_sync) observer = _observer;
            if (layer == GodsEyeLayerNames.Traffic && string.IsNullOrWhiteSpace(_settings.GetProviderKeys().TomTomApiKey))
            {
                if (observer is null) { MarkFailure(layer, "Operator QTH is unavailable.", null); return false; }
                lock (_sync) PublishLocked(layer, SimulatedTraffic(observer.Value, settings.MaxCount), settings, _timeProvider.GetUtcNow(), "Simulated traffic; add a TomTom key for live flow.");
                return true;
            }
            var url = BuildUrl(layer, settings, observer, _settings.GetProviderKeys().TomTomApiKey);
            if (url is null) { MarkFailure(layer, "Operator QTH is unavailable.", null); return false; }
            RecordRequest(layer);
            using var response = await _httpClients.CreateClient(HttpClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = RetryDelay(response, layer);
                lock (_sync) _nextAllowed[layer] = _timeProvider.GetUtcNow() + retry;
                MarkRateLimited(layer, $"Feed rate limited; retrying after {Math.Ceiling(retry.TotalSeconds)} seconds.");
                return false;
            }
            response.EnsureSuccessStatusCode();
            var responseLimit = layer == GodsEyeLayerNames.MilitaryFlights
                ? MilitaryFlightsMaxResponseBytes : MaxResponseBytes;
            if (response.Content.Headers.ContentLength > responseLimit) throw new InvalidDataException($"{layer} response exceeds byte cap.");
            await response.Content.LoadIntoBufferAsync(responseLimit, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var items = layer switch
            {
                GodsEyeLayerNames.Earthquakes => GodsEyeParsers.ParseEarthquakes(payload),
                GodsEyeLayerNames.Launches => GodsEyeParsers.ParseLaunches(payload),
                GodsEyeLayerNames.Aircraft => GodsEyeParsers.ParseAircraft(payload),
                GodsEyeLayerNames.Fires => GodsEyeParsers.ParseFires(payload),
                GodsEyeLayerNames.MilitaryFlights => GodsEyeParsers.ParseMilitaryFlights(payload),
                GodsEyeLayerNames.Radio => GodsEyeParsers.ParseRadioStations(payload),
                GodsEyeLayerNames.Bikeshare => GodsEyeParsers.ParseBikeshare(payload),
                GodsEyeLayerNames.Traffic => GodsEyeParsers.ParseTraffic(payload),
                GodsEyeLayerNames.MappedInstallations => GodsEyeParsers.ParseMappedInstallations(payload),
                _ => [],
            };
            var currentSettings = _settings.GetInternal()[layer];
            lock (_sync)
            {
                _nextAllowed.Remove(layer); _backoffAttempts.Remove(layer);
                if (!currentSettings.Enabled || !currentSettings.Configured)
                {
                    ApplyConfigurationStateLocked(layer, currentSettings);
                    return false;
                }
                PublishLocked(layer, items, currentSettings, _timeProvider.GetUtcNow());
                if (layer == GodsEyeLayerNames.Aircraft)
                    _nextAllowed[layer] = _timeProvider.GetUtcNow() + OpenSkyAnonymousMinimumInterval;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            RegisterBackoff(layer);
            var reason = layer == GodsEyeLayerNames.Fires && ex is InvalidDataException
                && ex.Message.Contains("rejected the map key", StringComparison.Ordinal)
                    ? "NASA FIRMS rejected the map key. Add or replace it in Gods Eye settings."
                    : $"{DisplayName(layer)} feed is unavailable.";
            var secret = layer == GodsEyeLayerNames.Fires
                ? settings.ApiKey
                : layer == GodsEyeLayerNames.Traffic ? _settings.GetProviderKeys().TomTomApiKey : string.Empty;
            MarkFailure(layer, reason, secret.Length > 0 ? SanitizeException(ex, secret) : ex);
            return false;
        }
    }

    private void PublishLocked(string layer, IReadOnlyList<GodsEyeItemDto> items, GodsEyeLayerSettings settings, DateTimeOffset fetchedUtc, string? reason = null)
    {
        var filtered = _observer is { } observer
            ? items.Where(item => DistanceKm(observer.LatitudeDeg, observer.LongitudeDeg, item.LatitudeDeg, item.LongitudeDeg) <= settings.RadiusKm).ToArray()
            : items.ToArray();
        var selected = filtered.OrderByDescending(x => x.TimestampUtc).Take(settings.MaxCount).ToArray();
        var cache = _caches[layer];
        cache.Items = selected; cache.TotalCount = filtered.Length; cache.FetchedUtc = fetchedUtc;
        cache.State = GodsEyeFreshness.Live; cache.Reason = reason;
        if (cache.Failed) _log.LogInformation("{Layer} feed recovered with {Count} item(s)", DisplayName(layer), selected.Length);
        cache.Failed = false;
    }

    private void MarkFailure(string layer, string reason, Exception? exception)
    {
        var settings = _settings.GetInternal()[layer];
        lock (_sync)
        {
            if (!settings.Enabled || !settings.Configured)
            {
                ApplyConfigurationStateLocked(layer, settings);
                return;
            }
            var cache = _caches[layer];
            cache.State = cache.FetchedUtc is null ? GodsEyeFreshness.Unavailable : GodsEyeFreshness.Stale;
            cache.Reason = reason;
            if (!cache.Failed) _log.LogWarning(exception, "{Layer}; retaining last-good data", reason);
            cache.Failed = true;
        }
    }

    private void MarkRateLimited(string layer, string reason)
    {
        var settings = _settings.GetInternal()[layer];
        lock (_sync)
        {
            if (!settings.Enabled || !settings.Configured)
            {
                ApplyConfigurationStateLocked(layer, settings);
                return;
            }
            var cache = _caches[layer];
            cache.State = GodsEyeFreshness.RateLimited;
            cache.Reason = reason;
            cache.Failed = true;
        }
    }

    private void RecordRequest(string layer)
    {
        lock (_sync)
        {
            _caches[layer].RequestCount++;
            _caches[layer].LastFetchUtc = _timeProvider.GetUtcNow();
        }
    }

    private void ApplyConfigurationStates()
    {
        var settings = _settings.GetInternal();
        lock (_sync) ApplyConfigurationStatesLocked(settings);
    }

    private void ApplyConfigurationStatesLocked(IReadOnlyDictionary<string, GodsEyeLayerSettings> settings)
    {
        foreach (var layer in GodsEyeLayerNames.FeedLayers)
        {
            var value = settings[layer];
            if (value.Enabled && value.Configured) continue;
            ApplyConfigurationStateLocked(layer, value);
        }
    }

    private void ApplyConfigurationStateLocked(string layer, GodsEyeLayerSettings value)
    {
        var cache = _caches[layer];
        cache.State = !value.Configured ? GodsEyeFreshness.Unconfigured : GodsEyeFreshness.Unavailable;
        cache.Reason = !value.Configured ? "Add a key in Gods Eye settings." : "Layer is disabled.";
        cache.Failed = false;
        cache.Items = [];
        cache.TotalCount = 0;
        cache.FetchedUtc = null;
    }

    private GodsEyeLayerSnapshot CurrentSnapshot(LayerCache cache, GodsEyeLayerSettings settings)
    {
        var state = cache.State;
        if (state == GodsEyeFreshness.Live && cache.FetchedUtc is { } fetched
            && _timeProvider.GetUtcNow() - fetched > EffectiveCadence(settings) * 2) state = GodsEyeFreshness.Stale;
        return new GodsEyeLayerSnapshot(cache.Layer, state, cache.FetchedUtc, cache.Items, cache.TotalCount,
            cache.Items.Count, cache.TotalCount > cache.Items.Count, cache.Reason, cache.RequestCount, cache.LastFetchUtc);
    }

    private TimeSpan RetryDelay(HttpResponseMessage response, string layer)
    {
        if (response.Headers.TryGetValues("X-Rate-Limit-Retry-After-Seconds", out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 24 * 60 * 60));
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        lock (_sync)
        {
            var attempt = _backoffAttempts.TryGetValue(layer, out var current) ? current : 0;
            _backoffAttempts[layer] = attempt + 1;
            return Backoff[Math.Min(attempt, Backoff.Length - 1)];
        }
    }

    private static string? BuildUrl(string layer, GodsEyeLayerSettings settings, GodsEyeObserver? observer, string tomTomApiKey)
    {
        if (layer == GodsEyeLayerNames.Earthquakes) return "https://earthquake.usgs.gov/earthquakes/feed/v1.0/summary/all_day.geojson";
        if (layer == GodsEyeLayerNames.Launches) return "https://ll.thespacedevs.com/2.3.0/launches/upcoming/?format=json&limit=100&mode=normal&ordering=net";
        if (layer == GodsEyeLayerNames.MilitaryFlights) return "https://api.adsb.lol/v2/mil";
        if (layer == GodsEyeLayerNames.Radio) return "https://de1.api.radio-browser.info/json/stations/search?hidebroken=true&has_geo_info=true&limit=750&order=clickcount&reverse=true";
        if (layer == GodsEyeLayerNames.Bikeshare) return "https://api.citybik.es/v2/networks?fields=id,name,location";
        if (observer is null) return null;
        var bounds = layer == GodsEyeLayerNames.Aircraft
            ? OpenSkyBoundsAround(observer.Value, settings.RadiusKm)
            : BoundsAround(observer.Value, settings.RadiusKm);
        if (layer == GodsEyeLayerNames.Aircraft)
            return FormattableString.Invariant($"https://opensky-network.org/api/states/all?lamin={bounds.South:F5}&lomin={bounds.West:F5}&lamax={bounds.North:F5}&lomax={bounds.East:F5}");
        if (layer == GodsEyeLayerNames.Fires)
            return FormattableString.Invariant($"https://firms.modaps.eosdis.nasa.gov/api/area/csv/{Uri.EscapeDataString(settings.ApiKey)}/VIIRS_SNPP_NRT/{bounds.West:F5},{bounds.South:F5},{bounds.East:F5},{bounds.North:F5}/1");
        if (layer == GodsEyeLayerNames.Traffic)
            return FormattableString.Invariant($"https://api.tomtom.com/traffic/services/4/flowSegmentData/relative0/10/json?point={observer.Value.LatitudeDeg:F5},{observer.Value.LongitudeDeg:F5}&unit=KMPH&key={Uri.EscapeDataString(tomTomApiKey)}");
        if (layer == GodsEyeLayerNames.MappedInstallations)
        {
            var query = FormattableString.Invariant($"[out:json][timeout:20];(nwr[\"military\"~\"^(airfield|naval_base|range|barracks|base)$\"]({bounds.South:F5},{bounds.West:F5},{bounds.North:F5},{bounds.East:F5});nwr[\"landuse\"=\"military\"]({bounds.South:F5},{bounds.West:F5},{bounds.North:F5},{bounds.East:F5}););out center 700;");
            return $"https://overpass-api.de/api/interpreter?data={Uri.EscapeDataString(query)}";
        }
        return null;
    }

    private static IReadOnlyList<GodsEyeItemDto> SimulatedTraffic(GodsEyeObserver observer, int maxCount)
    {
        var count = Math.Min(maxCount, 120);
        var now = DateTimeOffset.UnixEpoch;
        return Enumerable.Range(0, count).Select(index =>
        {
            var ring = 0.01 + (index % 12) * 0.004;
            var angle = index * Math.PI * (3 - Math.Sqrt(5));
            var latitude = Math.Clamp(observer.LatitudeDeg + Math.Sin(angle) * ring, -89.99, 89.99);
            var longitude = Math.Clamp(observer.LongitudeDeg + Math.Cos(angle) * ring, -179.99, 179.99);
            return new GodsEyeItemDto($"traffic-sim-{index}", "Simulated traffic", latitude, longitude, now,
                HeadingDeg: angle * 180 / Math.PI % 360, SpeedKnots: 12 + index % 24,
                Status: "Approximate simulation");
        }).ToArray();
    }

    internal static GodsEyeBounds BoundsAround(GodsEyeObserver observer, double radiusKm)
    {
        var latitudeDelta = Math.Min(90, radiusKm / 111.32);
        var longitudeScale = Math.Max(0.01, Math.Cos(observer.LatitudeDeg * Math.PI / 180));
        var longitudeDelta = Math.Min(180, radiusKm / (111.32 * longitudeScale));
        return new GodsEyeBounds(Math.Max(-90, observer.LatitudeDeg - latitudeDelta), Math.Max(-180, observer.LongitudeDeg - longitudeDelta),
            Math.Min(90, observer.LatitudeDeg + latitudeDelta), Math.Min(180, observer.LongitudeDeg + longitudeDelta));
    }

    internal static GodsEyeBounds OpenSkyBoundsAround(GodsEyeObserver observer, double radiusKm)
    {
        var bounds = BoundsAround(observer, radiusKm);
        var latitudeSpan = bounds.North - bounds.South;
        var longitudeSpan = bounds.East - bounds.West;
        var area = latitudeSpan * longitudeSpan;
        if (area <= OpenSkyMaximumBoundsAreaSquareDegrees) return bounds;
        var scale = Math.Sqrt(OpenSkyMaximumBoundsAreaSquareDegrees / area);
        var centerLatitude = (bounds.South + bounds.North) / 2;
        var centerLongitude = (bounds.West + bounds.East) / 2;
        var halfLatitude = latitudeSpan * scale / 2;
        var halfLongitude = longitudeSpan * scale / 2;
        return new GodsEyeBounds(centerLatitude - halfLatitude, centerLongitude - halfLongitude,
            centerLatitude + halfLatitude, centerLongitude + halfLongitude);
    }

    internal static TimeSpan EffectiveCadence(GodsEyeLayerSettings settings) =>
        settings.Layer == GodsEyeLayerNames.Aircraft && TimeSpan.FromSeconds(settings.CadenceSeconds) < OpenSkyAnonymousMinimumInterval
            ? OpenSkyAnonymousMinimumInterval : TimeSpan.FromSeconds(settings.CadenceSeconds);

    private void RegisterBackoff(string layer)
    {
        lock (_sync)
        {
            var attempt = _backoffAttempts.TryGetValue(layer, out var current) ? current : 0;
            _backoffAttempts[layer] = attempt + 1;
            _nextAllowed[layer] = _timeProvider.GetUtcNow() + Backoff[Math.Min(attempt, Backoff.Length - 1)];
        }
    }

    internal static Exception SanitizeException(Exception exception, string secret)
    {
        if (string.IsNullOrEmpty(secret)) return exception;
        var escaped = Uri.EscapeDataString(secret);
        var message = exception.Message
            .Replace(escaped, "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Replace(secret, "[redacted]", StringComparison.Ordinal);
        return exception is HttpRequestException http
            ? new HttpRequestException(message, null, http.StatusCode)
            : new InvalidOperationException(message);
    }

    internal static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0088;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Pow(Math.Sin(dLon / 2), 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string DisplayName(string layer) => char.ToUpperInvariant(layer[0]) + layer[1..];

    private sealed class LayerCache(string layer)
    {
        public string Layer { get; } = layer;
        public string State { get; set; } = GodsEyeFreshness.Unavailable;
        public DateTimeOffset? FetchedUtc { get; set; }
        public IReadOnlyList<GodsEyeItemDto> Items { get; set; } = [];
        public int TotalCount { get; set; }
        public string? Reason { get; set; } = "Awaiting first refresh.";
        public bool Failed { get; set; }
        public long RequestCount { get; set; }
        public DateTimeOffset? LastFetchUtc { get; set; }
    }

    public override void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _configurationChanged.Cancel();
            _configurationChanged.Dispose();
        }
        if (_ownsViewers) _viewers.Dispose();
        _observerResolutionGate.Dispose();
        base.Dispose();
    }
}
