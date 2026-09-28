// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Keying detector for one smoothed Goertzel amplitude.
///
/// The noise floor is the 25th percentile of recent amplitude, not the
/// median or the mean. Key-down duty sits above the middle of the window
/// and would drag either of those up until a weak mark could no longer
/// clear the gate. While the spread looks like Rayleigh noise the floor
/// is lifted toward the mean; once marks dominate the upper quartile the
/// floor is already the noise and is used as it stands. A signal tracker
/// follows the recent key-down amplitude, including a fade. Attack and
/// release sit on either side of the linear midpoint of that pair. A mark
/// that has faded under the last peak still opens at the noise gate, and a
/// dwell at that level becomes the local mark. A falling edge is not a
/// dwell: it releases, and it does not chatter back on through the gate.
/// The extra hold that gate adds on a loud ramp is reported to the timing
/// stage and taken back out of the mark. A pure tone whose gaps are
/// digital zero never fills the quartile, so while those gaps persist the
/// tone is judged against the zero floor. A mark far above that floor keys
/// even when its absolute amplitude is under the strong-tone level. The
/// first sustained non-zero noise returns the gate to the measured
/// percentile. A loud steady level does not become that floor, and a later
/// zero does not bring the epsilon back.
/// </summary>
internal sealed class AdaptiveThreshold
{
    private const double DigitalZeroPower = 1e-20;
    // Two seconds at 256 samples / 48 kHz. A squelch gap this long rebases
    // the floor so the first live block is not mistaken for a mark.
    private const int DigitalZeroReacquireBlocks = 375;
    // Numerical floor under the amplitude. Real receiver noise sits well
    // above it; the clamp only stops a dead input from collapsing a ratio.
    private const double MinimumNoiseFloor = 1e-12;
    // 512 blocks is 2.73 s at 256 samples / 48 kHz.
    private const int History = 512;
    // Quartile needs a few dozen blocks before a single low draw is the floor.
    // 32 blocks is ~170 ms, inside the generator's 250 ms lead-in.
    private const int MatureBlocks = 32;
    // Amplitude ratios. A power ratio of R is an amplitude ratio of sqrt(R):
    // 20× power is 4.5× amplitude, 6× is 2.5×, 10× is 3.2×.
    // A +10 dB clip is +12.5 dB in the 282 Hz bin, about 4.8× the mean
    // noise amplitude, so the 2.5× gate clears it and a smoothed noise
    // draw does not. The gate is not capped at a fixed multiple of the
    // noise: that cap put both edges at the bottom of a loud ramp (the
    // mark ran long and the dit estimate swallowed letter spaces) and at
    // the top of a faded one (the element was cut short).
    private const double ImmatureJump = 4.5;
    private readonly double _bootstrapRatio;
    private readonly double _adoptRatio;

