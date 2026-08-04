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

// Persists per-mode filter-slot overrides and the last-selected preset slot
// across server restarts. Lives in the shared station-engine.db file.
//
// On first run, USB and LSB VAR1 are seeded from the default SSB preset table
// so stored overrides do not narrow the low-frequency audio response.
public sealed class FilterPresetStore : IDisposable
{
    // BsonMapper.Global is a shared, lazily-built entity map. When multiple
    // WebApplicationFactory hosts boot in parallel (xUnit test collections),
    // concurrent first-touches of a type can lose the race with EnsureIndex's
    // LINQ resolver and throw "Member X not found on BsonMapper". Force the
    // entity mapping to be materialized once, under a static lock, before any
    // collection constructs a LINQ expression that walks its members.
    private static readonly object _mapperInitLock = new();
    private static bool _mapperInitialized;

    private static void EnsureMapperRegistered()
    {
        if (_mapperInitialized) return;
        lock (_mapperInitLock)
        {
            if (_mapperInitialized) return;
            BsonMapper.Global.Entity<FilterSlotOverride>();
            BsonMapper.Global.Entity<FilterPresetStoreEntry>()
                .Id(x => x.Id);
            _mapperInitialized = true;
        }
    }

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<FilterPresetStoreEntry> _entries;
    private readonly ILogger<FilterPresetStore> _log;
    private readonly object _sync = new();

    public FilterPresetStore(ILogger<FilterPresetStore> log)
    {
        _log = log;
        EnsureMapperRegistered();

        var dbPath = PrefsDbPath.EngineGet();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<FilterPresetStoreEntry>("filter_presets");
        _entries.EnsureIndex("ModeKey", "$.ModeKey", unique: true);

        SeedDefaults();
        _log.LogInformation("FilterPresetStore initialized at {Path}", dbPath);
    }

    private FilterPresetStoreEntry? FindByMode(string key) =>
        _entries.FindOne("$.ModeKey = @0", key);

    // Returns the merged stored override for any slot. VAR1/VAR2 fall back to
    // the legacy scalar columns when an older preferences DB has not yet
    // acquired a SlotOverrides entry.
    public FilterSlotOverride? GetSlotOverride(RxMode mode, string slotName)
    {
        lock (_sync)
        {
            var e = FindByMode(mode.ToString());
            if (e is null) return null;

            var stored = e.SlotOverrides?
                .FirstOrDefault(x => string.Equals(x.SlotName, slotName, StringComparison.OrdinalIgnoreCase));
            bool legacyHasWidth = slotName switch
            {
                "VAR1" => e.HasVar1,
                "VAR2" => e.HasVar2,
                _ => false,
            };
            if (stored is null && !legacyHasWidth) return null;

            int legacyLo = slotName == "VAR2" ? e.Var2Lo : e.Var1Lo;
            int legacyHi = slotName == "VAR2" ? e.Var2Hi : e.Var1Hi;
            return new FilterSlotOverride
            {
                SlotName = slotName,
                HasWidth = stored?.HasWidth == true || legacyHasWidth,
                LowHz = stored?.HasWidth == true ? stored.LowHz : legacyLo,
                HighHz = stored?.HasWidth == true ? stored.HighHz : legacyHi,
                Label = stored?.Label,
            };
        }
    }

    // Returns the stored override for a VAR slot, or null if not overridden.
    public (int LowHz, int HighHz)? GetVarOverride(RxMode mode, string slotName)
    {
        if (slotName is not ("VAR1" or "VAR2")) return null;
        var stored = GetSlotOverride(mode, slotName);
        return stored?.HasWidth == true ? (stored.LowHz, stored.HighHz) : null;
    }

    public void UpsertVarOverride(RxMode mode, string slotName, int loHz, int hiHz)
    {
        if (slotName is not ("VAR1" or "VAR2"))
            throw new ArgumentException("Expected VAR1 or VAR2.", nameof(slotName));
        UpsertSlotWidthOverride(mode, slotName, loHz, hiHz);
    }

