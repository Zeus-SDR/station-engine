// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Globalization;
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Persists the CW operator's preferences (WPM, Farnsworth split, 6 macro
/// slots, sidetone gain/pitch) so the macro pad and slider state survive
/// server restarts. Shares <c>station-engine.db</c> with the other engine stores —
/// CW settings aren't sensitive.
///
/// Single-row schema (one operator per backend). Macros is stored as a
/// fixed-length string[6]; absent / shorter persisted arrays are padded
/// to 6 with the seeded defaults on read, so legacy DBs hydrate cleanly.
/// </summary>
public sealed class CwSettingsStore : IDisposable
{
    /// <summary>Default macros — match the previously-hardcoded list in
    /// <c>zeus-web/src/components/design/CwKeyer.tsx</c> so a fresh install
    /// shows the same six buttons the UI used pre-persistence.</summary>
    public static readonly string[] DefaultMacros =
        { "CQ CQ CQ", "TU 73", "QRZ?", "AGN?", "5NN TU", "UR RST" };

    public const int DefaultWpm = 22;
    public const double DefaultSidetoneGainDb = -10.0;
    public const int DefaultSidetoneHz = 600;
    public const bool DefaultBreakIn = true;
    public const int DefaultHangMs = 300;
    public const int DefaultWeight = 50;
    public const bool DefaultPaddleReverse = false;
    public const bool DefaultAutoCq = false;
    public const bool DefaultAutoLog = false;
    public const bool DefaultClickToArm = false;
    public const int DefaultRepeatLimit = 3;
    public const int DefaultPartnerIdleSec = 2;
    public const bool DefaultFinalEe = false;
    public const string DefaultSeqCq = "CQ CQ DE {MYCALL} {MYCALL} K";
    public const string DefaultSeqAnswer = "{CALL} DE {MYCALL} {MYCALL} K";
    public const string DefaultSeqReport = "{CALL} DE {MYCALL} TU {RST} {RST} BK";
    public const string DefaultSeqConfirm = "{CALL} DE {MYCALL} TU 73 SK";
    public const string DefaultRstToSend = "5NN";
    /// <summary>Default-safe keyer mode. Straight means the HL2 gateware
    /// passes the key line through directly and ignores keyer speed — a
    /// straight/bug key is never mis-keyed as a paddle. Iambic is opt-in
    /// (see [[zeus-bks]]).</summary>
    public const CwKeyerMode DefaultKeyerMode = CwKeyerMode.Straight;
    /// <summary>Hard cap on the total number of macros — keeps the UI
    /// list manageable and bounds the broadcast frame size. Operators can
    /// add up to this many slots; <see cref="DefaultMacros"/> seeds the
    /// first six on a fresh install.</summary>
    public const int MaxMacros = 32;
    /// <summary>Hard cap on a single macro to keep wire frames small and
    /// reject runaway paste / accidental long strings at the API edge.</summary>
    public const int MaxMacroChars = 200;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<CwSettingsEntry> _docs;
    private readonly ILogger<CwSettingsStore> _log;
    private readonly object _sync = new();
    // Highest seq applied for each (page session, field). Process lifetime:
    // the race is two HTTP writes to this engine, and a restart drops both.
    // A field applies only when its rev is newer than the one already stored
    // for that field. Other fields in the same patch are judged on their own.
    // A write with no rev (or a rev this process has not parsed) does not
    // touch the map.
    private readonly Dictionary<RevKey, long> _lastAppliedRev = new();
    // Snapshot version handed to every client. Starts at the persisted value,
    // or 1 when the row has none, and increases by 1 on each applied save.
    private long _settingsVersion;

    public CwSettingsStore(ILogger<CwSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _docs = _db.GetCollection<CwSettingsEntry>("cw_settings");
        var stored = _docs.FindAll().FirstOrDefault()?.SettingsVersion ?? 0;
        _settingsVersion = stored >= 1 ? stored : 1;

        _log.LogInformation("CwSettingsStore initialized at {Path}", dbPath);
    }

    public CwSettingsDto Get()
    {
        lock (_sync) return SnapshotUnlocked();
    }

    /// <summary>
    /// Merge a PATCH-shaped request on top of the persisted row. Null
    /// fields preserve their current value; non-null fields are validated
    /// (Wpm clamped 5..50, sidetone clamped to sane ranges, macros truncated
    /// to <see cref="MaxMacros"/> with a per-string length cap).
    /// Returns the post-merge snapshot. Each field of a revisioned patch
    /// applies only when its rev is newer than the last rev stored for that
    /// field. A patch whose every field is stale leaves the row alone.
    /// </summary>
    public CwSettingsDto Save(CwSettingsSetRequest req) => Write(req).Settings;

