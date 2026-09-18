// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists the operator's TX monitor volume in the engine preferences database.
/// </summary>
public sealed class TxMonitorSettingsStore : IDisposable
{
    public const double DefaultVolumeDb = 0.0;
    public const double MinVolumeDb = -60.0;
    public const double MaxVolumeDb = 0.0;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<TxMonitorSettingsEntry> _settings;
    private readonly object _sync = new();

    public TxMonitorSettingsStore(
        ILogger<TxMonitorSettingsStore> log,
        string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _settings = _dbLease.Database.GetCollection<TxMonitorSettingsEntry>(
            "tx_monitor_settings");

        log.LogInformation("TxMonitorSettingsStore initialized at {Path}", dbPath);
    }

    public double VolumeDb
    {
        get
        {
            lock (_sync)
            {
                return Normalize(_settings.FindAll().FirstOrDefault()?.VolumeDb);
            }
        }
    }

    public double SetVolumeDb(double volumeDb)
    {
        var normalized = Normalize(volumeDb);
        lock (_sync)
        {
            var entry = _settings.FindAll().FirstOrDefault() ?? new TxMonitorSettingsEntry();
            entry.VolumeDb = normalized;
            entry.UpdatedUtc = DateTime.UtcNow;
            if (entry.Id == 0) _settings.Insert(entry);
            else _settings.Update(entry);
        }

        return normalized;
    }

    public void Dispose() => _dbLease.Dispose();

    private static double Normalize(double? volumeDb)
    {
        var value = volumeDb is { } candidate && double.IsFinite(candidate)
            ? candidate
            : DefaultVolumeDb;
        return Math.Clamp(value, MinVolumeDb, MaxVolumeDb);
    }
}

public sealed class TxMonitorSettingsEntry
{
    public int Id { get; set; }
    public double VolumeDb { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