    public AdaptiveThreshold(double bootstrapRatio = 2.5, double adoptRatio = 3.2)
    {
        _bootstrapRatio = bootstrapRatio;
        _adoptRatio = adoptRatio;
    }
    // Fraction of the noise-to-mark span. 0.15 puts attack at 0.65 of the
    // span and release at 0.35, symmetric about the linear midpoint.
    private const double Hysteresis = 0.15;
    // While keyed, follow the smoothed mark in a handful of blocks so a
    // fade moves the gate. A block under FadePull of the local mark is the
    // fade itself and is tracked faster. While quiet, creep back so a
    // one-block dip does not. The tracker waits for a second keyed block,
    // so a single spike never moves the midpoint.
    private const double SignalTrack = 0.3;
    private const double FadePull = 0.75;
    private const double FadeTrack = 0.55;
    // A fade dwells. Once a ramp is under the release threshold it drops
    // by more than this from one block to the next, so it never confirms.
    // Two blocks at one level is past a single ramp sample.
    private const double FadeDwellRatio = 0.85;
    private const int FadeDwellBlocks = 2;
    // Goertzel amplitude of a strong mark. Quality-clip noise and the
    // 1e-7 warmup floor sit under it, so a flat noise run is not a tone.
    private const double AbsoluteToneFloor = 2e-2;
    // Against the epsilon, not against the tone. 0.01 is a real mark after
    // a digital-zero gap; a 1e-3 noise run is not, and still has to become
    // the measured floor. 1e-12 * 2e9 = 0.002.
    private const double ZeroFloorMarkRatio = 2e9;
    // A keying edge is one partial block. Eight blocks is about 40 ms at
    // 48 kHz: long enough that a real noise run has replaced digital zero,
    // short enough that one edge does not.
    private const int SustainedNoiseBlocks = 8;
    // The adopted mark is still the mark inside this ratio. Half of it is
    // a new plateau. A +20 dB carrier wobbles less than this.
    private const double AdoptedMarkRatio = 0.7;
    // ~400 ms at 256 samples / 48 kHz.
    private const double SignalDecay = 0.013;
    // p75/p25 of Rayleigh amplitude is about 2.2, and the mean sits 1.65×
    // above p25. Smoothed noise is tighter (spread near 1.5). A spread
    // past ~4.5 is marks in the window: p25 is then the noise itself.
    private const double FlatSpread = 1.15;
    private const double RayleighSpread = 2.20;
    private const double MarkedSpread = 4.5;
    private const double RayleighMeanOverP25 = 1.65;

    private readonly double[] _history = new double[History];
    private readonly double[] _sorted = new double[History];
    private int _count;
    private int _cursor;
    private double _signalLevel;
    private double _snrDb;
    private int _digitalZeroBlocks;
    private bool _hasNonZeroInput;
    private bool _reacquire;
    private bool _wasKeyed;
    private bool _fadeLatch;
    private double _fadeLatchLevel;
    private int _fadeLatchBlocks;
    private double _prevLevel;
    private bool _zeroRegime;
    private int _subToneBlocks;
    private bool _measuredFloor;
    private bool _quietFloorLatched;
    private double _lastNoise = MinimumNoiseFloor;
    // Noise floor and mark level at the moment a real mark was adopted.
    // A later window full of that same mark is not a key-up. A step down
    // to a new plateau is the noise that replaced the window.
    private double _adoptedNoise;
    private double _adoptedLevel;

    public bool TonePresent { get; private set; }

    public double SnrDb => _snrDb;

    /// <summary>True once a keyed block has produced an SNR.</summary>
    internal bool HasMeasurement { get; private set; }

    internal double NoiseFloor => Percentile(25);

