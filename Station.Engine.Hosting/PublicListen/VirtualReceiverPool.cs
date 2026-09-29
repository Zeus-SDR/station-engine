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

using Zeus.Contracts;
using Zeus.Dsp.Wdsp;

namespace Zeus.Server.PublicListen;

/// <summary>
/// One virtual receiver (Phase 4) as the host wants it. <see cref="Generation"/>
/// is unique per allocation (monotonic), so a reused slot never inherits the
/// previous listener's channel, meter or queued audio.
/// </summary>
public readonly record struct VrxRequest(int Slot, long Generation, GuestReceiverSettings Settings);

/// <summary>
/// The operator RX1 span a virtual receiver may tune inside: the RX1 DDC centre
/// ± <see cref="HalfWidthHz"/> (the capture half-width less the widest listener
/// passband and a margin). The dial must lie inside [<see cref="MinHz"/>, <see cref="MaxHz"/>].
/// </summary>
public readonly record struct VrxSpan(bool Valid, long CenterHz, long HalfWidthHz)
{
    public long MinHz => Valid ? CenterHz - HalfWidthHz : 0;

    public long MaxHz => Valid ? CenterHz + HalfWidthHz : 0;

    public bool Contains(long hz) => Valid && hz >= CenterHz - HalfWidthHz && hz <= CenterHz + HalfWidthHz;
}

/// <summary>A virtual receiver the engine stopped, with the listener-facing <c>vrx-ended</c> reason.</summary>
public readonly record struct VrxEnded(int Slot, long Generation, string Reason);

/// <summary><c>vrx-ended</c> reasons the engine itself produces.</summary>
public static class VrxEngineEndReasons
{
    /// <summary>The operator retuned RX1 and the receiver's dial left the span.</summary>
    public const string OperatorMoved = "operator-moved";

    /// <summary>The channel failed, or the radio went away / cannot host receivers.</summary>
    public const string Error = "error";

    /// <summary>The operator lowered the receiver count below the number running.</summary>
    public const string Disabled = "disabled";
}

/// <summary>
/// Engine seam for Public Listening virtual receivers (docs/designs/public-listening.md,
/// "Phase 4 — virtual receivers"). The host decides WHO holds which slot; the
/// engine owns the WDSP channels, the span rule and the operator-always-wins
/// capacity ceiling. A virtual receiver never touches the radio: it is a WDSP
/// RXA channel fed the operator's RX1 IQ and shifted inside that span.
/// </summary>
public interface IVirtualReceiverPool
{
    /// <summary>Slot numbers run 0..MaxSlots-1 (stream id 0xC0 + slot).</summary>
    int MaxSlots { get; }

    /// <summary>Noise reduction modes available in this engine.</summary>
    IReadOnlyList<GuestNrMode> NrModes => [GuestNrMode.Off, GuestNrMode.Nr1, GuestNrMode.Nr2];

    /// <summary>A radio is connected whose RX1 IQ the engine demodulates itself (Protocol 1 / 2).</summary>
    bool Supported { get; }

    /// <summary>Usable slots now: min(pool size, the WDSP-pool ceiling) while supported, else 0.</summary>
    int Capacity { get; }

    /// <summary>The operator RX1 span receivers must stay inside.</summary>
    VrxSpan Span { get; }

    /// <summary>The operator's receiver cap (0 = virtual receivers off).</summary>
    void SetPoolSize(int size);

    /// <summary>Replace the desired receiver set (invalid or duplicate slots are ignored).</summary>
    void SetReceivers(IReadOnlyList<VrxRequest> receivers);

    /// <summary>The receiver's calibrated S-meter and a noise-floor-relative SNR, when it has one.</summary>
    bool TryGetMeter(int slot, long generation, out double dbm, out double snrDb);

    /// <summary>Receivers the engine stopped (operator moved, error). Raised off the engine's locks.</summary>
    event Action<IReadOnlyList<VrxEnded>>? ReceiversEnded;

