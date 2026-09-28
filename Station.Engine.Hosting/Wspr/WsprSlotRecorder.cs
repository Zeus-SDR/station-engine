// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>A finished slot capture: audio to decode, or the reason it was skipped.</summary>
internal sealed record WsprCapturedSlot(
    int Receiver,
    long SlotStartUnixMs,
    long DialHz,
    float[]? Samples,
    string? SkipReason);

/// <summary>
/// Assembles one receiver's 12 kHz audio into WSPR slots: the 114 s window that
/// starts on each even UTC minute. Samples are placed by wall clock (the time
/// the block's last sample was captured), so a stalled audio stream leaves a
/// counted gap instead of shifting the slot, and the audio clock never drifts
/// the capture off the minute. Not thread-safe: one caller (the capture
/// worker) owns an instance.
/// </summary>
internal sealed class WsprSlotRecorder
{
    internal const long SlotMs = 120_000;
    internal const int SamplesPerMs = WsprNativeMethods.SampleRate / 1000;
    internal const int CaptureSamples = WsprNativeMethods.SlotSamples;
    internal const long CaptureMs = CaptureSamples / SamplesPerMs;

    /// <summary>Tuning changes this early in a slot re-base the dial instead of spoiling it.</summary>
    internal const long RebaseWindowMs = 1_000;
    /// <summary>A capture that begins later than this into the slot is partial.</summary>
    internal const int PartialThresholdSamples = 1_000 * SamplesPerMs;
    /// <summary>More missing audio than this fraction spoils the slot.</summary>
    internal const double MaxGapFraction = 0.02;
    /// <summary>Clock/sample-count disagreement tolerated before re-anchoring.</summary>
    internal const int ReanchorSamples = 100 * SamplesPerMs;

    private readonly int _receiver;

    private float[]? _buffer;
    private long _slotStartMs;
    private long _slotDialHz;
    private RxMode _slotMode;
    private int _pos;
    private int _firstPos;
    private long _gapSamples;
    private bool _tx;
    private bool _retuned;

    private long _dialHz;
    private RxMode _mode = RxMode.USB;
    private long _tuningGeneration;

    // Slots our transmitter was keyed in while no capture of that slot
    // existed. TX suppresses receive audio, so a key-down that spans a slot
    // boundary (or a whole slot) starts no capture; those slots must still be
    // reported as skipped for tx. Oldest first, no duplicates.
    private readonly List<(long SlotStartMs, long DialHz)> _pendingTxSlots = [];

    public WsprSlotRecorder(int receiver) => _receiver = receiver;

    public int Receiver => _receiver;

    /// <summary>Start of the slot being captured, or null between slots.</summary>
    public long? CapturingSlotStartUnixMs => _buffer is null ? null : _slotStartMs;

    public static long SlotStartFor(long unixMs) => Math.DivRem(unixMs, SlotMs, out long rem) * SlotMs - (rem < 0 ? SlotMs : 0);

    /// <summary>Record the receiver's current dial and mode, observed at <paramref name="nowUnixMs"/>.</summary>
    public void NoteTuning(long dialHz, RxMode mode, long nowUnixMs) =>
        NoteTuning(dialHz, mode, _tuningGeneration, nowUnixMs, nowUnixMs);

    /// <summary>
    /// Record the receiver's dial and mode. <paramref name="generation"/> counts
    /// every tuning change the radio reported, so a transient change that was
    /// undone before this call (A to B and back to A) still counts as a retune.
    /// The changes happened between <paramref name="firstChangedAtUnixMs"/> and
    /// <paramref name="lastChangedAtUnixMs"/>; only the endpoints are known, so
    /// a span that overlaps the capture window at all spoils the slot.
    /// </summary>
    public void NoteTuning(long dialHz, RxMode mode, long generation, long firstChangedAtUnixMs, long lastChangedAtUnixMs)
    {
        if (generation == _tuningGeneration && dialHz == _dialHz && mode == _mode) return;
        _tuningGeneration = generation;
        _dialHz = dialHz;
        _mode = mode;
        if (_buffer is null) return;
        long first = Math.Min(firstChangedAtUnixMs, lastChangedAtUnixMs) - _slotStartMs;
        long last = Math.Max(firstChangedAtUnixMs, lastChangedAtUnixMs) - _slotStartMs;
        if (first >= CaptureMs) return;
        // Changes that all landed before the slot began, or within its first
        // second, describe the whole capture: re-base instead of spoiling it.
        if (last < RebaseWindowMs)
        {
            _slotDialHz = dialHz;
            _slotMode = mode;
        }
        else
        {
            _retuned = true;
        }
    }

    /// <summary>Our transmitter was keyed at <paramref name="nowUnixMs"/>.</summary>
    public void NoteTransmit(long nowUnixMs)
    {
        if (_buffer is not null)
        {
            long offset = nowUnixMs - _slotStartMs;
            if (offset >= 0 && offset < CaptureMs)
            {
                _tx = true;
                return;
            }
            // Before this capture, or in its tail after the window: nothing to spoil.
            if (offset < SlotMs) return;
        }
        long slot = SlotStartFor(nowUnixMs);
        if (nowUnixMs - slot >= CaptureMs) return;
        // Remember the dial at key-down: the operator may retune before the
        // slot is published.
        if (_pendingTxSlots.Count == 0 || _pendingTxSlots[^1].SlotStartMs < slot)
            _pendingTxSlots.Add((slot, _dialHz));
    }

