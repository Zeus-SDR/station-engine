// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Persists the CW station ID timer preferences (speed, tone, level,
/// interval). Single-row schema in <c>station-engine.db</c>, mirroring
/// <see cref="CwSettingsStore"/>. The running/stopped state and the callsign
/// are deliberately NOT persisted: the timer always starts stopped, and the
/// operator presses Start each session.
/// </summary>
public sealed class CwIdSettingsStore : IDisposable
{
    public const int DefaultWpm = 20;
    public const int DefaultToneHz = 800;
    public const double DefaultLevelDb = -18.0;
    public const int DefaultIntervalMinutes = 10;

    /// <summary>FCC 97.119(b)(1): a CW ID sent with a phone emission must not
    /// exceed 20 WPM.</summary>
    public const int MaxWpm = 20;
    public const int MinWpm = 5;
    public const int MinToneHz = 400;
    public const int MaxToneHz = 1200;
    public const double MinLevelDb = -40.0;
    public const double MaxLevelDb = -6.0;
    public const int MinIntervalMinutes = 1;
    /// <summary>97.119(a): identify at least every 10 minutes.</summary>
    public const int MaxIntervalMinutes = 10;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<CwIdSettingsEntry> _docs;
    private readonly object _sync = new();

    public CwIdSettingsStore(ILogger<CwIdSettingsStore> log, string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _docs = _dbLease.Database.GetCollection<CwIdSettingsEntry>("cw_id_settings");
        log.LogInformation("CwIdSettingsStore initialized at {Path}", dbPath);
    }

    public CwIdSettingsDto Get()
    {
        lock (_sync)
            return ToDto(_docs.FindAll().FirstOrDefault() ?? new CwIdSettingsEntry());
    }

    /// <summary>Merge a PATCH-shaped request onto the persisted row, clamping
    /// every field to its legal range. Returns the post-merge snapshot.</summary>
    public CwIdSettingsDto Save(CwIdSettingsSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        lock (_sync)
        {
            var existing = _docs.FindAll().FirstOrDefault();
            var entry = existing ?? new CwIdSettingsEntry();

            if (req.Wpm is int w) entry.Wpm = Math.Clamp(w, MinWpm, MaxWpm);
            if (req.ToneHz is int hz) entry.ToneHz = Math.Clamp(hz, MinToneHz, MaxToneHz);
            if (req.LevelDb is double db && double.IsFinite(db))
                entry.LevelDb = Math.Clamp(db, MinLevelDb, MaxLevelDb);
            if (req.IntervalMinutes is int m)
                entry.IntervalMinutes = Math.Clamp(m, MinIntervalMinutes, MaxIntervalMinutes);

            entry.UpdatedUtc = DateTime.UtcNow;
            if (existing is null) _docs.Insert(entry);
            else _docs.Update(entry);
            return ToDto(entry);
        }
    }

    public void Dispose() => _dbLease.Dispose();

    // Clamp on read too, so a hand-edited or future-schema row can never
    // push an out-of-range speed or level into the live mixer.
    private static CwIdSettingsDto ToDto(CwIdSettingsEntry e) => new(
        Wpm: Math.Clamp(e.Wpm, MinWpm, MaxWpm),
        ToneHz: Math.Clamp(e.ToneHz, MinToneHz, MaxToneHz),
        LevelDb: double.IsFinite(e.LevelDb) ? Math.Clamp(e.LevelDb, MinLevelDb, MaxLevelDb) : DefaultLevelDb,
        IntervalMinutes: Math.Clamp(e.IntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes));
}

public sealed class CwIdSettingsEntry
{
    public int Id { get; set; }
    public int Wpm { get; set; } = CwIdSettingsStore.DefaultWpm;
    public int ToneHz { get; set; } = CwIdSettingsStore.DefaultToneHz;
    public double LevelDb { get; set; } = CwIdSettingsStore.DefaultLevelDb;
    public int IntervalMinutes { get; set; } = CwIdSettingsStore.DefaultIntervalMinutes;
    public DateTime UpdatedUtc { get; set; }
}
