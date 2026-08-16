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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server;

public sealed class TxService
{
    private readonly RadioService _radio;
    private readonly DspPipelineService _pipeline;
    private readonly StreamingHub _hub;
    private readonly IBandPlanService _bandPlan;
    private readonly ILogger<TxService> _log;
    private readonly Func<long> _stopwatchTicks;
    private readonly EngineTransmitSafetyModule _safety = new();
    private readonly object _sync = new();
    // Serializes complete state/DSP/wire transactions against trips. This is
    // deliberately separate from _sync (small host-state reads) and from the
    // policy monitor lock; blocking DSP work never holds either of those.
    private readonly object _transitionSync = new();
    private TransmitIntent? _activeIntent;
    private long _transitionRevision;
    private bool _moxOn;
    private bool _tunOn;
    // Session-only operator intent for Thetis-style MON during ordinary MOX.
    // TUN and two-tone clear it alongside Preview so their generators never
    // become audible through a stale voice-TX monitor choice.
    private int _monitorOnTransmit;
    private DateTime? _moxStartedAt;
    private DateTime? _tunStartedAt;
    // Who currently owns MOX, set on the rising edge and cleared on the
    // falling one. See <see cref="MoxSource"/> for the release rule: only
    // the owning source can drop MOX, except UI (master override) and
    // <see cref="TryTripForAlert"/> (always wins). Null when MOX is off.
    private MoxSource? _moxOwner;
    private long _productPluginMoxGeneration;
    private MoxSource? _tunOwner;
    // TX pre-key (MOX/TUNE) delay window deadline, in Stopwatch ticks (issue #630).
    // 0 = no active window. Armed on a UI voice-MOX or UI TUNE rising edge when
    // RadioService.TxMoxPreKeyDelayMs > 0; the IQ producers substitute silence
    // for modulated IQ until the deadline so an external amp's T/R relay settles
    // before RF appears. The window is cleared on every MOX-off, TUN-off,
    // two-tone, and trip so a stale deadline can never bleed into a later
    // transmission. Written under _sync, read lock-free via Interlocked elsewhere.
    private long _preKeyOpenAtTicks;

    /// <summary>Stopwatch-tick deadline until which modulated TX IQ should be
    /// muted (silence substituted) after a UI MOX/TUNE key-down. 0 = no mute.
    /// Read by <see cref="TxAudioIngest"/> and <see cref="TxTuneDriver"/> on
    /// their producer threads.</summary>
    public long PreKeyOpenAtTicks => Interlocked.Read(ref _preKeyOpenAtTicks);

    /// <summary>Apply or clear the engine-owned, lease-scoped product injection
    /// drive cap. This never mutates or persists the operator's DrivePct.</summary>
    internal void SetProductPluginDriveCap(long generation, int? percent) =>
        _radio.SetProductPluginDriveCap(generation, percent);

    internal void ClearProductPluginDriveCap(long generation) =>
        _radio.ClearProductPluginDriveCap(generation);

    internal bool TrySetProductPluginMox(long generation, out string? error)
    {
        lock (_transitionSync)
        {
            if (!TrySetMox(true, MoxSource.ProductPlugin, out error)) return false;
            lock (_sync)
            {
                if (!_moxOn || _moxOwner != MoxSource.ProductPlugin)
                {
                    error = "the product-plugin key was preempted during admission";
                    return false;
                }
                _productPluginMoxGeneration = generation;
            }
            return true;
        }
    }

    internal bool TryReleaseProductPluginMox(long generation, out string? error)
    {
        lock (_transitionSync)
        {
            lock (_sync)
            {
                if (_moxOwner != MoxSource.ProductPlugin
                    || _productPluginMoxGeneration != generation)
                {
                    error = null;
                    return true;
                }
            }
            return TryReleaseMoxImmediately(MoxSource.ProductPlugin, out error);
        }
    }

    // Only voice UI MOX uses the pre-key window. CW would clip the first dit,
    // and digital/FreeDV timing is owned by external modem sequencing.
    private static bool IsCwMode(RxMode mode) => mode is RxMode.CWU or RxMode.CWL;
    private static bool IsPreKeyVoiceMode(RxMode mode) =>
        mode is RxMode.LSB or RxMode.USB or RxMode.AM or RxMode.SAM or RxMode.DSB or RxMode.FM;

    private static bool IsRogerBeepMode(RxMode mode) =>
        mode is RxMode.LSB or RxMode.USB or RxMode.AM or RxMode.SAM or RxMode.DSB or RxMode.FM;

    public TxService(RadioService radio, DspPipelineService pipeline, StreamingHub hub, IBandPlanService bandPlan, ILogger<TxService> log)
        : this(radio, pipeline, hub, bandPlan, log, System.Diagnostics.Stopwatch.GetTimestamp)
    {
    }

    internal TxService(
        RadioService radio,
        DspPipelineService pipeline,
        StreamingHub hub,
        IBandPlanService bandPlan,
        ILogger<TxService> log,
        Func<long> stopwatchTicks)
    {
        _radio = radio;
        _pipeline = pipeline;
        _hub = hub;
        _bandPlan = bandPlan;
        _log = log;
        _stopwatchTicks = stopwatchTicks;
        _radio.Disconnected += OnRadioDisconnected;
        _radio.P2Disconnected += OnRadioDisconnected;
        _hub.LastClientDisconnected += OnLastClientDisconnected;
        _radio.TransmitSafetyStateChanging = OnTransmitSafetyStateChanging;
        _radio.ConfigureHardwareCwArmSafety(EvaluateHardwareCwArm);
        _radio.ConfigureTxDriveSafety(EvaluateDriveRequest);
        _radio.SetTxSafetyAuthority(false);
        _radio.ApplyTxSafetyInhibit();
        _bandPlan.PlanChanged += OnBandPlanChanged;
    }

