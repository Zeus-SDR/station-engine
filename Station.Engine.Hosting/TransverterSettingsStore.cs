// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Persists the station-wide 14-slot transverter table. Workspace enablement
/// and active-band selection live in LayoutStore and are composed on reads.
/// </summary>
public sealed class TransverterSettingsStore : IDisposable
{
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
        bool migrateLegacy = entry is not null
            && (entry.Bands is null || entry.Bands.Count == 0);
        bool migratePresetTemplates = entry?.Bands?.Any(IsEmptyBand) == true;
        bool migrateLegacySlotLabel = entry?.Bands?.Any(band => band.Id == 0
            && IsPlaceholderLabel(band.ButtonText, band.Id)
            && PresetLabelForRfHz(entry.RfFrequencyHz) is not null) == true;
        TransverterSettingsDto loaded;
        try
        {
            loaded = entry is null
                ? CreatePresetSettings()
                : FromEntry(entry);
        }
        catch (OverflowException)
        {
            loaded = CreatePresetSettings();
        }
        _current = TransverterFrequencyConverter.TryValidate(loaded, out _)
            ? loaded with { Enabled = false, ActiveBandId = null }
            : CreatePresetSettings();

        if (migrateLegacy || migratePresetTemplates || migrateLegacySlotLabel)
            Persist(_current, entry!);

