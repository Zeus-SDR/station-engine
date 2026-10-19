// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
namespace Zeus.Server;
/// <summary>
/// Compares the established wide and short coherent paths with a bounded
/// 64 ms coherent hypothesis for slow keying with expanded marks. The slow
/// hypothesis needs mixed complete marks and repeated short gaps before its
/// joint clock/skew fit can replace uncommitted legacy output. Legacy timing,
/// frequency bounds and logical sender gaps keep their original schedule.
/// </summary>
internal sealed class CwDecoderCore
{
    private const int OpeningHistoryBlocks = 384; // 2.048 s of 256-sample blocks at 48 kHz.
    private const double FrequencyClusterToleranceHz = 3.125; // Quarter of a 12.5 Hz bank step.
    private readonly GoertzelDetector _detector;
    private readonly CoherentToneDetector _coherent = new();
    private readonly CoherentToneDetector _slowCoherent = new(fixedBlocks: 12);
    private readonly CwEnvelopeDecoder _slow = new(narrow: true, jointCandidate: true);
    private readonly Queue<MorseDecodedSymbol> _slowText = new(128);
    private readonly float[] _slowOpening = new float[OpeningHistoryBlocks * GoertzelDetector.BlockSize];
    private int _slowOpeningBlocks;
    private bool _slowOpeningExpired;
    private double _slowHz;
    private double _slowCandidateHz;
    private int _slowCandidateBlocks;
    private readonly Action<MorseDecodedSymbol> _collectSlow;
    private readonly CoherentToneDetector _toneEvidence = new(windowed: true);
    private readonly CwEnvelopeDecoder _wide = new(narrow: false);
    private readonly CwEnvelopeDecoder _narrow = new(narrow: true);
    private readonly Queue<MorseDecodedSymbol> _wideText = new(128);
    private readonly Queue<MorseDecodedSymbol> _narrowText = new(128);
    private readonly Action<MorseDecodedSymbol> _collectWide;
    private readonly Action<MorseDecodedSymbol> _collectNarrow;
    private readonly float[] _block = new float[GoertzelDetector.BlockSize];
    private int _blockFill;
    private double _centerHz;
    private double _narrowFrequencyHz;
    private double _narrowCandidateHz;
    private int _narrowCandidateBlocks;
    private int _narrowUnconfirmedBlocks;
    private const double NarrowConfirmHz = 6.25; // Half the 12.5 Hz tone-evidence offset step.
    private bool _locked;
    private double _searchHalfWidthHz = GoertzelDetector.PitchSearchHalfWidthHz;
    internal const double OperatorRetargetResetHz = 50.0;
    private const double IdenticalRequestHz = 0.5;
    private int _choice;
    private int _legacyChoice;
    private int _legacyWaitBlocks;
    private bool _legacyIdle;
    private int _resumeEvidenceRun;
    private int _resumeAge;
    private double _resumeObservedHz;
    private int _lastChoice;
    private bool _transmitResume;
    private double _continuousToneMs;
    private bool _idle;
    private int EffectiveChoice => _choice == 0 ? _lastChoice : _choice;
    private CwEnvelopeDecoder Selected => EffectiveChoice == 3 ? _slow : EffectiveChoice == 2 ? _narrow : _wide;

    public CwDecoderCore(int sampleRateHz, double centerFrequencyHz)
    {
        SampleRateHz = sampleRateHz;
        _centerHz = centerFrequencyHz;
        _detector = new GoertzelDetector(sampleRateHz, centerFrequencyHz);
        // Nothing clicked yet: the pitch search.
        _detector.Retune(centerFrequencyHz, locked: false, GoertzelDetector.PitchSearchHalfWidthHz);
        _collectWide = symbol => Collect(_wideText, symbol);
        _collectNarrow = symbol => Collect(_narrowText, symbol);
        _collectSlow = symbol => Collect(_slowText, symbol);
    }

