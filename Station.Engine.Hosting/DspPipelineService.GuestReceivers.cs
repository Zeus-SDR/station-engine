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
using GuestDdcSpec = Zeus.Protocol2.Protocol2Client.GuestDdcSpec;

namespace Zeus.Server;

// Public Listening Phase 3 (docs/designs/public-listening.md, "Phase 3 — guest
// receiver control protocol"): guest receivers on spare Protocol 2 DDCs.
//
// Isolation rules this file keeps (each pinned by GuestAudioIsolationTests):
//   • every guest owns its own WDSP RXA channel, opened through the same
//     engine.OpenChannel as a secondary receiver (WdspDspEngine opens at
//     state 0 and flips SetChannelState(id, 1, 0) after the worker is live —
//     docs/lessons/wdsp-init-gotchas.md), and its state is applied only by
//     ApplyStateToGuestChannel from the guest's own settings: never the
//     operator's AGC, AGC-T, NR, squelch, zoom or AF gain;
//   • guests never enter _secondaryRx, the operator mix, RxAudioAvailable,
//     ReceiverAudioAvailable, the product-plugin audio port, the hub, the
//     meters stream, StateDto.Receivers or TxReceiverIndex;
//   • guest IQ (ReceiverIndex -2 - slot) feeds only that guest's channel and
//     never falls through to RX1; IQ that may still belong to a previous
//     occupant of the slot's DDC is dropped for GuestIqSettleTime;
//   • guest display and audio leave the engine only through the listener feed
//     (0x41 / 0x43, stream 0xE0 + slot), where PublicTransmitHold applies.
public partial class DspPipelineService
{
    /// <summary>
    /// After a guest channel opens, or its physical DDC moves or retunes, IQ
    /// that is still in flight from the previous DDC assignment is dropped for
    /// this long (the radio applies a CmdRx / high-priority packet in a few ms).
    /// </summary>
    internal static readonly TimeSpan GuestIqSettleTime = TimeSpan.FromMilliseconds(250);

    /// <summary>Default guest AGC ceiling: the stock default, never the operator's AGC-T (the listener may change it).</summary>
    internal const double GuestAgcTopDb = GuestReceiverSettings.DefaultAgcTopDb;

    /// <summary>Gain used when a guest turns AGC off (fixed-gain AGC).</summary>
    internal const double GuestFixedAgcGainDb = 20.0;

    /// <summary>Guest AF gain is fixed: listener volume is the listener's own business.</summary>
    internal const double GuestAfGainDb = 0.0;

    // Recentre the guest DDC before the dial reaches its capture edge: never
    // later than this fraction of the rate (the pre-existing high-rate
    // behaviour), and never so late that the widest guest passband edge
    // (MaxFilterEdgeHz) plus this margin would pass the DDC's Nyquist edge.
    private const double GuestRecentreFraction = 0.40;
    internal const int GuestRecentreMarginHz = 2_000;

    // Guest channel-open / apply faults are logged at most this often.
    private static readonly TimeSpan GuestFaultLogInterval = TimeSpan.FromSeconds(10);

    private readonly GuestReceiverPool _guestPool = new();
    private readonly GuestRx[] _guestRx = CreateGuestRxSlots();
    // Serializes every guest channel open / close / reconfigure with the P2
    // re-rate and the engine-swap reset. Taken on the DSP thread (and by the
    // swap under _engineLock: order is always _engineLock -> _guestLock);
    // never held while taking _engineLock, so MOX / TUNE never wait on it.
    private readonly object _guestLock = new();
    private int _guestReconcileQueued;
    // 1 while a P2 re-rate has RX1 closed (or failed to reopen it). No guest
    // RXA may open then: WdspDspEngine adopts the first RXA it opens while
    // it has no primary as its primary (PureSignal / MOX) channel.
    private int _rx1Reopening;
    private int _guestOpenCount;
    private long _guestFaults;
    private long _guestReconcileFaults;
    private long _guestReconcileFaultLogTimestamp = long.MinValue;

    /// <summary>The engine side of the guest lease book (resolved by the host as <see cref="IGuestReceiverPool"/>).</summary>
    internal GuestReceiverPool GuestPool => _guestPool;