    /// <summary>
    /// Same merge as <see cref="Save"/>. <see cref="CwSettingsWrite.Applied"/>
    /// is false when <paramref name="req"/> carries a revision and every
    /// field in it is older than or equal to the rev already applied for
    /// that field on that session. Fields that are newer still apply.
    /// The settings are the current row either way.
    /// </summary>
    public CwSettingsWrite Write(CwSettingsSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        lock (_sync)
        {
            var tracked = TryReadRevision(req.Rev, out var session, out var seq);
            var existing = _docs.FindAll().FirstOrDefault();
            var entry = existing ?? SeedDefaultsEntry();
            var appliedAny = false;

            // A revisioned field applies only when this seq is strictly newer
            // than the last one stored for (session, field). An equal seq is
            // the same write arriving twice. An unrevisioned write applies
            // and does not move the map.
            bool Take(CwRevField field)
            {
                if (tracked
                    && _lastAppliedRev.TryGetValue(new RevKey(session, field), out var last)
                    && seq <= last)
                    return false;
                appliedAny = true;
                if (tracked) _lastAppliedRev[new RevKey(session, field)] = seq;
                return true;
            }

            if (req.Wpm is int w && Take(CwRevField.Wpm))
                entry.Wpm = Math.Clamp(w, 5, 50);
            if (req.FarnsworthWpm is int fw && Take(CwRevField.FarnsworthWpm))
                entry.FarnsworthWpm = fw <= 0 ? null : Math.Clamp(fw, 5, 50);
            if (req.Macros is { } macros && Take(CwRevField.Macros))
                entry.Macros = NormalizeMacros(macros);
            if (req.SidetoneGainDb is double g && Take(CwRevField.SidetoneGainDb))
                // Operator-safe sidetone band: -60..+0 dB. Above 0 risks
                // hard-clipping; below -60 is effectively silent.
                entry.SidetoneGainDb = Math.Clamp(g, -60.0, 0.0);
            if (req.SidetoneHz is int hz && Take(CwRevField.SidetoneHz))
                // CW pitch operator preference range. Below 200 is sub-bass
                // and below the WDSP RX bandpass; above 1200 is uncomfortable.
                entry.SidetoneHz = Math.Clamp(hz, 200, 1200);
            if (req.KeyerMode is CwKeyerMode km && Take(CwRevField.KeyerMode))
                // Reject out-of-range enum bytes (defensive — only 0/1/2 are
                // valid gateware modes); fall back to the safe default.
                entry.KeyerMode = Enum.IsDefined(km) ? km : DefaultKeyerMode;
            if (req.BreakIn is bool breakIn && Take(CwRevField.BreakIn))
                entry.BreakIn = breakIn;
            if (req.HangMs is int hangMs && Take(CwRevField.HangMs))
                entry.HangMs = Math.Clamp(hangMs, 0, 1000);
            if (req.Weight is int weight && Take(CwRevField.Weight))
                entry.Weight = Math.Clamp(weight, 33, 66);
            if (req.PaddleReverse is bool paddleReverse && Take(CwRevField.PaddleReverse))
                entry.PaddleReverse = paddleReverse;
            if (req.AutoCq is bool autoCq && Take(CwRevField.AutoCq))
                entry.AutoCq = autoCq;
            if (req.AutoLog is bool autoLog && Take(CwRevField.AutoLog))
                entry.AutoLog = autoLog;
            if (req.ClickToArm is bool clickToArm && Take(CwRevField.ClickToArm))
                entry.ClickToArm = clickToArm;
            if (req.RepeatLimit is int repeatLimit && Take(CwRevField.RepeatLimit))
                entry.RepeatLimit = Math.Clamp(repeatLimit, 1, 9);
            if (req.PartnerIdleSec is int partnerIdleSec && Take(CwRevField.PartnerIdleSec))
                entry.PartnerIdleSec = Math.Clamp(partnerIdleSec, 1, 15);
            if (req.FinalEe is bool finalEe && Take(CwRevField.FinalEe))
                entry.FinalEe = finalEe;
            if (req.SeqCq is not null && Take(CwRevField.SeqCq))
                entry.SeqCq = MessageOrDefault(req.SeqCq, DefaultSeqCq);
            if (req.SeqAnswer is not null && Take(CwRevField.SeqAnswer))
                entry.SeqAnswer = MessageOrDefault(req.SeqAnswer, DefaultSeqAnswer);
            if (req.SeqReport is not null && Take(CwRevField.SeqReport))
                entry.SeqReport = MessageOrDefault(req.SeqReport, DefaultSeqReport);
            if (req.SeqConfirm is not null && Take(CwRevField.SeqConfirm))
                entry.SeqConfirm = MessageOrDefault(req.SeqConfirm, DefaultSeqConfirm);
            if (req.RstToSend is not null && Take(CwRevField.RstToSend))
                entry.RstToSend = NormalizeRst(req.RstToSend);

            // Older clients and the TCI bridge send no rev. That write still
            // lands, including one that changes nothing, as it always has.
            // A revisioned patch that did not win a single field does not
            // bump the snapshot version.
            if (tracked && !appliedAny)
                return new CwSettingsWrite(SnapshotUnlocked(), Applied: false);

            var nextVersion = _settingsVersion + 1;
            entry.SettingsVersion = nextVersion;
            entry.UpdatedUtc = DateTime.UtcNow;
            if (existing is null) _docs.Insert(entry);
            else _docs.Update(entry);
            _settingsVersion = nextVersion;

            return new CwSettingsWrite(ToDto(entry), Applied: true);
        }
    }

