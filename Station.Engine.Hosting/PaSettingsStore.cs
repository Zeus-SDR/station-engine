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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using LiteDB;
using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

// PA settings (per-band gain, OC pin masks, globals). Shares the unencrypted
// station-engine.db with BandMemoryStore — neither PA gain values nor OC pin
// assignments are sensitive. Fires Changed on any write so RadioService can
// recompute the drive byte and protocol clients can pick up new OC masks on
// the next C&C/HPC tick.
public sealed class PaSettingsStore : IDisposable
{
    public const int DefaultCalibrationSafetyPercent = 125;
    public const int MinCalibrationSafetyPercent = 105;
    public const int MaxCalibrationSafetyPercent = 200;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PaBandEntry> _bands;
    private readonly ILiteCollection<PaBandDriveEntry> _bandDrive;
    private readonly ILiteCollection<PaGlobalEntry> _globals;
    private readonly ILogger<PaSettingsStore> _log;
    private readonly object _sync = new();
    // Calibration edits must affect the live drive calculation without
    // becoming durable until the entire eleven-band run succeeds.  The
    // overlay is visible to GetAll (and therefore RadioService) but never
    // touches LiteDB; ClearCalibrationOverlay provides atomic rollback.
    private PaSettingsDto? _calibrationOverlay;
    private bool _calibrationCommitInProgress;
    // Dedup key for the cross-board PA-gain substitution warning (issue #1180).
    // PaBandEntry rows are not board-scoped, so a value stored under one board
    // family's semantics survives a session into another board family — fired
    // once per (band, board, variant) tuple on read so the operator sees the
    // substitution in the log without flooding it on every recompute.
    private readonly HashSet<(string Band, HpsdrBoardKind Board, OrionMkIIVariant Variant)> _crossBoardWarned = new();
    // Non-positive dB gain is a separate legacy-Unknown contamination class
    // from an out-of-range cross-board value. Keep its warning distinct.
    private readonly HashSet<(string Band, HpsdrBoardKind Board, OrionMkIIVariant Variant)> _nonPositiveGainWarned = new();
    // A legacy/non-connected session can persist PaMaxPowerWatts=0 in the
    // board-agnostic global row. Warn once per resolved board when read repair
    // substitutes that board/variant's calibrated full-scale target.
    private readonly HashSet<(HpsdrBoardKind Board, OrionMkIIVariant Variant)> _maxPowerWarned = new();

    public event Action? Changed;

    public bool CalibrationOverlayActive
    {
        get
        {
            lock (_sync)
                return _calibrationOverlay is not null || _calibrationCommitInProgress;
        }
    }

    public PaSettingsStore(ILogger<PaSettingsStore> log, string? dbPathOverride = null)
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
        _bands = _db.GetCollection<PaBandEntry>("pa_bands");
        _bands.EnsureIndex(x => x.Band, unique: true);
        _bandDrive = _db.GetCollection<PaBandDriveEntry>("pa_band_drive");
        _bandDrive.EnsureIndex(x => x.Band, unique: true);
        _globals = _db.GetCollection<PaGlobalEntry>("pa_globals");

