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

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Band stack (issue #179): a collection of operator-pinned named presets per
// band that snapshot (hz, mode, filter). Lives in station-engine.db alongside
// band_memory — not sensitive, and colocated so both survive the same reset.
// Distinct from BandMemoryStore's single automatic last-used slot: entries
// here have a stable Id + Label and are managed only by explicit push/delete.
public sealed class BandStackStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<BandStackEntry> _entries;
    private readonly ILogger<BandStackStore> _log;

    public BandStackStore(ILogger<BandStackStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<BandStackEntry>("band_stack");
        _entries.EnsureIndex(x => x.Band);

        _log.LogInformation("BandStackStore initialized at {Path}", dbPath);
    }

    public IReadOnlyList<BandStackEntryDto> GetAll()
    {
        return _entries
            .FindAll()
            .OrderBy(e => e.Band, StringComparer.Ordinal)
            .ThenBy(e => e.UpdatedUtc)
            .ThenBy(e => e.Id)
            .Select(ToDto)
            .ToArray();
    }

    public IReadOnlyList<BandStackEntryDto> GetForBand(string band)
    {
        return _entries
            .Find(x => x.Band == band)
            .OrderBy(e => e.UpdatedUtc)
            .ThenBy(e => e.Id)
            .Select(ToDto)
            .ToArray();
    }

    public BandStackEntryDto Add(string band, string label, long hz, RxMode mode, int? filterLowHz, int? filterHighHz)
    {
        var entry = new BandStackEntry
        {
            Band = band,
            Label = label,
            Hz = hz,
            Mode = mode,
            FilterLowHz = filterLowHz,
            FilterHighHz = filterHighHz,
            UpdatedUtc = DateTime.UtcNow,
        };
        _entries.Insert(entry);
        return ToDto(entry);
    }

    public bool Delete(string band, int id)
    {
        var e = _entries.FindOne(x => x.Band == band && x.Id == id);
        if (e is null) return false;
        return _entries.Delete(e.Id);
    }

    private static BandStackEntryDto ToDto(BandStackEntry e) => new(
        e.Id,
        e.Band,
        e.Label,
        e.Hz,
        e.Mode,
        e.FilterLowHz,
        e.FilterHighHz,
        new DateTimeOffset(DateTime.SpecifyKind(e.UpdatedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds());

    public void Dispose() => _dbLease.Dispose();
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
