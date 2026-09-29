// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
namespace Zeus.Server;

/// <summary>One envelope's independent threshold, clock and Morse state.</summary>
internal sealed class CwEnvelopeDecoder
{
    private readonly bool _narrow;
    private readonly EnvelopeSmoother _smoother = new();
    private readonly AdaptiveThreshold _threshold = new();
    private readonly MatchedToneThreshold _matched = new();
    private readonly AdaptiveThreshold _fadeThreshold = new(1.8, 2.2);
    private bool _fading;
    private double _markPeak;
    private double _previousPeak;
    private double _markQuality;
    private int _markBlocks;
    private readonly MorseTimingEstimator _timing = new();
    private readonly MorseFsm _fsm;
    private int _openRun;
    private bool _open;
    private double _quietMs;
    private double _signalQuality = 1;
    private double _detectorDelayMs;
    private double _openingQuality;
    private double _snrSum;
    private int _snrCount;
    private int _raisedNoiseBlocks;
    // Narrow blocks seen while the matched rails are still being measured.
    // The first element of a weak sender is spent measuring them, so those
    // blocks are re-read once the rails exist instead of being lost.
    private const int OpeningCapacity = 384;
    private readonly double[] _openingAmplitude = new double[OpeningCapacity];
    private readonly double[] _openingSignalQuality = new double[OpeningCapacity];
    // About 43 ms: shorter than a 50 WPM dah, longer than a noise crossing.
    private const int OpeningMarkBlocks = 8;
    private const double OpeningMarkCoherence = 0.9;
    // The rail separation the matched threshold itself requires. A mark
    // closer to the noise than that is copied no better by re-reading it.
    private const double OpeningMarkOverNoise = 3.5;
    // Keep the element space before the found mark: an opening dit can sit
    // one element gap ahead of the first mark long enough to qualify.
    private const int OpeningLeadBlocks = 48;
    // Replay only after a real pause. With less history the rails separate
    // almost at once and cost the sender nothing; a short buffer is a fade
    // dip inside one transmission, where re-reading only adds noise.
    private const int MinOpeningReplayBlocks = 128;
    private int _openingCount;
    private int _openingCursor;

    public CwEnvelopeDecoder(bool narrow)
    {
        _narrow = narrow;
        _fsm = new MorseFsm(_timing);
    }

    public double Wpm => _timing.Wpm;
    public double SnrDb => _narrow ? (_fading ? _fadeThreshold.SnrDb : _matched.SnrDb) : _threshold.SnrDb;
    public double MeanSnrDb => _snrCount == 0 ? 0 : _snrSum / _snrCount;
    public double QuietMs => _quietMs;
    public double DitMs => _timing.DitMs;
    public int ToneBlocks => _snrCount;

