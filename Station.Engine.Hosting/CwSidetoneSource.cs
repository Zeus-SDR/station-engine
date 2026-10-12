// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Host-side sidetone generator for CW keying. WDSP exports no sidetone
/// API in the libraries we ship (re-verified 2026-08-23: `nm -D libwdsp.so |
/// grep -i sidetone` is empty across linux-x64, linux-arm64, osx-arm64,
/// osx-x64), so
/// — like the carrier itself in <see cref="CwEngine"/> — we synthesise the
/// monitor tone in C# and mix it into the RX audio buffer just before the
/// pipeline publishes the block. Sinking through the existing RX audio bus
/// means every output that already plays band RX (browser WebSocket, native
/// audio, TCI audio_stream) hears sidetone automatically; no per-sink
/// special case.
///
/// Single instance per process — registered as a singleton. Multiple
/// keying sources can drive it concurrently:
/// <list type="bullet">
///   <item><see cref="CwEngine"/> for macro / cw_msg / cw_macros / raw-key</item>
///   <item><c>ExternalPttService</c> for the HL2 KEY-jack and any other
///         hardware key the radio reports via its C&amp;C status path</item>
/// </list>
/// Calls to <see cref="Down"/> and <see cref="Up"/> are idempotent — two
/// <c>Down</c> in a row do not double the envelope, and an <c>Up</c> with
/// no preceding <c>Down</c> is a no-op. This lets the hardware-key path
/// fire transition events without bookkeeping about who else might be keying.
///
/// A second, independent keying input drives a <see cref="HostIambicKeyer"/>
/// from raw paddle levels (issue #2977, legacy Orion-MkII DLE gateware).
/// Its output is OR'd with <see cref="Down"/>/<see cref="Up"/> so a CWX
/// element and a paddle element can overlap without cutting each other.
/// </summary>
public sealed class CwSidetoneSource
{
    /// <summary>Mono RX audio output rate — matches
    /// <c>DspPipelineService.AudioOutputRateHz</c>. The render path is
    /// always called against the RX audio buffer so the rate has to track
    /// the WDSP RXA output.</summary>
    public const int OutputRateHz = 48_000;

    /// <summary>Raised-cosine attack / release length in samples. 5 ms at
    /// 48 kHz = 240 samples. Same envelope shape and length as the on-air
    /// CW envelope in <see cref="CwEngine"/> so the sidetone the operator
    /// hears matches what's actually being transmitted; shorter than 2 ms
    /// produces audible monitor clicks, longer than 10 ms starts to round
    /// off short dits at 30+ WPM.</summary>
    private const int RampSamples = 240;

    private const int MinPitchHz = 200;
    private const int MaxPitchHz = 1200;
    private const double MinGainDb = -60.0;
    private const double MaxGainDb = 0.0;
    private const double SilenceLinear = 1e-4; // < -80 dBFS — operator can't hear

    // --- Hot-path state — read/written only by RenderInto (DSP thread) ---

    private double _phase;
    // Last keyed value rendered. Compared against _keyed on each call so
    // an external Down/Up between ticks triggers an envelope reset.
    private bool _renderedKeyed;
    // Samples since the last edge. Saturates at RampSamples (steady state
    // for plateau / full release). Reset on every edge. Initialised to
    // RampSamples so a freshly-constructed source reports as "release ramp
    // already complete" — the idle fast path returns immediately without
    // generating tone on the never-keyed-yet case.
    private int _envelopePos = RampSamples;
    private double _envelopeLevel;
    private double _edgeStartLevel;

    // --- Cross-thread state ---

    // Latest commanded key state. Volatile so DSP thread sees writes from
    // CwEngine / ExternalPttService without a memory barrier.
    private volatile bool _keyed;

    // Pitch is a single 4-byte field — volatile read/write is atomic on
    // every platform we ship to.
    private volatile int _pitchHz = 600;