    /// <summary>Clock for the guest IQ settle window; replaced only by tests.</summary>
    internal TimeProvider GuestClock { get; set; } = TimeProvider.System;

    /// <summary>Open guest WDSP channels (diagnostics / tests).</summary>
    internal int GuestChannelCount => Volatile.Read(ref _guestOpenCount);

    /// <summary>Guest channel opens / applies that failed and evicted their lease (diagnostics / tests).</summary>
    internal long GuestReconcileFaults => Interlocked.Read(ref _guestReconcileFaults);

    internal int GuestChannelId(int slot) =>
        slot is >= 0 and < GuestReceiverPool.MaxGuestSlots ? Volatile.Read(ref _guestRx[slot].ChannelId) : -1;

    /// <summary>The physical DDC the pipeline last saw carrying <paramref name="slot"/> (diagnostics / tests).</summary>
    internal int GuestPhysicalDdc(int slot) =>
        slot is >= 0 and < GuestReceiverPool.MaxGuestSlots ? Volatile.Read(ref _guestRx[slot].PhysicalDdc) : -1;

    private sealed class GuestRx
    {
        public int ChannelId = -1;      // Volatile; -1 = no channel
        public IDspEngine? Engine;      // the engine ChannelId belongs to
        public long Generation = -1;    // lease generation the open channel belongs to
        public int PhysicalDdc = -1;    // Interlocked; the DDC the guest's IQ comes from
        public long DropIqUntil;        // GuestClock timestamp; Volatile/Interlocked
        public long LoHz;               // the guest DDC centre (CTUN-frozen)
        public bool LoInit;
        public float[] RowBuf = [];
        public int DisplayZoom = 1;
        public readonly float[] AudioBuf = new float[AudioDrainCapacity];
        public readonly ListenerChannelApplied Applied = new();
        public GuestReceiverSettings? Settings; // DSP thread: what the tick gates on (squelch)
        public long SquelchOpenUntil = long.MinValue;

        public void ResetApplied()
        {
            Applied.Reset();
            Settings = null;
            SquelchOpenUntil = long.MinValue;
            LoInit = false;
        }
    }

    private static GuestRx[] CreateGuestRxSlots()
    {
        var slots = new GuestRx[GuestReceiverPool.MaxGuestSlots];
        for (int i = 0; i < slots.Length; i++) slots[i] = new GuestRx();
        return slots;
    }

    private long GuestSettleTicks =>
        (long)(GuestIqSettleTime.TotalSeconds * GuestClock.TimestampFrequency);

    // ---- IQ (RX thread) ----------------------------------------------------

    private void FeedGuestIq(IDspEngine engine, int receiverIndex, ReadOnlySpan<double> iq)
    {
        int slot = Zeus.Protocol2.Protocol2Client.FirstGuestReceiverIndex - receiverIndex;
        if (slot is < 0 or >= GuestReceiverPool.MaxGuestSlots) return;
        var guest = _guestRx[slot];
        int chan = Volatile.Read(ref guest.ChannelId);
        if (chan < 0) return;
        // The guest's physical DDC moves whenever the operator's run or a
        // lower guest changes (on the state thread, ahead of any reconcile).
        // IQ still in flight on the new DDC belongs to its previous owner —
        // possibly the operator's own receiver — so a move re-arms the drop.
        int ddc = _p2Client?.GuestDdcIndex(slot) ?? -1;
        if (ddc < 0) return;
        long now = GuestClock.GetTimestamp();
        if (Interlocked.Exchange(ref guest.PhysicalDdc, ddc) != ddc)
        {
            Interlocked.Exchange(ref guest.DropIqUntil, now + GuestSettleTicks);
            return;
        }
        if (now < Interlocked.Read(ref guest.DropIqUntil)) return;
        engine.FeedIq(chan, iq);
    }

    // ---- reconcile ---------------------------------------------------------

    // Any thread (the lease book changed): the work runs on the DSP thread.
    private void OnGuestLeasesChanged() => RequestGuestReconcile();

