// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

internal enum TransmitIntent
{
    Mox,
    Tun,
    TwoTone,
    HardwareCw,
}

internal enum TransmitSafetyReasonCode
{
    Allowed,
    NotConnected,
    UnknownBoard,
    UnknownBoardVariant,
    OutOfBand,
    TransverterProfile,
    ReceiveOnly,
    CrossIntentActive,
    FaultLatched,
    SwrTrip,
    TxTimeout,
}

internal enum ProtectionEvidenceState
{
    Available,
    Unavailable,
    Invalid,
    Stale,
}

internal readonly record struct TxEmissionEnvelope(long LowHz, long HighHz)
{
    public TxEmissionEnvelope Normalize() => LowHz <= HighHz ? this : new(HighHz, LowHz);
}

internal sealed record TransmitSafetySnapshot(
    StateDto State,
    bool Connected,
    HpsdrBoardKind Board,
    OrionMkIIVariant Variant,
    BandRegion Region,
    IReadOnlyList<BandSegment> Plan,
    bool RegulatoryOverride,
    TransmitIntent? ActiveIntent,
    MoxSource? Source = null,
    bool TransverterProfileRequired = false,
    TransverterBandDto? TransverterBand = null);

internal readonly record struct TransmitSafetyDecision(
    bool Allowed,
    TransmitSafetyReasonCode ReasonCode,
    string OperatorText,
    TxEmissionEnvelope Envelope,
    int EffectiveDrivePercent)
{
    public static TransmitSafetyDecision Allow(TxEmissionEnvelope envelope, int effectiveDrivePercent = 100) =>
        new(true, TransmitSafetyReasonCode.Allowed, string.Empty, envelope, effectiveDrivePercent);

    public static TransmitSafetyDecision Deny(
        TransmitSafetyReasonCode reasonCode,
        string operatorText,
        TxEmissionEnvelope envelope = default) =>
        new(false, reasonCode, operatorText, envelope, 0);
}

internal readonly record struct ProtectionSample(
    ProtectionEvidenceState SwrEvidence,
    double Swr,
    DateTime Now,
    TransmitIntent Intent,
    DateTime KeyedAt,
    int TimeoutSeconds,
    // Measured forward power (W) and the current tune-drive request (%) gate
    // the low-power TUN SWR bypass. Defaults are deliberately fail-safe — a
    // sample built without power context reads as "high power", so protection
    // is NOT bypassed unless a caller supplies genuinely low readings.
    double FwdWatts = double.MaxValue,
    int TuneDrivePercent = 100);

internal readonly record struct ProtectionDecision(
    bool Trip,
    AlertKind? AlertKind,
    TransmitSafetyReasonCode ReasonCode,
    string? OperatorText)
{
    public static readonly ProtectionDecision None = new(false, null, TransmitSafetyReasonCode.Allowed, null);
}

/// <summary>
/// Engine-owned transmit policy and protection monitor. It evaluates immutable
/// snapshots only; TxService remains the sole executor of DSP and radio edges.
/// </summary>
internal sealed class EngineTransmitSafetyModule
{
    internal const double SwrTripThresholdMox = 2.5;
    internal const double SwrTripThresholdTun = 6.0;
    internal static readonly TimeSpan SwrTripDurationMox = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan SwrTripDurationTun = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan SwrStartupGraceMox = TimeSpan.FromMilliseconds(300);
    internal static readonly TimeSpan SwrStartupGraceTun = TimeSpan.FromMilliseconds(500);

    // Thetis-style low-power TUN SWR bypass. An external ATU (e.g. an LDG
    // autotuner) deliberately presents a wildly mismatched load while it hunts
    // for a match, and a slow tuner can search for several seconds — longer
    // than the 500 ms sustain window — which nuisance-trips today's flat
    // 6.0:1 TUN guard (issue #1659). Thetis suppresses SWR protection during
    // TUN, but only while the transmitted power stays low enough that a bad
    // match cannot harm the PA. Zeus mirrors that: while TUN is active AND
    // both the measured forward power and the tune-drive request stay within
    // these guarded limits, the SWR trip is suppressed so the tuner can hunt.
    // Above either limit the normal 6.0:1 / 500 ms TUN trip still applies, MOX
    // protection is untouched, and the overall TX timeout always bounds the
    // transmission. This is NOT a blanket time grace — extreme SWR at real
    // power still drops the PA immediately.
    internal const double SwrTripTunBypassMaxFwdWatts = 25.0;
    internal const int SwrTripTunBypassMaxDrivePercent = 25;

    private readonly object _sync = new();
    private DateTime? _swrAboveThresholdSince;
    private DateTime? _timeoutWarningForKeyedAt;
    private long _faultEpoch;
    private bool _faultLatched;