    // Gain is read on the hot path and written from REST/UI thread. Wrap
    // in a lock so DSP can't grab a half-updated float, even though the
    // tear window on .NET is theoretical. Reads are fast (uncontended).
    private float _gainLinear = (float)Math.Pow(10.0, -10.0 / 20.0);
    private readonly object _gainLock = new();

    // --- Paddle-emulator mailbox (P2 RX thread → DSP render thread) ---

    // One word so a render cannot observe a new jack level without the
    // rising edge that produced it, or a stale edge after the level has
    // already moved. Bits 0..1 are the live jacks; bits 2..3 are latched
    // rises. SetPaddles is the single level writer (P2 RX thread) and
    // publishes both halves with one CompareExchange.
    private const int LevelDotBit = 1 << 0;
    private const int LevelDashBit = 1 << 1;
    private const int PendingDotBit = 1 << 2;
    private const int PendingDashBit = 1 << 3;
    private const int LevelMask = LevelDotBit | LevelDashBit;
    private const int PendingMask = PendingDotBit | PendingDashBit;

    private int _paddleWord;
    // ReleasePaddles(reset: true) asks the render thread to Reset() the
    // emulator at the start of the next block.
    private int _resetRequested;
    // Immutable holder so the four-field config publishes as a single
    // atomic reference; the struct itself is larger than a pointer.
    private volatile PaddleConfigHolder? _paddleConfig;
    // DSP-thread-owned. Never touched from SetPaddles/ReleasePaddles.
    private readonly HostIambicKeyer _paddleKeyer = new();

    private sealed class PaddleConfigHolder
    {
        public readonly HostIambicKeyerConfig Config;
        public PaddleConfigHolder(HostIambicKeyerConfig config) => Config = config;
    }

    /// <summary>Operator commanded key-down. Idempotent — repeated calls
    /// without an intervening <see cref="Up"/> are a no-op (do not
    /// re-trigger the attack ramp). Cheap; safe from any thread.</summary>
    public void Down() => _keyed = true;

    /// <summary>Operator commanded key-up. Idempotent — repeated calls
    /// without an intervening <see cref="Down"/> are a no-op. Cheap; safe
    /// from any thread. Does not cut a paddle-emulator element — the two
    /// keying inputs are OR'd.</summary>
    public void Up() => _keyed = false;

    /// <summary>
    /// Publish the current raw paddle levels and keyer knobs. Callable from
    /// any thread (the Protocol-2 RX thread in production). Rising edges
    /// are latched so a tap that fits between two <see cref="RenderInto"/>
    /// calls still produces an element.
    /// </summary>
    public void SetPaddles(bool dot, bool dash, HostIambicKeyerConfig config)
    {
        // Hi-priority status arrives hundreds of times a second. The holder
        // is immutable, so an unchanged config keeps the published instance.
        var published = _paddleConfig;
        if (published is null || published.Config != config)
            _paddleConfig = new PaddleConfigHolder(config);
        int newLevels = (dot ? LevelDotBit : 0) | (dash ? LevelDashBit : 0);
        int word = Volatile.Read(ref _paddleWord);
        while (true)
        {
            int oldLevels = word & LevelMask;
            int rising = 0;
            if (dot && (oldLevels & LevelDotBit) == 0) rising |= PendingDotBit;
            if (dash && (oldLevels & LevelDashBit) == 0) rising |= PendingDashBit;
            // Keep edges already latched, add rises from this update, and
            // replace the live levels. One CAS, so the two halves publish
            // together.
            int updated = (word & PendingMask) | rising | newLevels;
            int seen = Interlocked.CompareExchange(ref _paddleWord, updated, word);
            if (seen == word) return;
            word = seen;
        }
    }

