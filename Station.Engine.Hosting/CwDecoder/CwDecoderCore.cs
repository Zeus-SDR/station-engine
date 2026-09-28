// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Runs the established Hann-bin envelope beside a coherent narrow envelope.
/// Each has its own clock, threshold and opening buffer. Choose once per send
/// from the measured SNR, so weak-signal integration cannot smear a strong
/// signal's edges or lose an opening character during selection.
/// </summary>
internal sealed class CwDecoderCore
{
    private readonly GoertzelDetector _detector;
    private readonly CoherentToneDetector _coherent = new();
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
    private bool _locked;
    private double _searchHalfWidthHz = GoertzelDetector.PitchSearchHalfWidthHz;
    internal const double OperatorRetargetResetHz = 50.0;
    private const double IdenticalRequestHz = 0.5;
    private int _choice;
    private int _lastChoice;
    private bool _transmitResume;
    private double _continuousToneMs;
    private bool _idle;
    private int EffectiveChoice => _choice == 0 ? _lastChoice : _choice;
    private CwEnvelopeDecoder Selected => EffectiveChoice == 2 ? _narrow : _wide;

    public CwDecoderCore(int sampleRateHz, double centerFrequencyHz)
    {
        SampleRateHz = sampleRateHz;
        _centerHz = centerFrequencyHz;
        _detector = new GoertzelDetector(sampleRateHz, centerFrequencyHz);
        // Nothing clicked yet: the pitch search.
        _detector.Retune(centerFrequencyHz, locked: false, GoertzelDetector.PitchSearchHalfWidthHz);
        _collectWide = symbol => Collect(_wideText, symbol);
        _collectNarrow = symbol => Collect(_narrowText, symbol);
    }

    public int SampleRateHz { get; }
    public double Wpm => Selected.Wpm;
    public double SnrDb => Selected.SnrDb;
    /// <summary>Audio tone being copied. Not a carrier the search has walked onto.</summary>
    internal double TrackedToneHz => EffectiveChoice == 2 ? _coherent.TrackedFrequencyHz : _detector.TrackedFrequencyHz;
    /// <summary>Bin the unlocked search is following.</summary>
    internal double SearchToneHz => _detector.SearchFrequencyHz;
    internal bool SearchLocked => _detector.SearchLocked;
    internal double SearchHalfWidthHz => _detector.SearchHalfWidthHz;

    /// <summary>Follow the radio pitch with nothing clicked: the pitch search.</summary>
    public void Retune(double centerFrequencyHz) =>
        Retune(centerFrequencyHz, locked: false, GoertzelDetector.PitchSearchHalfWidthHz);

    public void Retune(double centerFrequencyHz, bool locked, double searchHalfWidthHz) =>
        Retune(centerFrequencyHz, locked, searchHalfWidthHz, onDecoded: null);