    /// <summary><see cref="Capacity"/> or <see cref="Supported"/> changed.</summary>
    event Action? CapacityChanged;
}

/// <summary>Result of one engine reconcile: the receivers to run (slot order).</summary>
internal readonly record struct VrxPlan(IReadOnlyList<VrxRequest> Retained, VrxSpan Span, int Capacity);

/// <summary>
/// Thread-safe virtual receiver book shared by the host and the DSP pipeline.
/// The host writes the desired set; the pipeline calls <see cref="Reconcile"/>
/// after every operator state push (state thread) and on every receiver
/// reconcile (DSP thread) with the live span, which ends receivers whose dial
/// left the span (<c>operator-moved</c>) or that can no longer run
/// (<c>error</c>). Events are dispatched off the caller's thread, so a host
/// handler can never re-enter the pipeline's locks.
///
/// Capacity never exceeds <see cref="WdspCeiling"/>: WDSP has 32 process-wide
/// channel slots, and the operator side may use up to
/// <see cref="OperatorChannelReserve"/> of them (every operator receiver, TX,
/// the TX monitor, the PureSignal feedback display, the wideband detail
/// display and every guest slot). Virtual receivers only ever get what is left,
/// so they can never starve an operator channel that opens later.
/// </summary>
public sealed class VirtualReceiverPool : IVirtualReceiverPool
{
    public const int MaxVrxSlots = PublicStreamId.MaxVrxSlots;

    /// <summary>native/wdsp comm.h MAX_CHANNELS.</summary>
    public const int WdspChannelLimit = 32;

    /// <summary>Channels the operator side may need: receivers + TX + TX monitor + PS feedback + wideband detail + guests.</summary>
    public const int OperatorChannelReserve = WireContract.MaxReceivers + 4 + PublicStreamId.MaxGuestSlots;

    /// <summary>The most virtual receivers the WDSP pool can host after the operator reserve.</summary>
    public const int WdspCeiling = WdspChannelLimit - OperatorChannelReserve;

    /// <summary>How fast the SNR noise-floor estimate may rise (it falls at once).</summary>
    internal const double FloorRiseDbPerSecond = 0.25;

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private Action<Action> _dispatch;
    private readonly Dictionary<int, VrxRequest> _receivers = [];
    private readonly Meter[] _meters = new Meter[MaxVrxSlots];
    private int _poolSize;
    private bool _supported;
    private int _capacity;
    private VrxSpan _span;

    public VirtualReceiverPool(Action<Action>? dispatch = null, TimeProvider? time = null)
    {
        _dispatch = dispatch ?? (work => ThreadPool.QueueUserWorkItem(static w => ((Action)w!)(), work));
        _time = time ?? TimeProvider.System;
        for (int i = 0; i < _meters.Length; i++) _meters[i] = new Meter();
    }

    public int MaxSlots => MaxVrxSlots;

    public IReadOnlyList<GuestNrMode> NrModes
    {
        get
        {
            var modes = new List<GuestNrMode> { GuestNrMode.Off, GuestNrMode.Nr1, GuestNrMode.Nr2 };
            if (WdspDspEngine.NnrAvailable) modes.Add(GuestNrMode.Nnr);
            if (WdspDspEngine.Nr4SbnrAvailable) modes.Add(GuestNrMode.Nr4);
            return modes;
        }
    }