    /// <summary>
    /// Release both paddles. With <paramref name="reset"/> true, the render
    /// thread <see cref="HostIambicKeyer.Reset"/>s the emulator at the start
    /// of its next block (disconnect, or leaving legacy-iambic mode). The
    /// audible tone still ramps down through the raised-cosine envelope.
    /// </summary>
    public void ReleasePaddles(bool reset)
    {
        Interlocked.And(ref _paddleWord, reset ? ~(LevelMask | PendingMask) : ~LevelMask);
        if (reset)
            Volatile.Write(ref _resetRequested, 1);
    }

    /// <summary>Set the sidetone pitch. Clamped to a sane operator range
    /// (200..1200 Hz — narrower than WDSP's CW bandpass even at extremes
    /// so the sidetone always sits where the receiver expects it).</summary>
    public void SetPitchHz(int hz) => _pitchHz = Math.Clamp(hz, MinPitchHz, MaxPitchHz);

    /// <summary>Set sidetone gain. -60..0 dB. Above 0 risks clipping the
    /// mix with band RX; below -60 is effectively silent.</summary>
    public void SetGainDb(double db)
    {
        double clamped = Math.Clamp(db, MinGainDb, MaxGainDb);
        float linear = (float)Math.Pow(10.0, clamped / 20.0);
        lock (_gainLock) _gainLinear = linear;
    }

    /// <summary>Published paddle-config holder. Equal configs share one
    /// instance. Test seam.</summary>
    internal object? PublishedPaddleConfigForTest => _paddleConfig;

    /// <summary>True when a raw paddle level is still published. Test seam.</summary>
    internal bool PaddlesHeldForTest =>
        (Volatile.Read(ref _paddleWord) & LevelMask) != 0;

    /// <summary>True when the render thread has been asked to reset the
    /// emulator and has not consumed the request yet. Test seam.</summary>
    internal bool PaddleResetPendingForTest => Volatile.Read(ref _resetRequested) != 0;

    /// <summary>True when the generator is producing non-silence — either
    /// currently keyed, with an unprocessed release edge pending render,
    /// still inside the release ramp, or with the paddle emulator holding
    /// timing state / a pending press. Test seam.</summary>
    internal bool IsActive =>
        _keyed
        || _renderedKeyed
        || _envelopePos < RampSamples
        || (Volatile.Read(ref _paddleWord) & (LevelMask | PendingMask)) != 0
        || Volatile.Read(ref _resetRequested) != 0
        || !_paddleKeyer.IsIdle;

