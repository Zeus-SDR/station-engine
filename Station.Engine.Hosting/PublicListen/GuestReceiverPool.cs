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

namespace Zeus.Server.PublicListen;

/// <summary>Guest receiver AGC, the listener-facing allowlist (docs/designs/public-listening.md, Phase 3).</summary>
public enum GuestAgcMode
{
    Off,
    Long,
    Slow,
    Med,
    Fast,
}

/// <summary>Listener receiver noise reduction modes, each applied only to its own RXA channel.</summary>
public enum GuestNrMode
{
    Off,
    Nr1,
    Nr2,
    Nnr,
    Nr4,
}

/// <summary>Listener receiver noise blanker (Phase 4): NB1 = ANB, NB2 = NOB.</summary>
public enum GuestNbMode
{
    Off,
    Nb1,
    Nb2,
}

/// <summary>
/// One listener receiver's own DSP state — a guest receiver (Phase 3) or a
/// virtual receiver (Phase 4). Nothing here is ever derived from the
/// operator's global settings; the pipeline applies exactly these values.
/// The Phase 4 fields (AGC ceiling, NB, ANF, SNB, squelch) default to the
/// values a Phase 3 guest always had, so an older host's lease is unchanged.
/// </summary>
public sealed record GuestReceiverSettings(
    long Hz,
    RxMode Mode,
    int FilterLowHz,
    int FilterHighHz,
    GuestAgcMode Agc,
    GuestNrMode Nr,
    double AgcTopDb = GuestReceiverSettings.DefaultAgcTopDb,
    GuestNbMode Nb = GuestNbMode.Off,
    bool Anf = false,
    bool Snb = false,
    bool Squelch = false,
    double SquelchDb = GuestReceiverSettings.DefaultSquelchDb,
    bool Apf = false,
    int EmnrGainMethod = 2,
    int EmnrNpeMethod = 0,
    bool EmnrAeRun = true,
    double Nr4ReductionAmount = 10,
    double Nr4SmoothingFactor = 0,
    int NnrModel = 0,
    double NnrMaskFloorDb = -25,
    double NnrAlpha = 1,
    double NnrKneeDb = 10,
    double NnrTauSeconds = 2,
    double NnrMaxGainDb = 12,
    double NnrAttackMs = 0,
    double NnrReleaseMs = 0,
    bool EqEnabled = false,
    int EqPreampDb = 0,
    int EqLowDb = 0,
    int EqMidDb = 0,
    int EqHighDb = 0,
    int DisplaySpanHz = 0, long DisplayCenterHz = 0)
{
    public const long MinHz = 10_000;
    public const long MaxHz = 55_000_000;
    public const int MaxFilterEdgeHz = 10_000;
    public const int MinFilterWidthHz = 25;

    /// <summary>AGC ceiling ("AGC-T", WDSP max gain): the stock Thetis default, never the operator's.</summary>
    public const double DefaultAgcTopDb = 90.0;
    public const double MinAgcTopDb = -20.0;
    public const double MaxAgcTopDb = 120.0;

    /// <summary>Squelch threshold in dBm on the receiver's own S-meter.</summary>
    public const double DefaultSquelchDb = -100.0;
    public const double MinSquelchDb = -160.0;
    public const double MaxSquelchDb = 0.0;

    /// <summary>The demodulators a guest may pick.</summary>
    public static readonly RxMode[] AllowedModes =
        [RxMode.LSB, RxMode.USB, RxMode.CWL, RxMode.CWU, RxMode.AM, RxMode.SAM, RxMode.FM];

    public static bool IsAllowedMode(RxMode mode) => Array.IndexOf(AllowedModes, mode) >= 0;

    /// <summary>A sensible passband for a freshly selected mode (signed, VFO-relative Hz).</summary>
    public static (int Low, int High) DefaultFilter(RxMode mode) => mode switch
    {
        RxMode.LSB => (-2800, -100),
        RxMode.CWL => (-850, -350),
        RxMode.CWU => (350, 850),
        RxMode.AM or RxMode.SAM => (-5000, 5000),
        RxMode.FM => (-8000, 8000),
        _ => (100, 2800),
    };

    /// <summary>
    /// Clamp into the allowlist: hz to 10 kHz…55 MHz, a disallowed mode to USB,
    /// the passband into ±10 kHz with a minimum width, and sideband-signed
    /// (a positive passband sent for LSB/CWL is mirrored, and vice versa).
    /// </summary>
    public GuestReceiverSettings Normalize()
    {
        var mode = IsAllowedMode(Mode) ? Mode : RxMode.USB;
        int low = Math.Clamp(FilterLowHz, -MaxFilterEdgeHz, MaxFilterEdgeHz);
        int high = Math.Clamp(FilterHighHz, -MaxFilterEdgeHz, MaxFilterEdgeHz);
        if (low > high) (low, high) = (high, low);
        bool lowerSideband = mode is RxMode.LSB or RxMode.CWL;
        bool upperSideband = mode is RxMode.USB or RxMode.CWU;
        if (lowerSideband && low >= 0) (low, high) = (-high, -low);
        else if (upperSideband && high <= 0) (low, high) = (-high, -low);
        if (high - low < MinFilterWidthHz) (low, high) = DefaultFilter(mode);
        return this with
        {
            Hz = Math.Clamp(Hz, MinHz, MaxHz),
            Mode = mode,
            FilterLowHz = low,
            FilterHighHz = high,
            Agc = Enum.IsDefined(Agc) ? Agc : GuestAgcMode.Med,
            Nr = Enum.IsDefined(Nr) ? Nr : GuestNrMode.Off,
            AgcTopDb = double.IsFinite(AgcTopDb) ? Math.Clamp(Math.Round(AgcTopDb), MinAgcTopDb, MaxAgcTopDb) : DefaultAgcTopDb,
            Nb = Enum.IsDefined(Nb) ? Nb : GuestNbMode.Off,
            SquelchDb = double.IsFinite(SquelchDb) ? Math.Clamp(Math.Round(SquelchDb), MinSquelchDb, MaxSquelchDb) : DefaultSquelchDb,
            EmnrGainMethod = Math.Clamp(EmnrGainMethod, 0, 3),
            EmnrNpeMethod = Math.Clamp(EmnrNpeMethod, 0, 2),
            Nr4ReductionAmount = double.IsFinite(Nr4ReductionAmount) ? Math.Clamp(Nr4ReductionAmount, 0, 20) : 10,
            Nr4SmoothingFactor = double.IsFinite(Nr4SmoothingFactor) ? Math.Clamp(Nr4SmoothingFactor, 0, 100) : 0,
            NnrModel = Math.Clamp(NnrModel, 0, 1),
            NnrMaskFloorDb = double.IsFinite(NnrMaskFloorDb) ? Math.Clamp(NnrMaskFloorDb, -60, 0) : -25,
            NnrAlpha = double.IsFinite(NnrAlpha) ? Math.Clamp(NnrAlpha, 0, 4) : 1,
            NnrKneeDb = double.IsFinite(NnrKneeDb) ? Math.Clamp(NnrKneeDb, 0, 40) : 10,
            NnrTauSeconds = double.IsFinite(NnrTauSeconds) ? Math.Clamp(NnrTauSeconds, 0.05, 30) : 2,
            NnrMaxGainDb = double.IsFinite(NnrMaxGainDb) ? Math.Clamp(NnrMaxGainDb, 0, 24) : 12,
            NnrAttackMs = double.IsFinite(NnrAttackMs) ? Math.Clamp(NnrAttackMs, 0, 500) : 0,
            EqPreampDb = Math.Clamp(EqPreampDb, -12, 12),
            EqLowDb = Math.Clamp(EqLowDb, -12, 12),
            EqMidDb = Math.Clamp(EqMidDb, -12, 12),
            EqHighDb = Math.Clamp(EqHighDb, -12, 12),
            DisplaySpanHz = Math.Clamp(DisplaySpanHz, 0, 60_000_000),
            DisplayCenterHz = Math.Clamp(DisplayCenterHz, 0, 60_000_000),
            NnrReleaseMs = double.IsFinite(NnrReleaseMs) ? Math.Clamp(NnrReleaseMs, 0, 500) : 0,
        };
    }
}

