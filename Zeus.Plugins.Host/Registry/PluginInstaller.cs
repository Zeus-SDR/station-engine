// SPDX-License-Identifier: GPL-2.0-or-later
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Registry;

namespace Zeus.Plugins.Host.Registry;

/// <summary>
/// Implements bring-your-own-plugin: downloads a zip, verifies SHA256
/// if supplied, validates the embedded plugin.json, extracts into the
/// plugin root, and asks <see cref="PluginManager"/> to activate.
/// </summary>
public sealed class PluginInstaller
{
    private readonly IPluginPackageDownloader _downloader;
    private readonly IRegistryClient _registry;
    private readonly PluginManager _manager;
    private readonly PluginOperationGate _operations;
    private readonly string _pluginRoot;
    private readonly ILogger<PluginInstaller>? _log;
    private readonly Func<string, bool>? _tryDeleteDirectory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public PluginInstaller(
        HttpClient http,
        IRegistryClient registry,
        PluginManager manager,
        string pluginRoot,
        ILogger<PluginInstaller>? log = null,
        Func<string, bool>? tryDeleteDirectory = null,
        PluginOperationGate? operations = null)
        : this(new PluginPackageDownloader(http), registry, manager, pluginRoot, log, tryDeleteDirectory, operations)
    {
    }

    public PluginInstaller(
        IPluginPackageDownloader downloader,
        IRegistryClient registry,
        PluginManager manager,
        string pluginRoot,
        ILogger<PluginInstaller>? log = null,
        Func<string, bool>? tryDeleteDirectory = null,
        PluginOperationGate? operations = null)
    {
        _downloader = downloader;
        _registry = registry;
        _manager = manager;
        _operations = operations ?? new PluginOperationGate();
        _pluginRoot = pluginRoot;
        _log = log;
        _tryDeleteDirectory = tryDeleteDirectory;
    }

    /// <summary>Install from a registry id+version. The catalog is consulted
    /// for the downloadUrl + expected sha256.</summary>
    public async Task<InstalledPlugin> InstallFromRegistryAsync(
        string id, string version, CancellationToken ct)
    {
        var catalog = await _registry.FetchAsync(ct).ConfigureAwait(false);
        var entry = catalog.Plugins.FirstOrDefault(p => p.Id == id)
            ?? throw new PluginInstallException($"plugin '{id}' not in registry");
        var ver = entry.Versions.FirstOrDefault(v => v.Version == version)
            ?? throw new PluginInstallException($"version '{version}' of '{id}' not in registry");

        return await InstallFromZipUrlAsync(ver.DownloadUrl, ver.Sha256, ct).ConfigureAwait(false);
    }

    /// <summary>Install from an arbitrary HTTPS URL that returns a Zeus plugin zip package.
    /// <paramref name="expectedSha256"/> is verified if supplied; pass null to skip.</summary>
    public async Task<InstalledPlugin> InstallFromZipUrlAsync(
        string url, string? expectedSha256, CancellationToken ct)
    {
        using var package = await _downloader
            .DownloadAndVerifyToTempFileAsync(url, expectedSha256, ct)
            .ConfigureAwait(false);
        return await InstallFromZipFileAsync(package.Path, ct).ConfigureAwait(false);
    }

    /// <summary>Backward-compatible alias for existing callers.</summary>
    public async Task<InstalledPlugin> InstallFromUrlAsync(
        string url, string? expectedSha256, CancellationToken ct)
    {
        return await InstallFromZipUrlAsync(url, expectedSha256, ct).ConfigureAwait(false);
    }