    /// <summary>
    /// Append 12 kHz samples whose last sample was captured at
    /// <paramref name="blockEndUnixMs"/> (UTC plus the clock offset). Returns
    /// every slot this block finished, oldest first (usually none or one).
    /// </summary>
    public IReadOnlyList<WsprCapturedSlot> Append(ReadOnlySpan<float> samples, long blockEndUnixMs, bool transmitting)
    {
        if (samples.IsEmpty) return [];
        List<WsprCapturedSlot>? finished = null;

        long blockStartMs = blockEndUnixMs - (samples.Length + SamplesPerMs - 1) / SamplesPerMs;
        long slot = SlotStartFor(blockStartMs);

        if (_buffer is not null && slot != _slotStartMs)
            (finished ??= []).Add(Finish());
        while (_pendingTxSlots.Count > 0 && _pendingTxSlots[0].SlotStartMs < slot)
            (finished ??= []).Add(TakePendingTx());

        if (_buffer is null)
        {
            // Between the end of one capture window and the next slot: wait
            // for the next slot, unless this block already starts inside a
            // capture window (it is then a partial slot, recorded honestly).
            if (blockStartMs - slot >= CaptureMs) return finished ?? (IReadOnlyList<WsprCapturedSlot>)[];
            Begin(slot, blockStartMs);
        }

        if (transmitting) _tx = true;

        int expected = (int)Math.Clamp((blockStartMs - _slotStartMs) * SamplesPerMs, 0, CaptureSamples);
        if (Math.Abs(expected - _pos) > ReanchorSamples)
        {
            if (expected > _pos) _gapSamples += expected - _pos;
            _pos = expected;
        }

        int room = CaptureSamples - _pos;
        int count = Math.Min(room, samples.Length);
        if (count > 0)
        {
            samples[..count].CopyTo(_buffer.AsSpan(_pos));
            _pos += count;
        }

        if (_pos >= CaptureSamples)
            (finished ??= []).Add(Finish());

        return finished ?? (IReadOnlyList<WsprCapturedSlot>)[];
    }

    /// <summary>
    /// Finish a capture whose audio stopped arriving (stream stalled, TX
    /// suppression, receiver closed) once its window has passed.
    /// </summary>
    public WsprCapturedSlot? FlushIfStale(long nowUnixMs)
    {
        if (_buffer is null)
        {
            // A slot our transmitter covered with no receive audio at all.
            return _pendingTxSlots.Count > 0 && nowUnixMs - _pendingTxSlots[0].SlotStartMs >= CaptureMs + 2_000
                ? TakePendingTx()
                : null;
        }
        return nowUnixMs - _slotStartMs >= CaptureMs + 2_000 ? Finish() : null;
    }

    /// <summary>Drop any capture in progress (decoder disabled or receiver removed).</summary>
    public void Reset()
    {
        ClearCapture();
        _pendingTxSlots.Clear();
    }

    private void ClearCapture()
    {
        _buffer = null;
        _pos = 0;
        _gapSamples = 0;
        _tx = false;
        _retuned = false;
    }

    private WsprCapturedSlot TakePendingTx()
    {
        var (slot, dialHz) = _pendingTxSlots[0];
        _pendingTxSlots.RemoveAt(0);
        return new WsprCapturedSlot(_receiver, slot, dialHz, null, WsprDecoderWire.SkipTx);
    }

    private void Begin(long slotStartMs, long blockStartMs)
    {
        _buffer = new float[CaptureSamples];
        _slotStartMs = slotStartMs;
        _slotDialHz = _dialHz;
        _slotMode = _mode;
        _pos = (int)Math.Clamp((blockStartMs - slotStartMs) * SamplesPerMs, 0, CaptureSamples);
        _firstPos = _pos;
        _gapSamples = 0;
        _tx = _pendingTxSlots.Count > 0 && _pendingTxSlots[0].SlotStartMs == slotStartMs;
        if (_tx) _pendingTxSlots.RemoveAt(0);
        _retuned = false;
    }

    private WsprCapturedSlot Finish()
    {
        float[] buffer = _buffer!;
        long missing = _gapSamples + Math.Max(0, CaptureSamples - _pos);
        string? skip =
            _tx ? WsprDecoderWire.SkipTx
            : _firstPos > PartialThresholdSamples ? WsprDecoderWire.SkipPartial
            : _retuned ? WsprDecoderWire.SkipRetuned
            : !IsWsprMode(_slotMode) ? WsprDecoderWire.SkipMode
            : missing > CaptureSamples * MaxGapFraction ? WsprDecoderWire.SkipGap
            : null;
        var result = new WsprCapturedSlot(_receiver, _slotStartMs, _slotDialHz, skip is null ? buffer : null, skip);
        ClearCapture();
        return result;
    }

    internal static bool IsWsprMode(RxMode mode) => mode is RxMode.USB or RxMode.DIGU;
}
