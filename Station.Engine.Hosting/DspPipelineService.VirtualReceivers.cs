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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Server.PublicListen;

namespace Zeus.Server;

// Public Listening Phase 4 (docs/designs/public-listening.md, "Phase 4 —
// virtual receivers"): every listener may run a WDSP RXA channel of their own,
// fed the operator's RX1 IQ and shifted to the listener's dial inside the RX1
// span.
//
// Isolation rules this file keeps (each pinned by VirtualReceiverIsolationTests):
//   • every virtual receiver owns its own WDSP RXA channel, opened through the
//     same engine.OpenChannel as a guest (WdspDspEngine opens at state 0 and
//     flips SetChannelState(id, 1, 0) after the worker is live —
//     docs/lessons/wdsp-init-gotchas.md), and its state is applied only by
//     ApplyStateToVrxChannel from the listener's own settings: never the
//     operator's AGC, AGC-T, NR, NB, squelch, zoom or AF gain;
//   • virtual receivers never enter _secondaryRx, the operator mix,
//     RxAudioAvailable, ReceiverAudioAvailable, the product-plugin audio port,
//     the hub, the meters stream, StateDto.Receivers or TxReceiverIndex, and
//     they never send anything to the radio (no DDC, no retune, no RF state);
//   • the operator's RX1 IQ is copied to them AFTER RX1 has been fed, and a
//     virtual receiver fault can never stop that feed or the operator's tick;
//   • no virtual receiver channel opens before RX1 (or while a re-rate has it
//     closed), so one can never become the engine's primary / PureSignal RXA;
//   • their audio leaves the engine only through the listener feed (0x43,
//     stream 0xC0 + slot), where PublicTransmitHold applies.
public partial class DspPipelineService
{
    /// <summary>Virtual receiver AF gain is fixed: listener volume is the listener's own business.</summary>
    internal const double VrxAfGainDb = 0.0;

    /// <summary>A listener squelch stays open this long after the signal last cleared the threshold.</summary>
    internal static readonly TimeSpan ListenerSquelchHang = TimeSpan.FromMilliseconds(300);

    private readonly VirtualReceiverPool _vrxPool = new();
    private readonly VrxRx[] _vrx = CreateVrxSlots();
    private int _vrxReconcileQueued;
    private int _vrxOpenCount;
    private long _vrxFaults;
    private long _vrxReconcileFaults;
    private long _vrxReconcileFaultLogTimestamp = long.MinValue;

    // The operator's calibrated RX1 S-meter for the listener status (op.dbm),
    // stamped with Environment.TickCount64; stale after PublicOperatorDbmMaxAge.
    private double _publicOperatorDbm = double.NaN;
    private long _publicOperatorDbmMs = long.MinValue;
    private const long PublicOperatorDbmMaxAgeMs = 2_000;

    /// <summary>The engine side of the virtual receiver book (resolved by the host as <see cref="IVirtualReceiverPool"/>).</summary>
    internal VirtualReceiverPool VrxPool => _vrxPool;

    /// <summary>Open virtual receiver WDSP channels (diagnostics / tests).</summary>
    internal int VrxChannelCount => Volatile.Read(ref _vrxOpenCount);

    /// <summary>Virtual receiver opens / applies that failed and ended the receiver (diagnostics / tests).</summary>
    internal long VrxReconcileFaults => Interlocked.Read(ref _vrxReconcileFaults);

    internal int VrxChannelId(int slot) =>
        slot is >= 0 and < VirtualReceiverPool.MaxVrxSlots ? Volatile.Read(ref _vrx[slot].ChannelId) : -1;

    /// <summary>
    /// The operator's RX1 S-meter (calibrated dBm) for the listener status, or
    /// NaN when there is no fresh reading (no radio, Protocol 3, stale).
    /// </summary>
    internal double PublicOperatorDbm
    {
        get
        {
            long at = Interlocked.Read(ref _publicOperatorDbmMs);
            if (at == long.MinValue || Environment.TickCount64 - at > PublicOperatorDbmMaxAgeMs) return double.NaN;
            return Volatile.Read(ref _publicOperatorDbm);
        }
    }

    private void NotePublicOperatorDbm(double dbm)
    {
        Volatile.Write(ref _publicOperatorDbm, dbm);
        Interlocked.Exchange(ref _publicOperatorDbmMs, Environment.TickCount64);
    }

