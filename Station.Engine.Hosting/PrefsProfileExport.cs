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
/// Builds the byte payload served by /api/prefs/databases/export.
///
/// Since the engine-persistence split, the operator's settings live in TWO
/// files per profile: the product prefs DB (profiles/X.db) and its engine
/// companion (profiles/X-station-engine.db). The split copies engine-owned
/// collections OUT of the product file but never deletes the sources, so the
/// product file keeps only the stale pre-split copies of collections like
/// radio_state, pa_bands, or filter_presets. Exporting the product file alone
/// would silently hand the operator a backup whose engine settings are frozen
/// at the moment their install first ran the split.
///
/// The merged snapshot keeps the single-file .zeusdb format (raw LiteDB) that
/// Import already accepts: start from the product file's bytes, then replace
/// every mergeable engine-owned collection with the CURRENT contents of the
/// profile's engine database. On import + activation the standard migration
/// (EnginePrefsDbMigration) replays those collections into the fresh engine
/// database, so a merged export round-trips on both the desktop host and the
/// standalone station engine. Product-only collections pass through untouched.
/// PureSignal calibration (ps_settings) is deliberately NOT merged — PS
/// persistence is a full-stop zone, so the export carries whatever copy the
/// product file already holds, exactly as it always has.
/// </summary>
internal static class PrefsProfileExport
{
    /// <summary>
    /// Returns the profile's export bytes. <paramref name="engineDbPath"/> may
    /// be null or name a missing file (a profile created but never activated);
    /// in that case the product bytes are returned unchanged.
    /// </summary>
    internal static byte[] BuildMergedSnapshot(string productDbPath, string? engineDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productDbPath);

        byte[] productBytes;
        // Flush the live product DB's WAL through the shared registry first
        // (when no store holds the file this lease briefly opens and closes
        // it), so the raw byte copy captures a checkpoint-consistent image
        // instead of risking a checkpoint landing mid-copy. The residual
        // window — a store committing during the copy itself — is the same
        // one the export has always had and is caught loudly when the merged
        // copy is opened below.
        using (var productLease = Zeus.Data.SharedLiteDatabase.Acquire(productDbPath))
        {
            productLease.Database.Checkpoint();
            using var fs = new FileStream(
                productDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            productBytes = ms.ToArray();
        }

        if (string.IsNullOrWhiteSpace(engineDbPath) || !File.Exists(engineDbPath))
            return productBytes;

        // Merge on a private temp copy: the live product file stays untouched
        // and the temp file is exclusively ours, so no shared-connection or
        // lock ordering concerns apply to it.
        var tempPath = Path.Combine(
            Path.GetTempPath(), $"zeus-profile-export-{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllBytes(tempPath, productBytes);

            // The engine DB may be live (exporting the active profile); lease
            // it through the shared registry exactly like the stores do.
            using (var engineLease = Zeus.Data.SharedLiteDatabase.Acquire(engineDbPath))
            {
                var engine = engineLease.Database;
                using (var snapshot = new LiteDatabase(tempPath))
                {
                    foreach (var name in EnginePrefsDbMigration.AllEngineOwnedCollectionNames())
                    {
                        var documents = engine
                            .GetCollection<BsonDocument>(name)
                            .FindAll()
                            .Select(static document => new BsonDocument(document))
                            .ToList();

                        if (!snapshot.BeginTrans())
                            throw new InvalidOperationException(
                                $"Could not begin profile export transaction for '{name}'.");
                        try
                        {
                            var destination = snapshot.GetCollection<BsonDocument>(name);
                            destination.DeleteAll();
                            if (documents.Count > 0)
                                destination.InsertBulk(documents);
                            snapshot.Commit();
                        }
                        catch
                        {
                            snapshot.Rollback();
                            throw;
                        }
                    }

                    // Flush so the bytes read below are the complete database,
                    // not a checkpoint short of the collections we just wrote.
                    snapshot.Checkpoint();
                }
            }

            return File.ReadAllBytes(tempPath);
        }
        finally
        {
            TryDelete(tempPath);
            TryDelete(tempPath + "-log");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort; the temp directory is the OS's to clean.
        }
    }
}