    private CwSettingsDto SnapshotUnlocked()
    {
        var e = _docs.FindAll().FirstOrDefault();
        return e is null ? DefaultDto() : ToDto(e);
    }

    // Seed entry carries SettingsVersion 0, which ToDto maps to the live
    // process version, so an empty store still hands out a real version.
    private CwSettingsDto DefaultDto() => ToDto(SeedDefaultsEntry());

    private CwSettingsDto ToDto(CwSettingsEntry e) => new(
        Wpm: e.Wpm,
        FarnsworthWpm: e.FarnsworthWpm,
        Macros: SanitizeStored(e.Macros),
        SidetoneGainDb: e.SidetoneGainDb,
        SidetoneHz: e.SidetoneHz,
        KeyerMode: e.KeyerMode,
        BreakIn: e.BreakIn,
        HangMs: e.HangMs,
        Weight: e.Weight,
        PaddleReverse: e.PaddleReverse,
        AutoCq: e.AutoCq,
        AutoLog: e.AutoLog,
        ClickToArm: e.ClickToArm,
        RepeatLimit: e.RepeatLimit is >= 1 and <= 9 ? e.RepeatLimit : DefaultRepeatLimit,
        PartnerIdleSec: e.PartnerIdleSec is >= 1 and <= 15 ? e.PartnerIdleSec : DefaultPartnerIdleSec,
        FinalEe: e.FinalEe,
        SeqCq: MessageOrDefault(e.SeqCq, DefaultSeqCq),
        SeqAnswer: MessageOrDefault(e.SeqAnswer, DefaultSeqAnswer),
        SeqReport: MessageOrDefault(e.SeqReport, DefaultSeqReport),
        SeqConfirm: MessageOrDefault(e.SeqConfirm, DefaultSeqConfirm),
        RstToSend: NormalizeRst(e.RstToSend),
        SettingsVersion: e.SettingsVersion >= 1 ? e.SettingsVersion : _settingsVersion);

    private readonly record struct RevKey(string Session, CwRevField Field);

    private enum CwRevField
    {
        Wpm,
        FarnsworthWpm,
        Macros,
        SidetoneGainDb,
        SidetoneHz,
        KeyerMode,
        BreakIn,
        HangMs,
        Weight,
        PaddleReverse,
        AutoCq,
        AutoLog,
        ClickToArm,
        RepeatLimit,
        PartnerIdleSec,
        FinalEe,
        SeqCq,
        SeqAnswer,
        SeqReport,
        SeqConfirm,
        RstToSend,
    }

