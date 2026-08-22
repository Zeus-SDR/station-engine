// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Zeus.Server;

/// <summary>
/// The single operator-facing Zeus database. The .zeusdb payload is a
/// versioned ZIP container because the live processes intentionally retain
/// separate LiteDB ownership boundaries. Operators nevertheless only need to
/// keep track of this one file.
/// </summary>
internal static class UnifiedDatabaseBackup
{
    internal const string Format = "zeus-database-backup";
    internal const int CurrentVersion = 1;
    internal const string PendingRestoreDirectoryEnvironmentVariable =
        "ZEUS_PENDING_DATABASE_RESTORE_PATH";
    internal const string ProductPreferencesPathEnvironmentVariable =
        "ZEUS_PRODUCT_PREFS_PATH";
    internal const string ProductDataDirectoryEnvironmentVariable =
        "ZEUS_PRODUCT_DATA_DIR";

    private const string ManifestEntryName = "manifest.json";
    private const string PendingDirectoryName = ".pending-database-restore";
    private const string RollbackDirectoryName = ".rollback";
    private const string RollbackManifestName = "rollback.json";
    private const string PureSignalCollection = "ps_settings";
    // Bound both individual snapshots and the complete container even when ZIP
    // compression provides little benefit. The upload route is sized from the
    // complete-container limit below.
    private const long MaximumEntryBytes = 96L * 1024 * 1024;
    internal const long MaximumBackupBytes = 120L * 1024 * 1024;

    private static readonly HashSet<string> LegacyProductPreferenceAnchors =
        new(StringComparer.Ordinal)
        {
            "credentials",
            "product_bundle_settings",
            "midi_config",
            "window_geometry",
            "settings_window_geometry",
            "open_workspace_windows",
            "audio_profiles",
            "audio_chain_settings",
            "audio_processing_mode",
            "rx_audio_profiles",
            "tx_audio_profiles",
            "user_management",
            "remote_password",
            "dxcluster_config",
            "spots_settings",
            "wsjtx_config",
            "n1mm_config",
            "g2_panel_settings",
            "chat_enabled",
        };
    private static readonly HashSet<string> LegacyEnginePreferenceAnchors =
        new(StringComparer.Ordinal)
        {
            "radio_state",
            "preferred_radio",
            "pa_bands",
            "dsp_settings",
            "display_settings",
            "theme_settings",
            "ui_layout",
            "ui_layouts_v2",
        };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal sealed record StoreSpec(string Key, string Kind, string Path, bool StationOwned);
    internal sealed record BackupEntry(
        string Key,
        string Kind,
        string ArchivePath,
        long Length,
        string Sha256);
    internal sealed record BackupManifest(
        string Format,
        int Version,
        DateTime CreatedUtc,
        IReadOnlyList<BackupEntry> Entries);
    private sealed record PreparedEntry(
        BackupEntry Entry,
        StoreSpec Store,
        string StagedPath,
        string PreparedPath);
    private sealed record RollbackEntry(
        string Key,
        string DestinationPath,
        bool DatabaseExisted,
        bool LogExisted,
        string DatabaseBackupPath,
        string LogBackupPath);

    internal static byte[] BuildContainer() => BuildContainer(DiscoverStores());

