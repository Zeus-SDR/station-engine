// SPDX-License-Identifier: GPL-2.0-or-later

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Persists the five station-wide tuning favorites in the engine preferences
/// database. Empty slots are synthesized on read rather than stored.
/// </summary>
public sealed class StationFavoriteStore : IDisposable
{
    public const int SlotCount = 5;
    internal const string CollectionName = "station_favorites";

    private readonly object _gate = new();
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<StationFavoriteEntry> _entries;

    public StationFavoriteStore(
        ILogger<StationFavoriteStore> log,
        string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _entries = _dbLease.Database.GetCollection<StationFavoriteEntry>(CollectionName);
        RemoveOutOfRangeAndDuplicateRows(log);
        _entries.EnsureIndex(entry => entry.Slot, unique: true);

        log.LogInformation("StationFavoriteStore initialized at {Path}", dbPath);
    }

    public IReadOnlyList<StationFavoriteDto> GetAll()
    {
        lock (_gate)
        {
            var bySlot = _entries.FindAll().ToDictionary(entry => entry.Slot);
            return Enumerable.Range(1, SlotCount)
                .Select(slot => bySlot.TryGetValue(slot, out var entry)
                    ? ToDto(entry)
                    : Empty(slot))
                .ToArray();
        }
    }

    public StationFavoriteDto Upsert(
        int slot,
        long frequencyHz,
        RxMode mode,
        int filterLowHz,
        int filterHighHz)
    {
        ValidateSlot(slot);

        lock (_gate)
        {
            var entry = _entries.FindOne(candidate => candidate.Slot == slot)
                ?? new StationFavoriteEntry { Slot = slot };
            entry.FrequencyHz = frequencyHz;
            entry.Mode = mode;
            entry.FilterLowHz = filterLowHz;
            entry.FilterHighHz = filterHighHz;
            entry.UpdatedUtc = DateTime.UtcNow;

            if (entry.Id == 0)
                _entries.Insert(entry);
            else
                _entries.Update(entry);

            return ToDto(entry);
        }
    }

    public void Clear(int slot)
    {
        ValidateSlot(slot);
        lock (_gate)
            _entries.DeleteMany(entry => entry.Slot == slot);
    }

    public static bool IsValidSlot(int slot) => slot is >= 1 and <= SlotCount;

    private static void ValidateSlot(int slot)
    {
        if (!IsValidSlot(slot))
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Favorite slot must be from 1 through 5.");
    }

    private void RemoveOutOfRangeAndDuplicateRows(ILogger log)
    {
        var rows = _entries.FindAll().ToArray();
        foreach (var row in rows.Where(row => !IsValidSlot(row.Slot)))
        {
            _entries.Delete(row.Id);
            log.LogWarning("Removed invalid station favorite row {Id} for slot {Slot}", row.Id, row.Slot);
        }

        foreach (var group in rows
            .Where(row => IsValidSlot(row.Slot))
            .GroupBy(row => row.Slot)
            .Where(group => group.Count() > 1))
        {
            var keep = group.OrderByDescending(row => row.UpdatedUtc).ThenByDescending(row => row.Id).First();
            foreach (var duplicate in group.Where(row => row.Id != keep.Id))
                _entries.Delete(duplicate.Id);
            log.LogWarning("Collapsed duplicate station favorite rows for slot {Slot}", group.Key);
        }
    }

    private static StationFavoriteDto Empty(int slot) =>
        new(slot, null, null, null, null, null);

    private static StationFavoriteDto ToDto(StationFavoriteEntry entry) => new(
        entry.Slot,
        entry.FrequencyHz,
        entry.Mode,
        entry.FilterLowHz,
        entry.FilterHighHz,
        new DateTimeOffset(DateTime.SpecifyKind(entry.UpdatedUtc, DateTimeKind.Utc))
            .ToUnixTimeMilliseconds());

    public void Dispose() => _dbLease.Dispose();
}

public sealed class StationFavoriteEntry
{
    public int Id { get; set; }
    public int Slot { get; set; }
    public long FrequencyHz { get; set; }
    public RxMode Mode { get; set; }
    public int FilterLowHz { get; set; }
    public int FilterHighHz { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