    private sealed class VrxRx
    {
        public int ChannelId = -1;      // Volatile; -1 = no channel
        public IDspEngine? Engine;      // the engine ChannelId belongs to
        public long Generation = -1;    // receiver generation the open channel belongs to
        public readonly float[] AudioBuf = new float[AudioDrainCapacity];
        public ListenerChannelApplied Applied = new();
        public GuestReceiverSettings? Settings; // DSP thread: what the tick gates on (squelch)
        public double LastDbm = double.NaN;
        public long SquelchOpenUntil = long.MinValue;
    }

    /// <summary>What was last pushed to a listener channel (guest or virtual receiver).</summary>
    private sealed class ListenerChannelApplied
    {
        public RxMode? Mode;
        public int FilterLowHz = int.MinValue;
        public int FilterHighHz = int.MinValue;
        public long VfoHz = long.MinValue;
        public int ShiftHz = int.MinValue;
        public GuestAgcMode? Agc;
        public double AgcTopDb = double.NaN;
        public NrConfig? Nr;
        public bool? Apf;
        public (bool, int, int, int, int)? Eq;

        public void Reset()
        {
            Mode = null;
            FilterLowHz = int.MinValue;
            FilterHighHz = int.MinValue;
            VfoHz = long.MinValue;
            ShiftHz = int.MinValue;
            Agc = null;
            AgcTopDb = double.NaN;
            Nr = null;
            Apf = null;
            Eq = null;
        }
    }

    private static VrxRx[] CreateVrxSlots()
    {
        var slots = new VrxRx[VirtualReceiverPool.MaxVrxSlots];
        for (int i = 0; i < slots.Length; i++) slots[i] = new VrxRx();
        return slots;
    }

    // ---- IQ (RX thread) ----------------------------------------------------

    /// <summary>
    /// Copy the operator's RX1 IQ into every open virtual receiver. Called
    /// AFTER RX1 was fed; a fault here is counted and swallowed so it can never
    /// stop the operator's receiver.
    /// </summary>
    private void FeedVirtualReceiversIq(IDspEngine engine, ReadOnlySpan<double> iq)
    {
        if (Volatile.Read(ref _vrxOpenCount) <= 0) return;
        for (int slot = 0; slot < _vrx.Length; slot++)
        {
            int chan = Volatile.Read(ref _vrx[slot].ChannelId);
            if (chan < 0) continue;
            try { engine.FeedIq(chan, iq); }
            catch (Exception ex) { NoteVrxFault(ex, slot, "feed"); }
        }
    }

    // ---- reconcile ---------------------------------------------------------

    // Any thread (the receiver book changed): the work runs on the DSP thread.
    private void OnVrxReceiversChanged() => RequestVrxReconcile();

    /// <summary>
    /// Queue one virtual receiver reconcile on the DSP thread (coalesced).
    /// Every virtual receiver channel open / close / reconfigure happens on the
    /// DSP thread under <see cref="_guestLock"/>, serialized with the P2 re-rate
    /// and never under <c>_engineLock</c>.
    /// </summary>
    private void RequestVrxReconcile()
    {
        if (Interlocked.Exchange(ref _vrxReconcileQueued, 1) != 0) return;
        PostDspCommand(RunQueuedVrxReconcile);
    }

    private void RunQueuedVrxReconcile()
    {
        Volatile.Write(ref _vrxReconcileQueued, 0);
        var engine = Volatile.Read(ref _engine);
        if (engine is null) return;
        var state = _radio.Snapshot();
        lock (_guestLock) ReconcileVirtualReceiversSafe(engine, state);
    }

    /// <summary>
    /// A connection whose RX1 IQ this engine demodulates itself: Protocol 1 or
    /// Protocol 2. Under Protocol 3 the sidecar owns RX DSP, so there is no IQ.
    /// </summary>
    private bool VrxSupported() =>
        !_radio.IsProtocol3Active && (_p2Client is not null || _attachedSinkP1 is not null);

    /// <summary>
    /// The operator RX1 span: the RX1 DDC centre (<c>RadioLoHz</c>) ± the same
    /// edge a guest DDC recentres at, so the widest listener passband plus a
    /// margin always stays inside the captured bandwidth.
    /// </summary>
    private VrxSpan OperatorVrxSpan(StateDto state)
    {
        long rate = Volatile.Read(ref _sampleRateHz);
        if (rate <= 0) rate = state.SampleRate;
        long half = rate > 0 ? GuestRecentreEdgeHz(rate) : 0;
        return state.RadioLoHz > 0 && half > 0 ? new VrxSpan(true, state.RadioLoHz, half) : default;
    }

