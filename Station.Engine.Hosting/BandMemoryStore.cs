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
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Per-band last-used (hz, mode). Lives in the unencrypted station-engine.db
// beside the product preferences database.
public sealed class BandMemoryStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<BandMemoryEntry> _entries;
    private readonly ILogger<BandMemoryStore> _log;

    public BandMemoryStore(ILogger<BandMemoryStore> log, string? dbPathOverride = null)
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
        _entries = _db.GetCollection<BandMemoryEntry>("band_memory");
        // Pre-existing rows can violate the unique-Band invariant if they were
        // written by a build before EnsureIndex(unique:true) was added, or by a
        // race in Upsert before the index existed. Build will fail with
        // "duplicate key" and every subsequent request 500s on construction —
        // self-heal by collapsing duplicates (newest UpdatedUtc wins) before
        // asking LiteDB to enforce uniqueness.
        DedupeByBand();
        _entries.EnsureIndex(x => x.Band, unique: true);

        _log.LogInformation("BandMemoryStore initialized at {Path}", dbPath);
    }

    private void DedupeByBand()
    {
        var dupGroups = _entries.FindAll()
            .GroupBy(e => e.Band, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var g in dupGroups)
        {
            var keeper = g.OrderByDescending(e => e.UpdatedUtc).First();
            var removed = 0;
            foreach (var dup in g)
            {
                if (dup.Id == keeper.Id) continue;
                _entries.Delete(dup.Id);
                removed++;
            }
            _log.LogWarning(
                "BandMemoryStore: collapsed {Removed} duplicate row(s) for band {Band}; kept Id={KeptId} (UpdatedUtc={Updated:o})",
                removed, g.Key, keeper.Id, keeper.UpdatedUtc);
        }
    }

    public IReadOnlyList<BandMemoryDto> GetAll()
    {
        return _entries
            .FindAll()
            // SetZoom may create a zoom-only row. It is not a frequency/mode
            // memory and must stay invisible to band-memory recall readers.
            .Where(e => e.Hz > 0)
            .Select(ToDto)
            .ToArray();
    }

    public BandMemoryDto? Get(string band)
    {
        var e = _entries.FindOne(x => x.Band == band);
        // Zoom-only rows have Hz=0 and no meaningful mode. Treat them as absent
        // so RestoreBandMode cannot interpret the default enum value as LSB.
        return e is null || e.Hz <= 0 ? null : ToDto(e);
    }

    private static BandMemoryDto ToDto(BandMemoryEntry e) =>
        new(e.Band, e.Hz, e.Mode, e.FilterLowHz, e.FilterHighHz, e.FilterMode);

    // Read the per-band scope zoom level (#128). Null when the operator has not
    // touched zoom while on that band yet, so RadioService.RestoreBandZoom leaves
    // the current slider alone rather than snapping to a default on first visit.
    public int? GetZoom(string band)
    {
        var e = _entries.FindOne(x => x.Band == band);
        return e?.ZoomLevel;
    }

    // Persist the per-band scope zoom level (#128). Isolated from Upsert so the
    // ZOOM slider write-back does not touch the last-used Hz/Mode row managed by
    // /api/bands/memory. Creates an entry with the current default Hz=0/Mode=0
    // when nothing exists yet. Get()/GetAll() deliberately ignore those Hz=0
    // rows for frequency/mode recall, while GetZoom still reads them.
    public void SetZoom(string band, int level)
    {
        var existing = _entries.FindOne(x => x.Band == band);
        if (existing is null)
        {
            try
            {
                _entries.Insert(new BandMemoryEntry
                {
                    Band = band,
                    ZoomLevel = level,
                    UpdatedUtc = DateTime.UtcNow,
                });
                return;
            }
            catch (LiteException ex) when (ex.ErrorCode == LiteException.INDEX_DUPLICATE_KEY)
            {
                existing = _entries.FindOne(x => x.Band == band);
                if (existing is null) throw;
            }
        }

        existing.ZoomLevel = level;
        existing.UpdatedUtc = DateTime.UtcNow;
        _entries.Update(existing);
    }

    public void Upsert(
        string band,
        long hz,
        RxMode mode,
        int? filterLowHz = null,
        int? filterHighHz = null,
        RxMode? filterMode = null)
    {
        var hasFilterPayload = filterLowHz is not null && filterHighHz is not null;
        var existing = _entries.FindOne(x => x.Band == band);
        if (existing is null)
        {
            // Concurrent PUTs for the same band (debounced batches, StrictMode
            // double-effects) can both observe FindOne == null and race into
            // Insert; the unique-Band index then trips one of them. Catch the
            // collision and fall through to the update path with a re-fetch.
            try
            {
                _entries.Insert(new BandMemoryEntry
                {
                    Band = band,
                    Hz = hz,
                    Mode = mode,
                    FilterLowHz = hasFilterPayload ? filterLowHz : null,
                    FilterHighHz = hasFilterPayload ? filterHighHz : null,
                    FilterMode = hasFilterPayload ? filterMode : null,
                    UpdatedUtc = DateTime.UtcNow,
                });
                return;
            }
            catch (LiteException ex) when (ex.ErrorCode == LiteException.INDEX_DUPLICATE_KEY)
            {
                existing = _entries.FindOne(x => x.Band == band);
                if (existing is null) throw;
            }
        }

        existing.Hz = hz;
        existing.Mode = mode;
        // Absent filter fields ALWAYS preserve the previously-stored filter
        // tuple — including on a mode change. The tuple carries its own
        // FilterMode and RestoreBandFilter only applies it when the recalled
        // mode matches, so a retained tuple under a different row Mode is
        // inert, never wrong. Clearing here instead would let an RX2-focused
        // mode change (which shares this per-band row and sends no filter
        // payload) silently wipe RX1's saved width for the band.
        if (hasFilterPayload)
        {
            existing.FilterLowHz = filterLowHz;
            existing.FilterHighHz = filterHighHz;
            existing.FilterMode = filterMode;
        }
        existing.UpdatedUtc = DateTime.UtcNow;
        _entries.Update(existing);
    }

    public void Dispose() => _dbLease.Dispose();

}

public sealed class BandMemoryEntry
{
    public int Id { get; set; }
    public string Band { get; set; } = string.Empty;
    public long Hz { get; set; }
    public RxMode Mode { get; set; }
    // Per-band scope (panadapter) zoom level (#128). Null on rows persisted
    // before #128 shipped — LiteDB is schema-less so old rows hydrate the
    // field as null and RadioService.RestoreBandZoom leaves the current
    // slider alone until the operator explicitly zooms while on that band.
    public int? ZoomLevel { get; set; }
    // Per-band RX1 signed bandpass edges (#179). Null on rows persisted before
    // #179 shipped; RadioService.RestoreBandFilter leaves the current filter
    // alone in that case.
    public int? FilterLowHz { get; set; }
    public int? FilterHighHz { get; set; }
    public RxMode? FilterMode { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