    /// <summary>
    /// Queue one guest reconcile on the DSP thread (coalesced). Listener and
    /// state threads only enqueue: every guest WDSP channel open / close /
    /// reconfigure and every guest DDC send happens on the DSP thread, under
    /// <see cref="_guestLock"/>, serialized with the P2 re-rate and never
    /// under <c>_engineLock</c>.
    /// </summary>
    private void RequestGuestReconcile()
    {
        if (Interlocked.Exchange(ref _guestReconcileQueued, 1) != 0) return;
        PostDspCommand(RunQueuedGuestReconcile);
    }

    private void RunQueuedGuestReconcile()
    {
        Volatile.Write(ref _guestReconcileQueued, 0);
        var engine = Volatile.Read(ref _engine);
        if (engine is null) return;
        var state = _radio.Snapshot();
        bool structural;
        lock (_guestLock) structural = ReconcileGuestReceiversSafe(engine, state);
        if (structural) ReconcileWidebandDetailSource(engine, state);
    }

    // Guest capability of the live connection: a guest-capable Protocol 2
    // board, with the display DDC always reserved behind the guests.
    private (bool Supported, int Hardware) GuestHardware()
    {
        var p2 = _p2Client;
        bool supported = p2 is not null && p2.SupportsGuestDdcs && !_radio.IsProtocol3Active;
        return (supported, supported ? p2!.GuestDdcCapacity(displayDdcWanted: true) : 0);
    }

