// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using LiteDB;
using Microsoft.AspNetCore.Mvc;

#if ZEUS_PRODUCT_HOST
using Zeus.Product.Hosting.GodsEye;
using SharedDatabase = Zeus.Product.Hosting.Data.SharedLiteDatabase;
namespace Zeus.Product.Hosting;
#else
using Zeus.Server.GodsEye;
using SharedDatabase = Zeus.Data.SharedLiteDatabase;
namespace Zeus.Server;
#endif

/// <summary>Carries the host's station-grid accessor to the settings endpoint.</summary>
public sealed class GodsEyeFallbackGrid(Func<string?> get)
{
    public string? Get() => get();
}

/// <summary>Registers and maps the radio-independent Gods Eye feature.</summary>
public static class GodsEyeHosting
{
    /// <param name="fallbackGrid">
    /// Supplies the operator's station grid so the distance-bounded layers have a
    /// point to measure from when no Gods Eye point of interest has been chosen.
    /// Each host passes its own accessor because the identity store lives in a
    /// different place in the engine and the product.
    /// </param>
    public static IServiceCollection AddGodsEyeServices(
        this IServiceCollection services,
        string preferencesDatabasePath,
        Func<IServiceProvider, bool>? featureEnabled = null,
        Func<IServiceProvider, string?>? fallbackGrid = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferencesDatabasePath);

