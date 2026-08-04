// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;

namespace Zeus.Server;

/// <summary>
/// One-time, rollback-safe split of engine-owned collections from the product
/// preferences database into station-engine.db. Source collections are never
/// changed or deleted, so an older Zeus build can still use zeus-prefs.db.
/// </summary>
internal static class EnginePrefsDbMigration
{
    internal const string MarkerCollectionName = "engine_persistence_migration";
    internal const string ExternalMarkerSuffix = ".migration-complete";
    internal const string V1MarkerId = "station-engine-v1";
    internal const string PsMarkerId = "station-engine-v2-ps";
    internal const string PsCollectionName = "ps_settings";
    internal const string StationControlMarkerId = "station-engine-v3-tci-cat";
    internal const int CurrentVersion = 3;

    // Every collection the standalone engine owns in station-engine.db that a
    // profile export may merge. This is the v1 engine families + v3 station
    // control — deliberately NOT ps_settings (v2). PureSignal persistence is a
    // full-stop zone: no export-path change that touches PS calibration data
    // ships without explicit maintainer approval, so a merged export keeps whatever
    // ps_settings copy the product file already holds (the stale pre-split
    // one), exactly as the desktop export always did.
    internal static IEnumerable<string> AllEngineOwnedCollectionNames()
    {
        foreach (var name in EngineCollectionNames)
            yield return name;
        foreach (var name in StationControlCollectionNames)
            yield return name;
    }

    internal static readonly IReadOnlyList<string> StationControlCollectionNames =
        Array.AsReadOnly(
        [
            "tci_config",
            "cat_config",
            "cat_serial_config",
        ]);

    internal static readonly IReadOnlyList<string> EngineCollectionNames =
        Array.AsReadOnly(
        [
            "antenna_bands",
            "audio_device_settings",
            "audio_frontend",
            "band_memory",
            "band_plan_overrides",
            "band_plan_prefs",
            "band_stack",
            "board_sample_rates",
            "cw_settings",
            "dsp_settings",
            "filter_presets",
            "hl2_gpio",
            "pa_band_drive",
            "pa_bands",
            "pa_globals",
            "preferred_radio",
            "ptt_settings",
            "radio_speaker_settings",
            "radio_state",
            "rf_filter_settings",
            StationFavoriteStore.CollectionName,
            "tx_fidelity_policy",
        ]);

    /// <summary>
    /// Copy each version's engine collections and write that version's marker
    /// last. Returns false when every version was already complete.
    /// </summary>
    internal static bool RunIfNeeded(
        string productDbPath,
        string engineDbPath,
        Action<int, string>? afterCollectionCopied = null,
        Action<string>? onCompletedSentinelRecovery = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineDbPath);