    /// <summary>Install from a local zip on disk (BYOP "Install from file…").</summary>
    public async Task<InstalledPlugin> InstallFromZipFileAsync(string zipPath, CancellationToken ct)
    {
        if (!File.Exists(zipPath))
            throw new PluginInstallException($"zip file not found: {zipPath}");

        // Pre-flight: extract the manifest before we touch the plugin root.
        PluginManifest manifest;
        try
        {
            using var probe = ZipFile.OpenRead(zipPath);
            var entry = probe.GetEntry("plugin.json")
                ?? throw new PluginInstallException("zip is missing plugin.json at the top level");
            await using var s = entry.Open();
            manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(s, JsonOpts, ct)
                .ConfigureAwait(false)
                ?? throw new PluginInstallException("plugin.json deserialised to null");
        }
        catch (InvalidDataException ex)
        {
            throw new PluginInstallException(
                "plugin package must be a zip file containing plugin.json at the top level", ex);
        }

        var errors = ManifestValidator.Validate(manifest);
        if (errors.Count > 0)
            throw new PluginInstallException(
                $"manifest invalid: {string.Join("; ", errors)}");

        if (!ManifestValidator.IsAbiCompatible(manifest, AbiVersion.Current, AbiVersion.SdkVersion))
            throw new PluginInstallException(
                $"plugin '{manifest.Id}' requires SDK abi={manifest.Sdk.Abi} minVersion={manifest.Sdk.MinVersion}; "
                + $"host is abi={AbiVersion.Current} version={AbiVersion.SdkVersion}");

        using var operation = await _operations.EnterAsync(manifest.Id, ct).ConfigureAwait(false);

        var safeId = SafeDirName(manifest.Id);
        var destDir = Path.Combine(_pluginRoot, safeId);
        var transactionDir = Path.Combine(
            _pluginRoot, ".updates", $"{safeId}-{Guid.NewGuid():N}");
        var stagingDir = Path.Combine(transactionDir, "staging");
        var backupDir = Path.Combine(transactionDir, "previous");

        // Fully extract and validate the filesystem shape before deactivating
        // the current plugin. A corrupt/zip-slip package must never leave the
        // live installation half-deleted.
        Directory.CreateDirectory(stagingDir);
        try
        {
            ExtractZipSafely(zipPath, stagingDir);
        }
        catch
        {
            TryDeleteDirectory(transactionDir);
            throw;
        }

        bool hadActivePlugin = _manager.Find(manifest.Id) is not null;
        bool previousMoved = false;
        bool replacementMoved = false;
        bool preserveTransaction = false;

        try
        {
            if (hadActivePlugin)
                await _manager.DeactivateAsync(manifest.Id, ct).ConfigureAwait(false);

            // Both paths live under the same plugin root, so these directory
            // renames are same-volume operations. Keep the complete previous
            // package until the replacement has activated successfully.
            if (Directory.Exists(destDir))
            {
                Directory.Move(destDir, backupDir);
                previousMoved = true;
            }

            Directory.Move(stagingDir, destDir);
            replacementMoved = true;

            var activated = await _manager.ActivateAsync(destDir, ct).ConfigureAwait(false);
            if (previousMoved && !TryDeleteDirectory(backupDir))
            {
                _log?.LogWarning(
                    "Installed plugin {Id}, but could not remove transaction backup {BackupDir}",
                    manifest.Id, backupDir);
            }

            _log?.LogInformation("Installed plugin {Id} v{Version} -> {Dir}",
                manifest.Id, manifest.Version, destDir);
            return new InstalledPlugin(manifest, destDir, activated);
        }
        catch (Exception installError)
        {
            // Activation can fail after the directory swap (bad assembly,
            // constructor, or InitializeAsync). Deactivate any partial new
            // instance, remove its live directory, and put the untouched old
            // package back before returning the error.
            if (_manager.Find(manifest.Id) is not null)
            {
                try { await _manager.DeactivateAsync(manifest.Id, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log?.LogWarning(ex, "Failed to deactivate replacement plugin {Id}", manifest.Id); }
            }

            bool replacementRemoved = !replacementMoved
                || !Directory.Exists(destDir)
                || TryDeleteDirectory(destDir);

            bool previousRestored = !previousMoved;
            if (previousMoved && replacementRemoved && Directory.Exists(backupDir))
            {
                try
                {
                    Directory.Move(backupDir, destDir);
                    previousRestored = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log?.LogError(ex,
                        "Failed to restore previous plugin {Id} from {BackupDir}",
                        manifest.Id, backupDir);
                }
            }

            // If rollback could not put the previous package back, its backup
            // is the operator's recovery copy. Never let the finally block
            // erase it while reporting that it was preserved.
            preserveTransaction = previousMoved && !previousRestored;

            if (hadActivePlugin && previousRestored && Directory.Exists(destDir))
            {
                try { await _manager.ActivateAsync(destDir, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log?.LogError(ex, "Failed to reactivate previous plugin {Id}", manifest.Id); }
            }

            if (installError is OperationCanceledException)
                throw;

            var recovery = previousRestored
                ? "the previous installation was restored"
                : $"the previous installation remains preserved at '{backupDir}'";
            throw new PluginInstallException(
                $"failed to install plugin '{manifest.Id}': {installError.Message}; {recovery}.",
                installError);
        }
        finally
        {
            if (!preserveTransaction)
                TryDeleteDirectory(transactionDir);
        }
    }

    /// <summary>Uninstall a plugin by id: deactivate then remove its dir.</summary>
    public async Task UninstallAsync(string id, CancellationToken ct)
    {
        using var operation = await _operations.EnterAsync(id, ct).ConfigureAwait(false);
        await _manager.DeactivateAsync(id, ct).ConfigureAwait(false);
        _manager.ForgetInstalled(id);
        await Task.Delay(50, ct).ConfigureAwait(false);

        var dir = Path.Combine(_pluginRoot, SafeDirName(id));
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows holds an open file handle on plugin DLLs after
                // ALC.Unload until GC reclaims the load context — both
                // IOException ("file in use") and UnauthorizedAccessException
                // ("access denied") surface depending on the open mode.
                // Either way the deactivation succeeded; the dir cleanup
                // needs an explicit GC + retry, or a Zeus restart. Drop the
                // pending-delete marker so PluginManager.StartAsync finishes
                // the removal on the next boot INSTEAD of re-activating the
                // leftover dir (the "uninstall resurrection" bug).
                try
                {
                    File.WriteAllText(Path.Combine(dir, PluginManager.PendingDeleteMarker), "");
                }
                catch (Exception markerEx)
                {
                    _log?.LogWarning(markerEx,
                        "Could not write {Marker} in {Dir}", PluginManager.PendingDeleteMarker, dir);
                }
                _log?.LogWarning(ex,
                    "Could not delete plugin dir {Dir} immediately; restart Zeus to finish removal.", dir);
                throw new PluginInstallException(
                    $"plugin '{id}' deactivated but its files could not be removed yet. Restart Zeus to complete.", ex);
            }
        }
    }