    public bool Update(double level)
    {
        if (level <= DigitalZeroPower || !double.IsFinite(level))
        {
            TonePresent = false;
            _wasKeyed = false;
            _fadeLatch = false;
            _subToneBlocks = 0;
            // Exact silence is the floor until a run of real noise replaces it.
            if (!_measuredFloor)
                _zeroRegime = true;
            if (_hasNonZeroInput && _digitalZeroBlocks < DigitalZeroReacquireBlocks)
                _digitalZeroBlocks++;
            if (_hasNonZeroInput && _digitalZeroBlocks >= DigitalZeroReacquireBlocks)
                _reacquire = true;
            return false;
        }

        if (_reacquire)
        {
            // The block that ends a long digital-zero gap is the new floor,
            // not a key-down edge. A cold decoder never takes this path, so
            // leading silence cannot hide the first keyed element.
            _reacquire = false;
            _digitalZeroBlocks = 0;
            _signalLevel = 0;
            _wasKeyed = false;
            _fadeLatch = false;
            _fadeLatchLevel = 0;
            _fadeLatchBlocks = 0;
            _prevLevel = 0;
            _quietFloorLatched = false;
            _zeroRegime = false;
            _subToneBlocks = 0;
            _measuredFloor = false;
            _adoptedNoise = 0;
            _adoptedLevel = 0;
            _count = 0;
            _cursor = 0;
            Push(Math.Max(level, MinimumNoiseFloor));
            _hasNonZeroInput = true;
            TonePresent = false;
            return false;
        }

        _digitalZeroBlocks = 0;
        _hasNonZeroInput = true;
        level = Math.Max(level, MinimumNoiseFloor);
        Push(level);

        double floor = Percentile(25);
        double noise = EstimatedNoise(floor);
        // A mature quartile well under this sample was measured from real
        // amplitudes. Remember it so a later plateau cannot replace it with
        // the epsilon after those samples leave the window.
        if (!_measuredFloor
            && _count >= MatureBlocks
            && floor > MinimumNoiseFloor * 10
            && floor < level * 0.25)
        {
            _measuredFloor = true;
        }

        // A flat run under the tone floor is noise. A keying edge is one
        // block. After a digital-zero gap, a mark far above the zero floor
        // is not that noise: counting it would latch a measured floor and
        // the dit would drop out after eight blocks. Outside that gap the
        // absolute floor is unchanged.
        bool aboveZeroFloor = _zeroRegime
            && level >= MinimumNoiseFloor * ZeroFloorMarkRatio;
        if (level < AbsoluteToneFloor && !aboveZeroFloor)
        {
            _subToneBlocks++;
            if (_subToneBlocks >= SustainedNoiseBlocks)
                _measuredFloor = true;
        }
        else
        {
            _subToneBlocks = 0;
        }

        // Digital-zero gaps never enter the quartile, so a pure tone's
        // floor is the tone and every ratio is 1. While those gaps persist,
        // judge a mark against the epsilon. The first sustained non-zero
        // noise ends that, and a loud plateau does not start it again.
        if (_measuredFloor)
        {
            _zeroRegime = false;
            _quietFloorLatched = false;
        }
        else if (_zeroRegime && aboveZeroFloor)
        {
            noise = MinimumNoiseFloor;
            _quietFloorLatched = true;
        }

        _lastNoise = noise;

        if (_count < MatureBlocks && !_quietFloorLatched)
        {
            double mid = Percentile(50);
            TonePresent = _count >= 6 && level >= mid * ImmatureJump && level >= floor * ImmatureJump;
            if (TonePresent)
                NoteSnr(level, noise);
            _wasKeyed = TonePresent;
            _prevLevel = level;
            return TonePresent;
        }

        bool haveSignal = _signalLevel >= noise * _adoptRatio;
        double noiseGate = noise * _bootstrapRatio;
        if (!haveSignal)
        {
            // The quartile caught up with the adopted mark, so every ratio
            // collapsed and the gate is above the tone. The mark is still
            // that level. A step down to a new plateau is the noise that
            // filled the window, not the mark.
            bool stillTheMark = _adoptedLevel > 0
                && level >= _adoptedLevel * AdoptedMarkRatio
                && level <= _adoptedLevel / AdoptedMarkRatio;
            bool steadyTone = _wasKeyed
                && _adoptedNoise > MinimumNoiseFloor * 10
                && stillTheMark
                && _signalLevel >= _adoptedNoise * _adoptRatio
                && _signalLevel >= AbsoluteToneFloor;
            TonePresent = steadyTone || level >= noiseGate;
            _fadeLatch = false;
        }
        else
        {
            double span = Math.Max(_signalLevel - noise, 0);
            double attack = noise + ((0.5 + Hysteresis) * span);
            double release = noise + ((0.5 - Hysteresis) * span);
            if (!TonePresent)
            {
                if (level >= attack)
                {
                    TonePresent = true;
                    _fadeLatch = false;
                }
                else if (level >= noiseGate && !_fadeLatch)
                {
                    // The gap already happened. A fade under the last peak
                    // still opens at the noise ratio.
                    TonePresent = true;
                }
                else if (level >= noiseGate && _fadeLatch && SameLevel(level, _fadeLatchLevel))
                {
                    _fadeLatchBlocks++;
                    if (_fadeLatchBlocks >= FadeDwellBlocks)
                    {
                        // Two blocks at one level: a dwell, not the next
                        // sample of a ramp. The tracker below skips this
                        // block because the previous one was unkeyed, so
                        // the local mark has to move here or the old
                        // release drops the key again.
                        TonePresent = true;
                        _fadeLatch = false;
                        _signalLevel += FadeTrack * (level - _signalLevel);
                    }
                }
                else if (level >= noiseGate && _fadeLatch)
                {
                    _fadeLatchLevel = level;
                    _fadeLatchBlocks = 1;
                }
                else
                {
                    TonePresent = false;
                    _fadeLatch = false;
                }
            }
            else if (level < release)
            {
                if (level >= noiseGate && level >= _signalLevel * 0.5)
                {
                    // The noise pedestal puts release above half the mark when
                    // the mark is only a few times the floor. Hold that sliver.
                    TonePresent = true;
                }
                else if (level >= noiseGate && SameLevel(level, _prevLevel))
                {
                    // Still on the level that opened. The tracker pulls the
                    // local mark down; this is not a key-up.
                    TonePresent = true;
                    _fadeLatch = false;
                }
                else
                {
                    TonePresent = false;
                    if (level >= noiseGate)
                    {
                        // Under the old release, over the noise gate. A ramp
                        // keeps falling and never confirms; a fade dwells.
                        _fadeLatch = true;
                        _fadeLatchLevel = level;
                        _fadeLatchBlocks = 1;
                    }
                    else
                    {
                        _fadeLatch = false;
                    }
                }
            }
            else
            {
                _fadeLatch = false;
            }
        }

        // Follow the smoothed mark after the second keyed block. A single
        // spike keys and releases without moving the midpoint. A quieter
        // keyed block is a fade and pulls the local mark down with it.
        if (TonePresent && _wasKeyed && level >= noiseGate)
        {
            if (_signalLevel <= 0)
                _signalLevel = level;
            else if (level < _signalLevel * FadePull)
                _signalLevel += FadeTrack * (level - _signalLevel);
            else
                _signalLevel += SignalTrack * (level - _signalLevel);
            if (_adoptedNoise <= MinimumNoiseFloor * 10
                && noise > MinimumNoiseFloor * 10
                && _signalLevel >= noise * _adoptRatio)
            {
                _adoptedNoise = noise;
                _adoptedLevel = _signalLevel;
            }
        }
        else if (_signalLevel > 0)
        {
            _signalLevel += SignalDecay * (Math.Min(level, _signalLevel) - _signalLevel);
        }

        if (TonePresent)
            NoteSnr(level, noise);
        _wasKeyed = TonePresent;
        _prevLevel = level;
        return TonePresent;
    }

