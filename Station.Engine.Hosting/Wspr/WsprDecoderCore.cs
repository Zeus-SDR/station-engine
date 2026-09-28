// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Dial and mode of one receiver, as the radio currently reports it.</summary>
internal readonly record struct WsprReceiverTuning(bool Present, long DialHz, RxMode Mode);

/// <summary>
/// The WSPR decoder's state machine, free of threads and host services so it
/// can be driven deterministically in tests. <see cref="WsprDecodeService"/>
/// owns the threads: the DSP thread calls <see cref="OnAudio"/> (copy only),
/// the capture worker calls <see cref="Pump"/>, and the decode worker calls
/// <see cref="TryTakeCaptured"/> + <see cref="Decode"/>.
/// </summary>
internal sealed class WsprDecoderCore
{
    internal const int RingCapacity = 1 << 17;       // ~2.7 s of 48 kHz audio per receiver
    internal const long LeaseMs = 90_000;
    internal const int RetainedSlots = 64;
    internal const double MaxClockOffsetMs = 60_000;
    /// <summary>
    /// Captured slots waiting for the decode worker. A decode takes under a
    /// second, so the queue is normally empty; the bound only matters if the
    /// native decoder hangs, which it cannot be interrupted from (the call
    /// never returns and its thread stays blocked). The oldest waiting slot is
    /// then published as skipped (busy) so memory stays bounded at about
    /// <c>MaxQueuedCaptures</c> x 5.5 MB.
    /// </summary>
    internal const int MaxQueuedCaptures = 8;

    private readonly object _gate = new();
    private readonly IWsprSlotDecoder _decoder;
    private readonly int _maxReceivers;

    // Per receiver. Rings are allocated once and reused; the DSP thread reads
    // the enabled mask and the ring reference without locking.
    private readonly FloatSpscRing?[] _rings;
    private readonly WsprSlotRecorder?[] _recorders;
    private readonly WsprDecimator?[] _decimators;
    private readonly WsprReceiverTuning[] _tuning;
    // Per receiver: how many tuning changes the radio has reported, and the
    // earliest and latest of the changes not yet handed to the recorder
    // (aligned ms; long.MinValue = none / "at the next pump"). The recorder
    // compares generations, so a change undone before the next pump still
    // spoils the slot, and it sees the whole span of unobserved changes, so an
    // early in-window change is not hidden by a later one past the window.
    private readonly long[] _tuningGeneration;
    private readonly long[] _tuningFirstChangeMs;
    private readonly long[] _tuningLastChangeMs;
    private int _enabledMask;

    private readonly float[] _drain48k = new float[8192];
    private readonly float[] _drain12k = new float[8192 / WsprDecimator.Factor + 1];

    private readonly Queue<WsprCapturedSlot> _captured = new();
    private readonly LinkedList<WsprDecodedSlot> _slots = new();

    private bool _enabled;
    private bool _deep;
    private double _clockOffsetMs;
    private long _leaseExpiresMs;
    private long _seq;
    private string? _lastError;
    private int _transmitting;
    private int _transmitLatched;
    private long _droppedSamples;