    public void Process(double amplitude, double blockMs, Action<MorseDecodedSymbol> output, double signalQuality = 1, double detectorDelayMs = 0)
    {
        _signalQuality = signalQuality;
        _detectorDelayMs = detectorDelayMs;
        // A receiver noise-floor step can resemble a continuous key-down to
        // an old amplitude threshold. Rebase only after repeated incoherent
        // in-passband samples; a louder coherent station is kept immediately.
        if (!_narrow && signalQuality < 0.85 && amplitude > 4 * _threshold.NoiseFloor)
        {
            if (++_raisedNoiseBlocks >= 12) BeginNextSender();
        }
        else if (signalQuality > 0.95 || amplitude < 2 * _threshold.NoiseFloor) _raisedNoiseBlocks = 0;
        bool keyed;
        if (_narrow)
        {
            bool matched = _matched.Update(amplitude);
            if (_matched.JustAcquired && !_open)
            {
                ReplayOpening(blockMs, output);
                _signalQuality = signalQuality;
                matched = _matched.Classify(amplitude);
            }
            else if (!_matched.Acquired && !_open) KeepOpeningBlock(amplitude, signalQuality);
            bool adaptive = _fadeThreshold.Update(amplitude);
            // Use the adaptive envelope to observe fresh fading evidence;
            // it can see a coherent mark below the old stable mark rail.
            if (adaptive)
            {
                _markPeak = Math.Max(_markPeak, amplitude);
                _markQuality += signalQuality;
                _markBlocks++;
            }
            // Fading changes complete mark peaks, not just their keying
            // ramps. Switch to the fading tracker only on a falling edge,
            // after coherent copy and a 6 dB change between full marks.
            else if (_markBlocks > 0)
            {
                double coherence = _markQuality / _markBlocks;
                // A peak needs a plateau beyond both integration ramps.
                // Otherwise fast dits and dahs have different peak heights
                // even without fading. Coherence also excludes noise fragments.
                if (_markBlocks * blockMs >= 2 * (2 * detectorDelayMs + blockMs)
                    && _markBlocks >= 4 && coherence >= 0.85 && _fsm.HasCoherentSupport
                    && (_markBlocks * blockMs >= 0.7 * _timing.DitMs || coherence >= 0.95))
                {
                    if (_previousPeak > 0 && (_markPeak > 2 * _previousPeak || _markPeak < 0.5 * _previousPeak))
                        _fading = true;
                    _previousPeak = _markPeak;
                }
                _markPeak = 0;
                _markQuality = 0;
                _markBlocks = 0;
            }
            keyed = _fading ? adaptive : matched;
        }
        else
        {
            _smoother.SetDit(_timing.FastAcquisition ? MorseTimingEstimator.MinDitMs : _timing.DitMs, blockMs);
            keyed = _threshold.Update(amplitude <= 1e-10 ? 0 : _smoother.Push(amplitude));
            if (amplitude <= 1e-10) _smoother.Clear();
        }
        Advance(keyed, signalQuality, blockMs, output);
    }

    private void Advance(bool keyed, double blockQuality, double blockMs, Action<MorseDecodedSymbol> output)
    {
        if (keyed)
        {
            _quietMs = 0;
            // The first character supplies the detector choice. Do not let
            // a later fade turn that decision into a switch inside a mark.
            if (_snrCount < 64)
            {
                _snrSum += SnrDb;
                _snrCount++;
            }
            _openRun++;
            _openingQuality += blockQuality;
        }
        else
        {
            _quietMs += blockMs;
            _openRun = 0;
            _openingQuality = 0;
        }
        if (!_open)
        {
            if (_openRun < 4) return;
            _open = true;
            _signalQuality = _openingQuality / _openRun;
            Feed(false, blockMs, output);
            for (int i = 0; i < _openRun; i++) Feed(true, blockMs, output);
            return;
        }
        Feed(keyed, blockMs, output);
    }

    private void KeepOpeningBlock(double amplitude, double signalQuality)
    {
        _openingAmplitude[_openingCursor] = amplitude;
        _openingSignalQuality[_openingCursor] = signalQuality;
        _openingCursor = (_openingCursor + 1) % OpeningCapacity;
        if (_openingCount < OpeningCapacity) _openingCount++;
    }

    /// <summary>
    /// The matched rails just separated. Re-read the opening marks they were
    /// measured on, so this sender's first element reaches the clock and the
    /// Morse state instead of being spent on the measurement. Only runs
    /// before the envelope opens, so no block is fed twice.
    /// </summary>
    private void ReplayOpening(double blockMs, Action<MorseDecodedSymbol> output)
    {
        int oldest = (_openingCursor - _openingCount + OpeningCapacity) % OpeningCapacity;
        if (_openingCount < MinOpeningReplayBlocks
            || !TryFindOpeningMark(oldest, out int first, out double markLevel))
        {
            // No real pause before this sender, or rails that separated on
            // noise alone. Re-reading either would turn quiet into letters;
            // leave the live path in charge.
            ClearOpening();
            return;
        }

        // Replay from just before that mark. The quiet ahead of it is noise,
        // and re-reading noise against any threshold is how letters appear
        // out of nothing. The held rail is the mark itself, not a percentile
        // of a buffer that is mostly quiet.
        _matched.HoldMarkRail(markLevel);
        for (int i = Math.Max(0, first - OpeningLeadBlocks); i < _openingCount; i++)
        {
            int index = (oldest + i) % OpeningCapacity;
            _signalQuality = _openingSignalQuality[index];
            Advance(_matched.Classify(_openingAmplitude[index]), _signalQuality, blockMs, output);
        }
        ClearOpening();
    }

