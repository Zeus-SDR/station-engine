// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Zeus side of the VST-engine audio plane: owns the shared-memory
/// double-buffer + the two auto-reset events, and exposes the realtime
/// <see cref="Process"/> tap. Zeus CREATES the shared memory + events; the
/// engine OPENS them. See <c>docs/designs/vst-engine-bridge-protocol.md</c>.
///
/// <para><b>Robust-path contract (load-bearing):</b> the realtime TX thread
/// NEVER blocks unbounded and NEVER allocates here. Each block is a memcpy-in →
/// signal → <em>bounded</em> wait → memcpy-out; on timeout / engine-down /
/// stale-seq it falls through to clean passthrough and keeps feeding the radio.
/// The view pointer + event handles are acquired once and held for the bridge's
/// lifetime. Windows-only (named shared memory + events).</para>
/// </summary>
internal sealed unsafe class VstEngineBridge : IVstEngineBridge
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _inEvent;
    private readonly EventWaitHandle _outEvent;

    private byte* _base;
    private readonly int _maxFrames;
    private readonly int _channels;
    private readonly int _regionBytes;
    private readonly bool _concealOnMiss;

    // RX-only concealment buffer (see _concealOnMiss). Pre-allocated once so the
    // realtime path never allocates. Capped so a stuck plugin degrades to a short
    // repeated blip rather than looping the same stale block indefinitely — Thetis
    // reuses the last wet block with no cap; Zeus intentionally bounds it.
    private readonly float[]? _lastGood;
    private int _lastGoodCount;
    private int _concealStreak;
    private const int MaxConcealStreak = 2;

    // Adaptive wait-budget state — realtime-thread-only, no lock needed (single
    // caller). Thetis-Plus tolerates engine scheduling jitter with a multi-slot
    // ring buffer + adaptive pipeline latency on BOTH sides of the wire; Zeus's
    // engine is a separate out-of-tree binary (KlayaR/VSTHost fork) this repo
    // can't change the wire protocol for, so the equivalent tolerance is built
    // entirely on the Zeus side instead: WaitBudgetMs is a FLOOR, and a small
    // extra allowance grows on a miss (more slack right when jitter is already
    // showing) and decays slowly after a long clean run — same shape as
    // Thetis-Plus's "grows on lateness, decays slowly when stable", applied to
    // the existing single-slot synchronous round trip rather than a ring depth.
    // Hard-capped at both a multiple of the floor AND a fraction of the actual
    // block period (computed per-call from ctx.Frames/ctx.SampleRate) so this
    // can never approach the "one wedged block costs one passthrough" guarantee
    // WaitBudgetMs exists to make.
    private int _adaptiveExtraMs;
    private int _cleanStreak;
    private const int AdaptiveGrowStepMs = 1;
    private const int AdaptiveDecayStepMs = 1;
    private const int AdaptiveDecayAfterCleanBlocks = 200;
    private const int AdaptiveCeilingFloorMultiple = 3;
    private const double AdaptiveCeilingBlockFraction = 0.6;

    // Adaptive spin-phase state — realtime-thread-only, no lock needed (single
    // caller), same discipline as the adaptive budget above. Before paying for
    // a kernel wait, Process polls the engine's out-seq directly for a short
    // window: a cross-process SetEvent → wake costs ~0.5–1.5 ms of scheduler
    // latency even when the engine has ALREADY written its response, while a
    // seq poll on our own core sees it in nanoseconds. The window self-tunes
    // (see NextSpinWindowTicks): grows while the engine answers inside it,
    // halves when it doesn't — a slow or contended machine falls back to the
    // plain event wait instead of burning the core, and a hung engine never
    // spins past the block deadline, so the "one wedged block costs at most
    // one passthrough block" guarantee is unchanged.
    private long _spinWindowTicks = MinSpinWindowTicks;
    internal static readonly long MinSpinWindowTicks = (long)(0.05e-3 * Stopwatch.Frequency);  // 50 µs
    internal static readonly long MaxSpinWindowTicks = (long)(2.0e-3 * Stopwatch.Frequency);   // 2 ms
    internal static readonly long SpinGrowStepTicks = (long)(0.10e-3 * Stopwatch.Frequency);   // +100 µs/hit

    private ulong _inSeq;
    private long _degraded;
    private int _disposed;
    private int _engineReady;

    // Round-trip latency telemetry. Written ONLY on the realtime thread (the
    // single Process caller), read lock-free by diagnostics — a momentarily
    // torn count/total pair skews one average sample, the same benign-race
    // discipline as the adaptive state above. Unlike _degraded (a lifetime
    // counter), these reset on ResetForRelaunch so they describe the CURRENT
    // engine session's latency, not a dead engine's history.
    private long _rttServiced;
    private long _rttTicksTotal;
    private long _rttMaxTicks;
    private long _rttLastTicks;

    /// <summary>
    /// Bounded wait ceiling per block (ms) — the FLOOR of the effective adaptive
    /// budget (see the adaptive-state fields above). A wedged plugin still costs
    /// at most one block of passthrough, never a dropped radio. Kept well under
    /// the block period (512f @ 48 kHz ≈ 10.6 ms).
    /// </summary>
    public int WaitBudgetMs { get; set; } = 4;

    /// <summary>
    /// Set true by the controller once the engine's <c>ready</c> handshake is
    /// seen (the engine's audio thread is then blocked on <c>.in</c>). While
    /// false, <see cref="Process"/> is pure passthrough — the realtime path
    /// never touches the engine.
    /// </summary>
    public bool EngineReady
    {
        get => Volatile.Read(ref _engineReady) != 0;
        set => Volatile.Write(ref _engineReady, value ? 1 : 0);
    }

    /// <summary>Count of blocks that fell through to passthrough (timeout / stale).</summary>
    public long DegradedBlocks => Interlocked.Read(ref _degraded);

    /// <summary>Blocks the engine serviced inside the budget since the last (re)launch.</summary>
    public long ServicedBlocks => Interlocked.Read(ref _rttServiced);

    /// <summary>Mean engine round-trip time (µs) over <see cref="ServicedBlocks"/> this session.</summary>
    public double AverageRoundTripMicros
    {
        get
        {
            long n = Interlocked.Read(ref _rttServiced);
            return n == 0 ? 0 : TicksToMicros(Interlocked.Read(ref _rttTicksTotal)) / n;
        }
    }

    /// <summary>Worst engine round-trip time (µs) since the last (re)launch.</summary>
    public double MaxRoundTripMicros => TicksToMicros(Interlocked.Read(ref _rttMaxTicks));

    /// <summary>Most recent engine round-trip time (µs).</summary>
    public double LastRoundTripMicros => TicksToMicros(Interlocked.Read(ref _rttLastTicks));

    internal static double TicksToMicros(long ticks) => ticks * 1e6 / Stopwatch.Frequency;

    /// <summary>
    /// Engine-written lifecycle state (OffEngineState: 0=init 1=running 2=draining).
    /// Diagnostics-only reader — capture a stable base pointer and null-check it so a
    /// race with <see cref="Dispose"/> degrades to 0 instead of faulting on a torn map.
    /// </summary>
    public uint EngineState
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return 0;
            var b = _base;
            return b == null ? 0u : *(uint*)(b + VstEngineProtocol.OffEngineState);
        }
    }

    /// <summary>Engine-written flags (OffFlags: bit0 = bypassed / empty chain). Diagnostics-only.</summary>
    public uint EngineFlags
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return 0;
            var b = _base;
            return b == null ? 0u : *(uint*)(b + VstEngineProtocol.OffFlags);
        }
    }

    public string ShmName { get; }

    private VstEngineBridge(string shm, MemoryMappedFile mmf, MemoryMappedViewAccessor view,
                            EventWaitHandle inEv, EventWaitHandle outEv,
                            int maxFrames, int channels, bool concealOnMiss)
    {
        ShmName = shm;
        _mmf = mmf;
        _view = view;
        _inEvent = inEv;
        _outEvent = outEv;
        _maxFrames = maxFrames;
        _channels = channels;
        _regionBytes = VstEngineProtocol.RegionBytes(maxFrames, channels);
        _concealOnMiss = concealOnMiss;
        _lastGood = concealOnMiss ? new float[maxFrames * channels] : null;

        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
    }

    /// <summary>Create the shared memory + events and initialise the header.</summary>
    /// <param name="concealOnMiss">
    /// When true, a late/timed-out block replays the last successfully processed
    /// output block (bounded by <see cref="MaxConcealStreak"/>) instead of falling
    /// straight to dry passthrough. Intended for RX only — TX must keep the
    /// existing dry-fallback behaviour, matching Thetis-Plus's own RX/TX split.
    /// </param>
    public static VstEngineBridge Create(string shm, int maxFrames, int rate, int channels, bool concealOnMiss = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("VST engine bridge is Windows-only.");
        if (maxFrames <= 0 || channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrames));

        long total = VstEngineProtocol.TotalBytes(maxFrames, channels);
        var mmf = MemoryMappedFile.CreateNew(shm, total);
        MemoryMappedViewAccessor? view = null;
        EventWaitHandle? inEv = null, outEv = null;
        try
        {
            view = mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
            inEv = new EventWaitHandle(false, EventResetMode.AutoReset, VstEngineProtocol.InEventName(shm));
            outEv = new EventWaitHandle(false, EventResetMode.AutoReset, VstEngineProtocol.OutEventName(shm));

            var bridge = new VstEngineBridge(shm, mmf, view, inEv, outEv, maxFrames, channels, concealOnMiss);
            bridge.InitHeader(rate);
            return bridge;
        }
        catch
        {
            outEv?.Dispose();
            inEv?.Dispose();
            view?.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    private void InitHeader(int rate)
    {
        WriteU32(VstEngineProtocol.OffMagic, VstEngineProtocol.Magic);
        WriteU32(VstEngineProtocol.OffProtocol, VstEngineProtocol.Version);
        WriteU32(VstEngineProtocol.OffMaxFrames, (uint)_maxFrames);
        WriteU32(VstEngineProtocol.OffChannels, (uint)_channels);
        WriteU32(VstEngineProtocol.OffRate, (uint)rate);
        WriteU32(VstEngineProtocol.OffFramesThisBlock, 0);
        WriteU64(VstEngineProtocol.OffInSeq, 0);
        WriteU64(VstEngineProtocol.OffOutSeq, 0);
        WriteU32(VstEngineProtocol.OffEngineState, 0);
        WriteU32(VstEngineProtocol.OffFlags, 0);
    }

    /// <summary>
    /// Realtime tap. Sends one block to the engine and reads the processed
    /// result back, or passes through unchanged on any failure. Returns true
    /// only when the output came from the engine. No allocation, no managed
    /// lock. See the robust-path contract on the class.
    /// </summary>
    public bool Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        int n = ctx.Frames, ch = ctx.Channels;
        int count = n * ch;

        if (Volatile.Read(ref _disposed) != 0 || !EngineReady
            || n <= 0 || n > _maxFrames || ch != _channels
            || input.Length < count || output.Length < count)
        {
            input.CopyTo(output);
            return false;
        }

        // Drain any stale .out left signaled by a LATE response to an earlier
        // block. Without this, a single timeout permanently desyncs the bridge:
        // the engine ends up one block behind, every .out we then wait on carries
        // the previous block's seq, so every block fails the seq check and we wedge
        // into 100% passthrough that never recovers even after the load drops.
        // Draining first means the next signal we wait on is for THIS block.
        while (_outEvent.WaitOne(0)) { /* discard stale signal */ }

        // input → shared input region
        long submitTs = Stopwatch.GetTimestamp();
        WriteU32(VstEngineProtocol.OffFramesThisBlock, (uint)n);
        var inRegion = new Span<float>(_base + VstEngineProtocol.HeaderBytes, count);
        input.Slice(0, count).CopyTo(inRegion);

        // publish: stamp seq, then wake the engine
        _inSeq++;
        WriteU64(VstEngineProtocol.OffInSeq, _inSeq);
        _inEvent.Set();

        // Bounded wait for OUR response (matching seq). A late response for an
        // earlier block is skipped, but we keep waiting WITHIN the same budget so
        // one slow block costs at most one passthrough — not a permanent cascade.
        // Never block the radio longer than the effective (floor + adaptive extra)
        // budget, itself capped well under this block's real-time deadline.
        int floorMs = WaitBudgetMs;
        double blockMs = n * 1000.0 / Math.Max(1, ctx.SampleRate);
        int ceilingMs = Math.Min(
            floorMs * AdaptiveCeilingFloorMultiple,
            (int)(blockMs * AdaptiveCeilingBlockFraction));
        int maxExtraMs = Math.Max(0, ceilingMs - floorMs);
        int extraMs = Math.Clamp(_adaptiveExtraMs, 0, maxExtraMs);
        int effectiveBudgetMs = floorMs + extraMs;
        long deadline = Stopwatch.GetTimestamp() + (long)(effectiveBudgetMs * 1e-3 * Stopwatch.Frequency);

        // Spin phase (see the spin-state comment above): bounded by both the
        // adaptive window and the block deadline. A hit means the engine's
        // response is already visible — skip the kernel wait entirely.
        long spinUntil = Math.Min(deadline, Stopwatch.GetTimestamp() + _spinWindowTicks);
        bool spinHit = false;
        while (Stopwatch.GetTimestamp() < spinUntil)
        {
            if (ReadU64Volatile(VstEngineProtocol.OffOutSeq) == _inSeq)
            {
                spinHit = true;
                break;
            }
            Thread.SpinWait(64);
        }
        _spinWindowTicks = NextSpinWindowTicks(_spinWindowTicks, spinHit, BlockTicks(n, ctx.SampleRate));

        if (!spinHit)
        {
            while (true)
            {
                long remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    Interlocked.Increment(ref _degraded);
                    (_adaptiveExtraMs, _cleanStreak) =
                        NextAdaptiveBudget(_adaptiveExtraMs, _cleanStreak, clean: false, maxExtraMs);
                    if (_concealOnMiss && _lastGoodCount == count && _concealStreak < MaxConcealStreak)
                    {
                        _concealStreak++;
                        _lastGood!.AsSpan(0, count).CopyTo(output);
                    }
                    else
                    {
                        input.CopyTo(output);
                    }
                    return false;
                }
                int remainingMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                bool signaled;
                if (remainingMs > 0)
                {
                    // Round down so this blocking wait never deliberately consumes the
                    // fractional remainder beyond the monotonic deadline.
                    signaled = _outEvent.WaitOne(remainingMs);
                }
                else
                {
                    // EventWaitHandle has only millisecond timeout precision. Poll the
                    // final sub-millisecond remainder instead of either degrading early
                    // or rounding up to a 1 ms wait that can exceed the block budget.
                    signaled = _outEvent.WaitOne(0);
                    if (!signaled) Thread.SpinWait(32);
                }
                if (!signaled) continue;
                // only trust output whose seq matches the block we just sent
                if (ReadU64(VstEngineProtocol.OffOutSeq) == _inSeq) break;
                // stale late response — loop and keep waiting within the budget
            }
        }
        else
        {
            // Consume the .out signal if it already arrived so the next block's
            // stale-drain doesn't have to; harmless if the engine Sets it later
            // (the drain loop discards it then).
            _outEvent.WaitOne(0);
        }

        // The engine answered inside the budget — record the round trip BEFORE
        // the decay bookkeeping so the telemetry covers the full submit→match
        // span (memcpy-in + signal + engine processing + wake/poll).
        long rttTicks = Stopwatch.GetTimestamp() - submitTs;
        _rttServiced++;
        _rttTicksTotal += rttTicks;
        _rttLastTicks = rttTicks;
        if (rttTicks > _rttMaxTicks) _rttMaxTicks = rttTicks;

        // Clean round trip — slowly relax the adaptive extra. Decaying only after
        // a long stable run (not on every clean block) avoids oscillating the
        // budget back down right before the jitter that caused it recurs.
        // See NextAdaptiveBudget for the policy itself.
        (_adaptiveExtraMs, _cleanStreak) =
            NextAdaptiveBudget(_adaptiveExtraMs, _cleanStreak, clean: true, maxExtraMs);

        var outRegion = new Span<float>(_base + VstEngineProtocol.HeaderBytes + _regionBytes, count);
        var dst = output.Slice(0, count);
        outRegion.CopyTo(dst);

        if (_concealOnMiss)
        {
            dst.CopyTo(_lastGood!.AsSpan(0, count));
            _lastGoodCount = count;
            _concealStreak = 0;
        }

        return true;
    }

    /// <summary>
    /// Disarm the gate and drain any stale <c>.in</c>/<c>.out</c> signals left by
    /// a crashed engine so the relaunched engine starts from a clean sequence.
    /// The realtime tap must already be disarmed (engine down) when this runs —
    /// the controller only calls it between process exit and re-arm.
    /// </summary>
    public void ResetForRelaunch()
    {
        EngineReady = false;
        // A relaunched engine starts a new session — never replay audio from
        // before the crash into it, and don't carry over adaptive slack earned
        // against the DEAD engine's jitter characteristics.
        _lastGoodCount = 0;
        _concealStreak = 0;
        _adaptiveExtraMs = 0;
        _cleanStreak = 0;
        _spinWindowTicks = MinSpinWindowTicks;
        // Latency telemetry describes the current engine session — a relaunched
        // engine gets a clean slate so a dead engine's samples never skew it.
        _rttServiced = 0;
        _rttTicksTotal = 0;
        _rttMaxTicks = 0;
        _rttLastTicks = 0;
        if (Volatile.Read(ref _disposed) != 0) return;
        // Discard any signals the dead engine (or our last unanswered Set) left
        // pending, so the first block after relaunch waits on ITS own response.
        while (_outEvent.WaitOne(0)) { }
        while (_inEvent.WaitOne(0)) { }
    }

    private void WriteU32(int off, uint v) => *(uint*)(_base + off) = v;
    private uint ReadU32(int off) => *(uint*)(_base + off);
    private void WriteU64(int off, ulong v) => *(ulong*)(_base + off) = v;
    private ulong ReadU64(int off) => *(ulong*)(_base + off);

    // Volatile variant for the spin phase: a plain unsafe read can be cached in
    // a register across loop iterations by the JIT; the spin loop needs to see
    // the ENGINE's store the moment it lands in shared memory.
    private ulong ReadU64Volatile(int off) => Volatile.Read(ref *(ulong*)(_base + off));

    private static long BlockTicks(int frames, int sampleRate) =>
        (long)(frames * (double)Stopwatch.Frequency / Math.Max(1, sampleRate));

    /// <summary>
    /// Next adaptive spin-window size (pure; unit-tested). Grows one step per
    /// spin hit, halves on a miss, clamped to [50 µs, min(2 ms, ¼ block
    /// period)]. The block-period cap keeps worst-case spin cost proportional
    /// to the audio deadline on any block size; the floor keeps a near-free
    /// pre-check (~50 µs/block) alive so the window can re-grow the moment the
    /// engine starts answering fast again.
    /// </summary>
    internal static long NextSpinWindowTicks(long currentTicks, bool spinHit, long blockTicks)
    {
        long cap = Math.Max(MinSpinWindowTicks, Math.Min(MaxSpinWindowTicks, blockTicks / 4));
        return spinHit
            ? Math.Min(currentTicks + SpinGrowStepTicks, cap)
            : Math.Max(currentTicks / 2, MinSpinWindowTicks);
    }

    /// <summary>
    /// Next adaptive wait-budget allowance (pure; unit-tested). A miss grows the
    /// extra by one step — capped at <paramref name="maxExtraMs"/>, which the caller
    /// derives from both the floor multiple and this block's real period — and resets
    /// the clean streak, so slack appears right when jitter is already showing. A
    /// clean round trip counts toward the streak and gives one step back only once
    /// the streak reaches <see cref="AdaptiveDecayAfterCleanBlocks"/>, so the budget
    /// doesn't oscillate back down immediately before the jitter that earned it
    /// recurs.
    ///
    /// <para>Pure for the same reason as <see cref="NextSpinWindowTicks"/>: this
    /// policy is platform-independent arithmetic, but the bridge that runs it is
    /// Windows-only (named shared memory + events), so this is the only seam where
    /// the policy can be asserted deterministically — and on every platform —
    /// instead of being inferred from the timing of a raced round trip. Returns a
    /// value tuple, so the realtime path still allocates nothing.</para>
    /// </summary>
    internal static (int ExtraMs, int CleanStreak) NextAdaptiveBudget(
        int extraMs, int cleanStreak, bool clean, int maxExtraMs)
    {
        // Miss: buy one more step of slack (never past the caller's ceiling, which
        // may be BELOW the current extra when the block period shrank) and start the
        // clean streak over.
        if (!clean)
            return (Math.Min(extraMs + AdaptiveGrowStepMs, maxExtraMs), 0);

        // No slack earned yet — nothing to relax, and the streak deliberately stays
        // put so a long clean run can't bank decay credit against a future grow.
        if (extraMs <= 0)
            return (extraMs, cleanStreak);

        int streak = cleanStreak + 1;
        // Math.Max is defensive only: with a 1 ms decay step the guard above already
        // rules out a negative result, so it exists to keep a future larger
        // AdaptiveDecayStepMs from driving the extra below the floor.
        return streak >= AdaptiveDecayAfterCleanBlocks
            ? (Math.Max(0, extraMs - AdaptiveDecayStepMs), 0)
            : (extraMs, streak);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        EngineReady = false;

        if (_base != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }
        _outEvent.Dispose();
        _inEvent.Dispose();
        _view.Dispose();
        _mmf.Dispose();
    }
}