    public void Reset()
    {
        _count = 0;
        _cursor = 0;
        _signalLevel = 0;
        _snrDb = 0;
        HasMeasurement = false;
        _digitalZeroBlocks = 0;
        _hasNonZeroInput = false;
        _reacquire = false;
        _wasKeyed = false;
        _fadeLatch = false;
        _fadeLatchLevel = 0;
        _fadeLatchBlocks = 0;
        _prevLevel = 0;
        _zeroRegime = false;
        _subToneBlocks = 0;
        _measuredFloor = false;
        _quietFloorLatched = false;
        _lastNoise = MinimumNoiseFloor;
        _adoptedNoise = 0;
        _adoptedLevel = 0;
        TonePresent = false;
    }

    /// <summary>
    /// Blocks a mark runs long because the attack sits on the noise gate
    /// and the release sits on the local mark. Zero when those crossings
    /// are the same distance from the two edges, and zero on a hard
    /// digital-zero key-up (there is no ramp to correct).
    /// </summary>
    internal double ExcessMarkBlocks(int smootherLength)
    {
        if (_quietFloorLatched || smootherLength < 2 || _signalLevel <= 0)
            return 0;
        double noise = Math.Max(_lastNoise, MinimumNoiseFloor);
        double span = _signalLevel - noise;
        if (span <= noise) return 0;
        double midpointAttack = noise + ((0.5 + Hysteresis) * span);
        double effectiveAttack = Math.Min(midpointAttack, noise * _bootstrapRatio);
        double release = noise + ((0.5 - Hysteresis) * span);
        double riseFrac = Math.Clamp((effectiveAttack - noise) / span, 0, 1);
        double fallFrac = Math.Clamp((release - noise) / span, 0, 1);
        int rise = CrossingIndex(smootherLength, riseFrac);
        int fall = HoldIndex(smootherLength, fallFrac);
        return Math.Max(0, fall - rise);
    }