    public bool IsMoxOn { get { lock (_sync) return _moxOn; } }
    public bool IsTunOn { get { lock (_sync) return _tunOn; } }
    public bool MonitorOnTransmit => Volatile.Read(ref _monitorOnTransmit) != 0;

    private void SetMonitorOnTransmitUnsafe(bool enabled) =>
        Volatile.Write(ref _monitorOnTransmit, enabled ? 1 : 0);

    internal Action? TxMonitorPreviewMutationEnteredForTest { get; set; }

    internal TxMonitorPreviewState ApplyTxMonitorPreview(
        bool enabled,
        bool meterOnly,
        bool? monitorOnTransmit = null)
    {
        lock (_transitionSync)
        {
            TxMonitorPreviewMutationEnteredForTest?.Invoke();

            bool appliedMeterOnly = enabled && meterOnly;
            bool appliedMonitorOnTransmit = monitorOnTransmit ?? MonitorOnTransmit;

            TransmitIntent? active;
            MoxSource source;
            lock (_sync)
            {
                active = _activeIntent;
                source = active == TransmitIntent.Mox
                    ? _moxOwner ?? MoxSource.UI
                    : active == TransmitIntent.Tun
                        ? _tunOwner ?? MoxSource.UI
                        : MoxSource.UI;
            }
            if (active is TransmitIntent.Tun or TransmitIntent.TwoTone)
            {
                enabled = false;
                appliedMeterOnly = false;
                appliedMonitorOnTransmit = false;
            }
            else if (active == TransmitIntent.Mox)
            {
                // While keyed, the audible monitor follows the independent TX
                // arm. The ordinary Preview request is strictly an RX/idle
                // tool and cannot turn monitoring on during transmission.
                enabled = appliedMonitorOnTransmit;
                appliedMeterOnly = false;
            }

            SetMonitorOnTransmitUnsafe(appliedMonitorOnTransmit);
            _pipeline.SetTxMonitorMeterOnly(appliedMeterOnly);
            var state = _radio.SetTxMonitor(new TxMonitorSetRequest(enabled));
            var result = new TxMonitorPreviewState(
                state,
                state.TxMonitorEnabled && appliedMeterOnly,
                appliedMonitorOnTransmit);
            if (active is not null)
            {
                BroadcastMoxState(
                    moxOn: active is TransmitIntent.Mox or TransmitIntent.TwoTone,
                    tunOn: active == TransmitIntent.Tun,
                    source);
            }
            return result;
        }
    }

