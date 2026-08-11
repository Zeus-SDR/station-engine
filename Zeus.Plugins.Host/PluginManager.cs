// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Registry;

namespace Zeus.Plugins.Host;

/// <summary>
/// Top-level orchestrator. Scans the plugin root on startup, loads each
/// plugin under try/catch + per-call timeout, exposes a snapshot of
/// activated plugins for the REST API + frontend.
/// </summary>
public sealed class PluginManager : IHostedService, IAsyncDisposable
{
    private readonly PluginLoader _loader;
    private readonly PluginSettingsStore _settings;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _logFactory;
    private readonly ILogger<PluginManager> _log;
    private readonly PluginManagerOptions _options;

    private readonly ConcurrentDictionary<string, ActivatedPlugin> _active = new();
    private readonly ConcurrentDictionary<string, PluginCatalogEntry> _catalog = new();
    private readonly SemaphoreSlim _lazyActivationGate = new(1, 1);
    private int _started; // 0 = pending, 1 = StartAsync ran

    public PluginManager(
        PluginLoader loader,
        PluginSettingsStore settings,
        IServiceProvider services,
        ILoggerFactory logFactory,
        PluginManagerOptions? options = null)
    {
        _loader = loader;
        _settings = settings;
        _services = services;
        _logFactory = logFactory;
        _log = logFactory.CreateLogger<PluginManager>();
        _options = options ?? new PluginManagerOptions();
    }

    /// <summary>Snapshot of currently-active plugins. Order is undefined.</summary>
    public IReadOnlyCollection<ActivatedPlugin> Active => _active.Values.ToArray();

    /// <summary>Snapshot of installed plugins, including scanned plugins whose
    /// assemblies have not been activated.</summary>
    public IReadOnlyCollection<PluginCatalogEntry> Catalog => _catalog.Values.ToArray();

    /// <summary>Try to find an active plugin by id.</summary>
    public ActivatedPlugin? Find(string id) => _active.TryGetValue(id, out var p) ? p : null;

    public PluginCatalogEntry? FindInstalled(string id) =>
        _catalog.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>Raised AFTER a plugin's IZeusPlugin.InitializeAsync returns
    /// cleanly and the plugin is registered in <see cref="Active"/>.
    /// Subscribers run synchronously on whichever thread called
    /// <see cref="ActivateAsync"/>; throws from subscribers are logged and
    /// swallowed so a buggy subscriber can't break activation.</summary>
    public event Action<ActivatedPlugin>? PluginActivated;

    /// <summary>Raised BEFORE the plugin's ShutdownAsync is called. The
    /// plugin is already removed from <see cref="Active"/>; subscribers
    /// can free per-plugin host-side resources (audio chain slots, HTTP
    /// route entries, etc.).</summary>
    public event Action<ActivatedPlugin>? PluginDeactivated;