    /// <summary>
    /// Move the detector. An operator retarget more than
    /// <see cref="OperatorRetargetResetHz"/> from the tone currently being
    /// decoded flushes any open character, then clears the envelope average,
    /// squelch, threshold, timing, and Morse state. That includes a click on
    /// the centre already stored after the search has acquired a different
    /// tone. A search step, a
    /// repeated request while that tone is already the one being copied, or
    /// locking that tone leaves the state alone.
    /// </summary>
    public void Retune(
        double centerFrequencyHz,
        bool locked,
        double searchHalfWidthHz,
        Action<MorseDecodedSymbol>? onDecoded)
    {
        // Compare with the tone being copied before Retune recentres the bank.
        // That tone is the decode bin. A carrier the search has walked onto
        // does not move it, and the walk is not a retune, so it cannot flush.
        // Locking the copied tone is the same station. A centre more than
        // OperatorRetargetResetHz from that tone is the operator changing
        // station, even when the number is the centre already stored.
        double copiedHz = TrackedToneHz;
        bool newStation = Math.Abs(centerFrequencyHz - copiedHz) > OperatorRetargetResetHz;
        bool sameRequest = locked == _locked
            && searchHalfWidthHz == _searchHalfWidthHz
            && Math.Abs(centerFrequencyHz - _centerHz) <= IdenticalRequestHz
            && !newStation;
        if (sameRequest) return;
        _centerHz = centerFrequencyHz;
        _locked = locked;
        _searchHalfWidthHz = searchHalfWidthHz;
        if (newStation)
        {
            while (_wide.TryFlush(out var symbol)) Collect(_wideText, symbol);
            while (_narrow.TryFlush(out var symbol)) Collect(_narrowText, symbol);
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
            if (_blockFill != _block.Length) continue;
            double blockMs = 1000.0 * _block.Length / SampleRateHz;
            double power = _detector.DetectPower(_block);
            _toneEvidence.Process(_block, _detector.TrackedFrequencyHz, SampleRateHz);
            // Resize only in a gap: both edges of a mark use one bandwidth.
            if (_narrow.QuietMs >= 40) _coherent.SetDit(_narrow.DitMs);
            // Choice is fixed until the next sender. Keep the inexpensive complex
            // sum for carrier-tail detection, but skip unused narrow decisions.
            bool narrowCandidate = _choice != 1;
            double narrowAmplitude = _coherent.Process(_block, _centerHz, SampleRateHz, measurePhase: narrowCandidate);
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
            _wide.Process(power > 1e-20 && double.IsFinite(power) ? Math.Sqrt(power) : 0,
                blockMs, _collectWide, _detector.HasCarrier ? 1 : _toneEvidence.PhaseCoherence);
            if (narrowCandidate && _coherent.IsReady)
                _narrow.Process(narrowAmplitude, blockMs, _collectNarrow,
                    _coherent.PhaseCoherence, _coherent.GroupDelayBlocks * blockMs);
            Choose(force: false);
            Drain(onDecoded);

            // A seven-dit word space plus integrated edge uncertainty is
            // still the same sender. Leave two dits of margin before
            // discarding its noise/fading calibration and timing evidence.
            double quiet = Selected.QuietMs;
            if (!_idle && _choice != 0 && quiet >= SenderGapMs(Selected))
            {
                _wide.BeginNextSender(keepEnvelope: true);
                _narrow.BeginNextSender(keepEnvelope: true);
                _wideText.Clear();
                _narrowText.Clear();
                _choice = 0;
                _idle = true;
            }
            else if (quiet < blockMs) _idle = false;
            _blockFill = 0;
        }
    }

    private static double SenderGapMs(CwEnvelopeDecoder decoder) => Math.Max(650, 9 * decoder.DitMs);

    private void Choose(bool force)
    {
        if (_choice != 0) return;
        if (_wide.ToneBlocks >= 12 && _wide.MeanSnrDb >= 13)
            _choice = 1;
        else if (force || _wideText.Count > 0 || _narrowText.Count > 0)
            _choice = _wide.MeanSnrDb >= 11.5 || _detector.HasCarrier || _narrow.ToneBlocks < 4 ? 1 : 2;
        if (_choice != 0) _lastChoice = _choice;
    }

    private static void Collect(Queue<MorseDecodedSymbol> queue, MorseDecodedSymbol symbol)
    {
        if (queue.Count == 128) queue.Dequeue();
        queue.Enqueue(symbol);
    }

    private void Drain(Action<MorseDecodedSymbol>? output)
    {
        if (_choice == 0) return;
        Queue<MorseDecodedSymbol> chosen = _choice == 2 ? _narrowText : _wideText;
        while (chosen.TryDequeue(out var symbol)) output?.Invoke(symbol);
        (_choice == 2 ? _wideText : _narrowText).Clear();
    }

    private void ResetEnvelopes()
    {
        _wide.Reset();
        _narrow.Reset();
        _wideText.Clear();
        _narrowText.Clear();
        _choice = 0;
        _lastChoice = 0;
        _idle = false;
    }

    public void Reset()
    {
        _blockFill = 0;
        _transmitResume = false;
        _continuousToneMs = 0;
        _detector.Reset();
        _coherent.Reset();
        _toneEvidence.Reset();
        ResetEnvelopes();
    }
}