    public int SampleRateHz { get; }
    public double Wpm => Selected.Wpm;
    public double SnrDb => Selected.SnrDb;
    /// <summary>Audio tone being copied. Not a carrier the search has walked onto.</summary>
    // The selected slow station is held through key-up. A phase estimate on
    // receiver tails must not move its retarget identity or reported tone.
    internal double TrackedToneHz => EffectiveChoice == 3
        ? (_locked || _slowHz <= 0 ? _centerHz : _slowHz)
        : EffectiveChoice == 2 ? _coherent.TrackedFrequencyHz : _detector.TrackedFrequencyHz;
    /// <summary>Bin the unlocked search is following.</summary>
    internal double SearchToneHz => _detector.SearchFrequencyHz;
    internal bool SearchLocked => _detector.SearchLocked;
    internal double SearchHalfWidthHz => _detector.SearchHalfWidthHz;

    /// <summary>Follow the radio pitch with nothing clicked: the pitch search.</summary>
    public void Retune(double centerFrequencyHz) => Retune(centerFrequencyHz, locked: false, GoertzelDetector.PitchSearchHalfWidthHz);
    public void Retune(double centerFrequencyHz, bool locked, double searchHalfWidthHz) => Retune(centerFrequencyHz, locked, searchHalfWidthHz, onDecoded: null);
    /// <summary>
    /// Move the detector. An operator retarget more than
    /// <see cref = "OperatorRetargetResetHz"/> from the tone currently being
    /// decoded flushes any open character, then clears the envelope average,
    /// squelch, threshold, timing, and Morse state. That includes a click on
    /// the centre already stored after the search has acquired a different
    /// tone. A search step, a
    /// repeated request while that tone is already the one being copied, or
    /// locking that tone leaves the state alone.
    /// </summary>
    public void Retune(double centerFrequencyHz, bool locked, double searchHalfWidthHz, Action<MorseDecodedSymbol>? onDecoded)
    {
        // Compare with the tone being copied before Retune recentres the bank.
        // That tone is the decode bin. A carrier the search has walked onto
        // does not move it, and the walk is not a retune, so it cannot flush.
        // Locking the copied tone is the same station. A centre more than
        // OperatorRetargetResetHz from that tone is the operator changing
        // station, even when the number is the centre already stored.
        double copiedHz = TrackedToneHz;
        bool newStation = Math.Abs(centerFrequencyHz - copiedHz) > OperatorRetargetResetHz;
        bool sameRequest = locked == _locked && searchHalfWidthHz == _searchHalfWidthHz && Math.Abs(centerFrequencyHz - _centerHz) <= IdenticalRequestHz && !newStation;
        if (sameRequest)
            return;
        _centerHz = centerFrequencyHz;
        _locked = locked;
        _searchHalfWidthHz = searchHalfWidthHz;
        // A small retarget preserves the character and its clock, but its
        // narrower search must not continue integrating an excluded tone.
        if (_narrowFrequencyHz > 0 && Math.Abs(_narrowFrequencyHz - _centerHz) > _searchHalfWidthHz)
            ClearNarrowFrequency();
        else if (Math.Abs(_narrowCandidateHz - _centerHz) > _searchHalfWidthHz)
        {
            _narrowCandidateHz = 0;
            _narrowCandidateBlocks = 0;
        }

        double slowSpan = locked ? 0 : Math.Max(0, searchHalfWidthHz);
        if (!newStation && ((_slowHz > 0 && Math.Abs(_slowHz - _centerHz) > slowSpan)
            || (_slowCandidateHz > 0 && Math.Abs(_slowCandidateHz - _centerHz) > slowSpan)))
        {
            _slowHz = _slowCandidateHz = 0;
            _slowCandidateBlocks = 0;
            _slowOpeningBlocks = 0;
            _slowOpeningExpired = false;
            _slowCoherent.Reset();
            _resumeEvidenceRun = _resumeAge = 0;
            if (_choice == 3 || (_choice == 0 && _lastChoice == 3))
            {
                ResetEnvelopes();
                _coherent.Reset();
            }
            else if (_choice == 0)
            {
                _slow.Reset();
                _slowText.Clear();
            }
        }

        if (newStation)
        {
            while (_wide.TryFlush(out var symbol))
                Collect(_wideText, symbol);
            while (_narrow.TryFlush(out var symbol))
                Collect(_narrowText, symbol);
            while (_slow.TryFlush(out var slowSymbol))
                Collect(_slowText, slowSymbol);
            Choose(force: true);
            Drain(onDecoded);
            Reset();
        }

        _detector.Retune(centerFrequencyHz, locked, searchHalfWidthHz);
    }