    /// <summary>
    /// State thread, after every operator push: end virtual receivers whose
    /// dial left the operator's new span (<c>operator-moved</c>) or that can no
    /// longer run, and queue the channel reconcile (the shift follows RX1's LO).
    /// Touches no WDSP channel and nothing on the radio.
    /// </summary>
    private void ClampVirtualReceiversToOperator(StateDto state)
    {
        try
        {
            _vrxPool.Reconcile(VrxSupported(), OperatorVrxSpan(state));
            if (_vrxPool.HasReceivers || VrxChannelCount > 0) RequestVrxReconcile();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "public-listen: virtual receiver clamp failed");
        }
    }

    // A virtual receiver fault must never stop the operator's state push or the re-rate.
    private void ReconcileVirtualReceiversSafe(IDspEngine engine, StateDto state)
    {
        try { ReconcileVirtualReceivers(engine, state); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "public-listen: virtual receiver reconcile failed");
        }
    }

    /// <summary>
    /// Bring the virtual receiver channels in line with the book. DSP thread,
    /// under <see cref="_guestLock"/>. One slot's failure closes that slot and
    /// ends its receiver (<c>error</c>); the others still run.
    /// </summary>
    private void ReconcileVirtualReceivers(IDspEngine engine, StateDto state)
    {
        var plan = _vrxPool.Reconcile(VrxSupported(), OperatorVrxSpan(state));
        var bySlot = new VrxRequest?[VirtualReceiverPool.MaxVrxSlots];
        foreach (var receiver in plan.Retained) bySlot[receiver.Slot] = receiver;

        // Never before RX1 exists (or while a re-rate has it closed).
        bool rx1Open = Volatile.Read(ref _channelId) >= 0 && Volatile.Read(ref _rx1Reopening) == 0;
        List<VrxRequest>? failed = null;
        for (int slot = 0; slot < VirtualReceiverPool.MaxVrxSlots; slot++)
        {
            var vrx = _vrx[slot];
            // A channel from a previous engine instance died with it.
            if (Volatile.Read(ref vrx.ChannelId) >= 0 && !ReferenceEquals(vrx.Engine, engine))
                ForgetVrxChannel(slot);

            if (bySlot[slot] is not { } receiver)
            {
                CloseVrxChannel(engine, slot);
                continue;
            }

            try
            {
                // A different receiver on this slot is a different listener:
                // never reuse the previous occupant's channel, AGC or audio.
                if (Volatile.Read(ref vrx.ChannelId) >= 0 && vrx.Generation != receiver.Generation)
                    CloseVrxChannel(engine, slot);

                if (Volatile.Read(ref vrx.ChannelId) < 0)
                {
                    if (!rx1Open) continue;
                    OpenVrxChannel(engine, slot, receiver, plan.Span.CenterHz, state);
                }
                else
                {
                    ApplyStateToVrxChannel(engine, Volatile.Read(ref vrx.ChannelId), vrx, receiver.Settings, plan.Span.CenterHz);
                }
            }
            catch (Exception ex)
            {
                CloseVrxChannel(engine, slot);
                (failed ??= []).Add(receiver);
                NoteVrxReconcileFault(ex, slot);
            }
        }

        if (failed is not null) _vrxPool.Fail(failed);
    }

    private void OpenVrxChannel(IDspEngine engine, int slot, VrxRequest receiver, long loHz, StateDto state)
    {
        var vrx = _vrx[slot];
        int rateHz = Volatile.Read(ref _sampleRateHz);
        if (rateHz <= 0) rateHz = state.SampleRate > 0 ? state.SampleRate : SyntheticSampleRateHz;

        // Same lifecycle as a guest: the engine opens the RXA quiescent and
        // flips it on after its worker is live; we apply the listener's own
        // state, then publish the id (release) so the RX thread only ever feeds
        // a fully configured channel.
        int opened = engine.OpenChannel(rateHz, _panadapterWidth);
        try
        {
            // The operator's manual notches and FM settings stay theirs.
            engine.IsolateChannelFromOperatorSettings(opened);
            vrx.Applied.Reset();
            vrx.Generation = receiver.Generation;
            vrx.LastDbm = double.NaN;
            vrx.SquelchOpenUntil = long.MinValue;
            engine.SetRxAfGainDb(opened, VrxAfGainDb);
            // Squelch is the listener's own dBm gate in the tick, never WDSP's
            // operator squelch stages.
            engine.SetSquelch(opened, new SquelchConfig());
            engine.SetZoom(opened, 1);
            ApplyStateToVrxChannel(engine, opened, vrx, receiver.Settings, loHz);
            vrx.Engine = engine;
            Volatile.Write(ref vrx.ChannelId, opened);
            Interlocked.Increment(ref _vrxOpenCount);
            _log.LogInformation(
                "public-listen: virtual receiver slot {Slot} opened channel={Channel} rate={Rate}", slot, opened, rateHz);
        }
        catch
        {
            vrx.Applied.Reset();
            vrx.Generation = -1;
            vrx.Settings = null;
            try { engine.CloseChannel(opened); } catch { /* best-effort */ }
            throw;
        }
    }

    private void CloseVrxChannel(IDspEngine engine, int slot)
    {
        var vrx = _vrx[slot];
        int chan = Interlocked.Exchange(ref vrx.ChannelId, -1);
        var owner = vrx.Engine;
        ResetVrxSlot(vrx);
        if (chan < 0) return;
        Interlocked.Decrement(ref _vrxOpenCount);
        if (ReferenceEquals(owner, engine))
        {
            try { engine.CloseChannel(chan); }
            catch (Exception ex) { _log.LogDebug(ex, "public-listen: virtual receiver slot {Slot} close failed", slot); }
        }
        _log.LogInformation("public-listen: virtual receiver slot {Slot} closed channel={Channel}", slot, chan);
    }

    // Drop a channel id whose engine is gone (nothing to close).
    private void ForgetVrxChannel(int slot)
    {
        var vrx = _vrx[slot];
        int chan = Interlocked.Exchange(ref vrx.ChannelId, -1);
        ResetVrxSlot(vrx);
        if (chan >= 0) Interlocked.Decrement(ref _vrxOpenCount);
    }

    private static void ResetVrxSlot(VrxRx vrx)
    {
        vrx.Engine = null;
        vrx.Generation = -1;
        vrx.Settings = null;
        vrx.LastDbm = double.NaN;
        vrx.SquelchOpenUntil = long.MinValue;
        vrx.Applied.Reset();
    }

    // Re-rate keeps the engine: close every virtual receiver so the reconcile
    // after the re-rate reopens them at the new sample rate. Under _guestLock.
    private void CloseVrxChannels(IDspEngine engine)
    {
        for (int slot = 0; slot < VirtualReceiverPool.MaxVrxSlots; slot++)
            CloseVrxChannel(engine, slot);
    }

    // Engine swap: the old engine and its channels are gone, so their ids are
    // forgotten; a receiver opened on the NEW engine meanwhile is closed. They
    // reopen on the next reconcile, after the new engine's RX1.
    private void ResetVrxChannels()
    {
        lock (_guestLock)
        {
            var current = Volatile.Read(ref _engine);
            for (int slot = 0; slot < VirtualReceiverPool.MaxVrxSlots; slot++)
            {
                if (current is not null && ReferenceEquals(_vrx[slot].Engine, current))
                    CloseVrxChannel(current, slot);
                else
                    ForgetVrxChannel(slot);
            }
            Volatile.Write(ref _vrxOpenCount, 0);
        }
        RequestVrxReconcile();
    }

    /// <summary>
    /// Apply one virtual receiver's own state to its channel. Reads ONLY
    /// <paramref name="settings"/> and the operator's RX1 DDC centre
    /// (<paramref name="loHz"/>, for the shift): never the operator's AGC,
    /// AGC-T, NR, NB, squelch, zoom, AF gain or filter.
    /// </summary>
    private static void ApplyStateToVrxChannel(
        IDspEngine engine, int chan, VrxRx vrx, GuestReceiverSettings settings, long loHz)
    {
        long effective = CwOffset.EffectiveLoHz(settings.Mode, settings.Hz);
        ApplyListenerChannel(engine, chan, vrx.Applied, settings, (int)(effective - loHz));
        vrx.Settings = settings;
    }

    /// <summary>
    /// Push a listener receiver's settings (guest or virtual) to its channel,
    /// only what changed. <paramref name="shiftHz"/> places the dial inside the
    /// channel's IQ span (the WDSP shift stage, like the operator's CTUN).
    /// </summary>
    private static void ApplyListenerChannel(
        IDspEngine engine, int chan, ListenerChannelApplied applied, GuestReceiverSettings settings, int shiftHz)
    {
        if (applied.Mode != settings.Mode)
        {
            engine.SetMode(chan, settings.Mode);
            applied.Mode = settings.Mode;
        }
        if (applied.FilterLowHz != settings.FilterLowHz || applied.FilterHighHz != settings.FilterHighHz)
        {
            engine.SetFilter(chan, settings.FilterLowHz, settings.FilterHighHz);
            applied.FilterLowHz = settings.FilterLowHz;
            applied.FilterHighHz = settings.FilterHighHz;
        }
        if (applied.VfoHz != settings.Hz)
        {
            engine.SetVfoHz(chan, settings.Hz);
            applied.VfoHz = settings.Hz;
        }
        if (applied.ShiftHz != shiftHz)
        {
            engine.SetCtunShift(chan, shiftHz);
            applied.ShiftHz = shiftHz;
        }
        // The AGC ceiling first: a canned AGC mode is applied against it.
        if (!applied.AgcTopDb.Equals(settings.AgcTopDb))
        {
            engine.SetAgcTop(chan, settings.AgcTopDb);
            applied.AgcTopDb = settings.AgcTopDb;
        }
        if (applied.Agc != settings.Agc)
        {
            engine.SetAgc(chan, GuestAgcConfig(settings.Agc));
            applied.Agc = settings.Agc;
        }
        var eq = (settings.EqEnabled, settings.EqPreampDb, settings.EqLowDb, settings.EqMidDb, settings.EqHighDb);
        if (applied.Eq != eq)
        {
            engine.SetReceiveEqualizer(chan, eq.EqEnabled, ListenerEqualizerPreampDb(settings), eq.EqLowDb, eq.EqMidDb, eq.EqHighDb);
            applied.Eq = eq;
        }
        bool apf = settings.Apf && settings.Mode is RxMode.CWL or RxMode.CWU;
        if (applied.Apf != apf)
        {
            engine.SetAudioPeakFilter(chan, apf);
            applied.Apf = apf;
        }
        var nr = ListenerNrConfig(settings);
        if (applied.Nr != nr)
        {
            engine.SetNoiseReduction(chan, nr);
            applied.Nr = nr;
        }
    }

    // WDSP adds the preamp to each band before AGC (eq.c / RXA.c). Reserve
    // gain at that stage, before public PCM framing can hard-clip peaks.
    // Keep the requested settings for round-trip/UI and preserve relative tone;
    // only the effective listener-channel preamp is reduced when necessary.
    internal static int ListenerEqualizerPreampDb(GuestReceiverSettings settings) => settings.EqEnabled
        ? Math.Min(settings.EqPreampDb, -Math.Max(settings.EqLowDb, Math.Max(settings.EqMidDb, settings.EqHighDb)))
        : settings.EqPreampDb;

    /// <summary>A listener receiver's NR / NB / ANF / SNB, as one engine config.</summary>
    internal static NrConfig ListenerNrConfig(GuestReceiverSettings settings) => new(
        NrMode: settings.Nr switch
        {
            GuestNrMode.Nr1 => NrMode.Anr,
            GuestNrMode.Nr2 => NrMode.Emnr,
            GuestNrMode.Nnr => NrMode.Nnr,
            GuestNrMode.Nr4 => NrMode.Sbnr,
            _ => NrMode.Off,
        },
        EmnrGainMethod: settings.EmnrGainMethod,
        EmnrNpeMethod: settings.EmnrNpeMethod,
        EmnrAeRun: settings.EmnrAeRun,
        Nr4ReductionAmount: settings.Nr4ReductionAmount,
        Nr4SmoothingFactor: settings.Nr4SmoothingFactor,
        NnrModel: settings.NnrModel,
        NnrMaskFloorDb: settings.NnrMaskFloorDb,
        NnrAlpha: settings.NnrAlpha,
        NnrKneeDb: settings.NnrKneeDb,
        NnrTauSeconds: settings.NnrTauSeconds,
        NnrMaxGainDb: settings.NnrMaxGainDb,
        NnrAttackMs: settings.NnrAttackMs,
        NnrReleaseMs: settings.NnrReleaseMs,
        AnfEnabled: settings.Anf,
        SnbEnabled: settings.Snb,
        NbMode: settings.Nb switch
        {
            GuestNbMode.Nb1 => NbMode.Nb1,
            GuestNbMode.Nb2 => NbMode.Nb2,
            _ => NbMode.Off,
        });

    /// <summary>
    /// The listener squelch: open while the receiver's own S-meter is at or
    /// above the threshold, and for <see cref="ListenerSquelchHang"/> after it
    /// last was. Returns the new "open until" timestamp.
    /// </summary>
    internal static bool ListenerSquelchOpen(
        bool enabled, double thresholdDbm, double dbm, long now, long hangTicks, ref long openUntil)
    {
        if (!enabled) return true;
        if (double.IsFinite(dbm) && dbm >= thresholdDbm)
        {
            openUntil = now + hangTicks;
            return true;
        }
        return openUntil != long.MinValue && now < openUntil;
    }

    // ---- tick (DSP thread) ---------------------------------------------------

    /// <summary>
    /// Drain every open virtual receiver (at most RX1's sample count — RX1 is
    /// the audio clock) and publish its audio and S-meter to the listener feed
    /// and the receiver book only.
    /// </summary>
    private void TickVirtualReceivers(IDspEngine engine, int rx1SampleCount)
    {
        if (Volatile.Read(ref _vrxOpenCount) <= 0) return;
        var tap = _hub.ListenerTap;
        bool held = tap?.IsTransmitHeld ?? true;
        long now = GuestClock.GetTimestamp();
        long hangTicks = (long)(ListenerSquelchHang.TotalSeconds * GuestClock.TimestampFrequency);
        double calOffsetDb = RadioCalibrations.RxMeterOffsetDb(_radio.EffectiveBoardKind, _radio.EffectiveOrionMkIIVariant);
        double physicalAttenuationDb = PhysicalReceiveMeterAttenuationDb(ActualPrimaryReceiveAdcSource());
        for (int slot = 0; slot < _vrx.Length; slot++)
        {
            var vrx = _vrx[slot];
            int chan = Volatile.Read(ref vrx.ChannelId);
            if (chan < 0) continue;
            try
            {
                long generation = vrx.Generation;
                // Meter first (the squelch gates on it). A reading taken while
                // keyed shows the operator's own transmission: none is published.
                double dbm = double.NaN;
                if (!held)
                {
                    double raw = engine.GetRxaSignalDbm(chan);
                    // -400 = xmeter never ran (docs/lessons/wdsp-init-gotchas.md): no reading.
                    dbm = CalibrateListenerSignalDbm(raw, calOffsetDb, physicalAttenuationDb);
                }
                vrx.LastDbm = dbm;
                _vrxPool.PublishMeter(slot, generation, dbm);

                // While RX1 is starved by a transmission the ring would back up
                // (virtual receivers are not stopped by MOX); drain and discard
                // it, the listener would only get silence for it anyway.
                int want = rx1SampleCount > 0
                    ? Math.Min(rx1SampleCount, vrx.AudioBuf.Length)
                    : held ? vrx.AudioBuf.Length : 0;
                int n = want > 0 ? engine.ReadAudio(chan, vrx.AudioBuf.AsSpan(0, want)) : 0;
                if (n <= 0 || rx1SampleCount <= 0 || tap is null) continue;

                var settings = vrx.Settings;
                if (settings is not null
                    && !ListenerSquelchOpen(settings.Squelch, settings.SquelchDb, dbm, now, hangTicks, ref vrx.SquelchOpenUntil))
                {
                    Array.Clear(vrx.AudioBuf, 0, n);
                }
                tap.OfferVrxAudio(
                    slot,
                    vrx.AudioBuf.AsSpan(0, n),
                    AudioOutputRateHz,
                    EstimateListenerCaptureUnixMs(
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        n,
                        vrx.AudioBuf.Length,
                        AudioOutputRateHz));
            }
            catch (Exception ex)
            {
                NoteVrxFault(ex, slot, "tick");
            }
        }
    }

    private void NoteVrxFault(Exception ex, int slot, string where)
    {
        long faults = Interlocked.Increment(ref _vrxFaults);
        if (faults == 1 || faults % 1000 == 0)
            _log.LogWarning(ex, "public-listen: virtual receiver slot {Slot} {Where} failed ({Faults} total)", slot, where, faults);
    }

    private void NoteVrxReconcileFault(Exception ex, int slot)
    {
        long faults = Interlocked.Increment(ref _vrxReconcileFaults);
        var clock = GuestClock;
        long now = clock.GetTimestamp();
        long last = Interlocked.Read(ref _vrxReconcileFaultLogTimestamp);
        if (last != long.MinValue && clock.GetElapsedTime(last, now) < GuestFaultLogInterval) return;
        if (Interlocked.CompareExchange(ref _vrxReconcileFaultLogTimestamp, now, last) != last) return;
        _log.LogWarning(
            ex,
            "public-listen: virtual receiver slot {Slot} channel failed; receiver ended ({Faults} total)",
            slot,
            faults);
    }
}