    /// <summary>Extract while rejecting zip-slip and arbitrary writes outside the dest dir.</summary>
    private static void ExtractZipSafely(string zipPath, string destDir)
    {
        var fullDest = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/"))
            {
                // Pure directory
                var dir = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                if (!dir.StartsWith(fullDest, StringComparison.Ordinal))
                    throw new PluginInstallException($"zip entry escapes plugin dir: {entry.FullName}");
                Directory.CreateDirectory(dir);
                continue;
            }

            var fileDest = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!fileDest.StartsWith(fullDest, StringComparison.Ordinal))
                throw new PluginInstallException($"zip entry escapes plugin dir: {entry.FullName}");

            var parent = Path.GetDirectoryName(fileDest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            entry.ExtractToFile(fileDest, overwrite: true);
        }
    }

    private bool TryDeleteDirectory(string path)
    {
        if (_tryDeleteDirectory is not null)
            return _tryDeleteDirectory(path);

        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Convert plugin id to a safe directory name. Reverse-DNS
    /// dots and hyphens stay; nothing else is permitted by the
    /// manifest's id pattern.</summary>
    internal static string SafeDirName(string pluginId)
    {
        Span<char> buf = stackalloc char[pluginId.Length];
        for (int i = 0; i < pluginId.Length; i++)
        {
            var c = pluginId[i];
            buf[i] = char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_';
        }
        return new string(buf);
    }
}

public sealed record InstalledPlugin(
    PluginManifest Manifest,
    string Directory,
    ActivatedPlugin Activated);

public sealed class PluginInstallException : Exception
{
    public PluginInstallException(string message) : base(message) { }
    public PluginInstallException(string message, Exception inner) : base(message, inner) { }
}