    internal void NoteOwnTransmit()
    {
        Reset();
        _transmitResume = true;
    }

    internal bool ConsumeTransmitResume()
    {
        bool resume = _transmitResume;
        _transmitResume = false;
        return resume;
    }

    public void Process(ReadOnlySpan<float> samples, Action<MorseDecodedSymbol> onDecoded)
    {
        int offset = 0;
        while (offset < samples.Length)
        {
            int copy = Math.Min(_block.Length - _blockFill, samples.Length - offset);
            samples.Slice(offset, copy).CopyTo(_block.AsSpan(_blockFill, copy));
            _blockFill += copy;
            offset += copy;
            if (_blockFill != _block.Length)
                continue;
            double blockMs = 1000.0 * _block.Length / SampleRateHz;
            double power = _detector.DetectPower(_block);
            _toneEvidence.Process(_block, _detector.TrackedFrequencyHz, SampleRateHz);
            ObserveResumeEvidence();
            // Resize only in a gap: both edges of a mark use one bandwidth.
            if (_narrow.QuietMs >= 40)
                _coherent.SetDit(_narrow.DitMs);
            // Choice is fixed until the next sender. Keep the inexpensive complex
            // sum for carrier-tail detection, but skip unused narrow decisions.
            bool narrowCandidate = _legacyChoice != 1;
            double narrowAmplitude = _coherent.Process(_block, NarrowFrequency(), SampleRateHz, measurePhase: narrowCandidate);
            // A long unkeyed sidetone/carrier is not a fist. Its falling edge
            // starts fresh acquisition, regardless of received amplitude.
            if (_coherent.Coherence >= 0.95)
                _continuousToneMs += blockMs;
            else
            {
                if (_continuousToneMs >= 1200)
                {
                    ResetEnvelopes();
                    _detector.Reset();
                }

                _continuousToneMs = 0;
            }

            _wide.Process(power > 1e-20 && double.IsFinite(power) ? Math.Sqrt(power) : 0, blockMs, _collectWide, _detector.HasCarrier ? 1 : _toneEvidence.PhaseCoherence);
            if (narrowCandidate && _coherent.IsReady)
                _narrow.Process(narrowAmplitude, blockMs, _collectNarrow, _coherent.PhaseCoherence, _coherent.GroupDelayBlocks * blockMs);
            if (_choice == 0 || _choice == 3)
            {
                if (_slowHz == 0 && !_slowOpeningExpired)
                {
                    if (_slowOpeningBlocks < OpeningHistoryBlocks)
                    {
                        _block.CopyTo(_slowOpening.AsSpan(_slowOpeningBlocks * 256, 256));
                        _slowOpeningBlocks++;
                    }
                    else
                    {
                        _slowOpeningExpired = true;
                        _slowOpeningBlocks = 0;
                    }
                }

                bool replayed = false;
                double slowAmplitude = _slowCoherent.Process(_block, _locked || _slowHz == 0 ? _centerHz : _slowHz, SampleRateHz);
                double hz = _slowCoherent.TrackedFrequencyHz;
                if (!_locked && _choice == 0 && _slowCoherent.PhaseCoherence >= .95 && Math.Abs(hz - _centerHz) <= _searchHalfWidthHz)
                {
                    if (Math.Abs(hz - _slowCandidateHz) < FrequencyClusterToleranceHz)
                    {
                        _slowCandidateBlocks++;
                        _slowCandidateHz = .75 * _slowCandidateHz + .25 * hz;
                    }
                    else
                    {
                        _slowCandidateHz = hz;
                        _slowCandidateBlocks = 1;
                    }

                    if (_slowCandidateBlocks >= 8 && _slowHz == 0)
                    {
                        _slowText.Clear();
                        _slow.Reset();
                        _slowCoherent.Reset();
                        _slowHz = _slowCandidateHz;
                        for (int j = 0; j < _slowOpeningBlocks; j++)
                        {
                            double a = _slowCoherent.Process(_slowOpening.AsSpan(j * 256, 256), _slowHz, SampleRateHz);
                            if (_slowCoherent.IsReady)
                                _slow.Process(a, blockMs, _collectSlow, _slowCoherent.PhaseCoherence, _slowCoherent.GroupDelayBlocks * blockMs);
                        }

                        replayed = !_slowOpeningExpired;
                        _slowOpeningBlocks = 0;
                        if (!replayed)
                            slowAmplitude = _slowCoherent.Process(_block, _slowHz, SampleRateHz);
                    }
                }
                else
                    _slowCandidateBlocks = 0;
                if (!replayed && _slowCoherent.IsReady)
                    _slow.Process(slowAmplitude, blockMs, _collectSlow, _slowCoherent.PhaseCoherence, _slowCoherent.GroupDelayBlocks * blockMs);
            }

            Choose(force: false);
            Drain(onDecoded);
            // A seven-dit word space plus integrated edge uncertainty is
            // still the same sender. Leave two dits of margin before
            // discarding its noise/fading calibration and timing evidence.
            CwEnvelopeDecoder legacy = _legacyChoice == 2 ? _narrow : _wide;
            double legacyQuiet = legacy.QuietMs;
            if (!_legacyIdle && _legacyChoice != 0 && legacyQuiet >= SenderGapMs(legacy))
            {
                if (_choice == 0 && (_legacyChoice == 1 ? _wideText.Count : _narrowText.Count) > 0)
                {
                    _choice = _legacyChoice;
                    _lastChoice = _choice;
                    Drain(onDecoded);
                }

                if (_choice == 1 || _choice == 2)
                {
                    _slow.Reset();
                    _slowCoherent.Reset();
                    _slowHz = _slowCandidateHz = 0;
                    _slowCandidateBlocks = 0;
                    _slowOpeningBlocks = 0;
                    _slowText.Clear();
                    _slowOpeningExpired = false;
                }

                _wide.BeginNextSender(keepEnvelope: true);
                _narrow.BeginNextSender(keepEnvelope: true);
                _wideText.Clear();
                _narrowText.Clear();
                _legacyChoice = 0;
                _legacyWaitBlocks = 0;
                _legacyIdle = true;
                if (_choice == 1 || _choice == 2)
                {
                    _choice = 0;
                    _idle = true;
                    _resumeEvidenceRun = _resumeAge = 0;
                }

                ClearNarrowFrequency();
            }
            else if (legacyQuiet < blockMs)
                _legacyIdle = false;
            double quiet = _slow.QuietMs;
            if (!_idle && _choice == 3 && quiet >= Math.Max(650, 12 * _slow.DitMs))
            {
                _slow.BeginNextSender(keepEnvelope: true);
                _slowCandidateBlocks = 0;
                _slowOpeningBlocks = 0;
                _slowText.Clear();
                _slowOpeningExpired = false;
                _choice = 0;
                _idle = true;
                _resumeEvidenceRun = _resumeAge = 0;
            }
            else if (quiet < blockMs)
                _idle = false;
            _blockFill = 0;
        }
    }

