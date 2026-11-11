// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Globalization;
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Copies the legacy favorite slots and band-stack entries into the unified
/// memory library. The legacy collections are only ever read, so an older
/// Zeus build still finds its own data. Stable ids
/// (<c>legacy_favorite_&lt;slot&gt;</c>, <c>legacy_band_stack_&lt;id&gt;</c>)
/// make a retry update the rows it already wrote instead of duplicating them.
/// </summary>
internal static class StationMemoryMigration
{
    // Written by a legacy database import that replaced one of the two
    // collections: the next store open makes that collection the truth for
    // its surface again, including the slots and entries it no longer has.
    internal const string PendingFavoritesMarkerId = StationMemoryStore.MigrationMarkerId + "-pending-favorites";
    internal const string PendingBandStackMarkerId = StationMemoryStore.MigrationMarkerId + "-pending-band-stack";

    internal const string BandStackCollectionName = "band_stack";
    private const int MarkerVersion = 1;

    /// <summary>
    /// Runs whatever the marker rows ask for and returns true when it wrote
    /// anything. Memory rows commit first; the marker rows change in a second
    /// transaction, so a run that dies in between is simply repeated.
    /// Legacy-shaped data never makes this throw: a row that cannot become a
    /// memory is skipped and logged.
    /// </summary>
    internal static bool Run(LiteDatabase db, ILogger log, Action? afterRowsCommitted = null)
    {
        var markers = db.GetCollection<BsonDocument>(StationMemoryStore.MigrationCollectionName);
        var firstRun = markers.FindById(StationMemoryStore.MigrationMarkerId) is null;
        var favoritesPending = markers.FindById(PendingFavoritesMarkerId) is not null;
        var bandStackPending = markers.FindById(PendingBandStackMarkerId) is not null;
        if (!firstRun && !favoritesPending && !bandStackPending)
            return false;

        var memories = db.GetCollection<StationMemoryDocument>(StationMemoryStore.CollectionName);
        var links = db.GetCollection<StationMemoryLinkDocument>(StationMemoryStore.LinksCollectionName);
        var favorites = firstRun || favoritesPending ? ReadFavorites(db, log) : null;
        var bandStack = firstRun || bandStackPending ? ReadBandStack(db, log) : null;

        InTransaction(db, () =>
        {
            if (favorites is not null)
                Apply(memories, links, favorites, favoritesPending, static memory => memory.FavoriteSlot is not null, log);
            if (bandStack is not null)
                Apply(memories, links, bandStack, bandStackPending, static memory => memory.BandStackId is not null, log);
        });
        afterRowsCommitted?.Invoke();

        InTransaction(db, () =>
        {
            if (firstRun)
            {
                markers.Upsert(new BsonDocument
                {
                    ["_id"] = StationMemoryStore.MigrationMarkerId,
                    ["version"] = MarkerVersion,
                    ["completed_utc"] = DateTime.UtcNow,
                });
            }
            markers.Delete(PendingFavoritesMarkerId);
            markers.Delete(PendingBandStackMarkerId);
        });

        log.LogInformation(
            "Memory migration finished from {Favorites} favorite row(s) and {BandStack} band stack row(s)",
            favorites?.Count ?? 0,
            bandStack?.Count ?? 0);
        return true;
    }

    internal static string FavoriteMemoryId(int slot) =>
        StationMemorySchema.LegacyFavoriteIdPrefix + slot.ToString(CultureInfo.InvariantCulture);

    internal static string BandStackMemoryId(int bandStackId) =>
        StationMemorySchema.LegacyBandStackIdPrefix + bandStackId.ToString(CultureInfo.InvariantCulture);

    internal static string FavoriteName(int slot) =>
        "Favorite " + slot.ToString(CultureInfo.InvariantCulture);

    /// <summary>The label without control characters, cut to the name limit;
    /// the band name when that leaves nothing.</summary>
    internal static string BandStackName(string? label, string? band)
    {
        var name = Clean(label, StationMemorySchema.MaxNameLength);
        if (name.Length == 0) name = Clean(band, StationMemorySchema.MaxNameLength);
        return name.Length == 0 ? "Band stack" : name;
    }

    internal static string BandStackGroup(string? band) =>
        Clean(StationMemorySchema.BandStackGroupPrefix + band, StationMemorySchema.MaxGroupLength);