/// <summary>
/// One leased guest receiver as the host wants it. <see cref="Generation"/> is
/// unique per lease (monotonic): a new lease on a reused slot always carries a
/// higher value, so the engine never feeds one guest's IQ into the next.
/// </summary>
public readonly record struct GuestLeaseRequest(int Slot, long Generation, GuestReceiverSettings Settings);

/// <summary>
/// Engine seam for Public Listening guest receivers (Phase 3). The host's lease
/// service decides WHO holds which slot; the engine owns the WDSP channels, the
/// Protocol 2 guest DDCs and, above all, the rule that the operator always wins.
/// </summary>
public interface IGuestReceiverPool
{
    /// <summary>Slot numbers run 0..MaxSlots-1 (stream id 0xE0 + slot).</summary>
    int MaxSlots { get; }

    /// <summary>The connected radio can host guests at all (Protocol 2, DDC-slot board with RxBaseDdc == 2).</summary>
    bool Supported { get; }

    /// <summary>Usable guest slots now: min(pool size, the radio's spare DDCs keeping the display DDC).</summary>
    int Capacity { get; }

    /// <summary>The operator's GuestPoolSize (0 = guests off).</summary>
    void SetPoolSize(int size);

    /// <summary>Replace the desired lease set (invalid or duplicate slots are ignored).</summary>
    void SetLeases(IReadOnlyList<GuestLeaseRequest> leases);