    /// <summary>
    /// "{session}:{seq}". Session is 1..64 of letters, digits, hyphen, and
    /// underscore. Seq is a non-negative integer. Anything else is not a
    /// revision, and the caller applies the body with no ordering.
    /// </summary>
    private static bool TryReadRevision(string? rev, out string session, out long seq)
    {
        session = string.Empty;
        seq = 0;
        if (string.IsNullOrEmpty(rev)) return false;
        var colon = rev.IndexOf(':');
        if (colon <= 0 || colon != rev.LastIndexOf(':') || colon >= rev.Length - 1) return false;
        var sessionPart = rev[..colon];
        if (sessionPart.Length > 64) return false;
        foreach (var c in sessionPart)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok) return false;
        }
        if (!long.TryParse(rev[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;
        session = sessionPart;
        seq = parsed;
        return true;
    }

    public void Dispose() => _dbLease.Dispose();

    private static CwSettingsEntry SeedDefaultsEntry() => new()
    {
        Wpm = DefaultWpm,
        FarnsworthWpm = null,
        Macros = (string[])DefaultMacros.Clone(),
        SidetoneGainDb = DefaultSidetoneGainDb,
        SidetoneHz = DefaultSidetoneHz,
        KeyerMode = DefaultKeyerMode,
        BreakIn = DefaultBreakIn,
        HangMs = DefaultHangMs,
        Weight = DefaultWeight,
        PaddleReverse = DefaultPaddleReverse,
        AutoCq = DefaultAutoCq,
        AutoLog = DefaultAutoLog,
        ClickToArm = DefaultClickToArm,
        RepeatLimit = DefaultRepeatLimit,
        PartnerIdleSec = DefaultPartnerIdleSec,
        FinalEe = DefaultFinalEe,
        SeqCq = DefaultSeqCq,
        SeqAnswer = DefaultSeqAnswer,
        SeqReport = DefaultSeqReport,
        SeqConfirm = DefaultSeqConfirm,
        RstToSend = DefaultRstToSend,
    };

    /// <summary>Normalise a persisted Macros array on read. LiteDB
    /// serialises empty strings as null on the round-trip — translate
    /// back to empty so the wire shape is consistent. Variable length:
    /// honours whatever the operator saved, up to <see cref="MaxMacros"/>.</summary>
    private static string[] SanitizeStored(string[]? persisted)
    {
        if (persisted is null || persisted.Length == 0) return Array.Empty<string>();
        int n = Math.Min(persisted.Length, MaxMacros);
        var result = new string[n];
        for (int i = 0; i < n; i++) result[i] = persisted[i] ?? string.Empty;
        return result;
    }

    /// <summary>Validate + length-cap a user-supplied Macros array. The
    /// length is preserved (operator chooses how many macros to keep —
    /// add and delete are first-class operations) but capped at
    /// <see cref="MaxMacros"/>; per-string content is capped at
    /// <see cref="MaxMacroChars"/>. Null entries become empty strings.</summary>
    private static string[] NormalizeMacros(string[] macros)
    {
        int n = Math.Min(macros.Length, MaxMacros);
        var result = new string[n];
        for (int i = 0; i < n; i++)
        {
            string raw = macros[i] ?? string.Empty;
            result[i] = raw.Length > MaxMacroChars ? raw[..MaxMacroChars] : raw;
        }
        return result;
    }

    private static string MessageOrDefault(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var trimmed = value.Trim();
        return trimmed.Length > MaxMacroChars ? trimmed[..MaxMacroChars] : trimmed;
    }

    private static string NormalizeRst(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultRstToSend;
        var trimmed = value.Trim().ToUpperInvariant();
        return trimmed.Length > 8 ? trimmed[..8] : trimmed;
    }
}

/// <summary>Result of one CW settings write. Settings are always the
/// row as stored after the call. Applied is false when the patch carried
/// a revision and no field in it was newer than the rev already recorded
/// for that field on that session.</summary>
public readonly record struct CwSettingsWrite(CwSettingsDto Settings, bool Applied);

public sealed class CwSettingsEntry
{
    public int Id { get; set; }
    public int Wpm { get; set; } = CwSettingsStore.DefaultWpm;
    public int? FarnsworthWpm { get; set; }
    public string[] Macros { get; set; } = (string[])CwSettingsStore.DefaultMacros.Clone();
    public double SidetoneGainDb { get; set; } = CwSettingsStore.DefaultSidetoneGainDb;
    public int SidetoneHz { get; set; } = CwSettingsStore.DefaultSidetoneHz;
    public CwKeyerMode KeyerMode { get; set; } = CwSettingsStore.DefaultKeyerMode;
    public bool BreakIn { get; set; } = CwSettingsStore.DefaultBreakIn;
    public int HangMs { get; set; } = CwSettingsStore.DefaultHangMs;
    public int Weight { get; set; } = CwSettingsStore.DefaultWeight;
    public bool PaddleReverse { get; set; } = CwSettingsStore.DefaultPaddleReverse;
    public bool AutoCq { get; set; } = CwSettingsStore.DefaultAutoCq;
    public bool AutoLog { get; set; } = CwSettingsStore.DefaultAutoLog;
    public bool ClickToArm { get; set; } = CwSettingsStore.DefaultClickToArm;
    public int RepeatLimit { get; set; } = CwSettingsStore.DefaultRepeatLimit;
    public int PartnerIdleSec { get; set; } = CwSettingsStore.DefaultPartnerIdleSec;
    public bool FinalEe { get; set; } = CwSettingsStore.DefaultFinalEe;
    public string SeqCq { get; set; } = CwSettingsStore.DefaultSeqCq;
    public string SeqAnswer { get; set; } = CwSettingsStore.DefaultSeqAnswer;
    public string SeqReport { get; set; } = CwSettingsStore.DefaultSeqReport;
    public string SeqConfirm { get; set; } = CwSettingsStore.DefaultSeqConfirm;
    public string RstToSend { get; set; } = CwSettingsStore.DefaultRstToSend;
    public long SettingsVersion { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
