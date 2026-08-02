// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Registry;

namespace Zeus.Plugins.Host.Registry;

internal sealed class PluginIdMigrator
{
    private readonly IRegistryClient _registry;
    private readonly IPluginPackageDownloader _downloader;
    private readonly PluginSettingsStore _settings;
    private readonly string _pluginRoot;
    private readonly ILogger<PluginIdMigrator> _log;
    private readonly Func<string, bool> _tryDeleteDirectory;

    private static readonly JsonSerializerOptions ManifestJsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public PluginIdMigrator(
        IRegistryClient registry,
        IPluginPackageDownloader downloader,
        PluginSettingsStore settings,
        string pluginRoot,
        ILogger<PluginIdMigrator> log,
        Func<string, bool>? tryDeleteDirectory = null)
    {
        _registry = registry;
        _downloader = downloader;
        _settings = settings;
        _pluginRoot = pluginRoot;
        _log = log;
        _tryDeleteDirectory = tryDeleteDirectory ?? TryDeleteDirectory;
    }

    public async Task<IReadOnlySet<string>> RunStartupMigrationsAsync(
        PluginInstaller installer,
        CancellationToken ct)
    {
        var suppressedOldDirs = new HashSet<string>(PathComparer);
        Task<RegistryCatalog>? catalogTask = null;
        Task<RegistryCatalog> GetCatalogAsync() =>
            catalogTask ??= _registry.FetchAsync(ct);

        foreach (var (oldId, newId) in PluginIdMigrations.Map)
        {
            var oldDir = Path.Combine(_pluginRoot, PluginInstaller.SafeDirName(oldId));
            if (!Directory.Exists(oldDir) ||
                File.Exists(Path.Combine(oldDir, PluginManager.PendingDeleteMarker)))
            {
                continue;
            }

            try
            {
                if (await MigrateOneAsync(oldId, newId, oldDir, installer, GetCatalogAsync, ct)
                        .ConfigureAwait(false))
                {
                    suppressedOldDirs.Add(Path.GetFullPath(oldDir));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Plugin id migration {OldId} -> {NewId} did not complete; old plugin will remain active.",
                    oldId, newId);
            }
        }

        return suppressedOldDirs;
    }

    private async Task<bool> MigrateOneAsync(
        string oldId,
        string newId,
        string oldDir,
        PluginInstaller installer,
        Func<Task<RegistryCatalog>> getCatalog,
        CancellationToken ct)
    {
        var newDir = Path.Combine(_pluginRoot, PluginInstaller.SafeDirName(newId));
        if (Directory.Exists(newDir))
        {
            if (File.Exists(Path.Combine(newDir, PluginManager.PendingDeleteMarker)))
            {
                if (!_tryDeleteDirectory(newDir))
                {
                    _log.LogWarning(
                        "Migrated plugin dir {Dir} still has {Marker}; old plugin {OldId} will remain active.",
                        newDir, PluginManager.PendingDeleteMarker, oldId);
                    return false;
                }
            }
            else if (HasMatchingManifest(newDir, newId))
            {
                CopySettings(oldId, newId);
                CleanupOldInstall(oldDir);
                _log.LogInformation(
                    "Completed plugin id migration cleanup {OldId} -> {NewId}; new install already present.",
                    oldId, newId);
                return true;
            }

            // The marker branch above may have already removed the dir; only
            // the still-present (incomplete/corrupt) case needs clearing here.
            if (Directory.Exists(newDir) && !_tryDeleteDirectory(newDir))
            {
                TryWritePendingDeleteMarker(
                    newDir,
                    "Could not write {Marker} in incomplete migrated plugin dir {Dir}");
                _log.LogWarning(
                    "Could not delete incomplete migrated plugin dir {Dir}; old plugin {OldId} will remain active.",
                    newDir, oldId);
                return false;
            }
        }

        var catalog = await getCatalog().ConfigureAwait(false);
        var entry = catalog.Plugins.FirstOrDefault(p => p.Id == newId)
            ?? throw new PluginInstallException($"plugin '{newId}' not in registry");
        var version = PickNewestVersion(entry)
            ?? throw new PluginInstallException($"plugin '{newId}' has no registry versions");

        using var package = await _downloader
            .DownloadAndVerifyToTempFileAsync(version.DownloadUrl, version.Sha256, ct)
            .ConfigureAwait(false);

        var settingsCopied = CopySettings(oldId, newId);

        try
        {
            await installer.InstallFromZipFileAsync(package.Path, ct).ConfigureAwait(false);
        }
        catch
        {
            if (settingsCopied)
                _settings.DropCollection(newId);
            CleanupFailedNewInstall(newDir);
            throw;
        }

        CleanupOldInstall(oldDir);
        _log.LogInformation(
            "Migrated plugin id {OldId} -> {NewId} v{Version}",
            oldId, newId, version.Version);
        return true;
    }

    private bool CopySettings(string oldId, string newId)
    {
        var existing = _settings.DumpCollection(newId);
        if (existing.Count > 0)
        {
            _log.LogDebug(
                "Skipping plugin settings migration {OldId} -> {NewId}; destination already has settings.",
                oldId, newId);
            return false;
        }

        var snapshot = _settings.DumpCollection(oldId);
        if (snapshot.Count == 0) return false;

        _settings.RestoreCollection(newId, snapshot);
        // Keep the old collection as cheap insurance; only the install directory
        // is removed/marked so rollback or manual recovery does not lose knobs.
        return true;
    }

    private void CleanupOldInstall(string oldDir)
    {
        if (_tryDeleteDirectory(oldDir)) return;

        if (!TryWritePendingDeleteMarker(
                oldDir,
                "Could not write {Marker} in migrated plugin dir {Dir}"))
            return;

        _log.LogWarning(
            "Could not delete migrated plugin dir {Dir}; wrote {Marker} so it will not activate.",
            oldDir, PluginManager.PendingDeleteMarker);
    }

    private void CleanupFailedNewInstall(string newDir)
    {
        if (!Directory.Exists(newDir)) return;

        if (_tryDeleteDirectory(newDir)) return;

        TryWritePendingDeleteMarker(
            newDir,
            "Could not write {Marker} in failed migrated plugin dir {Dir}");
    }

    private bool TryWritePendingDeleteMarker(string dir, string messageTemplate)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, PluginManager.PendingDeleteMarker), "");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                messageTemplate,
                PluginManager.PendingDeleteMarker, dir);
            return false;
        }
    }

    private static bool HasMatchingManifest(string dir, string expectedId)
    {
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath)) return false;

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, ManifestJsonOpts);
            return string.Equals(manifest?.Id, expectedId, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static PluginVersion? PickNewestVersion(PluginEntry entry) =>
        entry.Versions
            .OrderBy(v => v.Version, VersionStringComparer.Instance)
            .LastOrDefault();

    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class VersionStringComparer : IComparer<string>
    {
        public static VersionStringComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (string.Equals(x, y, StringComparison.Ordinal)) return 0;

            var vx = TryParseVersion(x);
            var vy = TryParseVersion(y);
            if (vx is not null && vy is not null)
                return vx.CompareTo(vy);
            if (vx is not null) return 1;
            if (vy is not null) return -1;
            return StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }

        private static Version? TryParseVersion(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;

            var core = version.Split('+', 2)[0].Split('-', 2)[0];
            if (core.Count(c => c == '.') == 0)
                core += ".0";

            return Version.TryParse(core, out var parsed) ? parsed : null;
        }
    }
}
