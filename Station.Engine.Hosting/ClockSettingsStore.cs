// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Text.RegularExpressions;
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Persists the Clock tile preferences. The SPA used to keep them only in
// localStorage, which is per-origin: the launcher, a bench, and a LAN browser
// each load the SPA from a different loopback origin, so a setting saved in
// one looked like it reset on the next launch. Lives in zeus-prefs.db
// alongside the other UI prefs (NrUiPrefs, ThemeSettings, PanWfSplit, …).
//
// Presentation-only. The server bounds every field so a malformed client
// can't store junk; the SPA additionally checks the time zone against its
// runtime's Intl zone list on the way in.
public sealed partial class ClockSettingsStore : IDisposable
{
    public const int LocalLabelMax = 12;
    public const int TimeZoneMax = 64;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<ClockSettingsEntry> _docs;
    private readonly ILogger<ClockSettingsStore> _log;
    private readonly object _sync = new();

    public ClockSettingsStore(ILogger<ClockSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _docs = _db.GetCollection<ClockSettingsEntry>("clock_settings");

        _log.LogInformation("ClockSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// Defaults reproduce the original tile: big UTC, 24-hour system-zone
    /// local time with seconds, Zulu date, and both station-ID cues off.
    /// Must match <c>CLOCK_SETTINGS_DEFAULTS</c> in the SPA.
    /// </summary>
    public static ClockSettingsDto Defaults { get; } = new(
        Saved: false,
        Primary: "utc",
        ShowLocal: true,
        LocalTimeZone: "",
        LocalLabel: "",
        LocalHourFormat: "24h",
        ShowZoneName: false,
        ShowSeconds: true,
        ShowUtcDate: true,
        ShowLocalDate: false,
        ShowWeekday: false,
        TenMinuteNotify: false,
        TenMinuteFlash: false);

    public ClockSettingsDto Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null) return Defaults;
            return new ClockSettingsDto(
                Saved: true,
                Primary: NormalizePrimary(e.Primary),
                ShowLocal: e.ShowLocal,
                LocalTimeZone: NormalizeTimeZone(e.LocalTimeZone),
                LocalLabel: NormalizeLabel(e.LocalLabel),
                LocalHourFormat: NormalizeHourFormat(e.LocalHourFormat),
                ShowZoneName: e.ShowZoneName,
                ShowSeconds: e.ShowSeconds,
                ShowUtcDate: e.ShowUtcDate,
                ShowLocalDate: e.ShowLocalDate,
                ShowWeekday: e.ShowWeekday,
                TenMinuteNotify: e.TenMinuteNotify,
                TenMinuteFlash: e.TenMinuteFlash);
        }
    }

    /// <summary>Replaces the whole stored snapshot and returns what was saved.</summary>
    public ClockSettingsDto Set(ClockSettingsSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new ClockSettingsEntry();
            e.Primary = NormalizePrimary(req.Primary);
            e.ShowLocal = req.ShowLocal;
            e.LocalTimeZone = NormalizeTimeZone(req.LocalTimeZone);
            e.LocalLabel = NormalizeLabel(req.LocalLabel);
            e.LocalHourFormat = NormalizeHourFormat(req.LocalHourFormat);
            e.ShowZoneName = req.ShowZoneName;
            e.ShowSeconds = req.ShowSeconds;
            e.ShowUtcDate = req.ShowUtcDate;
            e.ShowLocalDate = req.ShowLocalDate;
            e.ShowWeekday = req.ShowWeekday;
            e.TenMinuteNotify = req.TenMinuteNotify;
            e.TenMinuteFlash = req.TenMinuteFlash;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
        return Get();
    }

    internal static string NormalizePrimary(string? v) => v == "local" ? "local" : "utc";

    internal static string NormalizeHourFormat(string? v) => v == "12h" ? "12h" : "24h";

    internal static string NormalizeLabel(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.Length <= LocalLabelMax ? v : v[..LocalLabelMax];
    }

    // IANA ids are ASCII letters, digits, '_', '+', '-' and '/'. Anything else
    // (or an over-long value) falls back to "" — the computer's zone.
    internal static string NormalizeTimeZone(string? v)
    {
        if (string.IsNullOrEmpty(v) || v.Length > TimeZoneMax) return "";
        return TimeZoneIdPattern().IsMatch(v) ? v : "";
    }

    [GeneratedRegex("^[A-Za-z0-9_+\\-]+(/[A-Za-z0-9_+\\-]+)*$")]
    private static partial Regex TimeZoneIdPattern();

    public void Dispose() => _dbLease.Dispose();
}

public sealed class ClockSettingsEntry
{
    public int Id { get; set; }
    public string Primary { get; set; } = "utc";
    public bool ShowLocal { get; set; } = true;
    public string LocalTimeZone { get; set; } = "";
    public string LocalLabel { get; set; } = "";
    public string LocalHourFormat { get; set; } = "24h";
    public bool ShowZoneName { get; set; }
    public bool ShowSeconds { get; set; } = true;
    public bool ShowUtcDate { get; set; } = true;
    public bool ShowLocalDate { get; set; }
    public bool ShowWeekday { get; set; }
    public bool TenMinuteNotify { get; set; }
    public bool TenMinuteFlash { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