    private static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var clean = new string(text.Where(static c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > maxLength ? clean[..maxLength].TrimEnd() : clean;
    }

    private static List<StationMemoryDocument> ReadFavorites(LiteDatabase db, ILogger log)
    {
        var rows = ReadLegacy<StationFavoriteEntry>(db, StationFavoriteStore.CollectionName, log);
        var result = new List<StationMemoryDocument>();
        // A duplicated slot keeps its newest row, as the favorite store did.
        foreach (var entry in rows
                     .GroupBy(static row => row.Slot)
                     .OrderBy(static group => group.Key)
                     .Select(static group => group
                         .OrderByDescending(static row => row.UpdatedUtc)
                         .ThenByDescending(static row => row.Id)
                         .First()))
        {
            if (!StationFavoriteStore.IsValidSlot(entry.Slot)
                || !StationMemoryValidator.IsSafeFrequency(entry.FrequencyHz)
                || !Enum.IsDefined(entry.Mode)
                || entry.FilterLowHz >= entry.FilterHighHz)
            {
                log.LogWarning(
                    "Skipped legacy favorite row {Id} (slot {Slot}): it cannot be stored as a memory",
                    entry.Id,
                    entry.Slot);
                continue;
            }

            FmMemory? fm = null;
            if (entry.Mode == RxMode.FM && !string.IsNullOrWhiteSpace(entry.FmJson))
            {
                fm = StationMemoryJson.Read<FmMemory>(entry.FmJson);
                if (fm is null)
                    log.LogWarning("Legacy favorite row {Id} has an unreadable FM memory; it is left out", entry.Id);
                else
                    fm = FmMemories.Normalize(fm, entry.FrequencyHz);
            }

            var utcMs = UtcMs(entry.UpdatedUtc);
            result.Add(new StationMemoryDocument
            {
                Id = FavoriteMemoryId(entry.Slot),
                Name = FavoriteName(entry.Slot),
                Group = StationMemorySchema.FavoritesGroup,
                Source = StationMemorySources.Favorite,
                FavoriteSlot = entry.Slot,
                FrequencyHz = entry.FrequencyHz,
                Mode = entry.Mode.ToString(),
                FilterLowHz = entry.FilterLowHz,
                FilterHighHz = entry.FilterHighHz,
                FmJson = StationMemoryJson.Write(fm),
                CreatedUtcMs = utcMs,
                UpdatedUtcMs = utcMs,
                Revision = 1,
            });
        }
        return result;
    }

    private static List<StationMemoryDocument> ReadBandStack(LiteDatabase db, ILogger log)
    {
        var result = new List<StationMemoryDocument>();
        foreach (var entry in ReadLegacy<BandStackEntry>(db, BandStackCollectionName, log)
                     .OrderBy(static row => row.Id))
        {
            if (entry.Id < 1
                || !StationMemoryValidator.IsSafeFrequency(entry.Hz)
                || !Enum.IsDefined(entry.Mode))
            {
                log.LogWarning("Skipped legacy band stack row {Id}: it cannot be stored as a memory", entry.Id);
                continue;
            }

            // The band-stack route stored the two edges independently, so a
            // lone, equal or inverted pair is possible; it becomes no filter.
            var hasFilter = entry.FilterLowHz is not null
                && StationMemoryValidator.IsFilterPair(entry.FilterLowHz, entry.FilterHighHz);
            if (!hasFilter && (entry.FilterLowHz is not null || entry.FilterHighHz is not null))
                log.LogWarning("Legacy band stack row {Id} has an unusable filter pair; it is stored without one", entry.Id);
            var name = BandStackName(entry.Label, entry.Band);
            if (!string.Equals(name, entry.Label, StringComparison.Ordinal))
                log.LogInformation("Legacy band stack row {Id} is stored under a cleaned name", entry.Id);

            var utcMs = UtcMs(entry.UpdatedUtc);
            result.Add(new StationMemoryDocument
            {
                Id = BandStackMemoryId(entry.Id),
                Name = name,
                Group = BandStackGroup(entry.Band),
                Source = StationMemorySources.BandStack,
                BandStackId = entry.Id,
                LegacyBand = entry.Band ?? string.Empty,
                FrequencyHz = entry.Hz,
                Mode = entry.Mode.ToString(),
                FilterLowHz = hasFilter ? entry.FilterLowHz : null,
                FilterHighHz = hasFilter ? entry.FilterHighHz : null,
                CreatedUtcMs = utcMs,
                UpdatedUtcMs = utcMs,
                Revision = 1,
            });
        }
        return result;
    }

    // Rows are mapped one at a time so a single document an older build left
    // in an unexpected shape costs that row only, never the store.
    private static List<T> ReadLegacy<T>(LiteDatabase db, string collectionName, ILogger log)
    {
        var rows = new List<T>();
        if (!db.CollectionExists(collectionName))
            return rows;
        foreach (var document in db.GetCollection<BsonDocument>(collectionName).FindAll())
        {
            try
            {
                rows.Add(db.Mapper.ToObject<T>(document));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogWarning(
                    ex,
                    "Skipped unreadable row {Id} of legacy collection {Collection}",
                    document["_id"].ToString(),
                    collectionName);
            }
        }
        return rows;
    }

    private static void Apply(
        ILiteCollection<StationMemoryDocument> memories,
        ILiteCollection<StationMemoryLinkDocument> links,
        List<StationMemoryDocument> rows,
        bool replace,
        Func<StationMemoryDocument, bool> isMapped,
        ILogger log)
    {
        if (replace)
        {
            // The imported collection is the truth for its surface: a mapped
            // memory whose legacy row is gone goes with it, links included.
            var kept = rows.Select(static row => row.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in memories.FindAll().Where(memory => isMapped(memory) && !kept.Contains(memory.Id)).ToList())
            {
                var staleId = stale.Id;
                links.DeleteMany(link => link.MemoryId == staleId);
                memories.Delete(staleId);
                log.LogInformation("Removed memory {Id}: its legacy row is no longer present", staleId);
            }
        }

        foreach (var row in rows)
        {
            var existing = memories.FindById(row.Id);
            if (existing is null)
            {
                if (Holder(memories, row) is { } holder)
                {
                    log.LogWarning(
                        "Skipped legacy row for memory {Id}: memory {Holder} already holds its slot",
                        row.Id,
                        holder.Id);
                    continue;
                }
                // Favorites take CAT channels 1 to 5 while those are free.
                if (row.FavoriteSlot is { } slot
                    && memories.FindOne(Query.EQ(nameof(StationMemoryDocument.CatChannel), slot)) is null)
                    row.CatChannel = slot;
                memories.Insert(row);
                continue;
            }

            if (Same(existing, row))
                continue;

            // Notes, assignments, links and everything else the legacy row
            // never carried stay as they are.
            existing.Name = row.Name;
            existing.Group = row.Group;
            existing.FrequencyHz = row.FrequencyHz;
            existing.Mode = row.Mode;
            existing.FilterLowHz = row.FilterLowHz;
            existing.FilterHighHz = row.FilterHighHz;
            existing.FmJson = row.FmJson;
            existing.FavoriteSlot = row.FavoriteSlot;
            existing.BandStackId = row.BandStackId;
            existing.LegacyBand = row.LegacyBand;
            // The band stack is ordered by creation time, so a re-imported
            // entry takes its place in the imported order.
            if (row.BandStackId is not null)
                existing.CreatedUtcMs = row.CreatedUtcMs;
            existing.UpdatedUtcMs = row.UpdatedUtcMs;
            existing.Revision++;
            memories.Update(existing);
            log.LogInformation("Updated memory {Id} from its legacy row", existing.Id);
        }
    }

    private static StationMemoryDocument? Holder(
        ILiteCollection<StationMemoryDocument> memories,
        StationMemoryDocument row) =>
        row.FavoriteSlot is { } slot
            ? memories.FindOne(Query.EQ(nameof(StationMemoryDocument.FavoriteSlot), slot))
            : row.BandStackId is { } bandStackId
                ? memories.FindOne(Query.EQ(nameof(StationMemoryDocument.BandStackId), bandStackId))
                : null;

    private static bool Same(StationMemoryDocument existing, StationMemoryDocument row) =>
        string.Equals(existing.Name, row.Name, StringComparison.Ordinal)
        && string.Equals(existing.Group, row.Group, StringComparison.Ordinal)
        && existing.FrequencyHz == row.FrequencyHz
        && string.Equals(existing.Mode, row.Mode, StringComparison.Ordinal)
        && existing.FilterLowHz == row.FilterLowHz
        && existing.FilterHighHz == row.FilterHighHz
        && Equals(StationMemoryJson.Read<FmMemory>(existing.FmJson), StationMemoryJson.Read<FmMemory>(row.FmJson))
        && existing.FavoriteSlot == row.FavoriteSlot
        && existing.BandStackId == row.BandStackId
        && string.Equals(existing.LegacyBand, row.LegacyBand, StringComparison.Ordinal)
        && (row.BandStackId is null || existing.CreatedUtcMs == row.CreatedUtcMs);

    // LiteDB hands a stored UTC date back as local time. The memory keeps the
    // real instant: the band stack is ordered by these numbers, and an entry
    // added after the migration is stamped from the UTC clock.
    private static long UtcMs(DateTime updatedUtc)
    {
        var utc = updatedUtc.Kind == DateTimeKind.Local
            ? updatedUtc.ToUniversalTime()
            : DateTime.SpecifyKind(updatedUtc, DateTimeKind.Utc);
        return Math.Max(0, new DateTimeOffset(utc).ToUnixTimeMilliseconds());
    }

    private static void InTransaction(LiteDatabase db, Action work)
    {
        if (!db.BeginTrans())
            throw new InvalidOperationException("Could not begin the memory migration transaction.");
        try
        {
            work();
            db.Commit();
        }
        catch
        {
            db.Rollback();
            throw;
        }
    }
}
