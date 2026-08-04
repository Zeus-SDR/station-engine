// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Persists the one global transverter conversion in station-engine.db. The
/// immutable snapshot is cached so TCI tuning broadcasts never read LiteDB on
/// their high-frequency path.
/// </summary>
public sealed class TransverterSettingsStore : IDisposable
{
    private static readonly TransverterSettingsDto DefaultSettings = new();

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<TransverterSettingsEntry> _rows;
    private readonly object _sync = new();
    private TransverterSettingsDto _current;

    public event Action? Changed;

    public TransverterSettingsStore(
        ILogger<TransverterSettingsStore> log,
        string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _rows = _db.GetCollection<TransverterSettingsEntry>("transverter_settings");

        var entry = _rows.FindAll().FirstOrDefault();
        var loaded = entry is null
            ? DefaultSettings
            : new TransverterSettingsDto(
                Enabled: false,
                entry.IfFrequencyHz,
                entry.RfFrequencyHz);
        _current = TransverterFrequencyConverter.TryValidate(loaded, out _)
            ? loaded
            : DefaultSettings;

        log.LogInformation("TransverterSettingsStore initialized at {Path}", dbPath);
    }

    public TransverterSettingsDto Get()
    {
        lock (_sync) return _current;
    }

    /// <summary>Compose the global IF/RF anchors with one workspace's flag.</summary>
    public TransverterSettingsDto GetForLayout(
        LayoutStore layouts,
        string radioKey,
        string layoutId) =>
        Get() with { Enabled = layouts.GetTransverterEnabled(radioKey, layoutId) };

    /// <summary>Compose the global anchors with a radio's active workspace.</summary>
    public TransverterSettingsDto GetForActiveLayout(
        LayoutStore layouts,
        string radioKey) =>
        Get() with { Enabled = layouts.GetActiveTransverterEnabled(radioKey) };

    /// <summary>Effective settings for the currently connected TCI radio.</summary>
    public TransverterSettingsDto GetForConnectedRadio(
        LayoutStore layouts,
        RadioService radio) =>
        GetForActiveLayout(layouts, radio.ConnectedBoardKind.ToString());

    public TransverterSettingsDto Set(
        TransverterSettingsDto settings,
        bool notifyChanged = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TransverterFrequencyConverter.TryValidate(settings, out var error))
            throw new ArgumentException(error, nameof(settings));

        // Enablement belongs to the selected workspace. Ignore any caller's
        // Enabled projection when persisting the global anchor row, including
        // old rows written before workspace-scoped enablement existed.
        settings = settings with { Enabled = false };

        bool changed;
        lock (_sync)
        {
            changed = settings != _current;
            var existing = _rows.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _rows.Insert(new TransverterSettingsEntry
                {
                    Enabled = settings.Enabled,
                    IfFrequencyHz = settings.IfFrequencyHz,
                    RfFrequencyHz = settings.RfFrequencyHz,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Enabled = settings.Enabled;
                existing.IfFrequencyHz = settings.IfFrequencyHz;
                existing.RfFrequencyHz = settings.RfFrequencyHz;
                existing.UpdatedUtc = DateTime.UtcNow;
                _rows.Update(existing);
            }
            _current = settings;
        }

        if (notifyChanged && changed) Changed?.Invoke();
        return settings;
    }

    /// <summary>Notify observers after a coordinated settings/layout update.</summary>
    internal void NotifyChanged() => Changed?.Invoke();

    public void Dispose() => _dbLease.Dispose();
}

public sealed class TransverterSettingsEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public long IfFrequencyHz { get; set; } = 28_000_000;
    public long RfFrequencyHz { get; set; } = 144_000_000;
    public DateTime UpdatedUtc { get; set; }
}
