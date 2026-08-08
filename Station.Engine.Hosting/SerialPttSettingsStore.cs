// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Persisted serial-PTT-switch config (Thetis "Bit Bang PTT" parity): which
// host COM/serial port carries the switch, which modem pins to sense, and the
// master enable. Default OFF with no port — a fresh install has nothing wired,
// and Thetis likewise defaults its bit-bang port to None.
//
// Single-row LiteDB collection ("serial_ptt_settings") sharing
// station-engine.db, mirroring PttSettingsStore. Insert/Update (NOT Upsert
// with Id=0) avoids the LiteDB Id=0-always-inserts bug (PR #387). The Changed
// event lets SerialPttService reopen its port without a server restart.
//
// Serial PTT is host-specific (a COM port / device path is meaningful only on
// the machine the adapter is plugged into), so — like serial CAT — it is
// store-only with NO appsettings section.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class SerialPttSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<SerialPttSettingsEntry> _rows;
    private readonly ILogger<SerialPttSettingsStore> _log;
    private readonly object _sync = new();

    // Fired on any write so SerialPttService re-resolves + reopens its port.
    public event Action? Changed;

    public SerialPttSettingsStore(ILogger<SerialPttSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _rows = _db.GetCollection<SerialPttSettingsEntry>("serial_ptt_settings");

        _log.LogInformation("SerialPttSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>The stored config. A missing row (fresh install / pre-feature
    /// DB) returns <see cref="SerialPttConfig.Defaults"/>: disabled, no port,
    /// both sense pins selected. Sanitized on read so a hand-edited row never
    /// feeds a whitespace port name downstream.</summary>
    public SerialPttConfig Get()
    {
        lock (_sync)
        {
            var entry = _rows.FindAll().FirstOrDefault();
            if (entry is null) return SerialPttConfig.Defaults;
            return Sanitize(new SerialPttConfig(
                Enabled: entry.Enabled,
                PortName: entry.PortName ?? string.Empty,
                SenseCts: entry.SenseCts,
                SenseDsr: entry.SenseDsr));
        }
    }

    /// <summary>Replace the stored config. Insert-then-Update (matching
    /// PttSettingsStore) avoids the LiteDB Id=0 upsert bug (PR #387).
    /// Fires <see cref="Changed"/>.</summary>
    public void Set(SerialPttConfig config)
    {
        var sanitized = Sanitize(config);
        lock (_sync)
        {
            var existing = _rows.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _rows.Insert(new SerialPttSettingsEntry
                {
                    Enabled = sanitized.Enabled,
                    PortName = sanitized.PortName,
                    SenseCts = sanitized.SenseCts,
                    SenseDsr = sanitized.SenseDsr,
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                existing.Enabled = sanitized.Enabled;
                existing.PortName = sanitized.PortName;
                existing.SenseCts = sanitized.SenseCts;
                existing.SenseDsr = sanitized.SenseDsr;
                existing.UpdatedUtc = nowUtc;
                _rows.Update(existing);
            }
        }
        Changed?.Invoke();
    }

    private static SerialPttConfig Sanitize(SerialPttConfig c) =>
        c with { PortName = (c.PortName ?? string.Empty).Trim() };

    public void Dispose() => _dbLease.Dispose();
}

public sealed class SerialPttSettingsEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string PortName { get; set; } = "";
    public bool SenseCts { get; set; } = true;
    public bool SenseDsr { get; set; } = true;
    public DateTime UpdatedUtc { get; set; }
}