    /// <summary>
    /// First real opening element: <see cref="OpeningMarkBlocks"/> consecutive
    /// blocks well clear of the noise floor whose tone phase holds.
    /// Band-limited noise can cross an amplitude threshold for a block or
    /// two, but it does not keep the tone's phase for that long.
    /// <paramref name="first"/> is the run's first block, counted from the
    /// oldest buffered block; <paramref name="markLevel"/> is its mean amplitude.
    /// </summary>
    private bool TryFindOpeningMark(int oldest, out int first, out double markLevel)
    {
        double threshold = OpeningMarkOverNoise * _matched.NoiseFloor;
        int run = 0;
        double quality = 0;
        double amplitude = 0;
        for (int i = 0; i < _openingCount; i++)
        {
            int index = (oldest + i) % OpeningCapacity;
            if (_openingAmplitude[index] >= threshold)
            {
                run++;
                quality += _openingSignalQuality[index];
                amplitude += _openingAmplitude[index];
                if (run >= OpeningMarkBlocks && quality / run >= OpeningMarkCoherence)
                {
                    first = i - run + 1;
                    markLevel = amplitude / run;
                    return true;
                }
            }
            else
            {
                run = 0;
                quality = 0;
                amplitude = 0;
            }
        }

        first = 0;
        markLevel = 0;
        return false;
    }

    private void ClearOpening()
    {
        _openingCount = 0;
        _openingCursor = 0;
    }

    private void Feed(bool tone, double blockMs, Action<MorseDecodedSymbol> output)
    {
        if (_narrow ? _matched.HasMeasurement : _threshold.HasMeasurement)
            _fsm.NoteSnr(SnrDb);
        // Include the analysis block midpoint. Omitting it at a one-block
        // smoothing length subtracts an extra quantum and overstates WPM.
        _fsm.NoteEdgeDelay(_narrow ? _detectorDelayMs : (_smoother.GroupDelayBlocks + 0.5) * blockMs);
        if (tone)
        {
            int integratedBlocks = (int)Math.Round(2 * _detectorDelayMs / blockMs + 1);
            double skew = _narrow
                ? (_fading ? _fadeThreshold.ExcessMarkBlocks(integratedBlocks) : 0)
                : _threshold.ExcessMarkBlocks(_smoother.Length);
            _fsm.NoteMarkSkew(skew * blockMs);
        }
        _fsm.NoteSignalQuality(_signalQuality, afterGlitch: !_narrow);
        _fsm.Process(tone, blockMs);
        while (_fsm.TryTake(out var symbol)) output(symbol);
    }

    public void BeginNextSender(bool keepEnvelope = false)
    {
        _timing.BeginReacquisition(allowFaster: true);
        if (keepEnvelope)
        {
            // A matched floor belongs to one sender. Re-measure it during
            // the quiet interval rather than retaining a frozen low rail.
            _matched.Reset();
            if (_narrow)
            {
                // The next sender opens again, so its first element can be
                // re-read once the new rails are measured.
                _open = false;
                _openRun = 0;
                _openingQuality = 0;
                ClearOpening();
            }
            ClearFade();
            _fsm.Reset();
            _snrSum = 0;
            _snrCount = 0;
        }
        else ClearEnvelope();
    }

    public void Reset()
    {
        _timing.Reset();
        ClearEnvelope();
    }

    private void ClearEnvelope()
    {
        _smoother.Reset();
        _threshold.Reset();
        _matched.Reset();
        ClearFade();
        _fsm.Reset();
        _openRun = 0;
        _openingQuality = 0;
        _open = false;
        _quietMs = 0;
        _snrSum = 0;
        _snrCount = 0;
        _raisedNoiseBlocks = 0;
        ClearOpening();
    }

    private void ClearFade()
    {
        _fadeThreshold.Reset();
        _fading = false;
        _markPeak = _previousPeak = _markQuality = 0;
        _markBlocks = 0;
    }

    public bool TryFlush(out MorseDecodedSymbol symbol) => _fsm.TryFlush(out symbol);
}