        var productFullPath = Path.GetFullPath(productDbPath);
        var engineFullPath = Path.GetFullPath(engineDbPath);
        var pathComparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(productFullPath, engineFullPath, pathComparison))
            throw new InvalidOperationException(
                "Product and engine preferences must use different database files.");

        var externalMarkerPath = ExternalMarkerPath(engineFullPath);
        using var engineLease = Zeus.Data.SharedLiteDatabase.Acquire(engineFullPath);
        var engine = engineLease.Database;
        var marker = engine.GetCollection<BsonDocument>(MarkerCollectionName);
        var v1Complete = marker.FindById(V1MarkerId) is not null;
        var psComplete = marker.FindById(PsMarkerId) is not null;
        var stationControlComplete = marker.FindById(StationControlMarkerId) is not null;
        var externalVersion = ReadExternalMarkerVersion(externalMarkerPath);
        var migrated = false;
        var recoveredFromSentinel = false;

        // A v2 marker can only be written after v1. Treat it as stronger
        // completion evidence if a damaged database somehow lost only v1's row.
        if (!v1Complete)
        {
            if (psComplete || stationControlComplete || externalVersion >= 1)
            {
                WriteMarker(engine, marker, V1MarkerId, version: 1);
                recoveredFromSentinel = true;
            }
            else
            {
                CopyCollections(
                    productFullPath,
                    engine,
                    EngineCollectionNames,
                    afterCollectionCopied,
                    callbackIndexOffset: 0);
                WriteMarker(engine, marker, V1MarkerId, version: 1);
                migrated = true;
            }
        }
        WriteExternalMarker(externalMarkerPath, V1MarkerId, version: 1);

        if (!psComplete)
        {
            if (stationControlComplete || externalVersion >= 2)
            {
                WriteMarker(engine, marker, PsMarkerId, version: 2);
                recoveredFromSentinel = true;
            }
            else
            {
                CopyCollections(
                    productFullPath,
                    engine,
                    [PsCollectionName],
                    afterCollectionCopied,
                    callbackIndexOffset: EngineCollectionNames.Count);
                WriteMarker(engine, marker, PsMarkerId, version: 2);
                migrated = true;
            }
        }
        WriteExternalMarker(externalMarkerPath, PsMarkerId, version: 2);

        if (!stationControlComplete)
        {
            if (externalVersion >= 3)
            {
                WriteMarker(engine, marker, StationControlMarkerId, version: 3);
                recoveredFromSentinel = true;
            }
            else
            {
                CopyCollections(
                    productFullPath,
                    engine,
                    StationControlCollectionNames,
                    afterCollectionCopied,
                    callbackIndexOffset: EngineCollectionNames.Count + 1);
                WriteMarker(engine, marker, StationControlMarkerId, version: 3);
                migrated = true;
            }
        }
        WriteExternalMarker(externalMarkerPath, StationControlMarkerId, version: 3);

        if (recoveredFromSentinel)
            onCompletedSentinelRecovery?.Invoke(engineFullPath);
        return migrated;
    }

    private static void CopyCollections(
        string productFullPath,
        LiteDatabase engine,
        IReadOnlyList<string> collectionNames,
        Action<int, string>? afterCollectionCopied,
        int callbackIndexOffset)
    {
        if (!File.Exists(productFullPath))
            return;

        using var productLease = Zeus.Data.SharedLiteDatabase.Acquire(productFullPath);
        var product = productLease.Database;
        for (var index = 0; index < collectionNames.Count; index++)
        {
            var name = collectionNames[index];
            var sourceDocuments = product
                .GetCollection<BsonDocument>(name)
                .FindAll()
                .Select(static document => new BsonDocument(document))
                .ToList();

            ReplaceCollection(engine, name, sourceDocuments);
            afterCollectionCopied?.Invoke(callbackIndexOffset + index + 1, name);
        }
    }

    internal static string ExternalMarkerPath(string engineDbPath) =>
        Path.GetFullPath(engineDbPath) + ExternalMarkerSuffix;

    internal static bool HasExternalMarker(string engineDbPath) =>
        GetExternalMarkerVersion(engineDbPath) > 0;

    internal static int GetExternalMarkerVersion(string engineDbPath) =>
        ReadExternalMarkerVersion(ExternalMarkerPath(engineDbPath));

    /// <summary>
    /// Mark an isolated recovery database complete without replaying the
    /// retained legacy source. Used only after a previously completed engine
    /// database becomes unavailable.
    /// </summary>
    internal static void InitializeEmptyCompleted(string engineDbPath)
        => InitializeEmptyThroughVersion(engineDbPath, CurrentVersion);

    /// <summary>
    /// Seed an isolated recovery database through the last version proven by
    /// the unavailable database's durable sentinel. A following RunIfNeeded
    /// copies only newer collections whose retained product source is still
    /// authoritative.
    /// </summary>
    internal static void InitializeEmptyThroughVersion(string engineDbPath, int version)
    {
        if (version is < 1 or > CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(version));

        var engineFullPath = Path.GetFullPath(engineDbPath);
        using var engineLease = Zeus.Data.SharedLiteDatabase.Acquire(engineFullPath);
        var engine = engineLease.Database;
        var marker = engine.GetCollection<BsonDocument>(MarkerCollectionName);
        WriteMarker(engine, marker, V1MarkerId, version: 1);
        if (version >= 2)
            WriteMarker(engine, marker, PsMarkerId, version: 2);
        if (version >= 3)
            WriteMarker(engine, marker, StationControlMarkerId, version: 3);

        var markerId = version switch
        {
            1 => V1MarkerId,
            2 => PsMarkerId,
            _ => StationControlMarkerId,
        };
        WriteExternalMarker(
            ExternalMarkerPath(engineFullPath),
            markerId,
            version);
    }

    private static void ReplaceCollection(
        LiteDatabase engine,
        string collectionName,
        IReadOnlyCollection<BsonDocument> sourceDocuments)
    {
        if (!engine.BeginTrans())
            throw new InvalidOperationException(
                $"Could not begin engine preferences migration transaction for '{collectionName}'.");

        try
        {
            var destination = engine.GetCollection<BsonDocument>(collectionName);
            destination.DeleteAll();
            if (sourceDocuments.Count > 0)
                destination.InsertBulk(sourceDocuments);
            engine.Commit();
        }
        catch
        {
            engine.Rollback();
            throw;
        }
    }

    private static void WriteMarker(
        LiteDatabase engine,
        ILiteCollection<BsonDocument> marker,
        string markerId,
        int version)
    {
        if (!engine.BeginTrans())
            throw new InvalidOperationException(
                "Could not begin engine preferences migration marker transaction.");

        try
        {
            marker.Upsert(new BsonDocument
            {
                ["_id"] = markerId,
                ["version"] = version,
                ["completed_utc"] = DateTime.UtcNow,
            });
            engine.Commit();
        }
        catch
        {
            engine.Rollback();
            throw;
        }
    }

    private static int ReadExternalMarkerVersion(string path)
    {
        if (!File.Exists(path))
            return 0;

        try
        {
            return File.ReadLines(path).FirstOrDefault() switch
            {
                StationControlMarkerId => 3,
                PsMarkerId => 2,
                _ => 1,
            };
        }
        catch
        {
            // Preserve the v1 safety rule: any durable sentinel proves the
            // original engine split completed, even if its text is unreadable.
            return 1;
        }
    }

    private static void WriteExternalMarker(string path, string markerId, int version)
    {
        if (ReadExternalMarkerVersion(path) >= version)
            return;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.WriteLine(markerId);
                writer.WriteLine(DateTime.UtcNow.ToString("O"));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Best effort cleanup; migration completion still depends on
                // the final sentinel, never on the temporary file.
            }
        }
    }
}