    public WsprDecoderCore(IWsprSlotDecoder decoder, int maxReceivers = WireContract.MaxReceivers)
    {
        _decoder = decoder;
        _maxReceivers = maxReceivers;
        _rings = new FloatSpscRing?[maxReceivers];
        _recorders = new WsprSlotRecorder?[maxReceivers];
        _decimators = new WsprDecimator?[maxReceivers];
        _tuning = new WsprReceiverTuning[maxReceivers];
        _tuningGeneration = new long[maxReceivers];
        _tuningFirstChangeMs = new long[maxReceivers];
        _tuningLastChangeMs = new long[maxReceivers];
        Array.Fill(_tuningFirstChangeMs, long.MinValue);
        Array.Fill(_tuningLastChangeMs, long.MinValue);
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    /// <summary>Wall clock used for slot alignment: engine UTC plus the product's SNTP offset.</summary>
    public long AlignedNow(long utcUnixMs)
    {
        lock (_gate) return utcUnixMs + (long)Math.Round(_clockOffsetMs);
    }

    // ---- DSP thread -------------------------------------------------------

    /// <summary>
    /// DSP-thread tap: copies one block into the receiver's ring. Returns true
    /// when samples were accepted (the caller then wakes the capture worker).
    /// </summary>
    public bool OnAudio(int receiver, int sampleRateHz, ReadOnlySpan<float> samples)
    {
        if (sampleRateHz != 48_000 || (uint)receiver >= (uint)_maxReceivers) return false;
        if ((Volatile.Read(ref _enabledMask) & (1 << receiver)) == 0) return false;
        FloatSpscRing? ring = Volatile.Read(ref _rings[receiver]);
        if (ring is null) return false;
        int written = ring.Write(samples);
        if (written < samples.Length)
            Interlocked.Add(ref _droppedSamples, samples.Length - written);
        return written > 0;
    }

    /// <summary>MOX/TUN edge. Any keying during a capture spoils that slot.</summary>
    public void OnTransmit(bool on)
    {
        Volatile.Write(ref _transmitting, on ? 1 : 0);
        if (on) Volatile.Write(ref _transmitLatched, 1);
    }

    // ---- control (HTTP threads) --------------------------------------------

    public WsprDecoderStatus Configure(WsprDecoderConfig config, long utcUnixMs)
    {
        lock (_gate)
        {
            _deep = string.Equals(config.Depth, WsprDecoderWire.DepthDeep, StringComparison.OrdinalIgnoreCase);
            _clockOffsetMs = double.IsFinite(config.ClockOffsetMs)
                ? Math.Clamp(config.ClockOffsetMs, -MaxClockOffsetMs, MaxClockOffsetMs)
                : 0;

            if (!config.Enabled)
            {
                DisableLocked();
                return StatusLocked(utcUnixMs);
            }

            int mask = 0;
            foreach (int receiver in config.Receivers ?? [0])
            {
                if ((uint)receiver < (uint)_maxReceivers) mask |= 1 << receiver;
            }
            if (mask == 0) mask = 1;

            for (int r = 0; r < _maxReceivers; r++)
            {
                bool want = (mask & (1 << r)) != 0;
                bool had = _enabled && (_enabledMask & (1 << r)) != 0;
                if (want && !had)
                {
                    _rings[r] ??= new FloatSpscRing(RingCapacity);
                    _recorders[r] ??= new WsprSlotRecorder(r);
                    _decimators[r] ??= new WsprDecimator();
                    _recorders[r]!.Reset();
                    _decimators[r]!.Reset();
                    long at = AlignedLocked(utcUnixMs);
                    _recorders[r]!.NoteTuning(_tuning[r].DialHz, _tuning[r].Mode, _tuningGeneration[r], at, at);
                }
                else if (!want && had)
                {
                    _recorders[r]?.Reset();
                }
            }

            _enabled = true;
            Volatile.Write(ref _enabledMask, mask);
            _leaseExpiresMs = utcUnixMs + LeaseMs;
            return StatusLocked(utcUnixMs);
        }
    }

    public WsprDecoderStatus Status(long utcUnixMs)
    {
        lock (_gate) return StatusLocked(utcUnixMs);
    }

    /// <summary>Slots with <c>seq &gt; after</c>; reading renews the lease.</summary>
    public WsprSlotBatch SlotsAfter(long after, long utcUnixMs)
    {
        lock (_gate)
        {
            if (_enabled) _leaseExpiresMs = utcUnixMs + LeaseMs;
            var slots = _slots.Where(s => s.Seq > after).ToArray();
            return new WsprSlotBatch(_seq, slots);
        }
    }

    /// <param name="utcUnixMs">When the state changed; null means "at the next pump".</param>
    public void OnRadioState(IReadOnlyList<WsprReceiverTuning> receivers, long? utcUnixMs = null)
    {
        lock (_gate)
        {
            for (int r = 0; r < _maxReceivers; r++)
            {
                var next = r < receivers.Count ? receivers[r] : default;
                if (next.DialHz == _tuning[r].DialHz && next.Mode == _tuning[r].Mode)
                {
                    _tuning[r] = next;
                    continue;
                }
                _tuning[r] = next;
                _tuningGeneration[r]++;
                if (utcUnixMs is long t)
                {
                    long at = AlignedLocked(t);
                    if (_tuningFirstChangeMs[r] == long.MinValue) _tuningFirstChangeMs[r] = at;
                    _tuningLastChangeMs[r] = at;
                }
            }
        }
    }

    // ---- capture worker ----------------------------------------------------

    /// <summary>
    /// Drain every enabled receiver's ring into its recorder, apply tuning and
    /// transmit observations, flush stalled captures, and expire the lease.
    /// Returns true when a captured slot is waiting for the decode worker.
    /// </summary>
    public bool Pump(long utcUnixMs)
    {
        lock (_gate)
        {
            if (!_enabled) return false;
            if (utcUnixMs >= _leaseExpiresMs)
            {
                DisableLocked();
                return false;
            }

            long now = AlignedLocked(utcUnixMs);
            bool transmitting = Volatile.Read(ref _transmitting) != 0
                                | Interlocked.Exchange(ref _transmitLatched, 0) != 0;
            int mask = _enabledMask;
            for (int r = 0; r < _maxReceivers; r++)
            {
                if ((mask & (1 << r)) == 0) continue;
                var recorder = _recorders[r]!;
                var ring = _rings[r]!;
                var decimator = _decimators[r]!;

                long firstChange = _tuningFirstChangeMs[r] == long.MinValue ? now : _tuningFirstChangeMs[r];
                long lastChange = _tuningLastChangeMs[r] == long.MinValue ? now : _tuningLastChangeMs[r];
                recorder.NoteTuning(_tuning[r].DialHz, _tuning[r].Mode, _tuningGeneration[r], firstChange, lastChange);
                _tuningFirstChangeMs[r] = long.MinValue;
                _tuningLastChangeMs[r] = long.MinValue;
                if (transmitting) recorder.NoteTransmit(now);

                // The newest drained sample was captured roughly now; older
                // samples still in the ring are placed before it.
                int pending = ring.Count;
                while (pending > 0)
                {
                    int take = Math.Min(pending, _drain48k.Length);
                    int read = ring.Read(_drain48k.AsSpan(0, take));
                    if (read <= 0) break;
                    pending -= read;
                    int produced = decimator.Process(_drain48k.AsSpan(0, read), _drain12k);
                    long endMs = now - (long)Math.Round(pending / 48.0);
                    foreach (var slot in recorder.Append(_drain12k.AsSpan(0, produced), endMs, transmitting))
                        EnqueueLocked(slot);
                }

                if (recorder.FlushIfStale(now) is { } stale)
                    EnqueueLocked(stale);
            }
            return _captured.Count > 0;
        }
    }

    // ---- decode worker -----------------------------------------------------

    /// <summary>Take the oldest captured slot, with the depth and the slot now in progress.</summary>
    public bool TryTakeCaptured(long utcUnixMs, out WsprCapturedSlot slot, out bool deep, out long currentSlotStartMs)
    {
        lock (_gate)
        {
            deep = _deep;
            currentSlotStartMs = WsprSlotRecorder.SlotStartFor(AlignedLocked(utcUnixMs));
            bool taken = _captured.TryDequeue(out var next);
            slot = next!;
            return taken;
        }
    }

    /// <summary>
    /// Decode (or record the skip of) one captured slot and publish it.
    /// <paramref name="currentSlotStartMs"/> is the slot now in progress: a
    /// capture older than the previous slot means the decoder fell a full
    /// cycle behind, and it is skipped as busy instead of decoded late.
    /// </summary>
    public WsprDecodedSlot Decode(WsprCapturedSlot captured, bool deep, long currentSlotStartMs, Func<long> elapsedMs)
    {
        string? skip = captured.SkipReason;
        IReadOnlyList<WsprRawSpot> spots = [];
        int decodeMs = 0;

        if (skip is null && currentSlotStartMs - captured.SlotStartUnixMs > WsprSlotRecorder.SlotMs)
            skip = WsprDecoderWire.SkipBusy;

        if (skip is null)
        {
            long started = elapsedMs();
            WsprSlotDecodeResult result;
            try
            {
                result = _decoder.Decode(
                    captured.Samples!,
                    captured.DialHz,
                    DateTimeOffset.FromUnixTimeMilliseconds(captured.SlotStartUnixMs).UtcDateTime,
                    deep);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new([], ex.Message);
            }
            decodeMs = (int)Math.Min(int.MaxValue, Math.Max(0, elapsedMs() - started));
            if (result.Error is not null)
            {
                skip = WsprDecoderWire.SkipError;
                lock (_gate) _lastError = result.Error;
            }
            else
            {
                spots = result.Spots;
            }
        }

        lock (_gate) return PublishLocked(captured, decodeMs, skip, spots);
    }

    internal void EnqueueCapturedForTest(WsprCapturedSlot slot)
    {
        lock (_gate) EnqueueLocked(slot);
    }


    // ---- helpers (caller holds _gate) ---------------------------------------

    private long AlignedLocked(long utcUnixMs) => utcUnixMs + (long)Math.Round(_clockOffsetMs);

    private void EnqueueLocked(WsprCapturedSlot slot)
    {
        while (_captured.Count >= MaxQueuedCaptures)
        {
            var oldest = _captured.Dequeue();
            PublishLocked(oldest, 0, oldest.SkipReason ?? WsprDecoderWire.SkipBusy, []);
        }
        _captured.Enqueue(slot);
    }

    private WsprDecodedSlot PublishLocked(
        WsprCapturedSlot captured, int decodeMs, string? skip, IReadOnlyList<WsprRawSpot> spots)
    {
        var decoded = new WsprDecodedSlot(
            ++_seq,
            captured.Receiver,
            captured.SlotStartUnixMs,
            captured.DialHz,
            decodeMs,
            skip is null ? WsprDecoderWire.OutcomeDecoded : WsprDecoderWire.OutcomeSkipped,
            skip,
            spots);
        _slots.AddLast(decoded);
        while (_slots.Count > RetainedSlots) _slots.RemoveFirst();
        return decoded;
    }

    private void DisableLocked()
    {
        _enabled = false;
        Volatile.Write(ref _enabledMask, 0);
        _leaseExpiresMs = 0;
        _captured.Clear();
        for (int r = 0; r < _maxReceivers; r++)
        {
            _recorders[r]?.Reset();
            _decimators[r]?.Reset();
            // Discard anything the DSP thread wrote before it saw the mask drop.
            _rings[r]?.Skip(RingCapacity);
        }
    }

    private WsprDecoderStatus StatusLocked(long utcUnixMs)
    {
        var receivers = new List<WsprReceiverStatus>();
        int mask = _enabled ? _enabledMask : 0;
        for (int r = 0; r < _maxReceivers; r++)
        {
            if ((mask & (1 << r)) == 0) continue;
            var tuning = _tuning[r];
            string? reason =
                !tuning.Present ? "receiver is not enabled on this radio"
                : !WsprSlotRecorder.IsWsprMode(tuning.Mode) ? $"receiver is in {tuning.Mode}; WSPR needs USB or DIGU"
                : null;
            receivers.Add(new WsprReceiverStatus(
                r,
                Enabled: true,
                tuning.DialHz,
                tuning.Mode.ToString(),
                Usable: reason is null,
                reason,
                _recorders[r]?.CapturingSlotStartUnixMs));
        }

        return new WsprDecoderStatus(
            _decoder.Available,
            _decoder.Available ? _decoder.Version : null,
            _enabled,
            _deep ? WsprDecoderWire.DepthDeep : WsprDecoderWire.DepthNormal,
            receivers,
            _seq,
            _decoder.Available ? _lastError : _decoder.UnavailableReason,
            _enabled ? _leaseExpiresMs : null);
    }
}