    private static bool SameLevel(double level, double reference)
    {
        if (!(reference > 0) || !(level > 0)) return false;
        double ratio = level / reference;
        return ratio >= FadeDwellRatio && ratio <= (1.0 / FadeDwellRatio);
    }

    private static int CrossingIndex(int length, double fraction)
    {
        if (fraction <= 0) return 0;
        if (fraction >= 1) return length;
        int k = (int)Math.Ceiling((fraction * length) - 1e-9);
        if (k < 1) k = 1;
        return k - 1;
    }

    private static int HoldIndex(int length, double fraction)
    {
        if (fraction >= 1) return 0;
        if (fraction <= 0) return length - 1;
        int hold = (int)Math.Floor((length * (1.0 - fraction)) + 1e-9);
        if (hold < 0) return 0;
        if (hold > length) return length;
        return hold;
    }

    private void NoteSnr(double level, double noise)
    {
        double ratio = level / Math.Max(noise, MinimumNoiseFloor);
        // 20·log10 because the level is an amplitude.
        _snrDb = Math.Clamp(20.0 * Math.Log10(Math.Max(ratio, 1e-6)), -30.0, 60.0);
        HasMeasurement = true;
    }

    private double EstimatedNoise(double floor)
    {
        double upper = Percentile(75);
        double spread = upper / Math.Max(floor, MinimumNoiseFloor);
        if (spread <= FlatSpread || spread >= MarkedSpread)
            return floor;

        double blend = spread <= RayleighSpread
            ? (spread - FlatSpread) / (RayleighSpread - FlatSpread)
            : (MarkedSpread - spread) / (MarkedSpread - RayleighSpread);
        if (blend < 0) blend = 0;
        if (blend > 1) blend = 1;
        return floor * (1.0 + (blend * (RayleighMeanOverP25 - 1.0)));
    }

    private double Percentile(int percent)
    {
        if (_count <= 0) return MinimumNoiseFloor;
        int index = (_count - 1) * percent / 100;
        if (index < 0) index = 0;
        if (index >= _count) index = _count - 1;
        return Math.Max(_sorted[index], MinimumNoiseFloor);
    }

    private void Push(double power)
    {
        if (_count < History)
        {
            _history[_count] = power;
            InsertSorted(_count, power);
            _count++;
            return;
        }

        double old = _history[_cursor];
        _history[_cursor] = power;
        _cursor++;
        if (_cursor == History) _cursor = 0;
        if (!RemoveSorted(old))
            return;
        InsertSorted(History - 1, power);
    }

    private void InsertSorted(int length, double value)
    {
        int lo = 0;
        int hi = length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_sorted[mid] <= value) lo = mid + 1;
            else hi = mid;
        }

        if (lo < length)
            Array.Copy(_sorted, lo, _sorted, lo + 1, length - lo);
        _sorted[lo] = value;
    }

    private bool RemoveSorted(double value)
    {
        int lo = 0;
        int hi = History;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }

        if (lo >= History || _sorted[lo] != value)
            return false;
        Array.Copy(_sorted, lo + 1, _sorted, lo, History - lo - 1);
        return true;
    }
}
