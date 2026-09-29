// SPDX-License-Identifier: GPL-2.0-or-later
//
// Keeps live voice TX near real time.
//
// The mic is clocked by the sound card (or browser) and the radio drains TX IQ
// on its own DAC clock, so the transport buffer between them (P1 TxIqRing, P2
// DUC packet queue) absorbs every capture stall, GC pause or network burst.
// Nothing drains that backlog faster than real time, so each hiccup used to add
// permanent delay for the rest of the over — the P1 ring could sit hundreds of
// milliseconds behind the operator's voice.
//
// The governor measures the backlog floor (the bottom of the per-block
// sawtooth) and, while it exceeds a cushion, removes 1 ms chunks from the
// *pauses between words* before WDSP. Speech is never cut: a chunk is only
// dropped once it sits inside a quiet run longer than a word gap, so short
// in-word dips (and PSK-style phase-reversal nulls) always pass. The splice is
// in the audio domain and smoothed by the TXA bandpass before it reaches RF.
//
// The cushion adapts: every transport underrun (the radio was fed silence) is
// proof the cushion was too thin for this source's jitter, so it grows and
// trimming pauses; it relaxes back slowly once the stream is stable.

namespace Zeus.Server;

/// <summary>Host-side TX IQ queued ahead of the radio plus the transport's
/// running underrun count. BacklogSamples &lt; 0 = transport can't report.</summary>
internal readonly record struct TxTransportLevel(int BacklogSamples, long Underruns)
{
    public static TxTransportLevel Unknown => new(-1, 0);
}

internal sealed class TxLatencyGovernor
{
    internal const int SampleRateHz = 48_000;
    internal const int SamplesPerMs = SampleRateHz / 1000;
    // 1 ms decision grain.
    internal const int ChunkSamples = SamplesPerMs;
    // Default cushion left at the bottom of the sawtooth. Callers pass a larger
    // floor for sources that arrive over a network.
    internal const int DefaultMinTargetSamples = 10 * SamplesPerMs;
    internal const int MaxTargetSamples = 80 * SamplesPerMs;
    // Cushion added per window that saw an underrun.
    internal const int UnderrunStepSamples = 20 * SamplesPerMs;
    // Relax 1 ms per window (5 ms/s) once stable.
    internal const int RelaxStepSamples = 1 * SamplesPerMs;
    // Only act on a clear excess so the loop doesn't chase measurement noise.
    internal const int HysteresisSamples = 5 * SamplesPerMs;
    // Floor = minimum backlog over a 200 ms window, however the mic arrives
    // (20 ms blocks or ASIO-buffer-sized chunks).
    internal const int WindowSamples = 200 * SamplesPerMs;
    // Legacy block count of one window at the 20 ms block cadence.
    internal const int WindowBlocks = WindowSamples / 960;
    // After an underrun, no trimming for this many windows (2 s).
    internal const int UnderrunHoldoffWindows = 10;
    // A quiet run must last longer than this before any of it is dropped:
    // longer than in-word dips and digital-mode envelope nulls.
    internal const int MinQuietRunChunks = 20;

    // A chunk is "quiet" when its peak sits within +6 dB of the tracked noise
    // floor and at least 20 dB under the recent speech peak. Absolute floor
    // guards a digitally-silent input (noise estimate of 0).
    private const float QuietAbsoluteFloor = 0.0003f;     // ~ -70 dBFS
    private const float NoiseMarginLinear = 2.0f;         // +6 dB over noise floor
    private const float SpeechMarginLinear = 0.1f;        // -20 dB under speech peak
    // Noise floor tracker: follows dips instantly, creeps up ~4 dB/s.
    private const float NoiseRisePerChunk = 1.0005f;
    // Speech-peak envelope decays ~9 dB/s (0.98 per 20 ms of audio).
    private const float SpeechDecayPer20Ms = 0.98f;

    private int _windowMin = int.MaxValue;
    private int _windowSampleCount;
    private int _droppedThisWindow;
    private int _budget;
    private int _target = DefaultMinTargetSamples;
    private int _holdoffWindows;
    private long _lastUnderruns = -1;
    private int _quietRunSamples;
    private float _noiseFloor = float.MaxValue;
    private float _speechPeak;

    /// <summary>Backlog floor (48 kHz samples) from the last completed window, or -1.</summary>
    public int LastFloorSamples { get; private set; } = -1;
    /// <summary>Current adaptive cushion, in 48 kHz samples.</summary>
    public int TargetSamples => _target;
    /// <summary>Samples trimmed since the last <see cref="Reset"/> (i.e. this over).</summary>
    public long DroppedSamples { get; private set; }

    public void Reset()
    {
        _windowMin = int.MaxValue;
        _windowSampleCount = 0;
        _droppedThisWindow = 0;
        _budget = 0;
        _target = DefaultMinTargetSamples;
        _holdoffWindows = 0;
        _lastUnderruns = -1;
        _quietRunSamples = 0;
        _noiseFloor = float.MaxValue;
        _speechPeak = 0f;
        LastFloorSamples = -1;
        DroppedSamples = 0;
    }