    /// <summary>The guest channel's calibrated S-meter reading, when that lease has one.</summary>
    bool TryGetSignalDbm(int slot, long generation, out double dbm);

    /// <summary>
    /// Leases the engine dropped because capacity fell below the lease count
    /// (the operator took DDCs back, the radio changed, the pool shrank). The
    /// newest leases go first. Raised off the engine's lock.
    /// </summary>
    event Action<IReadOnlyList<GuestLeaseRequest>>? LeasesReclaimed;

    /// <summary>
    /// Leases the engine dropped because their guest channel failed to open
    /// or configure (the lease ends with reason <c>error</c>; other guests are
    /// unaffected). Raised off the engine's lock.
    /// </summary>
    event Action<IReadOnlyList<GuestLeaseRequest>>? LeasesFailed;

    /// <summary><see cref="Capacity"/> or <see cref="Supported"/> changed.</summary>
    event Action? CapacityChanged;
}

/// <summary>Result of one engine reconcile: the leases to run (slot order) and those evicted.</summary>
internal readonly record struct GuestPoolPlan(
    IReadOnlyList<GuestLeaseRequest> Retained,
    IReadOnlyList<GuestLeaseRequest> Evicted,
    int Capacity);

/// <summary>
/// Thread-safe lease book shared by the host lease service and the DSP
/// pipeline. The host writes the desired set; the pipeline calls
/// <see cref="Reconcile"/> after every operator receiver push (state thread)
/// and on every guest reconcile (DSP thread), which clamps the set to the
/// current capacity (evicting the newest leases) and returns what to put on
/// the wire. Events are dispatched off the caller's thread, so a host handler
/// that answers a reclaim with <see cref="SetLeases"/> can never re-enter the
/// pipeline's locks.
/// </summary>
public sealed class GuestReceiverPool : IGuestReceiverPool
{
    public const int MaxGuestSlots = PublicStreamId.MaxGuestSlots;

    private readonly object _gate = new();
    private Action<Action> _dispatch;
    private readonly Dictionary<int, GuestLeaseRequest> _leases = [];
    private readonly double[] _dbm = new double[MaxGuestSlots];
    private readonly long[] _dbmGeneration = new long[MaxGuestSlots];
    private int _poolSize;
    private bool _supported;
    private int _hardwareCapacity;
    private int _capacity;

    public GuestReceiverPool(Action<Action>? dispatch = null)
    {
        _dispatch = dispatch ?? (work => ThreadPool.QueueUserWorkItem(static w => ((Action)w!)(), work));
        Array.Fill(_dbm, double.NaN);
        Array.Fill(_dbmGeneration, -1L);
    }

    public int MaxSlots => MaxGuestSlots;