    public async Task StartAsync(CancellationToken ct)
    {
        // StartAsync may be invoked manually before app.Run() so that
        // PluginEndpoints.MapAll sees an already-populated Active set;
        // the hosted-service path then re-invokes it. Guard against the
        // second call, otherwise activated plugins would be torn down
        // and replaced — invalidating any backend-route closures that
        // captured the first instance.
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        if (_options.SafeMode)
        {
            _log.LogWarning("Plugin safe mode enabled — skipping plugin discovery.");
            return;
        }

        var root = _options.PluginRoot ?? PluginRoot.EnsureExists();
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        _log.LogInformation("Plugin root: {Root}", root);

        // Complete deferred uninstalls FIRST. On Windows the ALC keeps plugin
        // DLLs file-locked past deactivation (until a full GC), so
        // PluginInstaller.UninstallAsync can fail the directory delete and
        // instead drops a .pending-delete marker. Without this sweep the next
        // boot would re-activate the "uninstalled" plugin from its leftover
        // dir — the uninstall silently undone.
        SweepPendingDeletes(root);

        var suppressedDirs = await RunStartupMigrationsAsync(root, ct)
            .ConfigureAwait(false);

        var pluginDirs = Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "plugin.json"))
                        && !File.Exists(Path.Combine(d, PendingDeleteMarker))
                        && !suppressedDirs.Contains(Path.GetFullPath(d))
                        && !IsAlreadyActiveDirectory(d))
            .ToArray();

        foreach (var dir in pluginDirs)
        {
            try
            {
                var installed = RegisterInstalled(dir);
                if (ShouldActivateAtStartup(installed.Manifest))
                    await ActivateAsync(dir, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to catalog or activate plugin from {Dir}", dir);
            }
        }
    }

    private bool ShouldActivateAtStartup(PluginManifest manifest)
    {
        if (!PluginEndpoints.IsScannedAudioPlugin(manifest.Id)) return true;
        return _services.GetService<IScannedPluginActivationPolicy>()
            ?.ShouldActivateAtStartup(manifest) == true;
    }

    /// <summary>Catalog an installed plugin without loading its assembly.</summary>
    public PluginCatalogEntry RegisterInstalled(string pluginDir)
    {
        var manifest = _loader.Inspect(pluginDir);
        var entry = new PluginCatalogEntry(manifest, pluginDir);
        _catalog[manifest.Id] = entry;
        return entry;
    }

    /// <summary>Activate one cataloged plugin on demand. Concurrent callers
    /// converge on a single activation.</summary>
    public async Task<ActivatedPlugin?> ActivateInstalledAsync(string id, CancellationToken ct)
    {
        if (_active.TryGetValue(id, out var active)) return active;
        if (!_catalog.TryGetValue(id, out var installed)) return null;

        await _lazyActivationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_active.TryGetValue(id, out active)) return active;
            return await ActivateAsync(installed.PluginDir, ct).ConfigureAwait(false);
        }
        finally
        {
            _lazyActivationGate.Release();
        }
    }

    /// <summary>
    /// Replace an active plugin with a freshly loaded instance from the same
    /// installed package. This is the authoritative factory-reset lifecycle:
    /// field initializers establish defaults before <c>InitializeAsync</c>
    /// sparsely hydrates whatever remains in the plugin settings collection.
    /// Returns null when the plugin is not currently active.
    /// </summary>
    public async Task<ActivatedPlugin?> ReactivateActivePluginAsync(
        string id,
        CancellationToken ct)
    {
        if (!_active.ContainsKey(id)) return null;

        await _lazyActivationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_active.TryGetValue(id, out var active)) return null;
            return await ActivateAsync(active.Loaded.PluginDir, ct).ConfigureAwait(false);
        }
        finally
        {
            _lazyActivationGate.Release();
        }
    }

    public void ForgetInstalled(string id) => _catalog.TryRemove(id, out _);

    /// <summary>Marker file a deferred uninstall leaves in a plugin dir whose
    /// files were still locked at uninstall time (Windows ALC file locks).
    /// Marked dirs are deleted — and never activated — on the next boot.</summary>
    public const string PendingDeleteMarker = ".pending-delete";

    private void SweepPendingDeletes(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!File.Exists(Path.Combine(dir, PendingDeleteMarker))) continue;
            try
            {
                if (!TryDeletePluginDirectory(dir))
                {
                    _log.LogWarning("Could not complete deferred uninstall of {Dir}; will retry next start.", dir);
                    continue;
                }
                _log.LogInformation("Completed deferred uninstall of {Dir}", dir);
            }
            catch (Exception ex)
            {
                // Still locked somehow — leave the marker in place so the dir
                // stays excluded from activation and the next boot retries.
                _log.LogWarning(ex, "Could not complete deferred uninstall of {Dir}; will retry next start.", dir);
            }
        }
    }

    private bool TryDeletePluginDirectory(string dir)
    {
        if (_options.TryDeleteDirectory is { } tryDeleteDirectory)
            return tryDeleteDirectory(dir);

        Directory.Delete(dir, recursive: true);
        return true;
    }

    private async Task<IReadOnlySet<string>> RunStartupMigrationsAsync(
        string root,
        CancellationToken ct)
    {
        var migrator = _services.GetService<PluginIdMigrator>();
        var installer = _services.GetService<PluginInstaller>();
        if (migrator is null || installer is null)
            return EmptyPathSet;

        using var migrationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        migrationCts.CancelAfter(_options.MigrationTimeout);

        try
        {
            return await migrator.RunStartupMigrationsAsync(installer, migrationCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && migrationCts.IsCancellationRequested)
        {
            _log.LogWarning(ex,
                "Plugin id startup migrations did not complete for {Root}; installed plugins will continue loading.",
                root);
            return EmptyPathSet;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Plugin id startup migrations did not complete for {Root}; installed plugins will continue loading.",
                root);
            return EmptyPathSet;
        }
    }

    private bool IsAlreadyActiveDirectory(string dir)
    {
        var full = NormalizeDirectoryPath(dir);
        return _active.Values.Any(p =>
            string.Equals(NormalizeDirectoryPath(p.Loaded.PluginDir), full, PathComparison));
    }

    private static string NormalizeDirectoryPath(string dir) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    private static readonly IReadOnlySet<string> EmptyPathSet =
        new HashSet<string>(PathComparer);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var entry in _active.Values.ToArray())
        {
            await DeactivateAsync(entry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Load + initialise a single plugin directory. Idempotent
    /// per id — if a plugin with the same id is already loaded it is
    /// shut down first.</summary>
    public async Task<ActivatedPlugin> ActivateAsync(string pluginDir, CancellationToken ct)
    {
        var loaded = _loader.Load(pluginDir);
        var id = loaded.Manifest.Id;
        _catalog[id] = new PluginCatalogEntry(loaded.Manifest, pluginDir);

        if (_active.TryRemove(id, out var existing))
        {
            await DeactivateAsync(existing, ct).ConfigureAwait(false);
        }

        var granted = ComputeGrantedCapabilities(loaded.Manifest);
        var pluginLogger = _logFactory.CreateLogger($"plugin/{id}");

        var ctx = new PluginContext(
            pluginId: id,
            manifest: loaded.Manifest,
            pluginRootPath: pluginDir,
            granted: granted,
            logger: pluginLogger,
            settings: _settings.ForPlugin(id),
            radio: granted.HasFlag(PluginCapabilities.ReadRadioState)
                ? _services.GetService<IRadioStateReader>()
                : null,
            radioController: granted.HasFlag(PluginCapabilities.ControlRadio)
                ? _services.GetService<IRadioController>()
                : null,
            hostDataDirectory: _options.HostDataDirectory ?? _settings.DataDirectory,
            // Audio playback sink (local monitor + on-air TX inject). Provided
            // by the host when available; on-air only reaches the air under
            // operator MOX, so this is not capability-gated here. Prefer the
            // per-plugin factory — the sink's over-air resampler is stateful,
            // so two plugins sharing one instance would leak residual samples
            // into each other's first block — falling back to a host-wide
            // singleton for hosts that only register that.
            playback: _services.GetService<Func<IAudioPlaybackSink>>()?.Invoke()
                ?? _services.GetService<IAudioPlaybackSink>(),
            // QRZ callsign lookup, gated on NetworkAccess — the host reuses the
            // operator's stored credentials + rate-limit gate (no second login).
            qrz: granted.HasFlag(PluginCapabilities.NetworkAccess)
                ? _services.GetService<IQrzLookup>()
                : null,
            operatorIdentity: _services.GetService<IOperatorIdentityProvider>());

        using (var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            initCts.CancelAfter(_options.InitTimeout);
            try
            {
                await loaded.Plugin.InitializeAsync(ctx, initCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "InitializeAsync threw for plugin {Id} — unloading", id);
                loaded.LoadContext.Unload();
                PluginLoader.TryDeleteShadowDirectory(loaded.ShadowDir);
                throw new PluginLoadException(
                    $"plugin '{id}' threw during InitializeAsync: {ex.Message}", ex);
            }
        }

        var activated = new ActivatedPlugin(loaded, ctx);
        _active[id] = activated;

        try { PluginActivated?.Invoke(activated); }
        catch (Exception ex) { _log.LogWarning(ex, "PluginActivated subscriber threw for {Id}", id); }

        return activated;
    }

    /// <summary>
    /// Re-run the LOADED instance's <see cref="IZeusPlugin.InitializeAsync"/>
    /// with the SAME <see cref="IPluginContext"/> it was activated with (kept on
    /// <see cref="ActivatedPlugin.Context"/>), so the plugin re-reads its
    /// persisted settings. Settings hydration lives only inside
    /// <c>InitializeAsync</c> for the in-house native audio plugins
    /// (set-context → log → hydrate, all idempotent), and a TX audio profile
    /// apply rewrites <see cref="PluginSettingsStore"/> underneath a live
    /// instance — without this the store holds the new voicing while the live
    /// DSP and its GET /params surface keep pre-apply values until restart.
    ///
    /// <para>Failure contract: a throw here is logged as a warning and the
    /// plugin stays ACTIVE with its prior in-memory state. The unload-on-throw
    /// path in <see cref="ActivateAsync"/> is for first-time activation only —
    /// a live TX plugin must never vanish from the chain because one rehydrate
    /// failed.</para>
    /// </summary>
    public async Task<bool> RehydratePluginSettingsAsync(string pluginId, CancellationToken ct)
    {
        if (!_active.TryGetValue(pluginId, out var entry)) return false;
        // Test-injected ActivatedPlugin entries can carry a null context; a
        // rehydrate without the original context would hand the plugin a
        // different settings scope than it was activated with, so skip.
        if (entry.Context is null)
        {
            _log.LogWarning(
                "Cannot rehydrate settings for plugin {Id}: no activation context retained", pluginId);
            return false;
        }

        using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initCts.CancelAfter(_options.InitTimeout);
        try
        {
            await entry.Loaded.Plugin.InitializeAsync(entry.Context, initCts.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "InitializeAsync threw during settings rehydrate for plugin {Id} — keeping it live with prior state",
                pluginId);
            return false;
        }
    }

    /// <summary>Shut down + unload an activated plugin. Idempotent.</summary>
    public async Task DeactivateAsync(string id, CancellationToken ct)
    {
        if (_active.TryRemove(id, out var entry))
            await DeactivateAsync(entry, ct).ConfigureAwait(false);
    }

    private async Task DeactivateAsync(ActivatedPlugin entry, CancellationToken ct)
    {
        var id = entry.Loaded.Manifest.Id;
        // Remove from the active map first so a concurrent caller can't
        // see a half-shutdown plugin. Idempotent: the public
        // DeactivateAsync(id) path also TryRemoves before calling us.
        _active.TryRemove(id, out _);

        // Notify subscribers BEFORE the plugin's own ShutdownAsync so
        // host-side per-plugin resources (audio chain slots, etc.) can
        // be released while the plugin instance is still alive.
        try { PluginDeactivated?.Invoke(entry); }
        catch (Exception ex) { _log.LogWarning(ex, "PluginDeactivated subscriber threw for {Id}", id); }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.ShutdownTimeout);
        try
        {
            await entry.Loaded.Plugin.ShutdownAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ShutdownAsync threw for plugin {Id} — continuing unload", id);
        }
        finally
        {
            try { entry.Loaded.LoadContext.Unload(); }
            catch (Exception ex) { _log.LogWarning(ex, "ALC unload threw for plugin {Id}", id); }
            PluginLoader.TryDeleteShadowDirectory(entry.Loaded.ShadowDir);
            _log.LogInformation("Deactivated plugin {Id}", id);
        }
    }

    private static PluginCapabilities ComputeGrantedCapabilities(PluginManifest m)
    {
        // v1 grants every declared capability; user-prompt UI is iter 5.
        // PersistSettings is implicit per ADR.
        return m.ParseCapabilities();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lazyActivationGate.Dispose();
        _settings.Dispose();
    }
}