    /// <summary>
    /// State thread, right after the operator's receivers were pushed: clamp
    /// the lease book to what they left free (evicting the newest guests)
    /// without touching any WDSP channel. The wire placement is already
    /// clamped by the protocol client itself.
    /// </summary>
    private void ClampGuestLeasesToOperator()
    {
        try
        {
            var (supported, hardware) = GuestHardware();
            _guestPool.Reconcile(supported, hardware);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "public-listen: guest lease clamp failed");
        }
    }

    // A guest fault must never stop the operator's state push or the re-rate.
    private bool ReconcileGuestReceiversSafe(IDspEngine engine, StateDto state)
    {
        try { return ReconcileGuestReceivers(engine, state); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "public-listen: guest receiver reconcile failed");
            return false;
        }
    }

    /// <summary>
    /// Bring the guest channels and guest DDCs in line with the lease book.
    /// DSP thread, under <see cref="_guestLock"/>. Capacity is what the
    /// operator's receivers left free on a guest-capable Protocol 2 board
    /// (keeping the hidden display DDC); anything else — Protocol 1,
    /// Protocol 3, Hermes-class P2 — is capacity 0. One slot's failure closes
    /// that slot and ends its lease (<c>error</c>); the others still run.
    /// Returns true when the set of open guest channels changed.
    /// </summary>
    private bool ReconcileGuestReceivers(IDspEngine engine, StateDto state)
    {
        var p2 = _p2Client;
        var (supported, hardware) = GuestHardware();
        var plan = _guestPool.Reconcile(supported, hardware);

        var bySlot = new GuestLeaseRequest?[GuestReceiverPool.MaxGuestSlots];
        foreach (var lease in plan.Retained) bySlot[lease.Slot] = lease;

        // Never before RX1 exists (or while a re-rate has it closed).
        bool rx1Open = Volatile.Read(ref _channelId) >= 0 && Volatile.Read(ref _rx1Reopening) == 0;
        bool structural = false;
        long now = GuestClock.GetTimestamp();
        long dropUntil = now + GuestSettleTicks;
        var specs = new List<GuestDdcSpec>(plan.Retained.Count);
        List<GuestLeaseRequest>? failed = null;
        byte adc = ReceiverAdcSource(state, 0);
        for (int slot = 0; slot < GuestReceiverPool.MaxGuestSlots; slot++)
        {
            var guest = _guestRx[slot];
            // A channel from a previous engine instance died with it.
            if (Volatile.Read(ref guest.ChannelId) >= 0 && !ReferenceEquals(guest.Engine, engine))
                structural |= ForgetGuestChannel(slot);

            if (bySlot[slot] is not { } lease)
            {
                structural |= CloseGuestChannel(engine, slot);
                continue;
            }

            try
            {
                // A different lease on this slot is a different listener: never
                // reuse the previous occupant's channel, AGC state or queued audio.
                if (Volatile.Read(ref guest.ChannelId) >= 0 && guest.Generation != lease.Generation)
                    structural |= CloseGuestChannel(engine, slot);

                if (Volatile.Read(ref guest.ChannelId) < 0)
                {
                    if (!rx1Open) continue;
                    OpenGuestChannel(engine, slot, lease, state, dropUntil);
                    structural = true;
                }

                // A recentre moves the guest's NCO: IQ in flight is from the old centre.
                if (ApplyStateToGuestChannel(engine, Volatile.Read(ref guest.ChannelId), guest, lease.Settings, state))
                    Interlocked.Exchange(ref guest.DropIqUntil, dropUntil);
                specs.Add(new GuestDdcSpec(slot, guest.LoHz, adc));
            }
            catch (Exception ex)
            {
                structural |= CloseGuestChannel(engine, slot);
                (failed ??= []).Add(lease);
                NoteGuestReconcileFault(ex, slot);
            }
        }

        p2?.SetGuestDdcs(specs);

        // The physical DDC each guest now sits on (operator run + rank, from
        // the protocol client's shared occupancy rule). A move re-arms the IQ
        // drop so operator IQ never reaches a guest and vice versa.
        for (int slot = 0; slot < GuestReceiverPool.MaxGuestSlots; slot++)
        {
            var guest = _guestRx[slot];
            if (Volatile.Read(ref guest.ChannelId) < 0) continue;
            int ddc = p2?.GuestDdcIndex(slot) ?? -1;
            if (Interlocked.Exchange(ref guest.PhysicalDdc, ddc) != ddc)
                Interlocked.Exchange(ref guest.DropIqUntil, dropUntil);
        }

        if (failed is not null) _guestPool.Fail(failed);
        return structural;
    }

    private void NoteGuestReconcileFault(Exception ex, int slot)
    {
        long faults = Interlocked.Increment(ref _guestReconcileFaults);
        var clock = GuestClock;
        long now = clock.GetTimestamp();
        long last = Interlocked.Read(ref _guestReconcileFaultLogTimestamp);
        if (last != long.MinValue && clock.GetElapsedTime(last, now) < GuestFaultLogInterval) return;
        if (Interlocked.CompareExchange(ref _guestReconcileFaultLogTimestamp, now, last) != last) return;
        _log.LogWarning(
            ex,
            "public-listen: guest slot {Slot} channel failed; lease ended ({Faults} total)",
            slot,
            faults);
    }

    private void OpenGuestChannel(IDspEngine engine, int slot, GuestLeaseRequest lease, StateDto state, long dropIqUntil)
    {
        var guest = _guestRx[slot];
        int rateHz = Volatile.Read(ref _sampleRateHz);
        if (rateHz <= 0) rateHz = state.SampleRate > 0 ? state.SampleRate : SyntheticSampleRateHz;

        // Same lifecycle as EnsureSecondaryRxChannel: the engine opens the
        // RXA quiescent and flips it on after its worker is live; we apply
        // the guest's own state, then publish the id (release) so the RX
        // thread sees a fully configured channel with the settle window armed.
        int opened = engine.OpenChannel(rateHz, 4096);
        try
        {
            // The operator's manual notches and FM settings stay theirs.
            engine.IsolateChannelFromOperatorSettings(opened);
            guest.ResetApplied();
            guest.Generation = lease.Generation;
            Interlocked.Exchange(ref guest.PhysicalDdc, -1);
            if (guest.RowBuf.Length != 4096) guest.RowBuf = new float[4096];
            engine.SetRxAfGainDb(opened, GuestAfGainDb);
            engine.SetSquelch(opened, new SquelchConfig());
            engine.SetRxDisplayFftSize(opened, 65_536);
            engine.SetRxDisplayZoom(opened, 1);
            guest.DisplayZoom = 1;
            ApplyStateToGuestChannel(engine, opened, guest, lease.Settings, state);
            Interlocked.Exchange(ref guest.DropIqUntil, dropIqUntil);
            guest.Engine = engine;
            Volatile.Write(ref guest.ChannelId, opened);
            Interlocked.Increment(ref _guestOpenCount);
            _log.LogInformation(
                "public-listen: guest slot {Slot} opened channel={Channel} rate={Rate}", slot, opened, rateHz);
        }
        catch
        {
            guest.ResetApplied();
            guest.Generation = -1;
            try { engine.CloseChannel(opened); } catch { /* best-effort */ }
            throw;
        }
    }

    private bool CloseGuestChannel(IDspEngine engine, int slot)
    {
        var guest = _guestRx[slot];
        int chan = Interlocked.Exchange(ref guest.ChannelId, -1);
        var owner = guest.Engine;
        guest.Engine = null;
        guest.Generation = -1;
        Interlocked.Exchange(ref guest.PhysicalDdc, -1);
        guest.ResetApplied();
        if (chan < 0) return false;
        Interlocked.Decrement(ref _guestOpenCount);
        if (ReferenceEquals(owner, engine))
        {
            try { engine.CloseChannel(chan); }
            catch (Exception ex) { _log.LogDebug(ex, "public-listen: guest slot {Slot} close failed", slot); }
        }
        _log.LogInformation("public-listen: guest slot {Slot} closed channel={Channel}", slot, chan);
        return true;
    }

    // Drop a channel id whose engine is gone (nothing to close).
    private bool ForgetGuestChannel(int slot)
    {
        var guest = _guestRx[slot];
        int chan = Interlocked.Exchange(ref guest.ChannelId, -1);
        guest.Engine = null;
        guest.Generation = -1;
        Interlocked.Exchange(ref guest.PhysicalDdc, -1);
        guest.ResetApplied();
        if (chan < 0) return false;
        Interlocked.Decrement(ref _guestOpenCount);
        return true;
    }

    // Re-rate keeps the engine: close every guest channel so the reconcile
    // after the re-rate reopens them at the new sample rate. Under _guestLock.
    private void CloseGuestChannels(IDspEngine engine)
    {
        for (int slot = 0; slot < GuestReceiverPool.MaxGuestSlots; slot++)
            CloseGuestChannel(engine, slot);
    }

    // Engine swap (under _engineLock, after _engine was replaced): the old
    // engine and its channels are gone, so their ids are forgotten; a guest
    // the DSP thread managed to open on the NEW engine meanwhile is closed.
    // Guests reopen on the next reconcile, after the new engine's RX1.
    private void ResetGuestChannels()
    {
        lock (_guestLock)
        {
            var current = Volatile.Read(ref _engine);
            for (int slot = 0; slot < GuestReceiverPool.MaxGuestSlots; slot++)
            {
                var guest = _guestRx[slot];
                if (current is not null && ReferenceEquals(guest.Engine, current))
                    CloseGuestChannel(current, slot);
                else
                    ForgetGuestChannel(slot);
            }
            Volatile.Write(ref _guestOpenCount, 0);
            Volatile.Write(ref _rx1Reopening, 0);
        }
        RequestGuestReconcile();
    }

    /// <summary>
    /// How far a guest's dial may roam from its DDC centre before the DDC
    /// recentres: the widest passband edge (<see cref="GuestReceiverSettings.MaxFilterEdgeHz"/>)
    /// plus <see cref="GuestRecentreMarginHz"/> always stays inside the DDC's
    /// Nyquist edge (rate / 2), and never later than
    /// <see cref="GuestRecentreFraction"/> of the rate.
    /// </summary>
    internal static long GuestRecentreEdgeHz(long sampleRateHz)
    {
        long nyquistEdge = sampleRateHz / 2 - GuestReceiverSettings.MaxFilterEdgeHz - GuestRecentreMarginHz;
        long fractionEdge = (long)(sampleRateHz * GuestRecentreFraction);
        return Math.Max(0, Math.Min(nyquistEdge, fractionEdge));
    }

    /// <summary>
    /// Apply one guest's own DSP state to its channel. Reads ONLY
    /// <paramref name="settings"/> (plus the connection's sample rate for the
    /// DDC recentre): never the operator's AGC, AGC-T, NR, NB, squelch, zoom,
    /// AF gain or filter. CTUN-style: the guest DDC NCO stays put while the
    /// dial roams inside the capture window and recentres near its edge.
    /// Returns true when the DDC centre moved.
    /// </summary>
    private bool ApplyStateToGuestChannel(
        IDspEngine engine, int chan, GuestRx guest, GuestReceiverSettings settings, StateDto state)
    {
        long effective = CwOffset.EffectiveLoHz(settings.Mode, settings.Hz);
        long span = Volatile.Read(ref _sampleRateHz);
        if (span <= 0) span = state.SampleRate > 0 ? state.SampleRate : SyntheticSampleRateHz;
        long edge = GuestRecentreEdgeHz(span);
        bool recentred = false;
        if (!guest.LoInit || Math.Abs(effective - guest.LoHz) > edge)
        {
            recentred = guest.LoInit && guest.LoHz != effective;
            guest.LoHz = effective;
            guest.LoInit = true;
        }
        ApplyListenerChannel(engine, chan, guest.Applied, settings, (int)(effective - guest.LoHz));
        int zoom = GuestViewportZoom((int)span, guest.LoHz, settings.DisplayCenterHz, settings.DisplaySpanHz);
        if (guest.DisplayZoom != zoom) { engine.SetRxDisplayZoom(chan, zoom); guest.DisplayZoom = zoom; }
        guest.Settings = settings;
        return recentred;
    }

    // Keep at least one native FFT bin per output pixel; never interpolate finer detail.
    internal static int GuestViewportZoom(int sampleRate, long captureCenter, long center, int span)
    {
        if (span <= 0) return 1;
        double needed = 2 * Math.Abs((double)center - captureCenter) + span;
        int zoom = 1;
        while (zoom < 16 && sampleRate / (zoom * 2.0) >= needed) zoom *= 2;
        return zoom;
    }

    internal static AgcConfig GuestAgcConfig(GuestAgcMode agc) => agc switch
    {
        GuestAgcMode.Off => new AgcConfig(AgcMode.Fixed, FixedGainDb: GuestFixedAgcGainDb),
        GuestAgcMode.Long => new AgcConfig(AgcMode.Long),
        GuestAgcMode.Slow => new AgcConfig(AgcMode.Slow),
        GuestAgcMode.Fast => new AgcConfig(AgcMode.Fast),
        _ => new AgcConfig(AgcMode.Med),
    };

    internal static NrConfig GuestNrConfig(GuestNrMode nr) => nr switch
    {
        GuestNrMode.Nr1 => new NrConfig(NrMode.Anr),
        GuestNrMode.Nr2 => new NrConfig(NrMode.Emnr),
        _ => new NrConfig(NrMode.Off),
    };

    // ---- tick (DSP thread) ---------------------------------------------------

    /// <summary>
    /// Drain every open guest channel (at most RX1's sample count, like the
    /// secondary receivers — RX1 is the audio clock) and publish its audio,
    /// panadapter row and S-meter to the listener feed only.
    /// </summary>
    private void TickGuestReceivers(IDspEngine engine, int rx1SampleCount, int sampleRateHz)
    {
        if (Volatile.Read(ref _guestOpenCount) <= 0) return;
        var tap = _hub.ListenerTap;
        bool held = tap?.IsTransmitHeld ?? true;
        for (int slot = 0; slot < GuestReceiverPool.MaxGuestSlots; slot++)
        {
            var guest = _guestRx[slot];
            int chan = Volatile.Read(ref guest.ChannelId);
            if (chan < 0) continue;
            try
            {
                // While RX1 is starved by a transmission the guest ring would
                // back up (guests are not stopped by MOX); drain and discard it,
                // the listener would only get silence for it anyway.
                int want = rx1SampleCount > 0
                    ? Math.Min(rx1SampleCount, guest.AudioBuf.Length)
                    : held ? guest.AudioBuf.Length : 0;
                int n = want > 0 ? engine.ReadAudio(chan, guest.AudioBuf.AsSpan(0, want)) : 0;
                if (n > 0 && rx1SampleCount > 0 && tap is not null)
                {
                    // The listener's own squelch (Phase 4), on the guest's own
                    // S-meter; nothing is read unless the listener turned it on.
                    if (guest.Settings is { Squelch: true } squelch)
                    {
                        double raw = tap.IsTransmitHeld ? double.NaN : engine.GetRxaSignalDbm(chan);
                        double dbm = double.IsFinite(raw) && raw > -399.0
                            ? CalibrateGuestSignalDbm(raw, slot)
                            : double.NaN;
                        long now = GuestClock.GetTimestamp();
                        long hang = (long)(ListenerSquelchHang.TotalSeconds * GuestClock.TimestampFrequency);
                        if (!ListenerSquelchOpen(true, squelch.SquelchDb, dbm, now, hang, ref guest.SquelchOpenUntil))
                            Array.Clear(guest.AudioBuf, 0, n);
                    }
                    tap.OfferGuestAudio(
                        slot,
                        guest.AudioBuf.AsSpan(0, n),
                        AudioOutputRateHz,
                        EstimateListenerCaptureUnixMs(
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            n,
                            guest.AudioBuf.Length,
                            AudioOutputRateHz));
                }

                if (tap is null)
                {
                    ClearGuestMeter(slot, guest);
                    continue;
                }
                if (tap.IsTransmitHeld)
                {
                    // A reading taken while keyed shows the operator's own
                    // transmission: none is published (the guest status sends
                    // dbm:null) until the hold releases.
                    ClearGuestMeter(slot, guest);
                }
                if (!tap.TryBeginGuestSpectrum(slot)) continue;
                double tsUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                int width = guest.RowBuf.Length;
                float hzPerPixel = VisibleDdcHzPerPixel(sampleRateHz > 0 ? sampleRateHz : SyntheticSampleRateHz, guest.DisplayZoom, width);
                if (tap.IsTransmitHeld)
                {
                    // Not read: a guest spectrum captured while keyed never leaves.
                    tap.OfferGuestSpectrum(slot, ReadOnlySpan<float>.Empty, width, tsUnixMs, guest.LoHz, hzPerPixel);
                    continue;
                }
                PublishGuestMeter(engine, slot, chan, guest);
                bool got = engine.TryGetDisplayPixels(chan, DisplayPixout.Waterfall, guest.RowBuf)
                    || engine.TryGetDisplayPixels(chan, DisplayPixout.Panadapter, guest.RowBuf);
                if (!got) continue;
                Array.Reverse(guest.RowBuf);
                SanitizeDisplayBuffer(guest.RowBuf);
                tap.OfferGuestSpectrum(slot, guest.RowBuf, width, tsUnixMs, guest.LoHz, hzPerPixel);
            }
            catch (Exception ex)
            {
                long faults = Interlocked.Increment(ref _guestFaults);
                if (faults == 1 || faults % 1000 == 0)
                    _log.LogWarning(ex, "public-listen: guest slot {Slot} tick failed ({Faults} total)", slot, faults);
            }
        }
    }

    private void PublishGuestMeter(IDspEngine engine, int slot, int chan, GuestRx guest)
    {
        double raw = engine.GetRxaSignalDbm(chan);
        // -400 = xmeter never ran (docs/lessons/wdsp-init-gotchas.md): no reading.
        if (!double.IsFinite(raw) || raw <= -399.0) return;
        double dbm = CalibrateGuestSignalDbm(raw, slot);
        _guestPool.PublishSignalDbm(slot, guest.Generation, dbm);
    }

    private double CalibrateGuestSignalDbm(double raw, int slot)
    {
        if (_p2Client is not { } client || !client.TryGetGuestAdcSource(slot, out byte adc)) return double.NaN;
        return CalibrateListenerSignalDbm(raw,
            RadioCalibrations.RxMeterOffsetDb(_radio.EffectiveBoardKind, _radio.EffectiveOrionMkIIVariant),
            PhysicalReceiveMeterAttenuationDb(adc));
    }

    private double PhysicalReceiveMeterAttenuationDb(byte adc) => _radio.EffectivePhysicalAdcAttenuationDb(adc);

    internal static double CalibrateListenerSignalDbm(double raw, double boardOffsetDb, double effectiveAttenuationDb) =>
        double.IsFinite(raw) && raw > -399.0 ? raw + boardOffsetDb + Math.Clamp(effectiveAttenuationDb, 0, 31) : double.NaN;

    private byte ActualPrimaryReceiveAdcSource() => _p2Client?.ReceiveFilters?.Banks
        .FirstOrDefault(b => b.Demands.Any(d => d.Role == "primary"))?.PhysicalAdcSource
        ?? PrimaryReceiverAdcSource(_radio.Snapshot());

    private void ClearGuestMeter(int slot, GuestRx guest) =>
        _guestPool.PublishSignalDbm(slot, guest.Generation, double.NaN);
}