        services.AddHttpClient(SatelliteTrackingService.HttpClientName, client =>
        {
            client.Timeout = SatelliteTrackingService.RequestTimeout;
            client.MaxResponseContentBufferSize = SatelliteTrackingService.MaxResponseBytes;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZeusSDR/1.0 (+https://github.com/Zeus-SDR/zeussdr)");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/plain");
        });
        services.AddHttpClient(GodsEyeFeedsService.HttpClientName, client =>
        {
            client.Timeout = GodsEyeFeedsService.RequestTimeout;
            client.MaxResponseContentBufferSize = GodsEyeFeedsService.MaxResponseBytes;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZeusSDR/1.0 (+https://github.com/Zeus-SDR/zeussdr)");
        });
        services.AddHttpClient(CameraFeedService.HttpClientName, client =>
        {
            client.Timeout = CameraFeedService.RequestTimeout;
            client.MaxResponseContentBufferSize = CameraFeedService.MaxResponseBytes;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZeusSDR/1.0 (+https://github.com/Zeus-SDR/zeussdr)");
        });

        services.AddSingleton(_ => new GodsEyeDatabaseLease(preferencesDatabasePath));
        services.AddSingleton(provider => new GodsEyeSettingsStore(
            provider.GetRequiredService<ILogger<GodsEyeSettingsStore>>(),
            provider.GetRequiredService<GodsEyeDatabaseLease>().Database));
        services.AddSingleton(provider => new SatelliteSettingsStore(
            provider.GetRequiredService<ILogger<SatelliteSettingsStore>>(),
            provider.GetRequiredService<GodsEyeDatabaseLease>().Database));
        services.AddSingleton<GodsEyeViewerRegistry>();
        services.AddSingleton(provider => new GodsEyeFeatureGate(
            () => featureEnabled?.Invoke(provider) ?? false));
        services.AddSingleton<IAisStreamClient, AisStreamClient>();
        services.AddSingleton(provider => new SatelliteTrackingService(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<SatelliteSettingsStore>(),
            provider.GetRequiredService<ILogger<SatelliteTrackingService>>(),
            viewers: provider.GetRequiredService<GodsEyeViewerRegistry>()));
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<GodsEyeSettingsStore>();
            return new GodsEyeFeedsService(
                provider.GetRequiredService<IHttpClientFactory>(),
                settings,
                provider.GetRequiredService<IAisStreamClient>(),
                provider.GetRequiredService<GodsEyeViewerRegistry>(),
                _ => Task.FromResult(settings.GetObserver(fallbackGrid?.Invoke(provider))),
                provider.GetRequiredService<ILogger<GodsEyeFeedsService>>());
        });
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<GodsEyeSettingsStore>();
            var cameras = new CameraFeedService(
                provider.GetRequiredService<IHttpClientFactory>(),
                settings,
                provider.GetRequiredService<GodsEyeViewerRegistry>(),
                provider.GetRequiredService<ILogger<CameraFeedService>>());
            cameras.SetObserverResolver(() => settings.GetObserver(fallbackGrid?.Invoke(provider)));
            return cameras;
        });
        services.AddSingleton(provider => new GodsEyeFallbackGrid(() => fallbackGrid?.Invoke(provider)));
        services.AddHostedService(provider => provider.GetRequiredService<SatelliteTrackingService>());
        services.AddHostedService(provider => provider.GetRequiredService<GodsEyeFeedsService>());
        services.AddHostedService(provider => provider.GetRequiredService<CameraFeedService>());
        return services;
    }

    public static IEndpointRouteBuilder MapGodsEyeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/godseye/satellites",
            (SatelliteTrackingService satellites, GodsEyeSettingsStore settings) =>
                Results.Ok(satellites.GetPositions(
                    DateTimeOffset.UtcNow,
                    SatelliteTrackingService.ResolveObserver(settings.GetObserver()))));
        endpoints.MapGet("/api/godseye/satellites/passes",
            async (SatelliteTrackingService satellites, GodsEyeSettingsStore settings) =>
                Results.Ok(await satellites.GetPassesAsync(
                    DateTimeOffset.UtcNow,
                    SatelliteTrackingService.ResolveObserver(settings.GetObserver())).ConfigureAwait(false)));
        endpoints.MapGet("/api/godseye/satellites/settings",
            (SatelliteSettingsStore store) => Results.Ok(store.Get()));
        endpoints.MapGet("/api/godseye/satellites/track/{noradId:int}",
            (int noradId, SatelliteTrackingService satellites) =>
                Results.Ok(satellites.GetTrack(noradId, DateTimeOffset.UtcNow)));
        endpoints.MapPut("/api/godseye/satellites/settings",
            (SatelliteSettings settings, SatelliteSettingsStore store, SatelliteTrackingService satellites) =>
            {
                var saved = store.Set(settings);
                satellites.SettingsChanged();
                return Results.Ok(saved);
            });

        endpoints.MapGet("/api/godseye/layers",
            (GodsEyeFeedsService feeds) => Results.Ok(feeds.GetSnapshot()));
        endpoints.MapGet("/api/godseye/layers/{layer}",
            (string layer, GodsEyeFeedsService feeds) =>
            {
                var snapshot = feeds.GetLayer(layer);
                return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
            });
        endpoints.MapGet("/api/godseye/cameras",
            (double? lat, double? lon, CameraFeedService cameras, GodsEyeSettingsStore settings,
                [FromServices] GodsEyeFallbackGrid fallback) =>
                Results.Ok(CameraSnapshot(lat, lon, cameras, settings, fallback)));
        endpoints.MapGet("/api/godseye/settings",
            (GodsEyeSettingsStore store, [FromServices] GodsEyeFallbackGrid fallback) =>
                Results.Ok(SettingsSnapshot(store, fallback)));
        endpoints.MapGodsEyeProviderCredentialsEndpoint();
        endpoints.MapPut("/api/godseye/settings",
            (GodsEyeSettingsRequest request, GodsEyeSettingsStore store,
                GodsEyeFeedsService feeds, CameraFeedService cameras,
                [FromServices] GodsEyeFallbackGrid fallback) =>
            {
                try
                {
                    store.Set(request);
                    feeds.SettingsChanged();
                    cameras.SettingsChanged();
                    return Results.Ok(store.GetPublic(fallback.Get()));
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
            });
        endpoints.MapPost("/api/godseye/viewers/{viewerId}",
            (string viewerId, GodsEyeViewerRegistry viewers) =>
            {
                viewers.Open(viewerId);
                return Results.NoContent();
            });
        endpoints.MapPut("/api/godseye/viewers/{viewerId}",
            (string viewerId, GodsEyeViewerRegistry viewers) =>
            {
                viewers.Heartbeat(viewerId);
                return Results.NoContent();
            });
        endpoints.MapDelete("/api/godseye/viewers/{viewerId}",
            (string viewerId, GodsEyeViewerRegistry viewers) =>
            {
                viewers.Close(viewerId);
                return Results.NoContent();
            });
        return endpoints;
    }

    internal static CameraFeedSnapshot CameraSnapshot(double? lat, double? lon, CameraFeedService cameras,
        GodsEyeSettingsStore settings, GodsEyeFallbackGrid fallback)
    {
        var observer = lat is >= -90 and <= 90 && lon is >= -180 and <= 180
            ? new GodsEyeObserver(lat.Value, lon.Value)
            : settings.GetObserver(fallback.Get());
        return cameras.GetSnapshot(observer);
    }

    internal static GodsEyeSettingsResponse SettingsSnapshot(GodsEyeSettingsStore store, GodsEyeFallbackGrid fallback) =>
        store.GetPublic(fallback.Get());

    public static IEndpointRouteBuilder MapGodsEyeProviderCredentialsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/godseye/provider-credentials", ProviderCredentials);
        return endpoints;
    }

    internal static GodsEyeProviderCredentialsResponse ProviderCredentials(
        HttpContext context, GodsEyeSettingsStore store, GodsEyeFeatureGate feature)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!feature.Enabled) return new GodsEyeProviderCredentialsResponse("", "");
        var keys = store.GetProviderKeys();
        return new GodsEyeProviderCredentialsResponse(keys.GoogleMapsApiKey, keys.CesiumIonToken);
    }
}

public sealed class GodsEyeFeatureGate(Func<bool> enabled)
{
    public bool Enabled => enabled();
}

internal sealed class GodsEyeDatabaseLease : IDisposable
{
    private readonly SharedDatabase.Lease _lease;

    public GodsEyeDatabaseLease(string path)
    {
        _lease = SharedDatabase.Acquire(path);
    }

    public LiteDatabase Database => _lease.Database;

    public void Dispose() => _lease.Dispose();
}