    private static double SenderGapMs(CwEnvelopeDecoder decoder) => Math.Max(650, 9 * decoder.DitMs);
    // Only fresh, supported carrier evidence starts this publication deadline. Quiet
    // cannot select a path. A different station can use the bounded opening quiet
    // rail without being compared with the previous station's amplitude.
    private void ObserveResumeEvidence()
    {
        if (_lastChoice == 3 && _choice == 0)
        {
            if (_resumeAge > 0)
                _resumeAge++;
            else
            {
                // Process returns the requested-bin amplitude; use the best aligned bank
                // here. Multiply by 2N/(N-1) to remove the block Hann coherent gain.
                    bool fresh = false;
                    if (_toneEvidence.PhaseCoherence >= .95 && _slow.NoiseFloor > 1e-10)
                    {
                        double observedHz = _toneEvidence.TrackedFrequencyHz;
                        bool differentStation = _slowHz > 0
                            && Math.Abs(observedHz - _slowHz) > OperatorRetargetResetHz;
                        double minimumAmplitude = differentStation
                            ? 3.5 * Math.Max(_slow.NoiseFloor, _slow.OpeningQuietNoise)
                            : Math.Max(3.5 * _slow.NoiseFloor, 7 * _wide.NoiseFloor);
                        double alignedAmplitude = _toneEvidence.TrackedAmplitude
                            * (2d * _block.Length / (_block.Length - 1));
                        fresh = alignedAmplitude >= minimumAmplitude
                            && Math.Abs(observedHz - _centerHz) <= _searchHalfWidthHz;
                    }
                _resumeEvidenceRun = fresh ? (Math.Abs(_toneEvidence.TrackedFrequencyHz - _resumeObservedHz) < FrequencyClusterToleranceHz ? _resumeEvidenceRun + 1 : 1) : 0;
                _resumeObservedHz = _toneEvidence.TrackedFrequencyHz;
                if (_resumeEvidenceRun >= 4)
                {
                    _resumeAge = 1;
                    if (_slowHz > 0 && Math.Abs(_resumeObservedHz - _slowHz) > OperatorRetargetResetHz)
                    {
                        _lastChoice = 0;
                        _slow.Reset();
                        _slowCoherent.Reset();
                        _slowHz = _slowCandidateHz = 0;
                        _slowCandidateBlocks = 0;
                        _slowOpeningBlocks = 0;
                        _slowText.Clear();
                        _slowOpeningExpired = false;
                        _legacyWaitBlocks = 0;
                        _resumeEvidenceRun = _resumeAge = 0;
                    }
                }
            }
        }
    }