    /// <summary>How events leave the reconcile path; tests make it synchronous.</summary>
    internal Action<Action> Dispatcher
    {
        get => Volatile.Read(ref _dispatch);
        set => Volatile.Write(ref _dispatch, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public bool Supported { get { lock (_gate) return _supported; } }

    public int Capacity { get { lock (_gate) return _capacity; } }

    public int PoolSize { get { lock (_gate) return _poolSize; } }

    /// <summary>Any lease is desired (cheap; no snapshot).</summary>
    internal bool HasLeases { get { lock (_gate) return _leases.Count > 0; } }

    public event Action<IReadOnlyList<GuestLeaseRequest>>? LeasesReclaimed;

    public event Action<IReadOnlyList<GuestLeaseRequest>>? LeasesFailed;

    public event Action? CapacityChanged;

    /// <summary>The desired set changed: the pipeline re-runs its guest reconcile.</summary>
    internal event Action? LeasesChanged;

    /// <summary>Snapshot of the desired set, slot order.</summary>
    public IReadOnlyList<GuestLeaseRequest> Leases
    {
        get { lock (_gate) return _leases.Values.OrderBy(l => l.Slot).ToArray(); }
    }

    public void SetPoolSize(int size)
    {
        size = Math.Clamp(size, 0, MaxGuestSlots);
        bool capacityChanged;
        lock (_gate)
        {
            if (size == _poolSize) return;
            _poolSize = size;
            int capacity = ComputeCapacity(_supported, size, _hardwareCapacity);
            capacityChanged = capacity != _capacity;
            _capacity = capacity;
        }
        if (capacityChanged) Dispatch(() => CapacityChanged?.Invoke());
        // The pipeline reconciles (and evicts over the new size) on its next pass.
        RaiseLeasesChanged();
    }

    public void SetLeases(IReadOnlyList<GuestLeaseRequest> leases)
    {
        ArgumentNullException.ThrowIfNull(leases);
        var next = new Dictionary<int, GuestLeaseRequest>();
        foreach (var lease in leases)
        {
            if (lease.Slot is < 0 or >= MaxGuestSlots || lease.Settings is null) continue;
            if (next.ContainsKey(lease.Slot)) continue;
            next[lease.Slot] = lease with { Settings = lease.Settings.Normalize() };
        }

        lock (_gate)
        {
            if (next.Count == _leases.Count && next.All(kv =>
                    _leases.TryGetValue(kv.Key, out var existing) && existing == kv.Value))
                return;
            _leases.Clear();
            foreach (var kv in next) _leases[kv.Key] = kv.Value;
        }
        RaiseLeasesChanged();
    }

    public bool TryGetSignalDbm(int slot, long generation, out double dbm)
    {
        dbm = double.NaN;
        if (slot is < 0 or >= MaxGuestSlots) return false;
        lock (_gate)
        {
            if (_dbmGeneration[slot] != generation || double.IsNaN(_dbm[slot])) return false;
            dbm = _dbm[slot];
            return true;
        }
    }

    /// <summary>
    /// The newest leases over <paramref name="capacity"/> — the operator always
    /// wins, and among guests the longest-held lease stays.
    /// </summary>
    public static IReadOnlyList<GuestLeaseRequest> SelectEvictions(
        IEnumerable<GuestLeaseRequest> leases, int capacity)
    {
        var all = leases.ToArray();
        int excess = all.Length - Math.Max(0, capacity);
        if (excess <= 0) return [];
        return all.OrderByDescending(l => l.Generation).Take(excess).ToArray();
    }

    /// <summary>Usable slots: zero when unsupported, else min(pool size, spare DDCs).</summary>
    public static int ComputeCapacity(bool supported, int poolSize, int hardwareCapacity) =>
        supported ? Math.Clamp(Math.Min(poolSize, hardwareCapacity), 0, MaxGuestSlots) : 0;

    /// <summary>
    /// Clamp the desired set to the current capacity. Called by the pipeline
    /// with the radio's spare-DDC count (<c>GuestDdcCapacity(displayDdcWanted: true)</c>)
    /// right after it pushed the operator's receivers.
    /// </summary>
    internal GuestPoolPlan Reconcile(bool supported, int hardwareCapacity)
    {
        IReadOnlyList<GuestLeaseRequest> evicted;
        GuestLeaseRequest[] retained;
        bool capacityChanged;
        int capacity;
        lock (_gate)
        {
            capacity = ComputeCapacity(supported, _poolSize, Math.Max(0, hardwareCapacity));
            capacityChanged = capacity != _capacity || supported != _supported;
            _capacity = capacity;
            _supported = supported;
            _hardwareCapacity = Math.Max(0, hardwareCapacity);
            evicted = SelectEvictions(_leases.Values, capacity);
            foreach (var lease in evicted)
            {
                _leases.Remove(lease.Slot);
                _dbm[lease.Slot] = double.NaN;
                _dbmGeneration[lease.Slot] = -1;
            }
            retained = _leases.Values.OrderBy(l => l.Slot).ToArray();
        }

        if (capacityChanged) Dispatch(() => CapacityChanged?.Invoke());
        if (evicted.Count > 0) Dispatch(() => LeasesReclaimed?.Invoke(evicted));
        return new GuestPoolPlan(retained, evicted, capacity);
    }

    /// <summary>
    /// The pipeline could not run these leases (their channel failed to open
    /// or configure): drop them from the desired set, if still current, and
    /// tell the host so each ends with reason <c>error</c>.
    /// </summary>
    internal void Fail(IReadOnlyList<GuestLeaseRequest> leases)
    {
        var failed = new List<GuestLeaseRequest>(leases.Count);
        lock (_gate)
        {
            foreach (var lease in leases)
            {
                if (!_leases.TryGetValue(lease.Slot, out var current) || current.Generation != lease.Generation)
                    continue;
                _leases.Remove(lease.Slot);
                _dbm[lease.Slot] = double.NaN;
                _dbmGeneration[lease.Slot] = -1;
                failed.Add(current);
            }
        }
        if (failed.Count > 0) Dispatch(() => LeasesFailed?.Invoke(failed));
    }

    internal void PublishSignalDbm(int slot, long generation, double dbm)
    {
        if (slot is < 0 or >= MaxGuestSlots) return;
        lock (_gate)
        {
            _dbm[slot] = dbm;
            _dbmGeneration[slot] = generation;
        }
    }

    private void RaiseLeasesChanged()
    {
        try { LeasesChanged?.Invoke(); }
        catch
        {
            // The pipeline logs its own reconcile faults; a lease update must
            // never throw back into the host's control path.
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
}