        _log.LogInformation("PaSettingsStore initialized at {Path}", dbPath);
    }

    // Fills missing bands with per-board defaults from PaDefaults. When board
    // is Unknown (no radio connected yet) the fallback is 0 dB, which keeps the
    // drive math pinned to legacy behavior until connect resolves the board.
    // The variant parameter resolves the 0x0A wire-byte alias collision per
    // issue #218; G2 default preserves pre-#218 behaviour for every other board.
    public PaSettingsDto GetAll(
        HpsdrBoardKind board = HpsdrBoardKind.Unknown,
        OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        lock (_sync)
        {
            if (_calibrationOverlay is not null)
                return _calibrationOverlay;
            var g = _globals.FindAll().FirstOrDefault();
            // When nothing is persisted yet, seed the global with board-specific
            // defaults so new operators don't land in the "PaMaxPowerWatts=0 →
            // PaGainDb ignored" legacy mode on first connect.
            var global = g is null
                ? new PaGlobalSettingsDto(
                    PaEnabled: true,
                    PaMaxPowerWatts: PaDefaults.GetMaxPowerWatts(board, variant),
                    PaCalibrationSafetyPercent: DefaultCalibrationSafetyPercent)
                : new PaGlobalSettingsDto(
                    g.PaEnabled,
                    ResolveMaxPowerWattsForBoard(g.PaMaxPowerWatts, board, variant),
                    NormalizeCalibrationSafetyPercent(g.PaCalibrationSafetyPercent));

            var existing = _bands.FindAll().ToDictionary(e => e.Band, e => e);
            var bands = BandUtils.HfBands
                .Select(b =>
                {
                    var auto = AutoOcMaskFor(board, b);
                    if (existing.TryGetValue(b, out var e))
                    {
                        var gain = ResolvePaGainDbForBoard(e.PaGainDb, e.Band, board, variant);
                        return new PaBandSettingsDto(e.Band, gain, e.DisablePa, e.OcTx, e.OcRx, auto, e.OcDxTx, e.OcDxRx, e.OcTune);
                    }
                    return new PaBandSettingsDto(b, PaGainDb: PaDefaults.GetPaGainDb(board, b, variant), AutoOcMask: auto);
                })
                .ToArray();

            return new PaSettingsDto(global, bands);
        }
    }

    // Pure board defaults — used by the "Reset to defaults" action in the
    // settings panel to stomp any prior per-operator calibration back to the
    // piHPSDR/Thetis-published seed values for the selected radio. Does NOT
    // consult the pa_bands / pa_globals collections; OC masks and DisablePa
    // stay out of this because they're wiring decisions, not per-board data.
    public PaSettingsDto GetDefaults(
        HpsdrBoardKind board,
        OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        var global = new PaGlobalSettingsDto(
            PaEnabled: true,
            PaMaxPowerWatts: PaDefaults.GetMaxPowerWatts(board, variant),
            PaCalibrationSafetyPercent: DefaultCalibrationSafetyPercent);
        var bands = BandUtils.HfBands
            .Select(b => new PaBandSettingsDto(
                b,
                PaGainDb: PaDefaults.GetPaGainDb(board, b, variant),
                AutoOcMask: AutoOcMaskFor(board, b)))
            .ToArray();
        return new PaSettingsDto(global, bands);
    }

    public PaBandSettingsDto GetBand(
        string band,
        HpsdrBoardKind board = HpsdrBoardKind.Unknown,
        OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        lock (_sync)
        {
            var auto = AutoOcMaskFor(board, band);
            var e = _bands.FindOne(x => x.Band == band);
            if (e is null)
                return new PaBandSettingsDto(band, PaGainDb: PaDefaults.GetPaGainDb(board, band, variant), AutoOcMask: auto);
            var gain = ResolvePaGainDbForBoard(e.PaGainDb, e.Band, board, variant);
            return new PaBandSettingsDto(e.Band, gain, e.DisablePa, e.OcTx, e.OcRx, auto, e.OcDxTx, e.OcDxRx, e.OcTune);
        }
    }

    // Sanity-check a stored PA-gain value against the connected board's drive-
    // profile range and substitute the per-board default when the stored value
    // is outside it. The pa_bands collection is not board-scoped, so a value
    // calibrated under one board's semantics (e.g. HL2 stores PaGainDb as a
    // 0..100 percentage) survives into the next session against a different
    // board family (Hermes / ANAN / Orion read PaGainDb as a 0..70 dB forward
    // gain). When HL2's 100 % surfaces on Angelia as "100 dB", FullByteDriveProfile
    // quantises the drive byte to 0 and TX goes silent (issue #1180:
    // pa.recompute gainDb=100.00 -> byte=0 -> drv=0 -> p1 IQ-zero short-circuit
    // at ControlFrame.cs:858 -> no RF). The substitute keeps RecomputePaAndPush
    // sane on the next session; the stored row stays untouched so an explicit
    // Reset → Apply is still required for the operator to persist the
    // board-appropriate value.
    private double ResolvePaGainDbForBoard(double stored, string band, HpsdrBoardKind board, OrionMkIIVariant variant)
    {
        // No connected board yet (preview / pre-discovery) — we can't reason
        // about the right range, so leave the stored value as-is.
        if (board == HpsdrBoardKind.Unknown) return stored;

        // HL2: 0..100 percentage (HermesLite2DriveProfile). Everything else:
        // dB forward gain. The PA Settings panel clamps non-HL2 input to
        // 0..70 dB (see docs/lessons/hl2-drive-model.md), so any persisted
        // value > 70 on a dB board is necessarily cross-board contamination.
        bool isHl2 = board == HpsdrBoardKind.HermesLite2;
        double upper = isHl2 ? 100.0 : 70.0;
        if (!isHl2 && stored <= 0.0)
        {
            var seededGain = PaDefaults.GetPaGainDb(board, band, variant);
            // Every supported non-HL2 board has a positive gain-table seed.
            // If a future board is missing one, preserve the stored value
            // rather than claiming that another non-positive value repaired it.
            if (seededGain <= 0.0) return stored;
            if (_nonPositiveGainWarned.Add((band, board, variant)))
            {
                _log.LogWarning(
                    "pa.gain.nonpositive_substituted band={Band} board={Board} variant={Variant} stored={Stored:F2} -> using per-board default {Fallback:F2}. The non-positive dB gain was persisted before a supported board was resolved; the value in pa_bands is unchanged.",
                    band, board, variant, stored, seededGain);
            }
            return seededGain;
        }
        if (stored >= 0.0 && stored <= upper) return stored;

        var fallback = PaDefaults.GetPaGainDb(board, band, variant);
        if (fallback <= 0.0) return stored;
        if (_crossBoardWarned.Add((band, board, variant)))
        {
            _log.LogWarning(
                "pa.gain.cross_board_substituted band={Band} board={Board} variant={Variant} stored={Stored:F2} validUpper={Upper:F1} -> using per-board default {Fallback:F2}. The value in pa_bands was persisted under a different board's semantics (likely HL2 ↔ Hermes/ANAN/Orion). Open PA Settings and press \"Reset to defaults\" then APPLY to overwrite the row.",
                band, board, variant, stored, upper, fallback);
        }
        return fallback;
    }

    private int ResolveMaxPowerWattsForBoard(
        int stored,
        HpsdrBoardKind board,
        OrionMkIIVariant variant)
    {
        // Unknown/unsupported hardware deliberately retains the raw-byte
        // fallback represented by a non-positive value.
        if (stored > 0 || board == HpsdrBoardKind.Unknown) return stored;

        var fallback = PaDefaults.GetMaxPowerWatts(board, variant);
        if (_maxPowerWarned.Add((board, variant)))
        {
            _log.LogWarning(
                "pa.max_power.nonpositive_substituted board={Board} variant={Variant} stored={Stored} -> using per-board default {Fallback}. The value in pa_globals is unchanged; open PA Settings and press APPLY to persist a positive value.",
                board, variant, stored, fallback);
        }
        return fallback;
    }

    // Read-only mirror of the on-wire auto-filter mask for the connected
    // board. Today only HL2 ships a board with an auto-mask path (N2ADR,
    // forced-on in RadioService.ConnectAsync). The PA Settings panel uses
    // this to show operators which OC pins are already being driven by the
    // firmware before they layer their own OcTx/OcRx wiring on top — closes
    // the perception gap from issue #217 where empty checkboxes implied no
    // pins were active.
    private static byte AutoOcMaskFor(HpsdrBoardKind board, string band) =>
        board == HpsdrBoardKind.HermesLite2
            ? N2adrBands.RxOcMaskForBand(band)
            : (byte)0;

    public PaGlobalSettingsDto GetGlobal(
        HpsdrBoardKind board = HpsdrBoardKind.Unknown,
        OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        lock (_sync)
        {
            var g = _globals.FindAll().FirstOrDefault();
            return g is null
                ? new PaGlobalSettingsDto(
                    PaEnabled: true,
                    PaMaxPowerWatts: PaDefaults.GetMaxPowerWatts(board, variant),
                    PaCalibrationSafetyPercent: DefaultCalibrationSafetyPercent)
                : new PaGlobalSettingsDto(
                    g.PaEnabled,
                    ResolveMaxPowerWattsForBoard(g.PaMaxPowerWatts, board, variant),
                    NormalizeCalibrationSafetyPercent(g.PaCalibrationSafetyPercent));
        }
    }

    // Per-band Drive/Tune slider recall (#128). Read as a nullable tuple so
    // callers can distinguish "never touched" (null → leave the current slider
    // alone on band change) from a stored value.
    public (int? DrivePct, int? TunePct) GetBandDrive(string band)
    {
        lock (_sync)
        {
            var e = _bandDrive.FindOne(x => x.Band == band);
            return (e?.DrivePct, e?.TunePct);
        }
    }

    // Persist the Drive slider position in its own collection. pa_bands holds
    // operator PA calibration and OC wiring only; slider recall must never
    // fabricate a calibration row. Clamped to 0..100 to match the slider range
    // enforced at the /api/tx/drive endpoint.
    public void SetBandDrive(string band, int drivePct)
    {
        int clamped = Math.Clamp(drivePct, 0, 100);
        lock (_sync)
        {
            var existing = _bandDrive.FindOne(x => x.Band == band);
            if (existing is null)
            {
                _bandDrive.Insert(new PaBandDriveEntry
                {
                    Band = band,
                    DrivePct = clamped,
                    UpdatedUtc = DateTime.UtcNow,
                });
                return;
            }
            existing.DrivePct = clamped;
            existing.UpdatedUtc = DateTime.UtcNow;
            _bandDrive.Update(existing);
        }
    }

    // TUN drive % counterpart to SetBandDrive.
    public void SetBandTune(string band, int tunePct)
    {
        int clamped = Math.Clamp(tunePct, 0, 100);
        lock (_sync)
        {
            var existing = _bandDrive.FindOne(x => x.Band == band);
            if (existing is null)
            {
                _bandDrive.Insert(new PaBandDriveEntry
                {
                    Band = band,
                    TunePct = clamped,
                    UpdatedUtc = DateTime.UtcNow,
                });
                return;
            }
            existing.TunePct = clamped;
            existing.UpdatedUtc = DateTime.UtcNow;
            _bandDrive.Update(existing);
        }
    }

    public void Save(PaSettingsDto dto) => Save(dto, calibrationCommit: false);

    private void Save(PaSettingsDto dto, bool calibrationCommit)
    {
        lock (_sync)
        {
            if (!calibrationCommit &&
                (_calibrationOverlay is not null || _calibrationCommitInProgress))
                throw new InvalidOperationException(
                    "PA settings are locked while calibration is running.");
            if (!_db.BeginTrans())
                throw new InvalidOperationException(
                    "Could not begin the PA settings transaction.");
            try
            {
                var existingGlobal = _globals.FindAll().FirstOrDefault();
                var g = existingGlobal ?? new PaGlobalEntry();
                g.PaEnabled = dto.Global.PaEnabled;
                g.PaMaxPowerWatts = Math.Max(0, dto.Global.PaMaxPowerWatts);
                g.PaCalibrationSafetyPercent = NormalizeCalibrationSafetyPercent(
                    dto.Global.PaCalibrationSafetyPercent);
                g.UpdatedUtc = DateTime.UtcNow;
                if (existingGlobal is null) _globals.Insert(g);
                else _globals.Update(g);

                foreach (var band in dto.Bands)
                {
                    if (!BandUtils.HfBands.Contains(band.Band)) continue;
                    var existing = _bands.FindOne(x => x.Band == band.Band);
                    byte dxTx = (byte)(band.OcDxTx & 0x0F);
                    byte dxRx = (byte)(band.OcDxRx & 0x0F);
                    byte tune = (byte)(band.OcTune & 0x7F);
                    byte tx = (byte)(band.OcTx & 0x7F);
                    byte rx = (byte)(band.OcRx & 0x7F);
                    if (existing is null)
                    {
                        _bands.Insert(new PaBandEntry
                        {
                            Band = band.Band,
                            PaGainDb = band.PaGainDb,
                            DisablePa = band.DisablePa,
                            OcTx = tx,
                            OcRx = rx,
                            OcDxTx = dxTx,
                            OcDxRx = dxRx,
                            OcTune = tune,
                            UpdatedUtc = DateTime.UtcNow,
                        });
                    }
                    else
                    {
                        existing.PaGainDb = band.PaGainDb;
                        existing.DisablePa = band.DisablePa;
                        existing.OcTx = tx;
                        existing.OcRx = rx;
                        existing.OcDxTx = dxTx;
                        existing.OcDxRx = dxRx;
                        existing.OcTune = tune;
                        existing.UpdatedUtc = DateTime.UtcNow;
                        _bands.Update(existing);
                    }
                }
                _db.Commit();
            }
            catch
            {
                _db.Rollback();
                throw;
            }
        }
        Changed?.Invoke();
    }

    public PaSettingsDto BeginCalibrationOverlay(
        HpsdrBoardKind board,
        OrionMkIIVariant variant)
    {
        PaSettingsDto snapshot;
        lock (_sync)
        {
            if (_calibrationOverlay is not null || _calibrationCommitInProgress)
                throw new InvalidOperationException(
                    "PA calibration overlay is already active.");
            snapshot = GetAll(board, variant);
            _calibrationOverlay = snapshot;
        }
        Changed?.Invoke();
        return snapshot;
    }

    public void BeginCalibrationOverlay(PaSettingsDto snapshot)
    {
        lock (_sync)
        {
            if (_calibrationOverlay is not null || _calibrationCommitInProgress)
                throw new InvalidOperationException("PA calibration overlay is already active.");
            _calibrationOverlay = snapshot;
        }
        Changed?.Invoke();
    }

    public void SetCalibrationGain(string band, double paGainDb)
    {
        lock (_sync)
        {
            var current = _calibrationOverlay
                ?? throw new InvalidOperationException("PA calibration overlay is not active.");
            var bands = current.Bands
                .Select(row => row.Band == band ? row with { PaGainDb = paGainDb } : row)
                .ToArray();
            _calibrationOverlay = current with { Bands = bands };
        }
        Changed?.Invoke();
    }

    public void CompleteCalibrationOverlay(bool persist)
    {
        PaSettingsDto? completed;
        bool refreshAfterFailedCommit = false;
        lock (_sync)
        {
            completed = _calibrationOverlay;
            _calibrationOverlay = null;
            _calibrationCommitInProgress = persist && completed is not null;
        }

        try
        {
            if (persist && completed is not null)
                Save(completed, calibrationCommit: true);
            else
                Changed?.Invoke();
        }
        catch
        {
            // Save rolled its transaction back, but RadioService last observed
            // the transient overlay. Force it to recompute from durable values
            // so a failed commit cannot leave calibration drive live on-air.
            refreshAfterFailedCommit = true;
            throw;
        }
        finally
        {
            lock (_sync) _calibrationCommitInProgress = false;
            if (refreshAfterFailedCommit)
            {
                try { Changed?.Invoke(); }
                catch (Exception ex)
                {
                    _log.LogError(ex,
                        "pa.calibration.rollback_refresh.failed");
                }
            }
        }
    }

    internal static int NormalizeCalibrationSafetyPercent(int percent) =>
        percent is >= MinCalibrationSafetyPercent and <= MaxCalibrationSafetyPercent
            ? percent
            : DefaultCalibrationSafetyPercent;

    public void Dispose() => _dbLease.Dispose();

}