    private double NarrowFrequency()
    {
        double hz = _toneEvidence.TrackedFrequencyHz;
        // The narrow integration loses a weak tone only 25 Hz from its centre.
        // Require a stable phase slope before moving it: following each noisy
        // peak raises the envelope floor and destroys the integration gain.
        // Four supported blocks span 21 ms at 48 kHz. Hold through key-up and
        // fix the frequency once this sender's detector has been selected.
        if (!_locked && _legacyChoice == 0 && _toneEvidence.PhaseCoherence >= 0.95 && Math.Abs(hz - _centerHz) >= 25 && Math.Abs(hz - _centerHz) <= _searchHalfWidthHz)
        {
            if (Math.Abs(hz - _narrowCandidateHz) < IdenticalRequestHz)
                _narrowCandidateBlocks = Math.Min(4, _narrowCandidateBlocks + 1);
            else
            {
                _narrowCandidateHz = hz;
                _narrowCandidateBlocks = 1;
            }

            if (_narrowCandidateBlocks >= 4)
                _narrowFrequencyHz = hz;
        }
        else
            _narrowCandidateBlocks = 0;
        // Before a sender is selected, the hold covers that sender's key-up
        // only. A fix with no supported evidence at its tone for a full sender
        // gap belongs to no sender: often a four-block noise run in a long
        // quiet lead. Holding it would integrate the next caller off its tone.
        if (!_locked && _legacyChoice == 0 && _narrowFrequencyHz > 0)
        {
            if (_toneEvidence.PhaseCoherence >= 0.95 && Math.Abs(hz - _narrowFrequencyHz) < NarrowConfirmHz)
                _narrowUnconfirmedBlocks = 0;
            else if (++_narrowUnconfirmedBlocks * 1000.0 * _block.Length / SampleRateHz >= SenderGapMs(_narrow))
                ClearNarrowFrequency();
        }

        return !_locked && _narrowFrequencyHz > 0 && Math.Abs(_narrowFrequencyHz - _centerHz) <= _searchHalfWidthHz ? _narrowFrequencyHz : _centerHz;
    }

    private void ClearNarrowFrequency()
    {
        _narrowFrequencyHz = 0;
        _narrowUnconfirmedBlocks = 0;
        _narrowCandidateHz = 0;
        _narrowCandidateBlocks = 0;
    }

