// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using Zeus.Contracts;

namespace Zeus.Server;

// Band stack (issue #179): a collection of operator-pinned named presets per
// band that snapshot (hz, mode, filter). Lives in station-engine.db alongside
// band_memory — not sensitive, and colocated so both survive the same reset.
// Distinct from BandMemoryStore's single automatic last-used slot: entries
// here have a stable Id + Label and are managed only by explicit push/delete.
public sealed class BandStackStore : IDisposable
{
    // Entries are memories of the unified library that carry a band-stack id.
    // The legacy band_stack collection is read once by the memory migration
    // and never written again.
    private readonly StationMemoryStore _memories;
    private readonly ILogger<BandStackStore> _log;

    public BandStackStore(ILogger<BandStackStore> log, string? dbPathOverride = null)
    {
        _log = log;
        _memories = new StationMemoryStore(log, dbPathOverride, clock: null);

        _log.LogInformation("BandStackStore initialized at {Path}", _memories.DatabasePath);
    }

    public IReadOnlyList<BandStackEntryDto> GetAll() => ToDtos(_memories.GetBandStack());

    public IReadOnlyList<BandStackEntryDto> GetForBand(string band) => ToDtos(_memories.GetBandStack(band));

    public BandStackEntryDto Add(string band, string label, long hz, RxMode mode, int? filterLowHz, int? filterHighHz)
    {
        // The returned entry shows what was stored, which for a label with
        // control characters or an unusable filter pair is the cleaned form.
        return ToDtos([_memories.AddBandStack(band, label, hz, mode, filterLowHz, filterHighHz)])[0];
    }

    public bool Delete(string band, int id) => _memories.DeleteBandStack(band, id);

    // BandStackEntryDto.Mode is not nullable. The memory rules keep a mode on
    // a band-stack memory; a row that lacks one anyway is left out so the
    // band-stack route cannot fail on stored data.
    private BandStackEntryDto[] ToDtos(IReadOnlyList<StationMemoryDto> memories)
    {
        var entries = new List<BandStackEntryDto>(memories.Count);
        foreach (var memory in memories)
        {
            if (memory.Legacy is not { BandStackId: { } id }
                || !StationMemoryValidator.TryParseMode(memory.Mode, out var mode))
            {
                _log.LogWarning("Memory {Id} is a band stack entry without a mode; it is left out", memory.Id);
                continue;
            }

            entries.Add(new BandStackEntryDto(
                id,
                memory.Legacy.Band ?? string.Empty,
                memory.Name,
                memory.FrequencyHz,
                mode,
                memory.FilterLowHz,
                memory.FilterHighHz,
                memory.CreatedUtcMs));
        }
        return entries.ToArray();
    }

    public void Dispose() => _memories.Dispose();
}

public sealed class BandStackEntry
{
    public int Id { get; set; }
    public string Band { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public long Hz { get; set; }
    public RxMode Mode { get; set; }
    public int? FilterLowHz { get; set; }
    public int? FilterHighHz { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