    /// <summary>
    /// Record the transport level observed just before this mic block is
    /// processed — the bottom of the sawtooth. An unknown level idles the
    /// governor.
    /// </summary>
    public void ObserveTransport(
        TxTransportLevel level,
        int minTargetSamples = DefaultMinTargetSamples,
        int blockSamples = 960)
    {
        int backlog = level.BacklogSamples;
        if (backlog < 0)
        {
            _windowMin = int.MaxValue;
            _windowSampleCount = 0;
            _droppedThisWindow = 0;
            _budget = 0;
            _lastUnderruns = -1;
            LastFloorSamples = -1;
            return;
        }

        // Baseline the underrun counter on the first reading of the over so
        // starvation inside the very first window is still detected.
        if (_lastUnderruns < 0) _lastUnderruns = level.Underruns;

        int minTarget = Math.Clamp(minTargetSamples, 0, MaxTargetSamples);
        if (_target < minTarget) _target = minTarget;

        // Something else (capture age-out, the transport's own stale-drop)
        // may have shrunk the backlog since the budget was set: never spend
        // past the cushion as of right now.
        _budget = Math.Min(_budget, Math.Max(0, backlog - _target));

        // Project each observation to "now": anything dropped after it was
        // taken has already come off the backlog. Storing obs + drops-so-far
        // and subtracting the window's total drops at the end does exactly that.
        int projected = backlog + _droppedThisWindow;
        if (projected < _windowMin) _windowMin = projected;
        _windowSampleCount += Math.Max(1, blockSamples);
        if (_windowSampleCount < WindowSamples) return;

        int floor = Math.Max(0, _windowMin - _droppedThisWindow);
        LastFloorSamples = floor;
        _windowMin = int.MaxValue;
        _windowSampleCount = 0;
        _droppedThisWindow = 0;

        bool underran = level.Underruns > _lastUnderruns;
        _lastUnderruns = level.Underruns;
        if (underran)
        {
            _target = Math.Min(MaxTargetSamples, _target + UnderrunStepSamples);
            _holdoffWindows = UnderrunHoldoffWindows;
            _budget = 0;
            return;
        }
        if (_holdoffWindows > 0)
        {
            _holdoffWindows--;
            _budget = 0;
            return;
        }
        _target = Math.Max(minTarget, _target - RelaxStepSamples);

        int excess = floor - _target;
        _budget = excess > HysteresisSamples ? excess : 0;
    }

    /// <summary>
    /// Remove 1 ms chunks that fall inside a sustained pause from
    /// <paramref name="block"/> in place, while a catch-up budget remains.
    /// Returns the number of samples kept at the front of the span.
    /// </summary>
    public int Compact(Span<float> block)
    {
        float blockPeak = 0f;
        for (int read = 0; read < block.Length; read += ChunkSamples)
        {
            float chunkPeak = Peak(block.Slice(read, Math.Min(ChunkSamples, block.Length - read)));
            if (chunkPeak > blockPeak) blockPeak = chunkPeak;
            int n = Math.Min(ChunkSamples, block.Length - read);
            _noiseFloor = chunkPeak < _noiseFloor
                ? chunkPeak
                : _noiseFloor * (n == ChunkSamples
                    ? NoiseRisePerChunk
                    : MathF.Pow(NoiseRisePerChunk, n / (float)ChunkSamples));
        }
        _speechPeak = blockPeak > _speechPeak
            ? blockPeak
            : _speechPeak * MathF.Pow(SpeechDecayPer20Ms, block.Length / (20f * SamplesPerMs));

        float threshold = Math.Min(
            Math.Max(_noiseFloor * NoiseMarginLinear, QuietAbsoluteFloor),
            Math.Max(_speechPeak * SpeechMarginLinear, QuietAbsoluteFloor));

        int write = 0;
        for (int read = 0; read < block.Length; read += ChunkSamples)
        {
            int n = Math.Min(ChunkSamples, block.Length - read);
            var chunk = block.Slice(read, n);
            if (Peak(chunk) <= threshold)
            {
                // Counted in samples: ASIO-sized blocks end in partial chunks.
                _quietRunSamples += n;
                if (_quietRunSamples > MinQuietRunChunks * ChunkSamples && _budget >= n)
                {
                    _budget -= n;
                    _droppedThisWindow += n;
                    DroppedSamples += n;
                    continue;
                }
            }
            else
            {
                _quietRunSamples = 0;
            }
            if (write != read) chunk.CopyTo(block.Slice(write, n));
            write += n;
        }
        return write;
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        foreach (float s in samples)
        {
            // A non-finite sample (misbehaving plugin) reads as full scale so
            // it can never be mistaken for silence or poison the envelopes.
            float a = float.IsFinite(s) ? (s < 0f ? -s : s) : 1f;
            if (a > peak) peak = a;
        }
        return peak;
    }
}