    public long FaultEpoch { get { lock (_sync) return _faultEpoch; } }
    public bool FaultLatched { get { lock (_sync) return _faultLatched; } }
    public ProtectionEvidenceState Protocol3SwrEvidence => ProtectionEvidenceState.Unavailable;
    public ProtectionEvidenceState ThermalEvidence => ProtectionEvidenceState.Unavailable;

    public TransmitSafetyDecision EvaluateKeyOn(TransmitIntent intent, TransmitSafetySnapshot snapshot)
    {
        var envelope = TxEmissionEnvelopeResolver.Resolve(intent, snapshot.State, snapshot.Source);

        if (!snapshot.Connected)
            return TransmitSafetyDecision.Deny(TransmitSafetyReasonCode.NotConnected, "not connected", envelope);
        if (snapshot.Board == HpsdrBoardKind.Unknown)
            return TransmitSafetyDecision.Deny(
                TransmitSafetyReasonCode.UnknownBoard,
                "TX blocked: connected radio identity is unknown; receive remains available",
                envelope);
        if (snapshot.Board == HpsdrBoardKind.OrionMkII && !Enum.IsDefined(snapshot.Variant))
            return TransmitSafetyDecision.Deny(
                TransmitSafetyReasonCode.UnknownBoardVariant,
                "TX blocked: Orion-MkII physical variant is unknown",
                envelope);
        if (snapshot.ActiveIntent is { } active && active != intent)
            return TransmitSafetyDecision.Deny(
                TransmitSafetyReasonCode.CrossIntentActive,
                $"TX blocked: {active} is active; unkey before starting {intent}",
                envelope);

        // A transverter selection is an equipment-safety gate, not a
        // regulatory preference. TxGuardIgnore must never bypass a missing
        // profile, a receive-only profile, or a filter/XIT envelope that
        // leaves the configured RF range.
        if (snapshot.TransverterProfileRequired)
        {
            if (snapshot.TransverterBand is not { } xvtr)
                return TransmitSafetyDecision.Deny(
                    TransmitSafetyReasonCode.TransverterProfile,
                    "TX blocked: no active transverter profile covers the transmit frequency",
                    envelope);
            if (xvtr.RxOnly)
                return TransmitSafetyDecision.Deny(
                    TransmitSafetyReasonCode.ReceiveOnly,
                    $"TX blocked: transverter profile {xvtr.ButtonText} is receive-only",
                    envelope);
            var normalized = envelope.Normalize();
            if (normalized.LowHz < xvtr.BeginFrequencyHz
                || normalized.HighHz > xvtr.EndFrequencyHz)
            {
                return TransmitSafetyDecision.Deny(
                    TransmitSafetyReasonCode.TransverterProfile,
                    $"TX blocked: emission envelope leaves transverter profile {xvtr.ButtonText}",
                    envelope);
            }
        }

        // TxGuardIgnore is deliberately regulatory-only. It never bypasses
        // connection, identity, source ownership, protection, or fault policy.
        if (!snapshot.RegulatoryOverride && !EnvelopeAllowed(snapshot, envelope))
        {
            return TransmitSafetyDecision.Deny(
                TransmitSafetyReasonCode.OutOfBand,
                $"TX blocked: {envelope.LowHz / 1_000_000.0:F6}-{envelope.HighHz / 1_000_000.0:F6} MHz emission envelope is not allowed for mode {snapshot.State.Mode} in region {snapshot.Region.DisplayName}",
                envelope);
        }

        return TransmitSafetyDecision.Allow(envelope);
    }

    public TransmitSafetyDecision RevalidateActive(
        TransmitIntent intent,
        TransmitSafetySnapshot snapshot) => EvaluateKeyOn(intent, snapshot with { ActiveIntent = intent });

    public TransmitSafetyDecision EvaluateHardwareCwArm(TransmitSafetySnapshot snapshot)
    {
        lock (_sync)
        {
            if (_faultLatched)
            {
                return TransmitSafetyDecision.Deny(
                    TransmitSafetyReasonCode.FaultLatched,
                    "hardware CW keyer remains disarmed after a protection trip until a new explicit transmit request is admitted");
            }
        }
        return EvaluateKeyOn(TransmitIntent.HardwareCw, snapshot);
    }

    /// <summary>
    /// Wave-0 found no approved per-board safe ceiling. Until a table is
    /// separately approved, known boards retain today's 0..100 request domain;
    /// unknown identities resolve to zero and PA-off at the executor boundary.
    /// </summary>
    public TransmitSafetyDecision ResolveEffectiveDrive(int requestedPercent, TransmitSafetySnapshot snapshot)
    {
        var decision = ResolveEffectiveDrive(requestedPercent, snapshot.Board, snapshot.Variant);
        if (!decision.Allowed || snapshot.TransverterBand is not { } xvtr)
            return decision;
        return decision with
        {
            EffectiveDrivePercent = Math.Min(decision.EffectiveDrivePercent, xvtr.Power),
        };
    }

