// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// The five station-wide tuning favorites, kept in the unified memory library
/// of the engine preferences database. Empty slots are synthesized on read
/// rather than stored.
/// </summary>
public sealed class StationFavoriteStore : IDisposable
{
    public const int SlotCount = 5;
    internal const string CollectionName = "station_favorites";

    // The slots are memories of the unified library that carry a favorite
    // slot. The legacy collection above is read once by the memory migration
    // and never written again.
    private readonly StationMemoryStore _memories;
    private readonly ILogger _log;

    public StationFavoriteStore(
        ILogger<StationFavoriteStore> log,
        string? dbPathOverride = null)
    {
        _log = log;
        _memories = new StationMemoryStore(log, dbPathOverride, clock: null);

        log.LogInformation("StationFavoriteStore initialized at {Path}", _memories.DatabasePath);
    }

    public IReadOnlyList<StationFavoriteDto> GetAll() =>
        Enumerable.Range(1, SlotCount)
            .Select(slot => ToDto(slot, _memories.GetByFavoriteSlot(slot)) ?? Empty(slot))
            .ToArray();

    public StationFavoriteDto? Get(int slot)
    {
        ValidateSlot(slot);
        return ToDto(slot, _memories.GetByFavoriteSlot(slot));
    }

    public StationFavoriteDto Upsert(
        int slot,
        long frequencyHz,
        RxMode mode,
        int filterLowHz,
        int filterHighHz,
        FmMemory? fm = null)
    {
        ValidateSlot(slot);
        var memory = _memories.UpsertFavorite(slot, frequencyHz, mode, filterLowHz, filterHighHz, fm);
        return ToDto(slot, memory) ?? Empty(slot);
    }

    public void Clear(int slot)
    {
        ValidateSlot(slot);
        _memories.DeleteFavorite(slot);
    }

    public static bool IsValidSlot(int slot) => slot is >= 1 and <= SlotCount;

    private static void ValidateSlot(int slot)
    {
        if (!IsValidSlot(slot))
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Favorite slot must be from 1 through 5.");
    }

    private static StationFavoriteDto Empty(int slot) =>
        new(slot, null, null, null, null, null);

    // A favorite needs a mode and both filter edges. The memory rules keep
    // them on a slot-mapped memory; a row that lacks one anyway reads as an
    // empty slot so the favorites route cannot fail on stored data.
    private StationFavoriteDto? ToDto(int slot, StationMemoryDto? memory)
    {
        if (memory is null) return null;
        if (!StationMemoryValidator.TryParseMode(memory.Mode, out var mode)
            || memory.FilterLowHz is not { } filterLowHz
            || memory.FilterHighHz is not { } filterHighHz)
        {
            _log.LogWarning(
                "Memory {Id} holds favorite slot {Slot} without a mode or filter; the slot reads as empty",
                memory.Id,
                slot);
            return null;
        }

        return new StationFavoriteDto(
            slot,
            memory.FrequencyHz,
            mode,
            filterLowHz,
            filterHighHz,
            memory.UpdatedUtcMs,
            mode == RxMode.FM ? memory.Fm : null);
    }

    public void Dispose() => _memories.Dispose();
}

public sealed class StationFavoriteEntry
{
    public int Id { get; set; }
    public int Slot { get; set; }
    public long FrequencyHz { get; set; }
    public RxMode Mode { get; set; }
    public int FilterLowHz { get; set; }
    public int FilterHighHz { get; set; }
    public string? FmJson { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