    /// <summary>
    /// Mix sidetone samples into <paramref name="dst"/> (additive, not
    /// overwrite — caller's existing audio survives). When neither
    /// currently keyed nor mid-release-ramp, returns immediately without
    /// touching <paramref name="dst"/>.
    ///
    /// Called from <c>DspPipelineService.Tick</c> on every audio block.
    /// Must be cheap when idle (the no-keying common case) and must not
    /// allocate.
    /// </summary>
    /// <returns>True if any non-zero samples were written. Useful for the
    /// caller to decide whether to skip a publish that would otherwise be
    /// pure silence.</returns>
    public bool RenderInto(Span<float> dst)
    {
        bool reset = Interlocked.Exchange(ref _resetRequested, 0) != 0;
        // And returns the word before the pending bits were cleared.
        int word = Interlocked.And(ref _paddleWord, ~PendingMask);
        if (reset) _paddleKeyer.Reset();
        bool latchedDot = (word & PendingDotBit) != 0;
        bool latchedDash = (word & PendingDashBit) != 0;
        bool liveDot = (word & LevelDotBit) != 0;
        bool liveDash = (word & LevelDashBit) != 0;
        var config = _paddleConfig?.Config ?? default;
        // Latch bits are physical jack edges. HostIambicKeyer swaps
        // internally when PaddleReverse is set, so a reversed dot tap
        // enters PREDASH and a physical-dot consumption test never
        // matches — the latch then looks held for the rest of the block.
        // Swap once into the keyer's logical space and step with the
        // swap already applied. Direct HostIambicKeyer callers still
        // pass PaddleReverse themselves.
        if (config.PaddleReverse)
        {
            (liveDot, liveDash) = (liveDash, liveDot);
            (latchedDot, latchedDash) = (latchedDash, latchedDot);
            config = config with { PaddleReverse = false };
        }
        bool paddleActive = !_paddleKeyer.IsIdle
            || latchedDot || latchedDash
            || liveDot || liveDash;

        if (!paddleActive)
        {
            // Emulator idle, nothing pending: the Down/Up path is the
            // original per-block renderer, bit-identical in output.
            bool wantKeyed = _keyed;
            if (wantKeyed != _renderedKeyed)
            {
                // An edge can arrive before the preceding ramp completes.
                // Continue from its last rendered level instead of jumping to
                // full amplitude on release or zero on a renewed attack.
                _edgeStartLevel = _envelopeLevel;
                _renderedKeyed = wantKeyed;
                _envelopePos = 0;
            }
            // Fast path: not keying and the release ramp is done.
            if (!wantKeyed && _envelopePos >= RampSamples) return false;

            int pitch = _pitchHz;
            float gain;
            lock (_gainLock) gain = _gainLinear;
            // Don't synthesize a tone the operator can't hear — saves a sin()
            // per sample when sidetone is dialed to -60 dB. Mirror the spec
            // floor used by the gain setter.
            if (gain < SilenceLinear) return false;

            double phaseStep = 2.0 * Math.PI * pitch / OutputRateHz;
            bool wroteNonZero = false;

            for (int i = 0; i < dst.Length; i++)
            {
                MixSample(ref dst[i], wantKeyed, gain, phaseStep, ref wroteNonZero);
            }
            return wroteNonZero;
        }

        // Paddle emulator is live: step it every sample so its timing state
        // never freezes, and detect keying edges per sample so iambic dits
        // get their own attack/release instead of one long tone.
        // SetGainDb's floor is -60 dB (1e-3), above SilenceLinear, so this
        // loop always mixes. The Down/Up path above keeps its own silence
        // check.
        int paddlePitch = _pitchHz;
        float paddleGain;
        lock (_gainLock) paddleGain = _gainLinear;
        double paddlePhaseStep = 2.0 * Math.PI * paddlePitch / OutputRateHz;
        bool paddleWrote = false;

        for (int i = 0; i < dst.Length; i++)
        {
            bool dot = liveDot || latchedDot;
            bool dash = liveDash || latchedDash;
            bool paddleOut = _paddleKeyer.Step(dot, dash, in config);
            if (latchedDot && (_paddleKeyer.InPredot || _paddleKeyer.DotMemory))
                latchedDot = false;
            if (latchedDash && (_paddleKeyer.InPredash || _paddleKeyer.DashMemory))
                latchedDash = false;

            bool wantKeyed = _keyed || paddleOut;
            MixSample(ref dst[i], wantKeyed, paddleGain, paddlePhaseStep, ref paddleWrote);
        }
        return paddleWrote;
    }

    private void AdvanceEnvelope(bool wantKeyed)
    {
        if (wantKeyed != _renderedKeyed)
        {
            _edgeStartLevel = _envelopeLevel;
            _renderedKeyed = wantKeyed;
            _envelopePos = 0;
        }
        double target = wantKeyed ? 1.0 : 0.0;
        double progress = _envelopePos < RampSamples
            ? 0.5 * (1.0 - Math.Cos(Math.PI * _envelopePos / RampSamples))
            : 1.0;
        _envelopeLevel = _edgeStartLevel + (target - _edgeStartLevel) * progress;
        if (_envelopePos < RampSamples) _envelopePos++;
    }

    private void MixSample(ref float dst, bool wantKeyed, float gain, double phaseStep, ref bool wroteNonZero)
    {
        AdvanceEnvelope(wantKeyed);
        double env = _envelopeLevel;
        if (env > 0.0)
        {
            dst += (float)(gain * env * Math.Sin(_phase));
            wroteNonZero = true;
        }
        _phase += phaseStep;
        if (_phase > 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
    }
}