    internal static byte[] BuildContainer(
        IReadOnlyList<StoreSpec> stores,
        Action<StoreSpec, string>? liteDatabaseSnapshotObserver = null)
    {
        ArgumentNullException.ThrowIfNull(stores);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entries = new List<BackupEntry>();
            var seenPaths = new HashSet<string>(PathComparer());
            long totalBytes = 0;
            foreach (var store in stores)
            {
                var fullPath = Path.GetFullPath(store.Path);
                if (!seenPaths.Add(fullPath) || !File.Exists(fullPath))
                    continue;

                var bytes = store.Kind == "litedb"
                    ? SnapshotLiteDatabase(
                        fullPath,
                        store.StationOwned,
                        snapshotPath => liteDatabaseSnapshotObserver?.Invoke(store, snapshotPath))
                    : File.ReadAllBytes(fullPath);
                if (bytes.LongLength > MaximumEntryBytes)
                    throw new InvalidOperationException(
                        $"The '{store.Key}' store is too large to export safely.");
                totalBytes = checked(totalBytes + bytes.LongLength);
                if (totalBytes > MaximumBackupBytes)
                    throw new InvalidOperationException(
                        $"The Zeus settings database became too large while adding the '{store.Key}' store.");

                var archivePath = $"data/{store.Key}{(store.Kind == "litedb" ? ".db" : ".json")}";
                var zipEntry = archive.CreateEntry(archivePath, CompressionLevel.Optimal);
                using (var destination = zipEntry.Open())
                    destination.Write(bytes);
                entries.Add(new BackupEntry(
                    store.Key,
                    store.Kind,
                    archivePath,
                    bytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
            }

            var entryKeys = entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            if (!entryKeys.Contains("preferences") || !entryKeys.Contains("station-engine"))
            {
                throw new InvalidOperationException(
                    "Zeus could not read every part of the complete settings database.");
            }

            var manifest = new BackupManifest(
                Format,
                CurrentVersion,
                DateTime.UtcNow,
                entries);
            var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using var manifestStream = manifestEntry.Open();
            JsonSerializer.Serialize(manifestStream, manifest, Json);
        }

        return output.ToArray();
    }

    internal static void StageImport(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var incomingPath = Path.Combine(
            Path.GetTempPath(), $"zeus-database-import-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var destination = new FileStream(
                incomingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
                if (destination.Length > MaximumBackupBytes)
                    throw new InvalidOperationException("That Zeus database backup is too large.");
            }

            if (IsZip(incomingPath))
                StageContainer(incomingPath);
            else
                StageLegacyLiteDatabase(incomingPath);
        }
        finally
        {
            TryDelete(incomingPath);
            TryDelete(incomingPath + "-log");
        }
    }

    /// <summary>
    /// Applies every Station-owned file in the operator-facing settings
    /// database. Product-owned entries stay staged for ZeusProduct to consume
    /// before it opens its own stores.
    /// </summary>
    internal static void ApplyPendingStationRestore(TimeSpan? restoreLockTimeout = null)
    {
        var pendingDirectory = PendingDirectory();
        using var restoreLock = TryAcquireRestoreLock(
            pendingDirectory,
            restoreLockTimeout ?? TimeSpan.FromSeconds(30));
        if (restoreLock is null)
        {
            // File + stderr: windowed builds swallow the console.
            ReportRestoreDiagnostic(
                "database.restore station skipped: timed out waiting for the restore lock; restore remains pending for the next start.");
            return;
        }
        if (!RecoverInterruptedRestore(pendingDirectory))
            return;
        var manifestPath = Path.Combine(pendingDirectory, ManifestEntryName);
        if (!File.Exists(manifestPath))
            return;

        BackupManifest manifest;
        try
        {
            manifest = ReadManifest(File.ReadAllBytes(manifestPath));
        }
        catch (Exception ex)
        {
            ReportRestoreDiagnostic(
                $"database.restore pending manifest invalid: {ex.Message}");
            return;
        }

        var destinations = DiscoverStores().ToDictionary(s => s.Key, StringComparer.Ordinal);
        var applicable = manifest.Entries
            .Where(entry => destinations.TryGetValue(entry.Key, out var store) && store.StationOwned)
            .Select(entry => (Entry: entry, Store: destinations[entry.Key]))
            .ToList();
        var remaining = manifest.Entries
            .Where(entry => !destinations.TryGetValue(entry.Key, out var store) || !store.StationOwned)
            .ToList();
        var prepared = new List<PreparedEntry>();
        var stagedPaths = new List<string>();
        try
        {
            // Validate and fully prepare every replacement before changing any
            // authoritative file. This catches corrupt/mismatched entries
            // without leaving a half-restored settings pair.
            foreach (var (entry, store) in applicable)
            {
                var stagedPath = SafePendingEntryPath(pendingDirectory, entry.ArchivePath);
                stagedPaths.Add(stagedPath);
                if (!File.Exists(stagedPath))
                {
                    // Crash-after-apply deletes staged files before the manifest
                    // shrinks, and the prepared transform usually changes the
                    // destination bytes, so a hash mismatch here is expected.
                    // Nothing is recoverable without the staged bytes — skip
                    // instead of wedging every subsequent boot.
                    if (!DestinationMatches(entry, store))
                        Console.Error.WriteLine(
                            $"database.restore staged entry '{entry.Key}' is missing; keeping the current local file.");
                    continue;
                }
                prepared.Add(new PreparedEntry(
                    entry,
                    store,
                    stagedPath,
                    PrepareEntry(stagedPath, entry, store)));
            }

            if (prepared.Count > 0)
            {
                CreateRollback(pendingDirectory, prepared);
                foreach (var item in prepared)
                    ReplacePreparedEntryWithRetry(item.PreparedPath, item.Store.Path);

                var rollbackDirectory = Path.Combine(pendingDirectory, RollbackDirectoryName);
                // Removing the rollback marker is the commit point. If cleanup
                // after this point is interrupted, the staged pair is simply
                // safe to apply again on the next start.
                DeleteIfExists(Path.Combine(rollbackDirectory, RollbackManifestName));
                TryDeleteDirectory(rollbackDirectory);
            }

            if (remaining.Count == 0)
                DeleteIfExists(manifestPath);
            else
                WriteManifestAtomically(manifestPath, manifest with { Entries = remaining });

            foreach (var stagedPath in stagedPaths)
                TryDelete(stagedPath);
            if (remaining.Count == 0)
                TryDeleteDirectory(pendingDirectory);
        }
        catch (Exception ex)
        {
            ReportRestoreDiagnostic(
                $"database.restore could not apply settings: {ex.Message}");
            if (!RecoverInterruptedRestore(pendingDirectory))
            {
                ReportRestoreDiagnostic(
                    "database.restore rollback remains pending for the next start.");
            }
            else
            {
                ReportRestoreDiagnostic(
                    "database.restore rollback completed; restore remains pending for the next start.");
            }
            return;
        }
        finally
        {
            foreach (var item in prepared)
            {
                TryDelete(item.PreparedPath);
                TryDelete(item.PreparedPath + "-log");
            }
        }

    }