    internal static TransmitSafetyDecision ResolveEffectiveDrive(
        int requestedPercent,
        HpsdrBoardKind board,
        OrionMkIIVariant variant)
    {
        if (board == HpsdrBoardKind.Unknown)
            return TransmitSafetyDecision.Deny(
                TransmitSafetyReasonCode.UnknownBoard,
                "TX drive inhibited: connected radio identity is unknown");
        if (board == HpsdrBoardKind.OrionMkII && !Enum.IsDefined(variant))
            return TransmitSafetyDecision.Deny(
                TransmitSafetyReasonCode.UnknownBoardVariant,
                "TX drive inhibited: Orion-MkII physical variant is unknown");
        return TransmitSafetyDecision.Allow(default, Math.Clamp(requestedPercent, 0, 100));
    }

    public long AdmitExplicitRequest()
    {
        lock (_sync)
        {
            _faultLatched = false;
            return _faultEpoch;
        }
    }

    public long RecordTrip()
    {
        lock (_sync)
        {
            _faultLatched = true;
            _swrAboveThresholdSince = null;
            _timeoutWarningForKeyedAt = null;
            return ++_faultEpoch;
        }
    }

    public void ObserveConfirmedIdle()
    {
        lock (_sync)
        {
            _swrAboveThresholdSince = null;
            _timeoutWarningForKeyedAt = null;
        }
    }

    public ProtectionDecision ObserveProtection(ProtectionSample sample)
    {
        var timeout = ObserveTimeout(sample.Now, sample.Intent, sample.KeyedAt, sample.TimeoutSeconds);
        if (timeout.Trip) return timeout;

        if (sample.SwrEvidence != ProtectionEvidenceState.Available)
        {
            lock (_sync) _swrAboveThresholdSince = null;
            return ProtectionDecision.None;
        }

        bool isTun = sample.Intent == TransmitIntent.Tun;

        // Low-power TUN bypass (see the constants above): while tuning into an
        // external ATU at low power, let the tuner hunt through a bad match
        // without dropping the PA. The TX-timeout guard (already evaluated
        // above) remains the sole bound on transmission length here.
        if (isTun
            && sample.FwdWatts <= SwrTripTunBypassMaxFwdWatts
            && sample.TuneDrivePercent <= SwrTripTunBypassMaxDrivePercent)
        {
            lock (_sync) _swrAboveThresholdSince = null;
            return ProtectionDecision.None;
        }

        var threshold = isTun ? SwrTripThresholdTun : SwrTripThresholdMox;
        var sustain = isTun ? SwrTripDurationTun : SwrTripDurationMox;
        var grace = isTun ? SwrStartupGraceTun : SwrStartupGraceMox;

        lock (_sync)
        {
            if (sample.Now - sample.KeyedAt < grace)
            {
                _swrAboveThresholdSince = null;
                return ProtectionDecision.None;
            }
            if (sample.Swr <= threshold)
            {
                _swrAboveThresholdSince = null;
                return ProtectionDecision.None;
            }
            if (_swrAboveThresholdSince is null)
            {
                _swrAboveThresholdSince = sample.Now;
                return ProtectionDecision.None;
            }
            if (sample.Now - _swrAboveThresholdSince.Value < sustain)
                return ProtectionDecision.None;

            _swrAboveThresholdSince = null;
            return new ProtectionDecision(
                true,
                AlertKind.SwrTrip,
                TransmitSafetyReasonCode.SwrTrip,
                $"SWR {sample.Swr:F1}:1 sustained >{(int)sustain.TotalMilliseconds} ms — dropped TX to protect PA");
        }
    }

    public ProtectionDecision ObserveTimeout(
        DateTime now,
        TransmitIntent intent,
        DateTime keyedAt,
        int timeoutSeconds)
    {
        if (timeoutSeconds <= 0 || now - keyedAt < TimeSpan.FromSeconds(timeoutSeconds))
            return ProtectionDecision.None;

        string label = intent == TransmitIntent.Tun ? "TUN" : "MOX";
        return new ProtectionDecision(
            true,
            AlertKind.TxTimeout,
            TransmitSafetyReasonCode.TxTimeout,
            $"TX timeout: {label} keyed >{timeoutSeconds} s — dropped to protect PA");
    }