/// <summary>Tunable timeouts + flags for <see cref="PluginManager"/>.</summary>
public sealed record PluginManagerOptions
{
    public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MigrationTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public bool SafeMode { get; init; } = false;
    internal Func<string, bool>? TryDeleteDirectory { get; init; }

    /// <summary>
    /// Override the plugin discovery root. Null (default) defers to
    /// <see cref="PluginRoot.Get"/>. Setting this explicitly lets tests
    /// run in parallel without fighting over the process-global
    /// <c>ZEUS_PLUGINS_PATH</c> env var.
    /// </summary>
    public string? PluginRoot { get; init; }

    /// <summary>
    /// The host data directory surfaced to plugins as
    /// <see cref="Zeus.Plugins.Contracts.IPluginContext.HostDataDirectory"/>.
    /// Null (default) falls back to the prefs database's directory — which is
    /// WRONG for hosts using prefs profiles (the prefs file moves into
    /// <c>profiles/</c> while per-data-dir files like <c>zeus-logbook.db</c>
    /// stay at the data-dir root), so the host should always set this to the
    /// data-dir root it wants plugins to see.
    /// </summary>
    public string? HostDataDirectory { get; init; }
}

/// <summary>Runtime state for one currently-active plugin.</summary>
public sealed record ActivatedPlugin(LoadedPlugin Loaded, IPluginContext Context);

public sealed record PluginCatalogEntry(PluginManifest Manifest, string PluginDir);

/// <summary>Host-owned policy for deciding which scanned audio plugins need a
/// real assembly activation during startup.</summary>
public interface IScannedPluginActivationPolicy
{
    bool ShouldActivateAtStartup(PluginManifest manifest);
}