    /// <summary>
    /// Runs a short safety-critical operation only when every Zeus transmit
    /// intent is idle, while preventing a new MOX, TUN, or TwoTone transition
    /// from beginning until that operation returns. This is intentionally
    /// non-blocking: a concurrent transition wins and the caller must fail
    /// closed instead of waiting to change external station equipment later.
    /// </summary>
    internal bool TryRunWithTransmitIdle(Action operation, out string? error)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Monitor.TryEnter(_transitionSync))
        {
            error = "A Zeus transmit transition is already in progress.";
            return false;
        }

        try
        {
            TransmitIntent? activeIntent;
            bool moxOn;
            bool tunOn;
            bool twoToneOn;
            lock (_sync)
            {
                activeIntent = _activeIntent;
                moxOn = _moxOn;
                tunOn = _tunOn;
                twoToneOn = IsTwoToneOn;
            }

            if (activeIntent is not null || moxOn || tunOn || twoToneOn || _radio.IsMox)
            {
                error = "The Zeus transmitter is not idle.";
                return false;
            }

            operation();
            error = null;
            return true;
        }
        finally
        {
            Monitor.Exit(_transitionSync);
        }
    }
    /// <summary>Source that currently holds MOX, or null when MOX is off.
    /// Subscribers (e.g. <c>CwEngine</c>) read this on the
    /// <see cref="TxActiveChanged"/> falling edge to tell apart "I dropped
    /// MOX myself" from "the operator overrode me from the UI".</summary>
    public MoxSource? MoxOwner { get { lock (_sync) return _moxOwner; } }
    internal EngineTransmitSafetyModule Safety => _safety;
    internal long TransitionRevision { get { lock (_sync) return _transitionRevision; } }
    internal bool IsMicIqProducerAllowed
    {
        get
        {
            lock (_sync)
                return _activeIntent is null
                    || (_activeIntent == TransmitIntent.Mox && _moxOwner != MoxSource.Cwx);
        }
    }

    /// <summary>
    /// Fires on every change to combined TX-active state
    /// (<c>IsMoxOn || IsTunOn</c>). Argument is the new combined value.
    /// Fired OFF the <c>_sync</c> lock so subscribers can call back into
    /// the service without deadlocking.
    ///
    /// <para>Primary subscriber: <see cref="NativeAudioSink"/> uses this
    /// to drain the RX audio ring on the rising edge so the operator
    /// hears instant silence on TX rather than the accumulated
    /// radio-clock-vs-soundcard-clock backlog. See issue #403 for the
    /// symptom this addresses on Windows.</para>
    /// </summary>
    public event Action<bool>? TxActiveChanged;

    /// <summary>
    /// Raised before a source attempts a rising transmit edge. The leased
    /// product-plugin port uses this pre-admission seam to relinquish its key
    /// before an operator, hardware, TUNE, CAT, or TCI request is evaluated.
    /// </summary>
    internal event Action<MoxSource>? TransmitRequested;

    // Last TX-active value observed by the firing path. Read+written
    // under _sync inside the helpers below. The "fire off the lock"
    // contract above means we capture the new value under the lock,
    // release it, then raise — so two rapid edges from different
    // threads can never reorder a stale notification past a fresh one.
    private bool _lastTxActiveFired;

    /// <summary>
    /// Recompute combined TX-active state under the lock; on change,
    /// capture the new value for off-lock notification. Returns the
    /// captured value or null if unchanged. Caller must invoke
    /// <see cref="RaiseTxActiveChanged"/> with the result outside the
    /// lock.
    /// </summary>
    private bool? CaptureTxActiveChangeUnderLock()
    {
        bool now = _moxOn || _tunOn;
        if (now == _lastTxActiveFired) return null;
        _lastTxActiveFired = now;
        return now;
    }

    private void RaiseTxActiveChanged(bool? captured)
    {
        if (captured is null) return;
        var handlers = TxActiveChanged?.GetInvocationList();
        if (handlers is null) return;
        foreach (var handler in handlers)
        {
            try { ((Action<bool>)handler)(captured.Value); }
            catch (Exception ex)
            {
                // Decision 12: one observer cannot starve later lifecycle
                // consumers (notably CW cancellation) of a falling edge.
                _log.LogWarning(ex, "tx.txActiveChanged subscriber threw");
            }
        }
    }

    /// <summary>
    /// Every UI client is gone. Release a UI-owned MOX so the radio cannot be
    /// left keyed with nobody able to unkey it.
    ///
    /// This closes a genuine dead-carrier hole: on a websocket drop the browser
    /// reverts its own indicator to RX and stops the mic uplink, on the belief
    /// (stated in ws-client.ts) that the hub unkeys server-side. It did not — so
    /// a blip mid-over left the radio transmitting silence into the amp until
    /// the TX timeout eventually caught it.
    ///
    /// Deliberately narrow on two axes:
    /// <list type="bullet">
    /// <item>Only when the LAST client leaves. With another browser still
    /// attached somebody can see the TX state and unkey it, and yanking MOX
    /// because a second tab closed would be its own surprise.</item>
    /// <item>Only when <see cref="MoxSource.UI"/> owns MOX. Hardware PTT, TCI,
    /// CAT, CW and plugin keying do not belong to the browser and must survive
    /// it closing — a footswitch-held transmission must not end because a
    /// webview died.</item>
    /// </list>
    /// TUN is left alone: it is not reachable from a disconnected UI either, but
    /// it is an operator-explicit carrier that the TX timeout already bounds.
    /// </summary>
    internal void OnLastClientDisconnected()
    {
        // Held across the check AND the release: UI is the master override, so a
        // hardware PTT that grabbed MOX in a gap between the two would be
        // unkeyed by this. _transitionSync is reentrant and TrySetMox takes it
        // itself, with the same _transitionSync -> _sync ordering used
        // throughout, so nesting here is safe.
        lock (_transitionSync)
        {
            MoxSource? owner;
            bool moxOn;
            lock (_sync)
            {
                owner = _moxOwner;
                moxOn = _activeIntent == TransmitIntent.Mox;
            }
            if (!moxOn || owner != MoxSource.UI) return;
            _log.LogWarning(
                "tx.ui.gone releasing UI-owned MOX — last websocket client disconnected while keyed");
            if (!TrySetMox(false, MoxSource.UI, out var error) && error is not null)
                _log.LogWarning("tx.ui.gone MOX release failed: {Error}", error);
        }
    }

    private void OnRadioDisconnected()
    {
        lock (_transitionSync)
        {
            bool wasActive;
            lock (_sync) wasActive = _activeIntent is not null;
            PrepareTxMonitorForTransmitStart(clearTransmitIntent: true);
            _safety.RecordTrip();
            ConvergeToSafeIdle(faultLatched: true);
            if (wasActive)
                _log.LogInformation("tx.disconnect.clear");
            // The session-only TX monitor arm can be set while RX/idle. Always
            // publish its authoritative cleared state so connected clients do
            // not leave the TX button visibly armed after a radio disconnect.
            BroadcastMoxState(moxOn: false, tunOn: false);
        }
    }

    // TwoTone latch — independent of MOX/TUN. Set by RadioService.SetTwoTone
    // on every state mutation. TxTuneDriver polls it so the WDSP TXA pump
    // runs even when no mic uplink is feeding fexchange2 (PostGen mode=1
    // injects the two-tone excitation regardless of mic input).
    public bool IsTwoToneOn { get; private set; }
    internal void SetTwoToneOn(bool on) { IsTwoToneOn = on; }

    public DateTime? MoxStartedAt { get { lock (_sync) return _moxStartedAt; } }
    public DateTime? TunStartedAt { get { lock (_sync) return _tunStartedAt; } }

    // Test seams: drive the keyed-at timestamps directly from a unit test
    // without routing through TrySetMox/TrySetTun (which require an active
    // Protocol1 client). Only the TxMetersService timeout path reads them.
    internal void SetMoxStartedAtForTest(DateTime? t) { lock (_sync) _moxStartedAt = t; }
    internal void SetTunStartedAtForTest(DateTime? t) { lock (_sync) _tunStartedAt = t; }

    internal static bool IsPreKeyMuteOpen(long openAtTicks, long nowTicks) =>
        openAtTicks != 0L && nowTicks < openAtTicks;

    private static long DelayMsToStopwatchTicks(int delayMs) =>
        (long)(delayMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);

    private void RebasePreKeyDeadlineIfStillActive(bool tune, long delayTicks)
    {
        if (delayTicks <= 0) return;

        lock (_sync)
        {
            if (tune ? !_tunOn : !_moxOn) return;
            Interlocked.Exchange(ref _preKeyOpenAtTicks, _stopwatchTicks() + delayTicks);
        }
    }

    private TransmitSafetySnapshot CaptureSafetySnapshot(
        StateDto state,
        TransmitIntent? activeIntent,
        MoxSource? source = null)
    {
        long txHz = RadioFrequencyResolver.TxFrequencyHz(state);
        TransverterBandDto? profile = _radio.TryResolveTransverterBand(txHz, out var resolved)
            ? resolved
            : null;
        bool profileRequired = profile is not null
            || txHz > TransverterFrequencyConverter.MaximumRadioFrequencyHz;
        return new TransmitSafetySnapshot(
            state,
            _radio.IsConnected,
            _radio.ConnectedBoardKind,
            _radio.EffectiveOrionMkIIVariant,
            _bandPlan.CurrentRegion,
            _bandPlan.CurrentPlan,
            _bandPlan.TxGuardIgnore,
            activeIntent,
            source,
            profileRequired,
            profile);
    }

    private bool EvaluateAdmission(TransmitIntent intent, MoxSource? source, out string? error)
    {
        TransmitIntent? active;
        lock (_sync) active = _activeIntent;
        var decision = _safety.EvaluateKeyOn(
            intent,
            CaptureSafetySnapshot(_radio.Snapshot(), active, source));
        if (decision.Allowed)
        {
            error = null;
            return true;
        }

        error = decision.OperatorText;
        if (decision.ReasonCode is TransmitSafetyReasonCode.UnknownBoard
            or TransmitSafetyReasonCode.UnknownBoardVariant)
        {
            _radio.ApplyTxSafetyInhibit();
        }
        _log.LogWarning(
            "tx.safety.blocked intent={Intent} reason={Reason} detail={Detail}",
            intent,
            decision.ReasonCode,
            decision.OperatorText);
        if (decision.ReasonCode == TransmitSafetyReasonCode.OutOfBand)
            _hub.Broadcast(new AlertFrame(AlertKind.OutOfBand, decision.OperatorText));
        return false;
    }

    private TransmitSafetyDecision EvaluateDriveRequest(int requestedPercent)
    {
        TransmitIntent? active;
        MoxSource? source;
        lock (_sync)
        {
            active = _activeIntent;
            source = active == TransmitIntent.Tun ? _tunOwner : _moxOwner;
        }
        return _safety.ResolveEffectiveDrive(
            requestedPercent,
            CaptureSafetySnapshot(_radio.Snapshot(), active, source));
    }

    private bool EvaluateHardwareCwArm(StateDto state)
    {
        TransmitIntent? active;
        MoxSource? owner;
        lock (_sync)
        {
            active = _activeIntent;
            owner = _moxOwner;
        }
        // Hardware-owned MOX is the FPGA keyer's own transmission, not a
        // competing intent. Mirror TrySetMox's source-aware block exemption so
        // a settings re-push cannot disarm the keyer that currently owns TX.
        if (active == TransmitIntent.Mox && owner == MoxSource.Hardware)
            active = null;
        return _safety.EvaluateHardwareCwArm(
            CaptureSafetySnapshot(state, active)).Allowed;
    }

    private void OnTransmitSafetyStateChanging(StateDto current, StateDto proposed)
    {
        if (!SafetyRelevantStateChanged(current, proposed)) return;
        // Decision 6: disconnect and every other unkey path bypass admission.
        // The disconnect event that follows this mutation owns convergence;
        // vetoing the state edge would strand the transport in a half-cleared
        // state and prevent that cleanup from running.
        if (current.Status == ConnectionStatus.Connected
            && proposed.Status != ConnectionStatus.Connected)
            return;
        // CAT may restore the receive dial after the radio wire has dropped
        // while post-wire DSP teardown is still serialized by _transitionSync.
        // Once host intent is clear there is no active transmission to
        // revalidate, so that safe RX-only tune must not be rejected merely
        // because teardown still owns the transition lock.
        TransmitIntent? activeIntent;
        // This callback may already hold RadioService._sync. Keep the TxService
        // lock field-only, release it, then make the reentrant radio read.
        lock (_sync) activeIntent = _activeIntent;
        if (activeIntent is null && !_radio.IsMox) return;
        if (!Monitor.TryEnter(_transitionSync))
            throw new TransmitSafetyRejectedException("TX state change blocked while a transmit transition is in progress");
        try
        {
            TransmitIntent? active;
            MoxSource? source;
            lock (_sync)
            {
                active = _activeIntent;
                source = active == TransmitIntent.Tun ? _tunOwner : _moxOwner;
            }
            if (active is null) return;
            var decision = _safety.RevalidateActive(
                active.Value,
                CaptureSafetySnapshot(proposed, active, source));
            if (decision.Allowed) return;

            // Decision 2/6: a safety-invalid active edit must use the same
            // unconditional fault path as every other active revalidation.
            TryTripForAlert(AlertKind.OutOfBand, decision.OperatorText);
            throw new TransmitSafetyRejectedException(decision.OperatorText);
        }
        finally
        {
            Monitor.Exit(_transitionSync);
        }
    }

    private void OnBandPlanChanged()
    {
        lock (_transitionSync)
        {
            TransmitIntent? active;
            MoxSource? source;
            lock (_sync)
            {
                active = _activeIntent;
                source = active == TransmitIntent.Tun ? _tunOwner : _moxOwner;
            }
            if (active is null)
            {
                _radio.RefreshHardwareCwArmPermission();
                return;
            }
            var decision = _safety.RevalidateActive(
                active.Value,
                CaptureSafetySnapshot(_radio.Snapshot(), active, source));
            if (!decision.Allowed)
                TryTripForAlert(AlertKind.OutOfBand, decision.OperatorText);
            else
                _radio.RefreshHardwareCwArmPermission();
        }
    }

    private static bool SafetyRelevantStateChanged(StateDto current, StateDto proposed) =>
        current.Status != proposed.Status
        || current.VfoHz != proposed.VfoHz
        || current.Mode != proposed.Mode
        || current.TxFilterLowHz != proposed.TxFilterLowHz
        || current.TxFilterHighHz != proposed.TxFilterHighHz
        || current.XitEnabled != proposed.XitEnabled
        || current.XitHz != proposed.XitHz
        || current.TwoToneFreq1 != proposed.TwoToneFreq1
        || current.TwoToneFreq2 != proposed.TwoToneFreq2
        || current.TxReceiverIndex != proposed.TxReceiverIndex
        || current.TxVfo != proposed.TxVfo
        || !ReceiverTxStateEqual(current, proposed);

    private static bool ReceiverTxStateEqual(StateDto left, StateDto right)
    {
        int index = Math.Max(left.TxReceiverIndex, right.TxReceiverIndex);
        if (index <= 0) return true;
        var l = left.Receivers?.FirstOrDefault(r => r.Index == index);
        var r = right.Receivers?.FirstOrDefault(x => x.Index == index);
        return l?.VfoHz == r?.VfoHz && l?.Mode == r?.Mode;
    }

    private void PrepareTxMonitorForTransmitStart(bool clearTransmitIntent = false)
    {
        if (clearTransmitIntent) SetMonitorOnTransmitUnsafe(false);
        _pipeline.SetTxMonitorMeterOnly(false);
        if (!_radio.Snapshot().TxMonitorEnabled) return;
        _radio.SetTxMonitor(new TxMonitorSetRequest(false));
    }

    private void ActivateTxMonitorForMox()
    {
        _pipeline.SetTxMonitorMeterOnly(false);
        bool enabled = MonitorOnTransmit;
        if (_radio.Snapshot().TxMonitorEnabled == enabled) return;
        _radio.SetTxMonitor(new TxMonitorSetRequest(enabled));
    }

    private void BroadcastMoxState(
        bool moxOn,
        bool tunOn,
        MoxSource source = MoxSource.UI) =>
        _hub.Broadcast(new MoxStateFrame(
            MoxOn: moxOn,
            TunOn: tunOn,
            Source: source,
            TxMonitorEnabled: _radio.Snapshot().TxMonitorEnabled,
            MonitorOnTransmit: MonitorOnTransmit));

    private long NextTransitionRevision()
    {
        lock (_sync) return ++_transitionRevision;
    }

    private void CommitActiveIntent(
        TransmitIntent intent,
        MoxSource? source,
        long revision,
        bool armPreKey)
    {
        bool? changed;
        lock (_sync)
        {
            _activeIntent = intent;
            _transitionRevision = revision;
            _moxOn = intent is TransmitIntent.Mox or TransmitIntent.TwoTone;
            _tunOn = intent == TransmitIntent.Tun;
            _moxStartedAt = _moxOn ? DateTime.UtcNow : null;
            _tunStartedAt = _tunOn ? DateTime.UtcNow : null;
            _moxOwner = _moxOn ? source : null;
            _tunOwner = _tunOn ? source : null;
            IsTwoToneOn = intent == TransmitIntent.TwoTone;
            Interlocked.Exchange(ref _preKeyOpenAtTicks, armPreKey ? long.MaxValue : 0);
            changed = CaptureTxActiveChangeUnderLock();
        }
        RaiseTxActiveChanged(changed);
    }

    private void ClearHostIntent(long revision)
    {
        bool? changed;
        lock (_sync)
        {
            _activeIntent = null;
            _transitionRevision = revision;
            _moxOn = false;
            _tunOn = false;
            _moxStartedAt = null;
            _tunStartedAt = null;
            _moxOwner = null;
            _productPluginMoxGeneration = 0;
            _tunOwner = null;
            IsTwoToneOn = false;
            Interlocked.Exchange(ref _preKeyOpenAtTicks, 0);
            changed = CaptureTxActiveChangeUnderLock();
        }
        RaiseTxActiveChanged(changed);
    }

    private void DrainMoxTailBestEffort()
    {
        var state = _radio.Snapshot();
        try { _pipeline.DrainFreeDvTxTail(); }
        catch (Exception ex) { _log.LogWarning(ex, "tx.tail.freedv.failed"); }

        int tailMs = _radio.TxMoxTailDelayMs;
        if (tailMs > 0 && !IsCwMode(state.Mode) && !_pipeline.IsFreeDvActive)
        {
            try
            {
                if (!_pipeline.DrainVoiceTxTail(tailMs)) Thread.Sleep(tailMs);
            }
            catch (Exception ex) { _log.LogWarning(ex, "tx.tail.voice.failed"); }
        }
        if (state.RogerBeepEnabled && IsRogerBeepMode(state.Mode))
        {
            try { _pipeline.DrainRogerBeepTail(); }
            catch (Exception ex) { _log.LogWarning(ex, "tx.tail.roger.failed"); }
        }
    }

    private void ConvergeToSafeIdle(
        bool faultLatched,
        Action? onPostWireIdle = null,
        bool stopTxMonitor = true)
    {
        long revision = NextTransitionRevision();
        bool failed = false;
        bool wireDropped = false;
        bool hostClearedEarly = false;
        void Safe(string step, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                failed = true;
                _log.LogWarning(ex, "tx.safeConverge.failed step={Step} revision={Revision}", step, revision);
            }
        }

        // Decision 11/12 ordering: revoke standing self-key permission and IQ
        // egress first, drop the radio wire next, then stop generators and the
        // potentially-blocking DSP teardown. Every action is independently
        // attempted; stale host booleans never suppress convergence.
        Safe("egress.revoke", _pipeline.RevokeTxEgress);
        Safe("hardwareCw.disarm", () => _radio.SetHardwareCwSafetyBlocked(true));
        Safe("wire.mox.off", () =>
        {
            _radio.SetMox(false);
            wireDropped = true;
        });
        var wireDroppedTs = _stopwatchTicks();
        Safe("authority.revoke", () => _radio.SetTxSafetyAuthority(false));
        Safe("generator.tune.off", () => _pipeline.SetTxTune(false));
        Safe("generator.twoTone.off", () => _radio.SetTwoToneRuntimeEnabled(false));
        Safe("tune.latch.off", _radio.ClearTunActiveForSafety);
        Safe("drive.inhibit", _radio.ApplyTxSafetyInhibit);

        // A CAT release can unblock its ordered command stream once the wire is
        // down and host state reports RX. The potentially-blocking DSP teardown
        // remains behind _transitionSync, so a new TX transition cannot race it;
        // only RX-safe state changes such as the Fake It FA restore may proceed.
        if (onPostWireIdle is not null && wireDropped)
        {
            ClearHostIntent(revision);
            _safety.ObserveConfirmedIdle();
            onPostWireIdle();
            hostClearedEarly = true;
        }

        Safe("dsp.mox.off", () => _pipeline.SetMox(false));
        if (stopTxMonitor) Safe("txMonitor.off", () =>
        {
            // Keep the monitor request asserted through WDSP's MOX-off edge.
            // That makes SetMox(false) leave TXA warm instead of attempting a
            // damped stop after the wire edge has already stopped mic blocks;
            // the latter can wait forever for a final fexchange and strand RX
            // suppression on. SetTxMonitor(false) intentionally leaves an idle
            // TXA warm, so clearing the request after MOX-off is non-blocking.
            _pipeline.SetTxMonitorMeterOnly(false);
            if (_radio.Snapshot().TxMonitorEnabled)
                _radio.SetTxMonitor(new TxMonitorSetRequest(false));
        });
        if (!hostClearedEarly)
        {
            ClearHostIntent(revision);
            _safety.ObserveConfirmedIdle();
            onPostWireIdle?.Invoke();
        }

        if (failed)
        {
            _safety.RecordTrip();
            faultLatched = true;
        }
        if (!faultLatched)
            Safe("hardwareCw.rearm", () => _radio.SetHardwareCwSafetyBlocked(false));

        var teardownMs = (_stopwatchTicks() - wireDroppedTs)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _log.LogInformation(
            "tx.safeConverge revision={Revision} faultLatched={FaultLatched} afterWireMs={Ms:F1}",
            revision,
            faultLatched,
            teardownMs);
    }

    /// <summary>Back-compat shim: callers that don't tag a source get
    /// <see cref="MoxSource.UI"/>, the master override. New callers should
    /// pass an explicit source so the release path can reject foreign drops.</summary>
    public bool TrySetMox(bool on, out string? error)
        => TrySetMox(on, MoxSource.UI, out error);

    /// <summary>
    /// Source-aware MOX setter. The <paramref name="source"/> tag determines
    /// whether the call is allowed when MOX is already held by another
    /// source — see <see cref="MoxSource"/> for the rule. UI always wins;
    /// any other source can only drop MOX it itself raised.
    /// </summary>
    public bool TrySetMox(bool on, MoxSource source, out string? error)
    {
        if (on) TransmitRequested?.Invoke(source);
        lock (_transitionSync)
        {
            if (!on)
                return TrySetMoxOffUnderTransitionLock(source, onPostWireIdle: null, out error);

            if (!EvaluateAdmission(TransmitIntent.Mox, source, out error)) return false;
            lock (_sync)
            {
                if (_activeIntent == TransmitIntent.Mox) { error = null; return true; }
            }
            int preKeyMs = source == MoxSource.UI ? _radio.TxMoxPreKeyDelayMs : 0;
            bool armPreKey = preKeyMs > 0 && IsPreKeyVoiceMode(_radio.Snapshot().Mode);
            long preKeyDelayTicks = armPreKey ? DelayMsToStopwatchTicks(preKeyMs) : 0;
            long revision = NextTransitionRevision();
            _safety.AdmitExplicitRequest();
            // A hardware-owned MOX rise is the FPGA keyer's own transmission;
            // disarming it here makes PTT fall, then re-arm, and oscillate.
            // This is the same invariant documented by
            // RadioService.SetHostCwKeying. Host-driven sources must still
            // block the competing hardware keyer.
            if (source != MoxSource.Hardware)
                _radio.SetHardwareCwSafetyBlocked(true);
            try
            {
                PrepareTxMonitorForTransmitStart();
                _pipeline.RevokeTxEgress();
                _radio.SetTxSafetyAuthority(true);
                _pipeline.SetTxTune(false);
                _radio.SetTwoToneRuntimeEnabled(false);
                _radio.NotifyTunActive(false);
                _pipeline.SetMox(true);
                _pipeline.PrimeTxDspForKeyDown();
                if (!_pipeline.SanitizeProtocol2TxBeforeKeyDown())
                    throw new InvalidOperationException("Protocol 2 pre-key TX-IQ sanitization failed");
                if (!EvaluateAdmission(TransmitIntent.Mox, source, out error))
                    throw new TransmitSafetyRejectedException(error ?? "TX safety revalidation failed");
                _log.LogInformation("tx.mox.on.recv ts={Ts}",
                    _stopwatchTicks());
                _radio.SetMox(true);
                ActivateTxMonitorForMox();
                _pipeline.CommitTxEgress(revision);
                _pipeline.SetPsMox(true);
                CommitActiveIntent(TransmitIntent.Mox, source, revision, armPreKey);
                RebasePreKeyDeadlineIfStillActive(tune: false, preKeyDelayTicks);
                _log.LogInformation("tx.mox on=true revision={Revision}", revision);
                BroadcastMoxState(moxOn: true, tunOn: false, source);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "tx.mox.keyOn.rollback revision={Revision}", revision);
                _safety.RecordTrip();
                ConvergeToSafeIdle(faultLatched: true);
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// CAT transition path. Release preserves the configured pre-wire TX tail,
    /// then unblocks the session as soon as hardware wire and host MOX truth are
    /// RX. Post-wire DSP teardown continues on the thread pool while holding
    /// the normal transition lock. Key-up deliberately remains synchronous and
    /// waits for that lock: CAT responsiveness never takes priority over
    /// completing teardown before a new transmission.
    /// </summary>
    internal async ValueTask<(bool Success, string? Error)> TrySetMoxFromCatAsync(
        bool on,
        CancellationToken cancellationToken)
    {
        if (on)
        {
            bool success = TrySetMox(true, MoxSource.Cat, out string? keyError);
            return (success, keyError);
        }

        var ready = new TaskCompletionSource<(bool Success, string? Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        bool queued = ThreadPool.QueueUserWorkItem(_ =>
        {
            bool success = false;
            string? releaseError = null;
            try
            {
                lock (_transitionSync)
                {
                    success = TrySetMoxOffUnderTransitionLock(
                        MoxSource.Cat,
                        () => ready.TrySetResult((true, null)),
                        out releaseError);
                }
            }
            catch (Exception ex)
            {
                releaseError = ex.Message;
                _log.LogWarning(ex, "tx.mox.cat.release.failed");
            }
            finally
            {
                ready.TrySetResult((success, releaseError));
            }
        });

        if (!queued)
        {
            bool success = TrySetMox(false, MoxSource.Cat, out string? releaseError);
            return (success, releaseError);
        }

        return await ready.Task.WaitAsync(cancellationToken);
    }

    private bool TrySetMoxOffUnderTransitionLock(
        MoxSource source,
        Action? onPostWireIdle,
        out string? error)
    {
        TransmitIntent? active;
        MoxSource? owner;
        lock (_sync) { active = _activeIntent; owner = _moxOwner; }
        if (active == TransmitIntent.Mox
            && source != MoxSource.UI
            && owner is not null
            && owner != source)
        {
            error = $"MOX held by {owner}; only UI can override";
            return false;
        }
        if (active is not null && active != TransmitIntent.Mox && source != MoxSource.UI)
        {
            error = $"TX held by {active}; only UI can force the master unkey";
            return false;
        }

        if (active == TransmitIntent.Mox)
            DrainMoxTailBestEffort();
        ConvergeToSafeIdle(
            faultLatched: false,
            onPostWireIdle: onPostWireIdle,
            stopTxMonitor: active is not null);
        _log.LogInformation("tx.mox on=false");
        BroadcastMoxState(moxOn: false, tunOn: false);
        error = null;
        return true;
    }

    /// <summary>
    /// Dead-man release for the out-of-process product-plugin lease. Unlike a
    /// normal operator release this deliberately skips voice/modem/roger-beep
    /// tails: liveness loss must drop the wire before one 20 ms audio block.
    /// Source ownership is still enforced, and an already-idle transmitter is
    /// a successful no-op.
    /// </summary>
    internal bool TryReleaseMoxImmediately(MoxSource source, out string? error)
    {
        lock (_transitionSync)
        {
            TransmitIntent? active;
            MoxSource? owner;
            lock (_sync) { active = _activeIntent; owner = _moxOwner; }
            if (active is null)
            {
                error = null;
                return true;
            }
            if (active != TransmitIntent.Mox)
            {
                error = $"TX held by {active}; product lease cannot release it";
                return false;
            }
            if (owner != source)
            {
                error = $"MOX held by {owner}; product lease cannot release it";
                return false;
            }

            ConvergeToSafeIdle(faultLatched: false);
            _log.LogInformation("tx.mox dead-man release source={Source}", source);
            BroadcastMoxState(moxOn: false, tunOn: false);
            error = null;
            return true;
        }
    }

    /// <summary>
    /// Arm or disarm the TwoTone test generator AND key MOX. Mirrors the Thetis
    /// chkTestIMD_CheckedChanged path (setup.cs:11162-11165, 11189-11216):
    /// TwoTone owns the MOX state while armed and unconditionally drops it on
    /// disarm. This matches the operator expectation "press 2-Tone → radio is
    /// transmitting two tones" without a separate MOX press.
    ///
    /// Order on arm: configure PostGen via RadioService.SetTwoTone (which arms
    /// xgen mode=1 with the sideband-correct signed freqs from Group A), THEN
    /// flip MOX on so TXA is alive when the generator starts running. On disarm
    /// the order is reversed — MOX off first so the radio stops emitting RF
    /// before the engine drops the generator run flag.
    /// </summary>
    public bool TrySetTwoTone(TwoToneSetRequest req, out string? error)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Enabled) TransmitRequested?.Invoke(MoxSource.UI);
        lock (_transitionSync)
        {
            TransmitIntent? active;
            lock (_sync) active = _activeIntent;
            if (!req.Enabled)
            {
                if (active is not null && active != TransmitIntent.TwoTone)
                {
                    error = $"TX held by {active}; unkey it before changing two-tone";
                    return false;
                }
                ConvergeToSafeIdle(
                    faultLatched: false,
                    stopTxMonitor: active is not null);
                _log.LogInformation("tx.twoTone on=false");
                BroadcastMoxState(moxOn: false, tunOn: false);
                error = null;
                return true;
            }

            if (!EvaluateAdmission(TransmitIntent.TwoTone, MoxSource.UI, out error)) return false;
            if (active == TransmitIntent.TwoTone)
            {
                // Live tone edits are revalidated by RadioService's pre-commit
                // safety hook; the intent and egress revision remain unchanged.
                try
                {
                    _radio.SetTwoTone(req);
                    error = null;
                    return true;
                }
                catch (TransmitSafetyRejectedException ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            long revision = NextTransitionRevision();
            _safety.AdmitExplicitRequest();
            _radio.SetHardwareCwSafetyBlocked(true);
            try
            {
                PrepareTxMonitorForTransmitStart(clearTransmitIntent: true);
                _pipeline.RevokeTxEgress();
                _radio.SetTxSafetyAuthority(true);
                _pipeline.SetTxTune(false);
                // Decision 11: generator and normal drive/OC revision are both
                // applied before the rising wire edge.
                _radio.SetTwoTone(req);
                _radio.NotifyTunActive(false);
                _pipeline.SetMox(true);
                if (!EvaluateAdmission(TransmitIntent.TwoTone, MoxSource.UI, out error))
                    throw new TransmitSafetyRejectedException(error ?? "TX safety revalidation failed");
                _radio.SetMox(true);
                _pipeline.CommitTxEgress(revision);
                CommitActiveIntent(TransmitIntent.TwoTone, MoxSource.UI, revision, armPreKey: false);
                _log.LogInformation(
                    "tx.twoTone on=true f1={F1} f2={F2} mag={Mag} revision={Revision}",
                    req.Freq1, req.Freq2, req.Mag, revision);
                BroadcastMoxState(moxOn: true, tunOn: false);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "tx.twoTone.keyOn.rollback revision={Revision}", revision);
                _safety.RecordTrip();
                ConvergeToSafeIdle(faultLatched: true);
                error = ex.Message;
                return false;
            }
        }
    }

    public bool TrySetTun(bool on, out string? error)
        => TrySetTun(on, MoxSource.UI, out error);

    public bool TrySetTun(bool on, MoxSource source, out string? error)
    {
        if (on) TransmitRequested?.Invoke(source);
        lock (_transitionSync)
        {
            TransmitIntent? active;
            MoxSource? owner;
            lock (_sync) { active = _activeIntent; owner = _tunOwner; }
            if (!on)
            {
                if (active is not null && active != TransmitIntent.Tun)
                {
                    error = $"TX held by {active}; unkey it before changing TUN";
                    return false;
                }
                if (active == TransmitIntent.Tun
                    && source != MoxSource.UI
                    && owner is not null
                    && owner != source)
                {
                    error = $"TUN held by {owner}; only UI can override";
                    return false;
                }
                ConvergeToSafeIdle(
                    faultLatched: false,
                    stopTxMonitor: active is not null);
                _log.LogInformation("tx.tun on=false");
                BroadcastMoxState(moxOn: false, tunOn: false);
                error = null;
                return true;
            }

            if (!EvaluateAdmission(TransmitIntent.Tun, source, out error)) return false;
            if (active == TransmitIntent.Tun) { error = null; return true; }

            int preKeyMs = source == MoxSource.UI ? _radio.TxMoxPreKeyDelayMs : 0;
            long preKeyDelayTicks = preKeyMs > 0 ? DelayMsToStopwatchTicks(preKeyMs) : 0;
            long revision = NextTransitionRevision();
            _safety.AdmitExplicitRequest();
            _radio.SetHardwareCwSafetyBlocked(true);
            try
            {
                PrepareTxMonitorForTransmitStart(clearTransmitIntent: true);
                _pipeline.RevokeTxEgress();
                _radio.SetTxSafetyAuthority(true);
                _radio.SetTwoToneRuntimeEnabled(false);
                _pipeline.SetTxTune(true);
                // Decision 11: apply generator and drive/PA/OC before DSP and
                // the rising wire edge so no voice-IQ or normal-drive window exists.
                // P2 SetTune itself emits a keyed high-priority packet, so align
                // the NCO before publishing the TUN latch. RadioService.SetMox
                // repeats this as an idempotent guard at the universal edge.
                _radio.BeginTxFrequencyTransition();
                _radio.AlignLoForTx();
                _radio.NotifyTunActive(true);
                _pipeline.SetMox(true);
                if (!EvaluateAdmission(TransmitIntent.Tun, source, out error))
                    throw new TransmitSafetyRejectedException(error ?? "TX safety revalidation failed");
                _radio.SetMox(true);
                _pipeline.CommitTxEgress(revision);
                CommitActiveIntent(TransmitIntent.Tun, source, revision, preKeyDelayTicks > 0);
                RebasePreKeyDeadlineIfStillActive(tune: true, preKeyDelayTicks);
                _log.LogInformation("tx.tun on=true revision={Revision}", revision);
                BroadcastMoxState(moxOn: false, tunOn: true);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "tx.tun.keyOn.rollback revision={Revision}", revision);
                _safety.RecordTrip();
                ConvergeToSafeIdle(faultLatched: true);
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Trip both MOX and TUN for a protection alert (SWR, timeout, etc.).
    /// Emits an <see cref="AlertFrame"/> over WS so the UI can inform the operator.
    /// Operator must manually re-key. PRD FR-6.
    /// </summary>
    public void TryTripForAlert(AlertKind kind, string reason)
    {
        lock (_transitionSync)
        {
            long epoch = _safety.RecordTrip();
            ConvergeToSafeIdle(faultLatched: true);
            _log.LogWarning(
                "tx.trip kind={Kind} reason={Reason} faultEpoch={FaultEpoch}",
                kind,
                reason,
                epoch);
            // Decision 7: trip output is level-triggered and operator-visible
            // even when stale latches claimed TX was already idle.
            _hub.Broadcast(new AlertFrame(kind, reason));
            BroadcastMoxState(moxOn: false, tunOn: false);
        }
    }
}

internal readonly record struct TxMonitorPreviewState(
    StateDto RadioState,
    bool MeterOnly,
    bool MonitorOnTransmit)
{
    public bool Enabled => RadioState.TxMonitorEnabled;
}