    public string? ObserveTimeoutWarning(DateTime now, DateTime? keyedAt, TransmitIntent? intent, int timeoutSeconds)
    {
        lock (_sync)
        {
            if (timeoutSeconds <= 0 || keyedAt is null || intent is null)
            {
                _timeoutWarningForKeyedAt = null;
                return null;
            }

            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            var lead = TimeSpan.FromSeconds(30);
            var minLead = TimeSpan.FromSeconds(5);
            if (lead >= timeout) lead = timeout - minLead;
            if (lead < TimeSpan.Zero) lead = TimeSpan.Zero;
            var elapsed = now - keyedAt.Value;
            if (elapsed < timeout - lead || elapsed >= timeout) return null;
            if (_timeoutWarningForKeyedAt == keyedAt) return null;

            _timeoutWarningForKeyedAt = keyedAt;
            int secondsRemaining = Math.Max(1, (int)Math.Ceiling((timeout - elapsed).TotalSeconds));
            string label = intent == TransmitIntent.Tun ? "TUN" : "MOX";
            return $"TX timeout warning: {secondsRemaining} s remaining — un-key or {label} will be dropped to protect PA";
        }
    }

    private static bool EnvelopeAllowed(TransmitSafetySnapshot snapshot, TxEmissionEnvelope rawEnvelope)
    {
        var envelope = rawEnvelope.Normalize();
        foreach (var segment in snapshot.Plan)
        {
            if (segment.Allocation != BandAllocation.Amateur) continue;
            if (!BandPlanService.ModeMatchesRestriction(snapshot.State.Mode, segment.ModeRestriction)) continue;

            // Internal half-open form adapts the present inclusive contract by
            // making HighHz+1 the exclusive end. This gives exact integer-Hz
            // ownership without changing Zeus.Contracts or persisted plans.
            long highExclusive = segment.HighHz == long.MaxValue ? long.MaxValue : segment.HighHz + 1;
            if (envelope.LowHz >= segment.LowHz && envelope.HighHz < highExclusive)
                return true;
        }
        return false;
    }
}

internal static class TxEmissionEnvelopeResolver
{
    // WDSP TXA.c constructs FM with 5 kHz deviation; its audio high edge is
    // supplied by the live TX filter. Carson containment is deviation + fmax.
    private const int FmDeviationHz = 5_000;

    public static TxEmissionEnvelope Resolve(TransmitIntent intent, StateDto state, MoxSource? source = null)
    {
        long baseCarrier = RadioFrequencyResolver.TxFrequencyHz(state);
        long carrier = source == MoxSource.Cwx
            ? baseCarrier // Decision 14: current host-CW base-VFO semantics remain.
            : RadioService.TxCarrierHz(state);

        if (intent == TransmitIntent.HardwareCw || source == MoxSource.Cwx)
            return new TxEmissionEnvelope(carrier, carrier);

        // TUN is an unmodulated carrier. Treating it as the current voice
        // filter would reject legal band-edge carrier tests that emit no
        // energy across that filter width.
        if (intent == TransmitIntent.Tun)
            return new TxEmissionEnvelope(carrier, carrier);

        int loAbs = Math.Min(Math.Abs(state.TxFilterLowHz), Math.Abs(state.TxFilterHighHz));
        int hiAbs = Math.Max(Math.Abs(state.TxFilterLowHz), Math.Abs(state.TxFilterHighHz));
        var txMode = RadioFrequencyResolver.TxMode(state);
        var effectiveMode = RadioService.EffectiveEngineMode(txMode, baseCarrier);
        var (low, high) = RadioService.SignedFilterForMode(effectiveMode, loAbs, hiAbs);

        if (intent == TransmitIntent.TwoTone)
        {
            bool lsb = effectiveMode is RxMode.LSB or RxMode.CWL or RxMode.DIGL;
            long f1 = (long)Math.Round(Math.Clamp(state.TwoToneFreq1, 50.0, 5_000.0));
            long f2 = (long)Math.Round(Math.Clamp(state.TwoToneFreq2, 50.0, 5_000.0));
            if (lsb) { f1 = -f1; f2 = -f2; }
            return new TxEmissionEnvelope(carrier + Math.Min(f1, f2), carrier + Math.Max(f1, f2));
        }

        if (txMode == RxMode.FM)
        {
            long extent = FmDeviationHz + hiAbs;
            return new TxEmissionEnvelope(carrier - extent, carrier + extent);
        }

        return new TxEmissionEnvelope(carrier + low, carrier + high).Normalize();
    }
}

internal sealed class TransmitSafetyRejectedException(string message) : InvalidOperationException(message);

/// <summary>
/// Revision token checked at the common P2/P3 egress and again immediately
/// before each transport send. Policy admission alone never opens this gate;
/// TxService commits it only after the matching wire transition succeeds.
/// </summary>
internal sealed class TransmitEgressGate
{
    private long _committedRevision;

    public long CommittedRevision => Interlocked.Read(ref _committedRevision);
    public bool IsCurrent(long revision) => revision > 0 && Interlocked.Read(ref _committedRevision) == revision;
    public void Commit(long revision) => Interlocked.Exchange(ref _committedRevision, revision);
    public void Revoke() => Interlocked.Exchange(ref _committedRevision, 0);
}
