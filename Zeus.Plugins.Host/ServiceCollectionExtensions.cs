// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Host.Registry;

namespace Zeus.Plugins.Host;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the plugin system. The settings store uses the path
    /// returned by <paramref name="prefsDbPathProvider"/> — typically a
    /// thin wrapper around Zeus.Server.PrefsDbPath.Get(). Callers MUST
    /// also call <see cref="PluginEndpoints.MapAll"/> on their endpoint
    /// route builder.
    /// </summary>
    public static IServiceCollection AddZeusPlugins(
        this IServiceCollection services,
        Func<string> prefsDbPathProvider,
        PluginManagerOptions? options = null,
        RegistryClientOptions? registryOptions = null)
    {
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<PluginOperationGate>();
        services.TryAddSingleton<IPluginInstallAccessGate, AllowAllPluginInstallAccessGate>();
        services.AddSingleton(sp => new PluginSettingsStore(
            prefsDbPathProvider(),
            sp.GetService<ILogger<PluginSettingsStore>>()));
        services.AddSingleton(sp => new PluginManager(
            loader: sp.GetRequiredService<PluginLoader>(),
            settings: sp.GetRequiredService<PluginSettingsStore>(),
            services: sp,
            logFactory: sp.GetRequiredService<ILoggerFactory>(),
            options: options));
        services.AddHostedService(sp => sp.GetRequiredService<PluginManager>());

        // Read-only radio state for plugins holding ReadRadioState. Registered
        // here (rather than in a specific host) so IPluginContext.Radio is wired
        // in EVERY host that installs the plugin system — the standalone station
        // engine as well as the retired monolithic host. Without this the engine
        // granted the capability but PluginManager resolved a null reader.
        // TryAdd lets a host substitute a richer reader if it ever needs one.
        services.TryAddSingleton<Zeus.Plugins.Contracts.IRadioStateReader, RadioStateReader>();

        // Registry + installer — uses the typed HttpClient pattern so
        // operators can replace the default user-agent / timeouts via
        // IHttpClientFactory configuration if needed.
        services.AddHttpClient<HttpRegistryClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ZeusSDR/1.0 (plugins-registry-client)");
        });
        services.AddSingleton<IRegistryClient>(sp => sp.GetRequiredService<HttpRegistryClient>());
        if (registryOptions is not null)
            services.AddSingleton(registryOptions);

        services.AddHttpClient<PluginPackageDownloader>(c =>
        {
            c.Timeout = TimeSpan.FromMinutes(2);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ZeusSDR/1.0 (plugins-installer)");
        });
        services.AddSingleton<IPluginPackageDownloader>(sp => sp.GetRequiredService<PluginPackageDownloader>());

        services.AddSingleton(sp => new PluginInstaller(
            downloader: sp.GetRequiredService<IPluginPackageDownloader>(),
            registry: sp.GetRequiredService<IRegistryClient>(),
            manager: sp.GetRequiredService<PluginManager>(),
            pluginRoot: options?.PluginRoot ?? PluginRoot.Get(),
            log: sp.GetService<ILogger<PluginInstaller>>(),
            operations: sp.GetRequiredService<PluginOperationGate>()));
        services.AddSingleton(sp => new PluginIdMigrator(
            registry: sp.GetRequiredService<IRegistryClient>(),
            downloader: sp.GetRequiredService<IPluginPackageDownloader>(),
            settings: sp.GetRequiredService<PluginSettingsStore>(),
            pluginRoot: options?.PluginRoot ?? PluginRoot.Get(),
            log: sp.GetRequiredService<ILogger<PluginIdMigrator>>()));

        // VST directory scanner — registers each .vst3 in an operator-
        // chosen folder as a generated plugin package (stub assembly +
        // synthesized manifest), so VSTs flow into the Audio Suite chain.
        services.AddSingleton(sp => new VstDirectoryScanService(
            manager: sp.GetRequiredService<PluginManager>(),
            pluginRoot: options?.PluginRoot ?? PluginRoot.Get(),
            log: sp.GetRequiredService<ILogger<VstDirectoryScanService>>(),
            // Optional: when the out-of-process engine is registered (it is in the
            // Zeus host), the scanner enumerates through it so shell VST3s like
            // Waves WaveShell expand into their hosted sub-plugins.
            engine: sp.GetService<Audio.VstEngineController>()));

        // AU component scanner — the macOS-only sibling of the VST3 scanner.
        // Enumerates installed AUv2 'aufx' effects from the OS AudioComponent
        // registry (via the native zeus-au-bridge) and registers each as a
        // generated plugin package, so Audio Units flow into the TX/RX Audio
        // Suite chains in-process. The service is a no-op off macOS (ScanAsync
        // returns an empty result), so registering it on every platform is safe.
        services.AddSingleton(sp => new AuComponentScanService(
            manager: sp.GetRequiredService<PluginManager>(),
            pluginRoot: options?.PluginRoot ?? PluginRoot.Get(),
            log: sp.GetRequiredService<ILogger<AuComponentScanService>>()));

        return services;
    }
}
