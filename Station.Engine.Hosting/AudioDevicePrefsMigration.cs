// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;

namespace Zeus.Server;

/// <summary>
/// One-time, destination-wins migration of the product's audio-device
/// selection into the standalone station-engine preferences database.
/// </summary>
internal static class AudioDevicePrefsMigration
{
    internal const string CollectionName = "audio_device_settings";
    internal const string MarkerCollectionName = "engine_persistence_migration";
    internal const string MarkerId = "station-engine-audio-device-v1";
    internal const string ExternalMarkerSuffix = ".audio-device-migration-complete";

    /// <summary>
    /// Copies the retained product selection only when the engine collection
    /// is empty and this migration has never completed. The source is never
    /// changed. Returns true only when documents were copied.
    /// </summary>
    internal static bool RunIfNeeded(string productDbPath, string engineDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineDbPath);

        var productFullPath = Path.GetFullPath(productDbPath);
        var engineFullPath = Path.GetFullPath(engineDbPath);
        var pathComparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(productFullPath, engineFullPath, pathComparison))
        {
            throw new InvalidOperationException(
                "Product and engine preferences must use different database files.");
        }

        var externalMarkerPath = ExternalMarkerPath(engineFullPath);
        using var engineLease = Zeus.Data.SharedLiteDatabase.Acquire(engineFullPath);
        var engine = engineLease.Database;
        var markers = engine.GetCollection<BsonDocument>(MarkerCollectionName);

        if (markers.FindById(MarkerId) is not null)
        {
            WriteExternalMarker(externalMarkerPath);
            return false;
        }

        // The sibling sentinel survives a corrupt or moved-aside database. It
        // prevents the retained, now-stale product copy from replaying into a
        // replacement engine database.
        if (File.Exists(externalMarkerPath))
        {
            WriteInternalMarker(engine, markers);
            return false;
        }

        var destination = engine.GetCollection<BsonDocument>(CollectionName);
        var copied = false;
        if (destination.Count() == 0 && File.Exists(productFullPath))
        {
            using var productLease = Zeus.Data.SharedLiteDatabase.Acquire(productFullPath);
            var sourceDocuments = productLease.Database
                .GetCollection<BsonDocument>(CollectionName)
                .FindAll()
                .Select(static document => new BsonDocument(document))
                .ToList();

            if (sourceDocuments.Count > 0)
                copied = CopyIfDestinationEmpty(engine, sourceDocuments);
        }

        WriteInternalMarker(engine, markers);
        WriteExternalMarker(externalMarkerPath);
        return copied;
    }

    internal static string ExternalMarkerPath(string engineDbPath) =>
        Path.GetFullPath(engineDbPath) + ExternalMarkerSuffix;

    internal static bool HasExternalMarker(string engineDbPath) =>
        File.Exists(ExternalMarkerPath(engineDbPath));

    private static bool CopyIfDestinationEmpty(
        LiteDatabase engine,
        IReadOnlyCollection<BsonDocument> sourceDocuments)
    {
        if (!engine.BeginTrans())
        {
            throw new InvalidOperationException(
                "Could not begin audio-device preferences migration transaction.");
        }

        try
        {
            var destination = engine.GetCollection<BsonDocument>(CollectionName);
            if (destination.Count() > 0)
            {
                engine.Commit();
                return false;
            }

            destination.InsertBulk(sourceDocuments);
            engine.Commit();
            return true;
        }
        catch
        {
            engine.Rollback();
            throw;
        }
    }

    private static void WriteInternalMarker(
        LiteDatabase engine,
        ILiteCollection<BsonDocument> markers)
    {
        if (!engine.BeginTrans())
        {
            throw new InvalidOperationException(
                "Could not begin audio-device preferences migration marker transaction.");
        }

        try
        {
            markers.Upsert(new BsonDocument
            {
                ["_id"] = MarkerId,
                ["version"] = 1,
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

    private static void WriteExternalMarker(string path)
    {
        if (File.Exists(path))
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
                writer.WriteLine(MarkerId);
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
                // Migration completion depends on the final sentinel, not
                // best-effort cleanup of a failed temporary write.
            }
        }
    }
}