// Resolved snapshot that RadioService pushes to the P1 client directly and to
// the P2 client via DspPipelineService. Keeps the protocol clients free of
// any knowledge of per-band gain or Stores.
//
// OcDxTxMask / OcDxRxMask carry the Anvelina-PRO3 DX OUT 7..10 wiring (4-bit
// masks, bit 0..3 = DX OUT 7..10). Pushed unconditionally; Protocol2Client
// gates whether they reach the wire by board + variant (#407 / EU2AV).
//
// TxAntenna / RxAntenna / HasTxAntennaRelays / RxAuxInput / MkiiBpfRxSelect
// carry the per-band external-antenna selection (external-ports plan — antenna
// slice, #804). Pushed unconditionally to the P2 client via
// DspPipelineService.SetAntennas; Protocol2Client gates the TX-antenna emission
// on HasTxAntennaRelays and routes the operator RX-aux strictly BEFORE the PS
// coupler OR (the PS-K36 firewall). All defaulted so existing constructions stay
// valid (default ANT1/ANT1/None = byte-identical to today).
//
// RfFilters carries the normalized Thetis-style RF filter matrix. Null means
// Protocol2Client uses its built-in Alex BPF/LPF tables exactly as before.
public sealed record PaRuntimeSnapshot(
    byte DriveByte,
    byte OcTxMask,
    byte OcRxMask,
    bool PaEnabled,
    byte OcDxTxMask = 0,
    byte OcDxRxMask = 0,
    HpsdrAntenna TxAntenna = HpsdrAntenna.Ant1,
    HpsdrAntenna RxAntenna = HpsdrAntenna.Ant1,
    bool HasTxAntennaRelays = false,
    int RxAuxInput = 0,
    bool MkiiBpfRxSelect = false,
    RfFilterRuntimeSettings? RfFilters = null,
    // Dedicated XVTR T/R output. Independent of the RX auxiliary input so a
    // receive-path selection cannot accidentally assert a transmit relay.
    bool XvtrEnabled = false,
    // Per-band OC-TUNE additive mask (issue #1325). OR'd on top of OcTxMask
    // while TUN is active; ignored during regular MOX / RX. Default 0 keeps
    // pre-#1325 behaviour byte-for-byte.
    byte OcTuneMask = 0);