    internal static IReadOnlyList<StoreSpec> DiscoverStores()
    {
        return
        [
            new("preferences", "litedb", PrefsDbPath.Get(), StationOwned: true),
            new("station-engine", "litedb", PrefsDbPath.EngineGet(), StationOwned: true),
            new("link-product", "litedb", ProductPreferencesPath(), StationOwned: false),
            new("logbook", "litedb", PrefsDbPath.LogbookPath(), StationOwned: true),
            new("link-logbook", "litedb", ProductLogbookPath(), StationOwned: false),
            new("audio-suite", "file", ProductPreferencesPath() + ".audio-suite.json", StationOwned: false),
            new("audio-suite-quarantine", "file",
                ProductPreferencesPath() + ".audio-suite.json.plugin-quarantine.json",
                StationOwned: false),
        ];
    }

    private static string ProductPreferencesPath()
    {
        var configured = Environment.GetEnvironmentVariable(
            ProductPreferencesPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(local, "Zeus", "zeus-link-product.db");
    }

    private static string ProductLogbookPath()
    {
        var configured = Environment.GetEnvironmentVariable(
            ProductDataDirectoryEnvironmentVariable);
        var dataDirectory = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZeusProduct");
        return Path.Combine(dataDirectory, "logbook", "zeus-logbook.db");
    }

    private static byte[] SnapshotLiteDatabase(
        string sourcePath,
        bool stationOwned,
        Action<string>? snapshotObserver = null)
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(), $"zeus-database-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            if (stationOwned)
            {
                // Station-owned stores share this process's lease registry, so
                // they can be checkpointed before the stable byte copy.
                using var lease = Zeus.Data.SharedLiteDatabase.Acquire(sourcePath);
                lease.Database.Checkpoint();
                CopySnapshotFile(sourcePath, tempPath);
            }
            else
            {
                // Product stores may belong to a different live process. Never
                // open or checkpoint that process's authoritative file: copy
                // the database and transaction sidecar, then open only the copy.
                CopySnapshotFile(sourcePath, tempPath);
                if (File.Exists(sourcePath + "-log"))
                    CopySnapshotFile(sourcePath + "-log", tempPath + "-log");
            }

            snapshotObserver?.Invoke(tempPath);
            try
            {
                SanitizeSnapshotCopy(tempPath);
            }
            catch (Exception ex) when (!stationOwned && ex is LiteException or IOException)
            {
                // The db and -log copies are taken independently; a foreign
                // checkpoint landing between them can tear the pair. One
                // bounded recopy converges on a consistent snapshot.
                TryDelete(tempPath);
                TryDelete(tempPath + "-log");
                CopySnapshotFile(sourcePath, tempPath);
                if (File.Exists(sourcePath + "-log"))
                    CopySnapshotFile(sourcePath + "-log", tempPath + "-log");
                SanitizeSnapshotCopy(tempPath);
            }
            return File.ReadAllBytes(tempPath);
        }
        finally
        {
            TryDelete(tempPath);
            TryDelete(tempPath + "-log");
        }
    }

    private static void SanitizeSnapshotCopy(string tempPath)
    {
        using var snapshot = new LiteDatabase(tempPath);
        // PureSignal calibration and arm-related persistence never
        // leave the machine through this backup surface.
        snapshot.DropCollection(PureSignalCollection);
        snapshot.Checkpoint();
    }

    private static void CopySnapshotFile(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
    }

    private static void StageContainer(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var manifestZipEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidOperationException("That file has no Zeus database manifest.");
        BackupManifest manifest;
        using (var stream = manifestZipEntry.Open())
        using (var memory = new MemoryStream())
        {
            CopyBounded(stream, memory, 1024 * 1024, "manifest");
            manifest = ReadManifest(memory.ToArray());
        }

        var pendingDirectory = PendingDirectory();
        var temporaryDirectory = pendingDirectory + $".staging-{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var known = DiscoverStores().Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            long total = 0;
            var stagedEntries = new List<BackupEntry>();
            foreach (var entry in manifest.Entries)
            {
                if (!known.Contains(entry.Key))
                    continue; // forward-compatible: unknown stores are ignored
                var store = DiscoverStores().Single(candidate => candidate.Key == entry.Key);
                if (!string.Equals(entry.Kind, store.Kind, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The '{entry.Key}' entry kind does not match its destination.");
                var zipEntry = archive.GetEntry(entry.ArchivePath)
                    ?? throw new InvalidOperationException(
                        $"The Zeus database backup is missing '{entry.Key}'.");
                if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
                    throw new InvalidOperationException(
                        $"The '{entry.Key}' entry has an invalid size.");
                total = checked(total + entry.Length);
                if (total > MaximumBackupBytes)
                    throw new InvalidOperationException("That Zeus database backup is too large.");

                var outputPath = SafePendingEntryPath(temporaryDirectory, entry.ArchivePath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using (var input = zipEntry.Open())
                using (var output = new FileStream(
                    outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CopyBounded(input, output, entry.Length, entry.Key);
                    if (output.Length != entry.Length)
                        throw new InvalidOperationException(
                            $"The '{entry.Key}' entry length does not match its manifest.");
                    output.Flush(flushToDisk: true);
                }

                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath)))
                    .ToLowerInvariant();
                if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"The '{entry.Key}' entry failed integrity verification.");
                if (entry.Kind == "litedb")
                    SanitizeAndValidateLiteDatabase(outputPath);
                else if (entry.Kind == "json")
                    ValidateJson(outputPath);
                else if (entry.Kind == "file")
                {
                    // Opaque product-owned state. Integrity and size were
                    // already checked above; the owning product validates it
                    // when it opens the restored file.
                }
                else
                    throw new InvalidOperationException(
                        $"The '{entry.Key}' entry has an unsupported kind.");

                // Sanitizing a legacy/custom container may remove ps_settings,
                // so pin the staged bytes rather than retaining the archive's
                // pre-sanitization digest.
                var stagedBytes = File.ReadAllBytes(outputPath);
                stagedEntries.Add(entry with
                {
                    Length = stagedBytes.LongLength,
                    Sha256 = Convert.ToHexString(SHA256.HashData(stagedBytes))
                        .ToLowerInvariant(),
                });
            }

            var stagedKeys = stagedEntries.Select(entry => entry.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (!stagedKeys.Contains("preferences") || !stagedKeys.Contains("station-engine"))
                throw new InvalidOperationException(
                    "That file is missing part of the complete Zeus settings database.");

            WriteManifestAtomically(
                Path.Combine(temporaryDirectory, ManifestEntryName),
                manifest with { Entries = stagedEntries });
            using var restoreLock = TryAcquireRestoreLock(
                pendingDirectory,
                TimeSpan.FromSeconds(30))
                ?? throw new InvalidOperationException(
                    "Zeus is currently applying another database restore; try the import again.");
            RejectIfRollbackPending(pendingDirectory);
            ReplacePendingDirectory(temporaryDirectory, pendingDirectory);
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    private static void StageLegacyLiteDatabase(string path)
    {
        EnsureLegacyPreferencesDatabase(path);
        SanitizeAndValidateLiteDatabase(path);
        var preferencesBytes = File.ReadAllBytes(path);
        var engineBytes = BuildLegacyEngineSnapshot(path);
        var entries = new[]
        {
            CreateBackupEntry("preferences", "data/preferences.db", preferencesBytes),
            CreateBackupEntry("station-engine", "data/station-engine.db", engineBytes),
        };
        var manifest = new BackupManifest(Format, CurrentVersion, DateTime.UtcNow, entries);
        var pendingDirectory = PendingDirectory();
        var temporaryDirectory = pendingDirectory + $".staging-{Guid.NewGuid():N}";
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, "data"));
        try
        {
            File.WriteAllBytes(
                Path.Combine(temporaryDirectory, "data", "preferences.db"), preferencesBytes);
            File.WriteAllBytes(
                Path.Combine(temporaryDirectory, "data", "station-engine.db"), engineBytes);
            WriteManifestAtomically(
                Path.Combine(temporaryDirectory, ManifestEntryName), manifest);
            using var restoreLock = TryAcquireRestoreLock(
                pendingDirectory,
                TimeSpan.FromSeconds(30))
                ?? throw new InvalidOperationException(
                    "Zeus is currently applying another database restore; try the import again.");
            RejectIfRollbackPending(pendingDirectory);
            ReplacePendingDirectory(temporaryDirectory, pendingDirectory);
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    private static BackupEntry CreateBackupEntry(string key, string archivePath, byte[] bytes)
        => new(
            key,
            "litedb",
            archivePath,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static byte[] BuildLegacyEngineSnapshot(string legacyPath)
    {
        var temporaryPath = Path.Combine(
            Path.GetTempPath(), $"zeus-legacy-engine-{Guid.NewGuid():N}.db");
        try
        {
            var currentEnginePath = PrefsDbPath.EngineGet();
            if (File.Exists(currentEnginePath))
                File.WriteAllBytes(
                    temporaryPath,
                    SnapshotLiteDatabase(currentEnginePath, stationOwned: true));
            else
            {
                using var empty = new LiteDatabase(temporaryPath);
                empty.Checkpoint();
            }

            using (var source = new LiteDatabase(legacyPath))
            using (var destination = new LiteDatabase(temporaryPath))
            {
                var sourceNames = source.GetCollectionNames().ToHashSet(StringComparer.Ordinal);
                foreach (var name in EnginePrefsDbMigration.AllEngineOwnedCollectionNames())
                {
                    // Older backups contain only the engine families known to the
                    // version that created them. Preserve newer local families
                    // instead of replacing them with empty collections.
                    if (!sourceNames.Contains(name))
                        continue;
                    var documents = source.GetCollection<BsonDocument>(name)
                        .FindAll()
                        .Select(static document => new BsonDocument(document))
                        .ToList();
                    if (!destination.BeginTrans())
                        throw new InvalidOperationException(
                            $"Could not begin legacy import transaction for '{name}'.");
                    try
                    {
                        var collection = destination.GetCollection<BsonDocument>(name);
                        collection.DeleteAll();
                        if (documents.Count > 0)
                            collection.InsertBulk(documents);
                        destination.Commit();
                    }
                    catch
                    {
                        destination.Rollback();
                        throw;
                    }
                }
                destination.DropCollection(PureSignalCollection);
                destination.Checkpoint();
            }
            return File.ReadAllBytes(temporaryPath);
        }
        finally
        {
            TryDelete(temporaryPath);
            TryDelete(temporaryPath + "-log");
        }
    }

    private static string PrepareEntry(string stagedPath, BackupEntry entry, StoreSpec store)
    {
        if (!string.Equals(entry.Kind, store.Kind, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Staged database entry kind does not match its destination.");
        if (!File.Exists(stagedPath))
            throw new FileNotFoundException("Staged database entry is missing.", stagedPath);
        var bytes = File.ReadAllBytes(stagedPath);
        if (bytes.LongLength != entry.Length
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                entry.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Staged database entry failed integrity verification.");
        }

        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(store.Path))!;
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory, $".{Path.GetFileName(store.Path)}.restore-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            if (entry.Kind == "litedb")
            {
                SanitizeAndValidateLiteDatabase(temporaryPath);
                PreserveLocalPureSignal(store.Path, temporaryPath);
            }
            else if (entry.Kind == "json")
            {
                ValidateJson(temporaryPath);
            }
            else if (entry.Kind != "file")
            {
                throw new InvalidOperationException(
                    "Staged database entry has an unsupported kind.");
            }

            return temporaryPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(temporaryPath + "-log");
            throw;
        }
    }

    private static bool DestinationMatches(BackupEntry entry, StoreSpec store)
    {
        if (!File.Exists(store.Path))
            return false;
        var bytes = File.ReadAllBytes(store.Path);
        return bytes.LongLength == entry.Length
            && string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                entry.Sha256,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateRollback(
        string pendingDirectory,
        IReadOnlyList<PreparedEntry> prepared)
    {
        var rollbackDirectory = Path.Combine(pendingDirectory, RollbackDirectoryName);
        TryDeleteDirectory(rollbackDirectory);
        Directory.CreateDirectory(rollbackDirectory);
        var entries = new List<RollbackEntry>();
        foreach (var item in prepared)
        {
            var databaseBackup = $"{item.Entry.Key}.db.original";
            var logBackup = $"{item.Entry.Key}.db-log.original";
            var databaseExisted = File.Exists(item.Store.Path);
            var logExisted = File.Exists(item.Store.Path + "-log");
            if (databaseExisted)
                CopyFileDurably(item.Store.Path, Path.Combine(rollbackDirectory, databaseBackup));
            if (logExisted)
                CopyFileDurably(item.Store.Path + "-log", Path.Combine(rollbackDirectory, logBackup));
            entries.Add(new RollbackEntry(
                item.Entry.Key,
                Path.GetFullPath(item.Store.Path),
                databaseExisted,
                logExisted,
                databaseBackup,
                logBackup));
        }

        var manifestPath = Path.Combine(rollbackDirectory, RollbackManifestName);
        using var stream = new FileStream(
            manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, entries, Json);
        stream.Flush(flushToDisk: true);
    }

    private static bool RecoverInterruptedRestore(string pendingDirectory)
    {
        var rollbackDirectory = Path.Combine(pendingDirectory, RollbackDirectoryName);
        var manifestPath = Path.Combine(rollbackDirectory, RollbackManifestName);
        if (!File.Exists(manifestPath))
        {
            TryDeleteDirectory(rollbackDirectory);
            return true;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<RollbackEntry>>(
                File.ReadAllBytes(manifestPath), Json)
                ?? throw new InvalidOperationException("The restore rollback manifest is empty.");
            foreach (var entry in entries)
            {
                var destinationDirectory = Path.GetDirectoryName(entry.DestinationPath)
                    ?? throw new InvalidOperationException("A rollback destination has no directory.");
                Directory.CreateDirectory(destinationDirectory);
                RestoreOriginal(
                    entry.DatabaseExisted,
                    Path.Combine(rollbackDirectory, entry.DatabaseBackupPath),
                    entry.DestinationPath);
                RestoreOriginal(
                    entry.LogExisted,
                    Path.Combine(rollbackDirectory, entry.LogBackupPath),
                    entry.DestinationPath + "-log");
            }
            TryDeleteDirectory(rollbackDirectory);
            return true;
        }
        catch (Exception ex)
        {
            ReportRestoreDiagnostic($"database.restore rollback failed: {ex.Message}");
            return false;
        }
    }

    internal static void ReplacePreparedEntryWithRetry(
        string preparedPath,
        string destinationPath,
        Action<int>? beforeAttempt = null,
        Action? waitBetweenAttempts = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                beforeAttempt?.Invoke(attempt);
                DeleteIfExists(destinationPath + "-log");
                File.Move(preparedPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                attempt < 4 && ex is IOException or UnauthorizedAccessException)
            {
                (waitBetweenAttempts ?? WaitForTransientFileRelease)();
            }
        }
    }

    private static void WaitForTransientFileRelease() => Thread.Sleep(100);

    private static void RestoreOriginal(bool existed, string backupPath, string destinationPath)
    {
        if (existed)
        {
            if (!File.Exists(backupPath))
                throw new FileNotFoundException("A restore rollback file is missing.", backupPath);
            CopyFileDurably(backupPath, destinationPath);
        }
        else
        {
            DeleteIfExists(destinationPath);
        }
    }

    private static void CopyFileDurably(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void PreserveLocalPureSignal(string currentPath, string restoredPath)
    {
        if (!File.Exists(currentPath))
            return;
        List<BsonDocument> current;
        try
        {
            using var db = new LiteDatabase(currentPath);
            current = db.GetCollection<BsonDocument>(PureSignalCollection)
                .FindAll()
                .Select(static document => new BsonDocument(document))
                .ToList();
        }
        catch (Exception ex)
        {
            // Never silently discard local calibration state. If it cannot be
            // read, abort the whole restore and leave the current pair intact.
            throw new InvalidOperationException(
                "The current local PureSignal settings could not be preserved.", ex);
        }

        if (current.Count == 0)
            return;
        using var restored = new LiteDatabase(restoredPath);
        var collection = restored.GetCollection<BsonDocument>(PureSignalCollection);
        collection.DeleteAll();
        collection.InsertBulk(current);
        restored.Checkpoint();
    }

    private static void SanitizeAndValidateLiteDatabase(string path)
    {
        try
        {
            using var database = new LiteDatabase(path);
            foreach (var collectionName in database.GetCollectionNames().ToList())
                _ = database.GetCollection(collectionName).FindAll().FirstOrDefault();
            database.DropCollection(PureSignalCollection);
            database.Checkpoint();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "That file is not a valid Zeus database backup.", ex);
        }
    }

    private static void EnsureLegacyPreferencesDatabase(string path)
    {
        try
        {
            using var database = new LiteDatabase(path);
            var names = database.GetCollectionNames().ToHashSet(StringComparer.Ordinal);
            // Accept in two shapes only:
            //  - any product anchor: every real pre-split single-file backup
            //    and every product-family database carries at least one;
            //  - prefs-resident engine anchors (display/theme/layout) with NO
            //    engine-exclusive collection present: a fresh split-era
            //    zeus-prefs.db whose operator has only touched those.
            // A file whose recognized names are engine-exclusive (radio_state,
            // dsp_settings, ...) is station-engine.db or a copy; staging it
            // would replace zeus-prefs.db wholesale and silently revert every
            // product setting, so those stay rejected on purpose. Names alone
            // cannot separate the dual-homed layout collections, hence the
            // engine-exclusive test rather than a simple family overlap.
            var engineExclusive = EnginePrefsDbMigration.AllEngineOwnedCollectionNames()
                .Except(new[] { "ui_layout", "ui_layouts_v2", "ui_saved_layouts", "cfc_presets" },
                    StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            var prefsResidentAnchors = new HashSet<string>(StringComparer.Ordinal)
            {
                "display_settings", "theme_settings", "ui_layout", "ui_layouts_v2",
            };
            var acceptable = names.Overlaps(LegacyProductPreferenceAnchors)
                || (names.Overlaps(prefsResidentAnchors) && !names.Overlaps(engineExclusive));
            if (!acceptable)
            {
                throw new InvalidOperationException(
                    "That LiteDB file is not a Zeus settings database.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "That file is not a valid Zeus settings database.", ex);
        }
    }

    private static void ValidateJson(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var _ = JsonDocument.Parse(stream);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("A JSON store in the backup is invalid.", ex);
        }
    }

    private static BackupManifest ReadManifest(byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<BackupManifest>(bytes, Json)
            ?? throw new InvalidOperationException("The Zeus database manifest is empty.");
        if (!string.Equals(manifest.Format, Format, StringComparison.Ordinal)
            || manifest.Version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported Zeus database backup format/version '{manifest.Format}/{manifest.Version}'.");
        }
        if (manifest.Entries.Count > 32)
            throw new InvalidOperationException("The Zeus database manifest has too many entries.");
        if (manifest.Entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count()
            != manifest.Entries.Count)
            throw new InvalidOperationException("The Zeus database manifest has duplicate entries.");
        foreach (var entry in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key)
                || string.IsNullOrWhiteSpace(entry.ArchivePath)
                || entry.ArchivePath.Contains("..", StringComparison.Ordinal)
                || Path.IsPathRooted(entry.ArchivePath)
                || entry.Sha256.Length != 64)
            {
                throw new InvalidOperationException("The Zeus database manifest contains an invalid entry.");
            }
        }
        return manifest;
    }

    private static string PendingDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(
            PendingRestoreDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        var activeDirectory = Path.GetDirectoryName(Path.GetFullPath(PrefsDbPath.Get()))
            ?? PrefsDbPath.DataDir;
        // Leaf-name check on purpose: ZEUS_PREFS_PATH relocates the prefs file
        // without moving DataDir, so an exact-path comparison against
        // DataDir/profiles misses relocated (and test) profile layouts. A data
        // root literally named "profiles" would be misclassified, but that is
        // a contrived layout and the env override above is the escape hatch.
        var directory = string.Equals(
            Path.GetFileName(activeDirectory),
            "profiles",
            StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(activeDirectory) ?? PrefsDbPath.DataDir
            : activeDirectory;
        return Path.Combine(directory, PendingDirectoryName);
    }

    private static string SafePendingEntryPath(string root, string archivePath)
    {
        var rootFullPath = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(
            rootFullPath,
            archivePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullPath
            : rootFullPath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison()))
            throw new InvalidOperationException("A backup entry escapes the staging directory.");
        return candidate;
    }

    private static void ReplacePendingDirectory(string temporary, string pending)
    {
        var parent = Path.GetDirectoryName(pending);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        if (Directory.Exists(pending))
            Directory.Delete(pending, recursive: true);
        Directory.Move(temporary, pending);
    }

    private static void RejectIfRollbackPending(string pendingDirectory)
    {
        if (File.Exists(Path.Combine(
                pendingDirectory, RollbackDirectoryName, RollbackManifestName)))
        {
            throw new InvalidOperationException(
                "Zeus must restart and finish recovering the previous database import before another backup can be imported.");
        }
    }

    private static FileStream? TryAcquireRestoreLock(
        string pendingDirectory,
        TimeSpan timeout)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(pendingDirectory))
            ?? PrefsDbPath.DataDir;
        Directory.CreateDirectory(parent);
        var lockPath = Path.Combine(parent, ".database-restore.lock");
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                if (wait.Elapsed >= timeout)
                    return null;
                var remaining = timeout - wait.Elapsed;
                Thread.Sleep((int)Math.Min(100, Math.Max(1, remaining.TotalMilliseconds)));
            }
        }
    }

    private static void WriteManifestAtomically(string path, BackupManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void CopyBounded(Stream source, Stream destination, long maximum, string name)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > maximum)
                throw new InvalidOperationException($"The '{name}' entry is larger than declared.");
            destination.Write(buffer, 0, read);
        }
    }

    private static bool IsZip(string path)
    {
        Span<byte> signature = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(signature) == signature.Length
            && signature[0] == (byte)'P'
            && signature[1] == (byte)'K';
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private static void ReportRestoreDiagnostic(string message)
    {
        try { Console.Error.WriteLine(message); } catch { }
        AppendRestoreDiagnostic(message);
    }

    private static void AppendRestoreDiagnostic(string message)
    {
        try
        {
            using var sink = new Diagnostics.DiagnosticLogFileSink(PrefsDbPath.AppLogPath());
            sink.Append($"{DateTime.Now:HH:mm:ss.fff} ERROR DatabaseRestore {message}");
        }
        catch
        {
            // Restore diagnostics are best-effort and must never block startup.
        }
    }
}