        log.LogInformation("TransverterSettingsStore initialized at {Path}", dbPath);
    }

    public TransverterSettingsDto Get()
    {
        lock (_sync) return _current;
    }

    public TransverterSettingsDto GetForLayout(
        LayoutStore layouts,
        string radioKey,
        string layoutId)
    {
        var selection = layouts.GetTransverterSelection(radioKey, layoutId);
        return Get() with
        {
            Enabled = selection.Enabled,
            ActiveBandId = selection.ActiveBandId,
        };
    }

    public TransverterSettingsDto GetForActiveLayout(
        LayoutStore layouts,
        string radioKey)
    {
        var selection = layouts.GetActiveTransverterSelection(radioKey);
        return Get() with
        {
            Enabled = selection.Enabled,
            ActiveBandId = selection.ActiveBandId,
        };
    }

    public TransverterSettingsDto GetForConnectedRadio(
        LayoutStore layouts,
        RadioService radio) =>
        GetForActiveLayout(layouts, radio.ConnectedBoardKind.ToString());

    public bool TryResolveActiveBandForLayout(
        LayoutStore layouts,
        string radioKey,
        string layoutId,
        out TransverterBandDto band) =>
        TransverterFrequencyConverter.TryResolveActiveBand(
            GetForLayout(layouts, radioKey, layoutId), out band);

    public bool TryResolveBandByRfHz(long rfHz, out TransverterBandDto band) =>
        TransverterFrequencyConverter.TryResolveBandByRfHz(rfHz, Get(), out band);

    public bool TryResolveBandByIfHz(long ifHz, out TransverterBandDto band) =>
        TransverterFrequencyConverter.TryResolveBandByIfHz(ifHz, Get(), out band);

    public TransverterSettingsDto Set(
        TransverterSettingsDto settings,
        bool notifyChanged = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Bands is null
            && !TransverterFrequencyConverter.TryValidate(settings, out var legacyError))
            throw new ArgumentException(legacyError, nameof(settings));
        settings = Normalize(settings);
        if (!TransverterFrequencyConverter.TryValidate(settings, out var error))
            throw new ArgumentException(error, nameof(settings));

        settings = settings with { Enabled = false, ActiveBandId = null };

        bool changed;
        lock (_sync)
        {
            changed = !GlobalEquals(settings, _current);
            var existing = _rows.FindAll().FirstOrDefault();
            if (existing is null)
            {
                existing = new TransverterSettingsEntry();
                Persist(settings, existing, insert: true);
            }
            else
            {
                Persist(settings, existing);
            }
            _current = settings;
        }

        if (notifyChanged && changed) Changed?.Invoke();
        return settings;
    }

    internal void NotifyChanged() => Changed?.Invoke();

    public void Dispose() => _dbLease.Dispose();

    private static TransverterSettingsDto Normalize(TransverterSettingsDto settings)
    {
        if (settings.Bands is not null)
            return settings with { Bands = ApplyPresetTemplates(settings.Bands) };

        var migrated = CreateLegacySettings(settings.IfFrequencyHz, settings.RfFrequencyHz);
        return migrated with
        {
            Enabled = settings.Enabled,
            ActiveBandId = settings.Enabled ? 0 : null,
        };
    }

    private static TransverterSettingsDto CreateLegacySettings(long ifHz, long rfHz)
    {
        long offset = checked(rfHz - ifHz);
        var bands = Enumerable.Range(0, TransverterFrequencyConverter.BandCount)
            .Select(id => id == 0
                ? new TransverterBandDto(
                    Id: 0,
                    Enabled: true,
                    ButtonText: PresetLabelForRfHz(rfHz) ?? "XVTR0",
                    LoOffsetHz: offset,
                    BeginFrequencyHz: checked(offset + TransverterFrequencyConverter.MinimumRadioFrequencyHz),
                    EndFrequencyHz: checked(offset + TransverterFrequencyConverter.MaximumTransverterIfFrequencyHz))
                : CreatePresetBand(id))
            .ToArray();
        return new TransverterSettingsDto(false, ifHz, rfHz, bands, null);
    }

    private static TransverterSettingsDto CreatePresetSettings() => new(
        Enabled: false,
        IfFrequencyHz: 28_000_000,
        RfFrequencyHz: 144_000_000,
        Bands: Enumerable.Range(0, TransverterFrequencyConverter.BandCount)
            .Select(CreatePresetBand)
            .ToArray(),
        ActiveBandId: null);

    private static TransverterSettingsDto FromEntry(TransverterSettingsEntry entry)
    {
        if (entry.Bands is null || entry.Bands.Count == 0)
            return CreateLegacySettings(entry.IfFrequencyHz, entry.RfFrequencyHz);

        var bands = ApplyLegacySlotLabel(
            ApplyPresetTemplates(entry.Bands.Select(FromEntry)),
            entry.RfFrequencyHz);
        return new TransverterSettingsDto(
            Enabled: false,
            entry.IfFrequencyHz,
            entry.RfFrequencyHz,
            bands,
            ActiveBandId: null);
    }

    // Disabled templates make the common bands discoverable without routing
    // RF or enabling TX before the operator has matched the actual converter.
    private static TransverterBandDto CreatePresetBand(int id) => id switch
    {
        0 => CreatePresetBand(0, "2m", 144_000_000, 148_000_000),
        1 => CreatePresetBand(1, "70cm", 420_000_000, 450_000_000),
        2 => CreatePresetBand(2, "1.25m", 222_000_000, 225_000_000),
        3 => CreatePresetBand(3, "33cm", 902_000_000, 928_000_000),
        4 => CreatePresetBand(4, "23cm", 1_240_000_000, 1_300_000_000),
        5 => CreatePresetBand(5, "4m", 70_000_000, 71_000_000),
        _ => new TransverterBandDto(id),
    };

    private static TransverterBandDto CreatePresetBand(int id, string label, long beginHz, long endHz) => new(
        Id: id,
        ButtonText: label,
        LoOffsetHz: beginHz - 28_000_000,
        BeginFrequencyHz: beginHz,
        EndFrequencyHz: endHz);

    private static IReadOnlyList<TransverterBandDto> ApplyPresetTemplates(
        IEnumerable<TransverterBandDto> bands) => bands
        .OrderBy(item => item.Id)
        .Select(item => IsEmptyBand(item) ? CreatePresetBand(item.Id) : item)
        .ToArray();

    private static IReadOnlyList<TransverterBandDto> ApplyLegacySlotLabel(
        IReadOnlyList<TransverterBandDto> bands,
        long rfHz)
    {
        var label = PresetLabelForRfHz(rfHz);
        if (label is null) return bands;
        return bands.Select(band => band.Id == 0 && IsPlaceholderLabel(band.ButtonText, band.Id)
            ? band with { ButtonText = label }
            : band).ToArray();
    }

    private static bool IsEmptyBand(TransverterBandEntry band) =>
        !band.Enabled
        && IsPlaceholderLabel(band.ButtonText, band.Id)
        && band.LoOffsetHz == 0
        && band.LoErrorHz == 0
        && band.BeginFrequencyHz == 0
        && band.EndFrequencyHz == 0;

    private static bool IsEmptyBand(TransverterBandDto band) =>
        !band.Enabled
        && IsPlaceholderLabel(band.ButtonText, band.Id)
        && band.LoOffsetHz == 0
        && band.LoErrorHz == 0
        && band.BeginFrequencyHz == 0
        && band.EndFrequencyHz == 0;

    private static bool IsPlaceholderLabel(string? value, int id)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var label = value.Trim();
        return label == id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || string.Equals(label, $"XVTR {id}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(label, $"XVTR{id}", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PresetLabelForRfHz(long rfHz) => rfHz switch
    {
        >= 70_000_000 and <= 71_000_000 => "4m",
        >= 144_000_000 and <= 148_000_000 => "2m",
        >= 222_000_000 and <= 225_000_000 => "1.25m",
        >= 420_000_000 and <= 450_000_000 => "70cm",
        >= 902_000_000 and <= 928_000_000 => "33cm",
        >= 1_240_000_000 and <= 1_300_000_000 => "23cm",
        _ => null,
    };

    private static TransverterBandDto FromEntry(TransverterBandEntry entry) => new(
        entry.Id,
        entry.Enabled,
        entry.ButtonText ?? string.Empty,
        entry.LoOffsetHz,
        entry.LoErrorHz,
        entry.BeginFrequencyHz,
        entry.EndFrequencyHz,
        entry.RxGainDb,
        entry.RxOnly,
        entry.Power,
        entry.DisablePa,
        entry.RxAntenna,
        entry.MaxPowerDbm);

    private void Persist(
        TransverterSettingsDto settings,
        TransverterSettingsEntry entry,
        bool insert = false)
    {
        entry.Enabled = false;
        entry.IfFrequencyHz = settings.IfFrequencyHz;
        entry.RfFrequencyHz = settings.RfFrequencyHz;
        entry.Bands = settings.Bands!.Select(item => new TransverterBandEntry
        {
            Id = item.Id,
            Enabled = item.Enabled,
            ButtonText = item.ButtonText,
            LoOffsetHz = item.LoOffsetHz,
            LoErrorHz = item.LoErrorHz,
            BeginFrequencyHz = item.BeginFrequencyHz,
            EndFrequencyHz = item.EndFrequencyHz,
            RxGainDb = item.RxGainDb,
            RxOnly = item.RxOnly,
            Power = item.Power,
            DisablePa = item.DisablePa,
            RxAntenna = item.RxAntenna,
            MaxPowerDbm = item.MaxPowerDbm,
        }).ToList();
        entry.UpdatedUtc = DateTime.UtcNow;
        if (insert) _rows.Insert(entry);
        else _rows.Update(entry);
    }

    private static bool GlobalEquals(
        TransverterSettingsDto left,
        TransverterSettingsDto right) =>
        left.IfFrequencyHz == right.IfFrequencyHz
        && left.RfFrequencyHz == right.RfFrequencyHz
        && (left.Bands ?? Array.Empty<TransverterBandDto>())
            .SequenceEqual(right.Bands ?? Array.Empty<TransverterBandDto>());
}

public sealed class TransverterSettingsEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public long IfFrequencyHz { get; set; } = 28_000_000;
    public long RfFrequencyHz { get; set; } = 144_000_000;
    public List<TransverterBandEntry>? Bands { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class TransverterBandEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string ButtonText { get; set; } = string.Empty;
    public long LoOffsetHz { get; set; }
    public long LoErrorHz { get; set; }
    public long BeginFrequencyHz { get; set; }
    public long EndFrequencyHz { get; set; }
    public double RxGainDb { get; set; }
    public bool RxOnly { get; set; }
    public int Power { get; set; } = 100;
    public bool DisablePa { get; set; } = true;
    public TransverterRxAntenna RxAntenna { get; set; }
    public double? MaxPowerDbm { get; set; }
}