    public void UpsertSlotWidthOverride(
        RxMode mode,
        string slotName,
        int loHz,
        int hiHz)
        => UpsertSlotOverride(mode, slotName, loHz, hiHz, updateLabel: false, label: null);

    public void UpsertSlotOverride(
        RxMode mode,
        string slotName,
        int loHz,
        int hiHz,
        bool updateLabel,
        string? label)
    {
        var key = mode.ToString();
        lock (_sync)
        {
            var existing = FindByMode(key);
            if (existing is null)
            {
                existing = new FilterPresetStoreEntry { ModeKey = key };
                UpsertWidth(existing, slotName, loHz, hiHz);
                if (updateLabel)
                    FindSlot(existing, slotName).Label = label;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Insert(existing);
            }
            else
            {
                UpsertWidth(existing, slotName, loHz, hiHz);
                if (updateLabel)
                    FindSlot(existing, slotName).Label = label;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public void UpsertSlotLabelOverride(RxMode mode, string slotName, string? label)
    {
        var key = mode.ToString();
        lock (_sync)
        {
            var existing = FindByMode(key) ?? new FilterPresetStoreEntry { ModeKey = key };
            existing.SlotOverrides ??= [];
            var slot = existing.SlotOverrides.FirstOrDefault(x =>
                string.Equals(x.SlotName, slotName, StringComparison.OrdinalIgnoreCase));
            if (slot is null)
            {
                slot = new FilterSlotOverride { SlotName = slotName };
                existing.SlotOverrides.Add(slot);
            }
            slot.Label = label;
            if (!slot.HasWidth && slot.Label is null)
                existing.SlotOverrides.Remove(slot);
            existing.UpdatedUtc = DateTime.UtcNow;
            if (existing.Id == 0) _entries.Insert(existing);
            else _entries.Update(existing);
        }
    }

    public void ResetSlotOverride(RxMode mode, string slotName)
    {
        lock (_sync)
        {
            var existing = FindByMode(mode.ToString());
            if (existing is null) return;
            existing.SlotOverrides?.RemoveAll(x =>
                string.Equals(x.SlotName, slotName, StringComparison.OrdinalIgnoreCase));
            if (slotName == "VAR1")
            {
                existing.HasVar1 = false;
                existing.Var1Lo = 0;
                existing.Var1Hi = 0;
            }
            if (slotName == "VAR2")
            {
                existing.HasVar2 = false;
                existing.Var2Lo = 0;
                existing.Var2Hi = 0;
            }
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
    }

    private static void UpsertWidth(
        FilterPresetStoreEntry entry,
        string slotName,
        int loHz,
        int hiHz)
    {
        var slot = FindSlot(entry, slotName);
        slot.HasWidth = true;
        slot.LowHz = loHz;
        slot.HighHz = hiHz;

        // Keep the old scalar schema current for compatibility with older
        // readers and the long-standing GetVarOverride contract.
        if (slotName == "VAR1")
        {
            entry.HasVar1 = true;
            entry.Var1Lo = loHz;
            entry.Var1Hi = hiHz;
        }
        else if (slotName == "VAR2")
        {
            entry.HasVar2 = true;
            entry.Var2Lo = loHz;
            entry.Var2Hi = hiHz;
        }
    }

    private static FilterSlotOverride FindSlot(
        FilterPresetStoreEntry entry,
        string slotName)
    {
        entry.SlotOverrides ??= [];
        var slot = entry.SlotOverrides.FirstOrDefault(x =>
            string.Equals(x.SlotName, slotName, StringComparison.OrdinalIgnoreCase));
        if (slot is not null) return slot;
        slot = new FilterSlotOverride { SlotName = slotName };
        entry.SlotOverrides.Add(slot);
        return slot;
    }

    public string? GetLastSelectedPreset(RxMode mode)
    {
        lock (_sync)
        {
            return FindByMode(mode.ToString())?.LastPreset;
        }
    }

    public void UpsertLastSelectedPreset(RxMode mode, string presetName)
    {
        var key = mode.ToString();
        lock (_sync)
        {
            var existing = FindByMode(key);
            if (existing is null)
            {
                _entries.Insert(new FilterPresetStoreEntry
                {
                    ModeKey = key,
                    LastPreset = presetName,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.LastPreset = presetName;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public void Dispose() => _dbLease.Dispose();

    // Sentinel mode key used for pane-visibility and other ribbon-scope flags
    // that aren't tied to any particular RX mode. Keeps the schema flat while
    // avoiding a second LiteDB collection just for a bool.
    private const string SettingsKey = "__SETTINGS__";

    // Advanced-ribbon visibility, persisted across server restarts so the
    // operator's close-the-ribbon choice sticks.
    public bool GetAdvancedPaneOpen()
    {
        lock (_sync)
        {
            return FindByMode(SettingsKey)?.AdvancedPaneOpen ?? false;
        }
    }

    public void SetAdvancedPaneOpen(bool open)
    {
        lock (_sync)
        {
            var existing = FindByMode(SettingsKey);
            if (existing is null)
            {
                _entries.Insert(new FilterPresetStoreEntry
                {
                    ModeKey = SettingsKey,
                    AdvancedPaneOpen = open,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.AdvancedPaneOpen = open;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    // Get favorite filter slots for a mode. Returns up to 3 slot names (e.g., ["F6", "F5", "F4"]).
    // Returns default favorites if not set: F6 (2.7k), F5 (2.9k), F4 (3.3k) for USB/LSB.
    public string[] GetFavoriteSlots(RxMode mode)
    {
        lock (_sync)
        {
            var existing = FindByMode(mode.ToString());
            if (existing?.FavoriteSlots is not null)
            {
                return existing.FavoriteSlots.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
            // Default favorites: 2.7k (F6), 2.9k (F5), 3.3k (F4) for USB/LSB
            return mode switch
            {
                RxMode.USB or RxMode.LSB => new[] { "F6", "F5", "F4" },
                RxMode.CWU or RxMode.CWL => new[] { "F4", "F5", "F6" }, // 500, 400, 250 Hz
                RxMode.AM or RxMode.SAM => new[] { "F7", "F8", "F9" }, // 8.0k, 7.0k, 6.0k
                RxMode.DSB => new[] { "F6", "F7", "F8" }, // 5.2k, 4.0k, 3.1k
                RxMode.DIGL or RxMode.DIGU => new[] { "F6", "F5", "F4" }, // 800, 1.0k, 1.5k
                _ => new[] { "F6", "F5", "F4" }
            };
        }
    }

    // Set favorite filter slots for a mode. Up to 3 slot names allowed.
    public void SetFavoriteSlots(RxMode mode, string[] slotNames)
    {
        if (slotNames.Length > 3)
            throw new ArgumentException("Maximum 3 favorite slots allowed", nameof(slotNames));

        var key = mode.ToString();
        var csv = string.Join(',', slotNames.Take(3));

        lock (_sync)
        {
            var existing = FindByMode(key);
            if (existing is null)
            {
                _entries.Insert(new FilterPresetStoreEntry
                {
                    ModeKey = key,
                    FavoriteSlots = csv,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.FavoriteSlots = csv;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    // Seed USB and LSB VAR1 with the same 100 Hz low edge as the SSB preset
    // table. Older builds seeded 150/2850; migrate exactly that stale default
    // so existing preference DBs regain low-end response without touching
    // operator-edited VAR1 values.
    private void SeedDefaults()
    {
        SeedVarIfAbsent(RxMode.USB, "VAR1",  100,  2800, legacyLoHz:  150, legacyHiHz:  2850);
        SeedVarIfAbsent(RxMode.LSB, "VAR1", -2800, -100, legacyLoHz: -2850, legacyHiHz: -150);
    }

    private void SeedVarIfAbsent(
        RxMode mode,
        string slotName,
        int loHz,
        int hiHz,
        int? legacyLoHz = null,
        int? legacyHiHz = null)
    {
        var key = mode.ToString();
        var existing = FindByMode(key);
        if (existing is null)
        {
            var entry = new FilterPresetStoreEntry
            {
                ModeKey = key,
                UpdatedUtc = DateTime.UtcNow,
            };
            if (slotName == "VAR1") { entry.HasVar1 = true; entry.Var1Lo = loHz; entry.Var1Hi = hiHz; }
            else                    { entry.HasVar2 = true; entry.Var2Lo = loHz; entry.Var2Hi = hiHz; }
            // Two stores can race the find-then-insert against the same shared
            // station-engine.db (xUnit boots WebApplicationFactory in parallel and
            // every host builds its own FilterPresetStore singleton). The
            // unique ModeKey index is what keeps the row count correct; if a
            // racer beat us in, that's exactly the seeded state we wanted.
            try { _entries.Insert(entry); }
            catch (LiteException ex) when (ex.ErrorCode == LiteException.INDEX_DUPLICATE_KEY) { }
        }
        else if (slotName == "VAR1" && !existing.HasVar1)
        {
            existing.HasVar1 = true;
            existing.Var1Lo = loHz;
            existing.Var1Hi = hiHz;
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
        else if (slotName == "VAR1" && existing.Var1Lo == legacyLoHz && existing.Var1Hi == legacyHiHz)
        {
            existing.Var1Lo = loHz;
            existing.Var1Hi = hiHz;
            var slot = existing.SlotOverrides?.FirstOrDefault(x =>
                x.SlotName == "VAR1" && x.HasWidth
                && x.LowHz == legacyLoHz && x.HighHz == legacyHiHz);
            if (slot is not null) { slot.LowHz = loHz; slot.HighHz = hiHz; }
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
        else if (slotName == "VAR2" && !existing.HasVar2)
        {
            existing.HasVar2 = true;
            existing.Var2Lo = loHz;
            existing.Var2Hi = hiHz;
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
        else if (slotName == "VAR2" && existing.Var2Lo == legacyLoHz && existing.Var2Hi == legacyHiHz)
        {
            existing.Var2Lo = loHz;
            existing.Var2Hi = hiHz;
            var slot = existing.SlotOverrides?.FirstOrDefault(x =>
                x.SlotName == "VAR2" && x.HasWidth
                && x.LowHz == legacyLoHz && x.HighHz == legacyHiHz);
            if (slot is not null) { slot.LowHz = loHz; slot.HighHz = hiHz; }
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
    }

}

public sealed class FilterPresetStoreEntry
{
    public int Id { get; set; }
    public string ModeKey { get; set; } = string.Empty;
    public int Var1Lo { get; set; }
    public int Var1Hi { get; set; }
    public bool HasVar1 { get; set; }
    public int Var2Lo { get; set; }
    public int Var2Hi { get; set; }
    public bool HasVar2 { get; set; }
    public List<FilterSlotOverride> SlotOverrides { get; set; } = [];
    public string? LastPreset { get; set; }
    // Ribbon-scope flag, only meaningful on the sentinel "__SETTINGS__" row.
    public bool AdvancedPaneOpen { get; set; }
    // Favorite filter slots (up to 3), stored as comma-separated preset names.
    // e.g., "F6,F5,F4" for 2.7k, 2.9k, 3.3k in USB/LSB.
    public string? FavoriteSlots { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class FilterSlotOverride
{
    public string SlotName { get; set; } = string.Empty;
    public bool HasWidth { get; set; }
    public int LowHz { get; set; }
    public int HighHz { get; set; }
    public string? Label { get; set; }
}