    /// <summary>How events leave the reconcile path; tests make it synchronous.</summary>
    internal Action<Action> Dispatcher
    {
        get => Volatile.Read(ref _dispatch);
        set => Volatile.Write(ref _dispatch, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public bool Supported { get { lock (_gate) return _supported; } }

    public int Capacity { get { lock (_gate) return _capacity; } }

    public VrxSpan Span { get { lock (_gate) return _span; } }

    public int PoolSize { get { lock (_gate) return _poolSize; } }

    /// <summary>Any receiver is desired (cheap; no snapshot).</summary>
    internal bool HasReceivers { get { lock (_gate) return _receivers.Count > 0; } }

    public event Action<IReadOnlyList<VrxEnded>>? ReceiversEnded;

    public event Action? CapacityChanged;

    /// <summary>The desired set changed: the pipeline re-runs its receiver reconcile.</summary>
    internal event Action? ReceiversChanged;

    /// <summary>Snapshot of the desired set, slot order.</summary>
    public IReadOnlyList<VrxRequest> Receivers
    {
        get { lock (_gate) return _receivers.Values.OrderBy(r => r.Slot).ToArray(); }
    }

    public static int ComputeCapacity(bool supported, int poolSize) =>
        supported ? Math.Clamp(poolSize, 0, Math.Min(MaxVrxSlots, WdspCeiling)) : 0;

    public void SetPoolSize(int size)
    {
        size = Math.Clamp(size, 0, MaxVrxSlots);
        bool capacityChanged;
        lock (_gate)
        {
            if (size == _poolSize) return;
            _poolSize = size;
            int capacity = ComputeCapacity(_supported, size);
            capacityChanged = capacity != _capacity;
            _capacity = capacity;
        }
        if (capacityChanged) Dispatch(() => CapacityChanged?.Invoke());
        RaiseReceiversChanged();
    }

    public void SetReceivers(IReadOnlyList<VrxRequest> receivers)
    {
        ArgumentNullException.ThrowIfNull(receivers);
        var next = new Dictionary<int, VrxRequest>();
        foreach (var receiver in receivers)
        {
            if (receiver.Slot is < 0 or >= MaxVrxSlots || receiver.Settings is null) continue;
            if (next.ContainsKey(receiver.Slot)) continue;
            next[receiver.Slot] = receiver with { Settings = receiver.Settings.Normalize() };
        }

        lock (_gate)
        {
            if (next.Count == _receivers.Count && next.All(kv =>
                    _receivers.TryGetValue(kv.Key, out var existing) && existing == kv.Value))
                return;
            _receivers.Clear();
            foreach (var kv in next) _receivers[kv.Key] = kv.Value;
            for (int slot = 0; slot < _meters.Length; slot++)
            {
                // A slot whose generation moved on must not report the previous listener's reading.
                if (!_receivers.TryGetValue(slot, out var current) || current.Generation != _meters[slot].Generation)
                    _meters[slot].Reset();
            }
        }
        RaiseReceiversChanged();
    }

    public bool TryGetMeter(int slot, long generation, out double dbm, out double snrDb)
    {
        dbm = double.NaN;
        snrDb = double.NaN;
        if (slot is < 0 or >= MaxVrxSlots) return false;
        lock (_gate)
        {
            var meter = _meters[slot];
            if (meter.Generation != generation || double.IsNaN(meter.Dbm)) return false;
            dbm = meter.Dbm;
            snrDb = meter.SnrDb;
            return true;
        }
    }

    /// <summary>
    /// Clamp the desired set to what can run now: nothing while unsupported
    /// (<c>error</c>), nothing whose dial left <paramref name="span"/>
    /// (<c>operator-moved</c>), and the newest over the capacity (<c>disabled</c>).
    /// </summary>
    internal VrxPlan Reconcile(bool supported, VrxSpan span)
    {
        var ended = new List<VrxEnded>();
        VrxRequest[] retained;
        bool capacityChanged;
        int capacity;
        lock (_gate)
        {
            capacity = ComputeCapacity(supported, _poolSize);
            capacityChanged = capacity != _capacity || supported != _supported;
            _capacity = capacity;
            _supported = supported;
            _span = supported ? span : default;

            foreach (var receiver in _receivers.Values.ToArray())
            {
                string? reason = !supported || !span.Valid
                    ? VrxEngineEndReasons.Error
                    : !span.Contains(receiver.Settings.Hz) ? VrxEngineEndReasons.OperatorMoved
                    : null;
                if (reason is null) continue;
                RemoveLocked(receiver);
                ended.Add(new VrxEnded(receiver.Slot, receiver.Generation, reason));
            }
            int excess = _receivers.Count - capacity;
            if (excess > 0)
            {
                foreach (var receiver in _receivers.Values.OrderByDescending(r => r.Generation).Take(excess).ToArray())
                {
                    RemoveLocked(receiver);
                    ended.Add(new VrxEnded(receiver.Slot, receiver.Generation, VrxEngineEndReasons.Disabled));
                }
            }
            retained = _receivers.Values.OrderBy(r => r.Slot).ToArray();
        }

        if (capacityChanged) Dispatch(() => CapacityChanged?.Invoke());
        if (ended.Count > 0) Dispatch(() => ReceiversEnded?.Invoke(ended));
        return new VrxPlan(retained, span, capacity);
    }

    /// <summary>The pipeline could not run these receivers: drop them (if still current) with <c>error</c>.</summary>
    internal void Fail(IReadOnlyList<VrxRequest> receivers)
    {
        var ended = new List<VrxEnded>(receivers.Count);
        lock (_gate)
        {
            foreach (var receiver in receivers)
            {
                if (!_receivers.TryGetValue(receiver.Slot, out var current) || current.Generation != receiver.Generation)
                    continue;
                RemoveLocked(current);
                ended.Add(new VrxEnded(current.Slot, current.Generation, VrxEngineEndReasons.Error));
            }
        }
        if (ended.Count > 0) Dispatch(() => ReceiversEnded?.Invoke(ended));
    }

    /// <summary>
    /// One calibrated S-meter reading for <paramref name="slot"/>. The SNR is
    /// measured against a floor estimate that follows the reading down at once
    /// and rises at most <see cref="FloorRiseDbPerSecond"/>. NaN clears the meter
    /// (no reading, e.g. while the station transmits).
    /// </summary>
    internal void PublishMeter(int slot, long generation, double dbm)
    {
        if (slot is < 0 or >= MaxVrxSlots) return;
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            // An in-flight DSP tick may complete after a release or slot reuse.
            // Never resurrect a reading belonging to the previous listener.
            if (!_receivers.TryGetValue(slot, out var receiver) || receiver.Generation != generation) return;
            var meter = _meters[slot];
            if (meter.Generation != generation)
            {
                meter.Reset();
                meter.Generation = generation;
            }
            if (!double.IsFinite(dbm))
            {
                meter.Dbm = double.NaN;
                meter.SnrDb = double.NaN;
                return;
            }
            if (double.IsNaN(meter.FloorDb) || dbm < meter.FloorDb)
            {
                meter.FloorDb = dbm;
            }
            else
            {
                double seconds = _time.GetElapsedTime(meter.FloorTimestamp, now).TotalSeconds;
                meter.FloorDb = Math.Min(dbm, meter.FloorDb + Math.Max(0, seconds) * FloorRiseDbPerSecond);
            }
            meter.FloorTimestamp = now;
            meter.Dbm = dbm;
            meter.SnrDb = Math.Max(0, dbm - meter.FloorDb);
        }
    }

    private void RemoveLocked(VrxRequest receiver)
    {
        _receivers.Remove(receiver.Slot);
        _meters[receiver.Slot].Reset();
    }

    private void RaiseReceiversChanged()
    {
        try { ReceiversChanged?.Invoke(); }
        catch
        {
            // The pipeline logs its own reconcile faults; an update must never
            // throw back into the host's control path.
        }
    }

    private void Dispatch(Action action) =>
        Dispatcher(() =>
        {
            try { action(); }
            catch
            {
                // Subscriber faults stay with the subscriber.
            }
        });

    private sealed class Meter
    {
        public long Generation = -1;
        public double Dbm = double.NaN;
        public double SnrDb = double.NaN;
        public double FloorDb = double.NaN;
        public long FloorTimestamp;

        public void Reset()
        {
            Generation = -1;
            Dbm = double.NaN;
            SnrDb = double.NaN;
            FloorDb = double.NaN;
            FloorTimestamp = 0;
        }
    }
}