    private void Choose(bool force)
    {
        if (_choice != 0)
            return;
        if (_legacyChoice == 0)
        {
            if (_wide.ToneBlocks >= 12 && _wide.MeanSnrDb >= 13)
                _legacyChoice = 1;
            else if (force || _wideText.Count > 0 || _narrowText.Count > 0)
                _legacyChoice = _wide.MeanSnrDb >= 11.5 || _detector.HasCarrier || _narrow.ToneBlocks < 4 ? 1 : 2;
        }

        if (_legacyChoice != 0)
            _legacyWaitBlocks++;
        double quantum = 256000d / SampleRateHz;
        int marks = Math.Max(_wide.FitnessMarks, Math.Max(_narrow.FitnessMarks, _slow.FitnessMarks));
        bool quiet = Math.Max(_wide.QuietMs, _narrow.QuietMs) >= Math.Max(200, 3 * Math.Min(_wide.DitMs, _narrow.DitMs));
        double slowFit = _slow.TimingFit(quantum, out bool mixed);
        double wFit = _wide.TimingFit(quantum, out bool wm), nFit = _narrow.TimingFit(quantum, out bool nm);
        double fastFit = Math.Min(wm ? wFit : double.PositiveInfinity, nm ? nFit : double.PositiveInfinity);
        if (_slow.JointSkew > 2 * quantum && _slow.ClockLocked && _slow.DitMs >= 50 && mixed && slowFit < .4 && slowFit + .1 < fastFit && slowFit < .7 * fastFit)
        {
            _choice = _lastChoice = 3;
            return;
        }

        if (!force && _wide.FitnessMarks == 0 && _narrow.FitnessMarks == 0 && _wideText.Count == 0 && _narrowText.Count == 0)
            return;
        bool shortFallback = _lastChoice != 3 && Math.Min(_wide.QuietMs, Math.Min(_narrow.QuietMs, _slow.QuietMs)) >= 650 && (_wideText.Count > 0 || _narrowText.Count > 0) && !_slow.ClockLocked;
        bool singleClusterFallback = _lastChoice != 3
            && (_wideText.Count > 0 || _narrowText.Count > 0)
            && _slow.SingleMarkCluster && !_slow.HasOpenLongMark;
        bool insideEvidenceBudget = _lastChoice == 3
            ? _resumeAge == 0 || _resumeAge < OpeningHistoryBlocks
            : _legacyWaitBlocks < OpeningHistoryBlocks;
        bool negligibleSkew = _slow.ClockLocked && Math.Abs(_slow.JointSkew) <= 2 * quantum;
        bool possibleSlow = _slow.HasOpenLongMark || _slow.MayBeSlow
            || (_lastChoice == 3 && fastFit > .2 && !_narrow.StrongShortMark);
        if (!force && !singleClusterFallback && !_slow.SupportedFastClock(quantum)
            && insideEvidenceBudget && !negligibleSkew && !shortFallback
            && fastFit > .2 && possibleSlow)
            return;
        if (_choice != 0)
            return;
        _choice = _legacyChoice;
        if (_choice != 0)
            _lastChoice = _choice;
    }

    private static void Collect(Queue<MorseDecodedSymbol> queue, MorseDecodedSymbol symbol)
    {
        if (queue.Count == 128)
            queue.Dequeue();
        queue.Enqueue(symbol);
    }

    private void Drain(Action<MorseDecodedSymbol>? output)
    {
        if (_choice == 0)
            return;
        Queue<MorseDecodedSymbol> chosen = _choice == 3 ? _slowText : _choice == 2 ? _narrowText : _wideText;
        while (chosen.TryDequeue(out var symbol))
            output?.Invoke(symbol);
        if (_choice != 1)
            _wideText.Clear();
        if (_choice != 2)
            _narrowText.Clear();
        if (_choice != 3)
            _slowText.Clear();
    }

    private void ResetEnvelopes()
    {
        ClearNarrowFrequency();
        _wide.Reset();
        _narrow.Reset();
        _slow.Reset();
        _slowHz = _slowCandidateHz = 0;
        _slowCandidateBlocks = 0;
        _slowText.Clear();
        _wideText.Clear();
        _narrowText.Clear();
        _choice = 0;
        _legacyChoice = 0;
        _legacyWaitBlocks = 0;
        _lastChoice = 0;
        _resumeEvidenceRun = _resumeAge = 0;
        _slowOpeningBlocks = 0;
        _slowOpeningExpired = false;
        _idle = false;
        _legacyIdle = false;
    }

    public void Reset()
    {
        _blockFill = 0;
        _transmitResume = false;
        _continuousToneMs = 0;
        _detector.Reset();
        _coherent.Reset();
        _slowCoherent.Reset();
        _toneEvidence.Reset();
        ResetEnvelopes();
    }
}
