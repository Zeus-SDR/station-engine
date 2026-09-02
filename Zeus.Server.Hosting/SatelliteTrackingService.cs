// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// CelesTrak GP feeds: https://celestrak.org/NORAD/elements/gp.php?GROUP=<group>&FORMAT=tle
// See ATTRIBUTIONS.md at the repository root for provenance.

using Microsoft.Extensions.Hosting;
#if ZEUS_PRODUCT_HOST
using Zeus.Product.Hosting.GodsEye;
using Zeus.Product.Hosting.Satellites;
#else
using Zeus.Server.GodsEye;
using Zeus.Server.Satellites;
#endif

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting;
#else
namespace Zeus.Server;
#endif

public sealed record SatellitePositionDto(int NoradId, string Name, double LatitudeDeg, double LongitudeDeg, double AltitudeKm, double VelocityKmS, double FootprintRadiusKm, bool AboveHorizon, DateTimeOffset TimestampUtc);
public sealed record SatellitePositionResponse(IReadOnlyList<SatellitePositionDto> Satellites, string? Reason = null, int SkippedTleSets = 0);
public sealed record SatellitePassDto(int NoradId, string Name, DateTimeOffset? AosUtc, DateTimeOffset MaxElevationUtc, DateTimeOffset LosUtc, double MaxElevationDeg, double? AosAzimuthDeg, double LosAzimuthDeg, double? DurationSeconds, bool InProgress);
public sealed record SatellitePassResponse(IReadOnlyList<SatellitePassDto> Passes, string? Reason = null, int SkippedTleSets = 0);
public sealed record SatelliteTrackPointDto(double LatitudeDeg, double LongitudeDeg, double AltitudeKm, DateTimeOffset TimestampUtc, bool Ahead);
public sealed record SatelliteTrackResponse(int NoradId, string Name, IReadOnlyList<SatelliteTrackPointDto> Points, string? Reason = null);

public sealed class SatelliteTrackingService : BackgroundService
{
    public const string HttpClientName = "CelesTrakTle";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    public const long MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _httpClients;
    private readonly SatelliteSettingsStore _store;
    private readonly ILogger<SatelliteTrackingService> _log;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly GodsEyeViewerRegistry? _viewers;
    private readonly object _sync = new();
    private readonly object _refreshSync = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource _enabledCancellation = new();
    private IReadOnlyList<TwoLineElement> _elements = Array.Empty<TwoLineElement>();
    private long _elementsGeneration;
    private int _skippedTleSets;
    private long _positionTick = long.MinValue;
    private GeodeticPoint? _positionObserver;
    private SatellitePositionResponse? _positionCache;
    private (long Generation, GeodeticPoint Observer, double MinimumElevationDeg, int HorizonHours)? _passKey;
    private Task<SatellitePassResponse>? _passTask;
    private SatellitePassResponse? _passCache;
    private readonly Dictionary<(int NoradId, long Tick, long Generation), SatelliteTrackResponse> _trackCache = new();
    private readonly Dictionary<(int NoradId, long Generation), SatelliteTrackResponse> _trackFailureCache = new();
    private bool _feedFailed;
    private int _passComputationCount;
    private readonly TaskCompletionSource _noViewerWaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<IReadOnlyList<TwoLineElement>, GeodeticPoint, DateTimeOffset, SatelliteSettings, int, SatellitePassResponse>? PassComputationForTesting { get; set; }