public sealed class PaBandEntry
{
    public int Id { get; set; }
    public string Band { get; set; } = string.Empty;
    public double PaGainDb { get; set; }
    public bool DisablePa { get; set; }
    public byte OcTx { get; set; }
    public byte OcRx { get; set; }
    // Anvelina DX OUT 7..10 per-band masks (issue #407). LiteDB is schema-
    // less so rows persisted before #407 hydrate these as 0, which is the
    // correct legacy default. Wire-encoded into P2 byte 1397 bits [4:1]
    // only when the active radio is OrionMkII + AnvelinaPro3 on P2.
    public byte OcDxTx { get; set; }
    public byte OcDxRx { get; set; }
    // Per-band additive mask asserted ON TOP OF OcTx while TUN is active
    // (issue #1325). Rows persisted before #1325 hydrate as 0, which is the
    // pre-#1325 wire behaviour.
    public byte OcTune { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class PaBandDriveEntry
{
    public int Id { get; set; }
    public string Band { get; set; } = string.Empty;
    public int? DrivePct { get; set; }
    public int? TunePct { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class PaGlobalEntry
{
    public int Id { get; set; }
    public bool PaEnabled { get; set; } = true;
    public int PaMaxPowerWatts { get; set; }
    public int PaCalibrationSafetyPercent { get; set; } =
        PaSettingsStore.DefaultCalibrationSafetyPercent;
    // NOTE: legacy rows persisted before #124 may carry an `OcTune` column.
    // LiteDB's BsonMapper silently ignores unknown fields when deserializing,
    // so existing PaSettings rows survive a load → save roundtrip with the
    // column dropped on the next write. The global "OC bits while Tune"
    // override was removed for hardware-safety (issue #124): it could hand
    // an external amp a confused band-select state during a steady tune
    // carrier and damage the finals. OC during TUN now follows OcTx.
    public DateTime UpdatedUtc { get; set; }
}
