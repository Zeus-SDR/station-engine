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
            _openingQuality += signalQuality;
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