    public SatelliteTrackingService(
        IHttpClientFactory httpClients,
        SatelliteSettingsStore store,
        ILogger<SatelliteTrackingService> log,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        GodsEyeViewerRegistry? viewers = null)
    {
        _httpClients = httpClients;
        _store = store;
        _log = log;
        _retryDelays = retryDelays ?? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];
        _viewers = viewers;
        LoadPersisted();
    }

    public IReadOnlyList<TwoLineElement> Elements { get { lock (_sync) return _elements; } }
    internal int PassComputationCount => Volatile.Read(ref _passComputationCount);
    internal Task NoViewerWaitEnteredForTesting => _noViewerWaitEntered.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_viewers is not { HasViewers: false })
        {
            try { await WaitForWakeAsync(TimeSpan.FromSeconds(Random.Shared.Next(5, 121)), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_viewers is { HasViewers: false })
            {
                _noViewerWaitEntered.TrySetResult();
                var changed = _viewers.ChangedToken;
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, changed);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, waiting.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
                continue;
            }
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
            var viewerChanged = _viewers?.ChangedToken;
            using var linked = viewerChanged is null
                ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken)
                : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, viewerChanged.Value);
            try { await WaitForWakeAsync(RefreshInterval, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { continue; }
        }
    }

    public void SettingsChanged()
    {
        var enabled = _store.Get().Enabled;
        lock (_refreshSync)
        {
            _enabledCancellation.Cancel();
            _enabledCancellation.Dispose();
            _enabledCancellation = new CancellationTokenSource();
            if (!enabled) _enabledCancellation.Cancel();
        }
        lock (_sync) InvalidateCachesLocked();
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var settings = _store.Get();
        if (!settings.Enabled) return false;
        CancellationToken enabledToken;
        lock (_refreshSync) enabledToken = _enabledCancellation.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, enabledToken);
        try
        {
            var urls = settings.CustomTleUrl.Length > 0
                ? [settings.CustomTleUrl]
                : settings.CatalogGroups!.Select(group => $"https://celestrak.org/NORAD/elements/gp.php?GROUP={Uri.EscapeDataString(group)}&FORMAT=tle").ToArray();
            var rawSets = new List<string>();
            foreach (var url in urls)
                rawSets.Add(await FetchWithRetryAsync(url, linked.Token).ConfigureAwait(false));
            var parsedSets = rawSets.Select(ParseSet).ToArray();
            var skipped = parsedSets.Sum(x => x.Skipped);
            var parsed = parsedSets.SelectMany(x => x.Elements).GroupBy(x => x.CatalogId).Select(x => x.OrderByDescending(t => t.EpochUtc).First()).OrderBy(x => x.CatalogId).ToArray();
            if (parsed.Length == 0) throw new InvalidDataException("CelesTrak returned no valid TLEs.");
            _store.SaveLastGoodTles(rawSets);
            lock (_sync)
            {
                _elements = parsed;
                _skippedTleSets = skipped;
                _elementsGeneration++;
                InvalidateCachesLocked();
            }
            if (skipped > 0) _log.LogWarning("Skipped {Count} malformed TLE sets during refresh", skipped);
            if (_feedFailed) _log.LogInformation("CelesTrak TLE feed recovered with {Count} objects", parsed.Length);
            _feedFailed = false;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (enabledToken.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (!_feedFailed) _log.LogWarning(ex, "CelesTrak TLE refresh failed; retaining last-good satellite data");
            _feedFailed = true;
            return false;
        }
    }

    public SatellitePositionResponse GetPositions(DateTimeOffset utc, GeodeticPoint? observer)
    {
        if (!_store.Get().Enabled)
            return new SatellitePositionResponse(Array.Empty<SatellitePositionDto>(), "Satellite tracking is disabled.");

        var tick = utc.ToUnixTimeSeconds() / 5;
        IReadOnlyList<TwoLineElement> elements;
        long generation;
        int skipped;
        lock (_sync)
        {
            if (tick == _positionTick && Nullable.Equals(observer, _positionObserver) && _positionCache is not null)
                return _positionCache;
            elements = _elements;
            generation = _elementsGeneration;
            skipped = _skippedTleSets;
        }
        if (elements.Count == 0)
            return new SatellitePositionResponse(Array.Empty<SatellitePositionDto>(), "Satellite elements are loading.", skipped);

        var result = new List<SatellitePositionDto>(elements.Count);
        foreach (var tle in elements)
        {
            try
            {
                var state = new Sgp4Propagator(tle).Propagate(utc);
                var ecef = CoordinateTransforms.TemeToEcef(state, utc);
                var geo = CoordinateTransforms.EcefToGeodetic(ecef.X, ecef.Y, ecef.Z);
                if (!double.IsFinite(geo.LatitudeDeg) || !double.IsFinite(geo.LongitudeDeg) || !double.IsFinite(geo.AltitudeKm)) continue;
                var above = observer is not null && CoordinateTransforms.LookAngle(observer.Value, ecef).ElevationDeg >= 0;
                result.Add(new SatellitePositionDto(tle.CatalogId, tle.Name, geo.LatitudeDeg, geo.LongitudeDeg, geo.AltitudeKm, state.SpeedKmS, CoordinateTransforms.FootprintRadiusKm(geo.AltitudeKm), above, utc.ToUniversalTime()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException or OverflowException)
            {
                _log.LogDebug(ex, "Skipping unpropagatable TLE {NoradId}", tle.CatalogId);
            }
        }
        var response = new SatellitePositionResponse(result, SkippedTleReason(skipped), skipped);
        lock (_sync)
        {
            if (generation == _elementsGeneration)
            {
                _positionTick = tick;
                _positionObserver = observer;
                _positionCache = response;
            }
        }
        return response;
    }

    public Task<SatellitePassResponse> GetPassesAsync(DateTimeOffset utc, GeodeticPoint? observer)
    {
        var settings = _store.Get();
        if (!settings.Enabled)
            return Task.FromResult(new SatellitePassResponse(Array.Empty<SatellitePassDto>(), "Satellite tracking is disabled."));
        if (observer is null)
            return Task.FromResult(new SatellitePassResponse(Array.Empty<SatellitePassDto>(), "Station QTH is not configured."));

        IReadOnlyList<TwoLineElement> elements;
        long generation;
        int skipped;
        lock (_sync)
        {
            elements = _elements;
            generation = _elementsGeneration;
            skipped = _skippedTleSets;
            var key = (generation, observer.Value, settings.MinimumPassElevationDeg, settings.PassHorizonHours);
            if (_passKey == key)
            {
                if (_passCache is not null) return Task.FromResult(CurrentPasses(_passCache, utc));
                if (_passTask is not null) return AwaitCurrentPassesAsync(_passTask, utc);
            }
            if (elements.Count == 0)
                return Task.FromResult(new SatellitePassResponse(Array.Empty<SatellitePassDto>(), "Satellite elements are loading.", skipped));
            _passKey = key;
            _passCache = null;
            _passTask = Task.Run(() => (PassComputationForTesting ?? ComputePasses)(elements, observer.Value, utc, settings, skipped));
            return PublishPassesAsync(_passTask, key, utc);
        }
    }

    public SatelliteTrackResponse GetTrack(int noradId, DateTimeOffset utc)
    {
        if (!_store.Get().Enabled)
            return new SatelliteTrackResponse(noradId, "", Array.Empty<SatelliteTrackPointDto>(), "Satellite tracking is disabled.");
        var tick = utc.ToUnixTimeSeconds() / 300;
        TwoLineElement? tle;
        long generation;
        lock (_sync)
        {
            generation = _elementsGeneration;
            if (_trackFailureCache.TryGetValue((noradId, generation), out var failed)) return failed;
            if (_trackCache.TryGetValue((noradId, tick, generation), out var cached)) return cached;
            tle = _elements.FirstOrDefault(x => x.CatalogId == noradId);
        }
        if (tle is null) return new SatelliteTrackResponse(noradId, "", Array.Empty<SatelliteTrackPointDto>(), "Satellite is not tracked.");
        SatelliteTrackResponse response;
        if (!double.IsFinite(tle.MeanMotionRevolutionsPerDay) || tle.MeanMotionRevolutionsPerDay <= 0)
            response = new SatelliteTrackResponse(tle.CatalogId, tle.Name, Array.Empty<SatelliteTrackPointDto>(), "Satellite mean motion is invalid.");
        else
        {
            try
            {
                var period = TimeSpan.FromMinutes(1440d / tle.MeanMotionRevolutionsPerDay);
                var start = utc - TimeSpan.FromTicks(period.Ticks / 2);
                var points = new List<SatelliteTrackPointDto>(121);
                var propagator = new Sgp4Propagator(tle);
                for (var i = 0; i <= 120; i++)
                {
                    var time = start + TimeSpan.FromTicks(period.Ticks * i / 120);
                    var state = propagator.Propagate(time);
                    var ecef = CoordinateTransforms.TemeToEcef(state, time);
                    var geo = CoordinateTransforms.EcefToGeodetic(ecef.X, ecef.Y, ecef.Z);
                    points.Add(new SatelliteTrackPointDto(geo.LatitudeDeg, geo.LongitudeDeg, geo.AltitudeKm, time, time >= utc));
                }
                response = new SatelliteTrackResponse(tle.CatalogId, tle.Name, points);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException or OverflowException)
            {
                response = new SatelliteTrackResponse(tle.CatalogId, tle.Name, Array.Empty<SatelliteTrackPointDto>(), "Satellite propagation failed.");
            }
        }
        lock (_sync)
        {
            if (generation != _elementsGeneration) return response;
            if (response.Reason is null) _trackCache[(noradId, tick, generation)] = response;
            else _trackFailureCache[(noradId, generation)] = response;
            foreach (var key in _trackCache.Keys.Where(key => key.Tick < tick - 1).ToArray()) _trackCache.Remove(key);
        }
        return response;
    }

    public static GeodeticPoint? ResolveObserver(GodsEyeObserver? observer) =>
        observer is { } point
            ? new GeodeticPoint(point.LatitudeDeg, point.LongitudeDeg, 0)
            : null;

    internal static bool TryMaidenhead(string? grid, out GeodeticPoint point)
    {
        point = default; var g = (grid ?? "").Trim().ToUpperInvariant();
        if (g.Length is not (4 or 6) || g[0] is < 'A' or > 'R' || g[1] is < 'A' or > 'R' || !char.IsAsciiDigit(g[2]) || !char.IsAsciiDigit(g[3])) return false;
        var lon = -180d + (g[0] - 'A') * 20d + (g[2] - '0') * 2d;
        var lat = -90d + (g[1] - 'A') * 10d + (g[3] - '0');
        var lonWidth = 2d; var latHeight = 1d;
        if (g.Length == 6)
        {
            if (g[4] is < 'A' or > 'X' || g[5] is < 'A' or > 'X') return false;
            lonWidth = 2d / 24; latHeight = 1d / 24; lon += (g[4] - 'A') * lonWidth; lat += (g[5] - 'A') * latHeight;
        }
        point = new GeodeticPoint(lat + latHeight / 2, lon + lonWidth / 2, 0); return true;
    }

    private async Task<string> FetchWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_store.Get().Enabled) throw new OperationCanceledException(cancellationToken);
            try
            {
                using var response = await _httpClients.CreateClient(HttpClientName)
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                    throw new InvalidDataException("CelesTrak response exceeds byte cap.");
                await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < 2)
            {
                last = ex;
                await Task.Delay(_retryDelays[Math.Min(attempt, _retryDelays.Count - 1)], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new HttpRequestException("Satellite TLE fetch failed.");
    }

    private static (IReadOnlyList<TwoLineElement> Elements, int Skipped) ParseSet(string raw)
    {
        var elements = TwoLineElement.ParseMany(raw, out var skipped);
        return (elements, skipped);
    }

    private SatellitePassResponse ComputePasses(
        IReadOnlyList<TwoLineElement> elements,
        GeodeticPoint observer,
        DateTimeOffset utc,
        SatelliteSettings settings,
        int skipped)
    {
        Interlocked.Increment(ref _passComputationCount);
        var passes = new List<SatellitePassDto>();
        foreach (var tle in elements)
        {
            try
            {
                foreach (var pass in PassPredictor.Predict(
                    new Sgp4Propagator(tle),
                    observer,
                    utc,
                    settings.MinimumPassElevationDeg,
                    TimeSpan.FromHours(settings.PassHorizonHours)))
                {
                    passes.Add(new SatellitePassDto(
                        tle.CatalogId,
                        tle.Name,
                        pass.AosUtc,
                        pass.MaxElevationUtc,
                        pass.LosUtc,
                        pass.MaxElevationDeg,
                        pass.AosAzimuthDeg,
                        pass.LosAzimuthDeg,
                        pass.DurationSeconds,
                        pass.InProgressAtStart || pass.AosUtc is null || pass.AosUtc <= utc && utc < pass.LosUtc));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException or OverflowException) { }
        }
        return new SatellitePassResponse(
            passes.OrderBy(x => x.AosUtc ?? DateTimeOffset.MinValue).ThenBy(x => x.MaxElevationUtc).ToArray(),
            SkippedTleReason(skipped),
            skipped);
    }

    private async Task<SatellitePassResponse> PublishPassesAsync(
        Task<SatellitePassResponse> task,
        (long Generation, GeodeticPoint Observer, double MinimumElevationDeg, int HorizonHours) key,
        DateTimeOffset utc)
    {
        SatellitePassResponse response;
        try { response = await task.ConfigureAwait(false); }
        catch
        {
            lock (_sync)
            {
                if (_passKey == key && ReferenceEquals(_passTask, task))
                {
                    _passTask = null;
                    _passKey = null;
                }
            }
            throw;
        }
        lock (_sync)
        {
            if (_elementsGeneration == key.Generation && _passKey == key && ReferenceEquals(_passTask, task))
            {
                _passCache = response;
                _passTask = null;
            }
        }
        return CurrentPasses(response, utc);
    }

    private static async Task<SatellitePassResponse> AwaitCurrentPassesAsync(Task<SatellitePassResponse> task, DateTimeOffset utc) =>
        CurrentPasses(await task.ConfigureAwait(false), utc);

    private static SatellitePassResponse CurrentPasses(SatellitePassResponse response, DateTimeOffset utc)
    {
        var passes = response.Passes
            .Where(x => x.LosUtc > utc)
            .Select(x => x with { InProgress = x.AosUtc is null || x.AosUtc <= utc && utc < x.LosUtc })
            .ToArray();
        return response with { Passes = passes };
    }

    private static string? SkippedTleReason(int skipped) =>
        skipped > 0 ? $"Skipped {skipped} malformed TLE set(s)." : null;

    private async Task WaitForWakeAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(delay);
        try { await _wake.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }

    private void InvalidateCachesLocked()
    {
        _positionTick = long.MinValue;
        _positionObserver = null;
        _positionCache = null;
        _passKey = null;
        _passTask = null;
        _passCache = null;
        _trackCache.Clear();
        _trackFailureCache.Clear();
    }

    private void LoadPersisted()
    {
        try
        {
            var parsedSets = _store.LoadLastGoodTles().Select(ParseSet).ToArray();
            var skipped = parsedSets.Sum(x => x.Skipped);
            var parsed = parsedSets.SelectMany(x => x.Elements).GroupBy(x => x.CatalogId).Select(x => x.OrderByDescending(t => t.EpochUtc).First()).OrderBy(x => x.CatalogId).ToArray();
            lock (_sync)
            {
                _elements = parsed;
                _skippedTleSets = skipped;
                _elementsGeneration++;
                InvalidateCachesLocked();
            }
            if (parsed.Length > 0) _log.LogInformation("Loaded {Count} satellites from persisted last-good TLE cache", parsed.Length);
            if (skipped > 0) _log.LogWarning("Skipped {Count} malformed TLE sets in persisted cache", skipped);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Persisted satellite TLE cache is invalid; awaiting refresh"); }
    }
}
