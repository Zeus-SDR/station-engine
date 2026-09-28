// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

internal readonly record struct MorseDecodedSymbol(string Text, float Confidence);

/// <summary>Converts thresholded key timing into International Morse text.</summary>
internal sealed class MorseFsm
{
    public const string UnknownPlaceholder = "?";

    // AR, BT, and KN share their wire patterns with +, =, and (. Those
    // conventional glyphs preserve round-trippable punctuation while also
    // rendering the corresponding prosigns. SK is unambiguous and is named.
    private static readonly IReadOnlyDictionary<string, string> Table =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".-"]="A", ["-..."]="B", ["-.-."]="C", ["-.."]="D", ["."]="E",
            ["..-."]="F", ["--."]="G", ["...."]="H", [".."]="I", [".---"]="J",
            ["-.-"]="K", [".-.."]="L", ["--"]="M", ["-."]="N", ["---"]="O",
            [".--."]="P", ["--.-"]="Q", [".-."]="R", ["..."]="S", ["-"]="T",
            ["..-"]="U", ["...-"]="V", [".--"]="W", ["-..-"]="X", ["-.--"]="Y", ["--.."]="Z",
            ["-----"]="0", [".----"]="1", ["..---"]="2", ["...--"]="3", ["....-"]="4",
            ["....."]="5", ["-...."]="6", ["--..."]="7", ["---.."]="8", ["----."]="9",
            [".-.-.-"]=".", ["--..--"]=",", ["..--.."]="?", ["-..-."]="/", [".--.-."]="@",
            ["-...-"]="=", [".-.-."]="+", ["..--.-"]="_", [".----."]="'", ["-.--.-"]=")",
            ["-.--."]="(", ["-.-.--"]="!", ["---..."]=":", ["-.-.-."]=";", [".-..-."]="\"",
            ["...-.-"]="<SK>"
        };

    // Runs held until the dit estimate locks, then replayed. A cold 20 WPM
    // estimate mis-reads a 12 WPM dit as a dah and misses a 30 WPM letter
    // gap; the opening characters are still in this buffer when it locks.
    private const int RunCapacity = 96;
    private const int PendingCapacity = 128;
    // Below this, a lone E, T, or I between word gaps is noise, not a letter.
    private const double FragileSnrDb = 8.0;
    // A locked dah longer than this is not the fist we locked. It may be a
    // slower sender, or one piece of carrier.
    private const double LongMarkDits = 6.0;
    // A 5 WPM dah is 720 ms. Past this, one mark is a carrier. 1.2 s still
    // sits under the 1.5 s lone tone that must not print.
    private const double SlowCarrierMs = 1200;
    // Three keyed marks, each longer than the locked fist, are a new sender.
    // One mark is not: a carrier has no key-up between elements.
    private const int SlowReacquireMarks = 3;
    // After an over-long pre-lock mark, a fragment that follows inside this
    // gap is the same carrier. A real pause is already longer: an element
    // space at 20 WPM is 60 ms. Four dits at 5 WPM (960 ms) held the carrier
    // open through a normal word space, so the next transmission never printed.
    private const double FragmentGapMs = 50;

    private double _signalQuality = 1;
    private double _qualitySum;
    private int _qualityCount;
    private int _supportedMarks;
    private double _supportSum;
    private int _readyCount;
    private bool _qualityWordGap;
    private bool _strongSupport;
    private bool _qualityAfterGlitch;
    internal bool HasCoherentSupport => _strongSupport;
    public void NoteSignalQuality(double quality, bool afterGlitch = false)
    {
        _signalQuality = quality;
        _qualityAfterGlitch = afterGlitch;
    }

    private readonly MorseTimingEstimator _timing;
    private readonly char[] _pattern = new char[8];
    private readonly Run[] _runs = new Run[RunCapacity];
    private readonly MorseDecodedSymbol[] _pending = new MorseDecodedSymbol[PendingCapacity];
    private int _patternLength;
    private int _runCount;
    private int _bufferedMarks;
    private int _pendingHead;
    private int _pendingCount;
    private bool _initialized;
    private bool _live;
    private bool _seenMark;
    private bool _filterReady;
    private bool _cleanTone;
    private int _disagree;
    private bool _tone;
    private double _durationMs;
    private double _fitSum;
    private int _fitCount;
    private bool _letterEmitted;
    private bool _wordEmitted;
    private double _quantumMs;
    // No measurement yet. A clean fixture that never reports an SNR is not
    // a 0 dB tone; the fragile-letter gate must not eat it.
    private double _snrDb = double.PositiveInfinity;
    private double _pendingEdgeDelayMs;
    private double _riseDelayMs;
    private double _fallDelayMs;
    private double _markSkewMs;
    private int _charsInWord;
    private MorseDecodedSymbol? _held;
    private bool _carrierEcho;
    private bool _holdingSlow;
    private int _slowMarks;

    private readonly record struct Run(bool Tone, double Ms);

    public MorseFsm(MorseTimingEstimator timing) => _timing = timing;

    internal static IReadOnlyDictionary<string, string> CharacterTable => Table;

    internal static string DecodePattern(string pattern) =>
        Table.TryGetValue(pattern, out string? text) ? text : UnknownPlaceholder;

    public void NoteSnr(double snrDb) => _snrDb = snrDb;

    /// <summary>
    /// Group delay of the envelope average, in milliseconds. Equal delay on
    /// both edges leaves the mark length alone; a resize between the edges
    /// is taken back out of the length.
    /// </summary>
    public void NoteEdgeDelay(double delayMs)
    {
        if (!double.IsFinite(delayMs) || delayMs < 0) delayMs = 0;
        _pendingEdgeDelayMs = delayMs;
    }

    /// <summary>
    /// Extra length the key decision adds on top of the smoother's group
    /// delay, in milliseconds. Subtracted from the mark. Zero leaves the
    /// delay path alone.
    /// </summary>
    public void NoteMarkSkew(double skewMs)
    {
        if (!double.IsFinite(skewMs) || skewMs < 0) skewMs = 0;
        _markSkewMs = skewMs;
    }

    public void Process(bool rawTone, double blockDurationMs)
    {
        _quantumMs = blockDurationMs;
        if (!rawTone && !_cleanTone && !_tone)
        {
            _qualitySum = 0;
            _qualityCount = 0;
        }
        if (rawTone && !_qualityAfterGlitch && double.IsFinite(_signalQuality))
        {
            _qualitySum += _signalQuality;
            _qualityCount++;
        }
        // Estimator was trained before any audio (tests, or a later session
        // that reset only the tone state). Decode live; there is nothing to replay.
        if (!_live && _runCount == 0 && !_seenMark && _timing.IsLocked)
            _live = true;

        bool tone = FilterGlitch(rawTone);
        if (tone && _qualityAfterGlitch && double.IsFinite(_signalQuality))
        {
            _qualitySum += _signalQuality;
            _qualityCount++;
        }
        if (!_initialized)
        {
            _initialized = true;
            _tone = tone;
            _durationMs = blockDurationMs;
            return;
        }

        if (tone == _tone)
        {
            _durationMs += blockDurationMs;
            if (!_tone)
            {
                if (_carrierEcho && _durationMs >= FragmentGapMs)
                    _carrierEcho = false;
                if (!_live) MaybeForceLock();
                if (_live) TryEmitGap();
                if (!_qualityWordGap && _durationMs >= _timing.WordGapThresholdMs + 2 * _quantumMs)
                {
                    _qualityWordGap = true;
                    if (!_strongSupport)
                    {
                        // An unconfirmed crash must not wait in the replay
                        // buffer and escape when a real station arrives.
                        _pendingCount = _readyCount;
                        _runCount = _bufferedMarks = 0;
                        _seenMark = false;
                        _live = false;
                        _held = null;
                        ClearPartial();
                        _timing.BeginReacquisition(allowFaster: true);
                    }
                    _strongSupport = false;
                    _supportedMarks = 0;
                    _supportSum = 0;
                }
            }
            return;
        }

        if (_tone)
        {
            _fallDelayMs = _pendingEdgeDelayMs;
            CloseMark();
            _tone = false;
            _durationMs = blockDurationMs;
            _letterEmitted = false;
            _wordEmitted = false;
            return;
        }

        _qualityWordGap = false;
        CloseSpace();
        _riseDelayMs = _pendingEdgeDelayMs;
        if (_live) FlushHeld();
        _tone = true;
        _durationMs = blockDurationMs;
        _letterEmitted = false;
        _wordEmitted = false;
    }

    public bool TryTake(out MorseDecodedSymbol symbol)
    {
        if (_pendingCount == 0 || _readyCount == 0)
        {
            symbol = default;
            return false;
        }

        symbol = _pending[_pendingHead];
        _pendingHead = (_pendingHead + 1) % _pending.Length;
        _pendingCount--;
        _readyCount--;
        return true;
    }

    /// <summary>
    /// Close what is still open and hand it out, one symbol per call. The
    /// operator has moved to another station, so no later gap will end it.
    /// Marks still buffered before the dit locked are replayed on the
    /// current estimate, then the open character is decoded. A lone E, T,
    /// or I on a weak tone ends its word here and is dropped, as a word
    /// gap would drop it. Returns false once nothing is left.
    /// </summary>
    internal bool TryFlush(out MorseDecodedSymbol symbol)
    {
        if (_holdingSlow) AbandonSlowCandidate();
        if (!_live && _bufferedMarks > 0)
        {
            if (_bufferedMarks < 3) SeedBufferedPair();
            Replay();
        }

        if (_patternLength > 0)
        {
            string text = DecodePattern(new string(_pattern, 0, _patternLength));
            float confidence = ConfidenceFor(text);
            ClearPartial();
            _letterEmitted = true;
            if (!(IsFragile(text) && _snrDb < FragileSnrDb && _charsInWord == 0))
            {
                Enqueue(text, confidence);
                _charsInWord++;
            }
        }

        _held = null;
        return TryTake(out symbol);
    }

    public void Reset()
    {
        _qualitySum = 0;
        _qualityCount = 0;
        _signalQuality = 1;
        _supportedMarks = 0;
        _supportSum = 0;
        _qualityWordGap = false;
        _readyCount = 0;
        _strongSupport = false;
        _patternLength = 0;
        _runCount = 0;
        _bufferedMarks = 0;
        _pendingHead = 0;
        _pendingCount = 0;
        _initialized = false;
        _live = false;
        _seenMark = false;
        _filterReady = false;
        _cleanTone = false;
        _disagree = 0;
        _tone = false;
        _durationMs = 0;
        _fitSum = 0;
        _fitCount = 0;
        _letterEmitted = false;
        _wordEmitted = false;
        _quantumMs = 0;
        _snrDb = double.PositiveInfinity;
        _pendingEdgeDelayMs = 0;
        _riseDelayMs = 0;
        _fallDelayMs = 0;
        _markSkewMs = 0;
        _charsInWord = 0;
        _held = null;
        _carrierEcho = false;
        _holdingSlow = false;
        _slowMarks = 0;
    }

    // Both edges wait out the glitch, so a real element keeps its length
    // and a click or a one-block dropout never becomes an element.
    private bool FilterGlitch(bool tone)
    {
        double limit = GlitchLimitMs();
        if (!_filterReady)
        {
            _filterReady = true;
            _cleanTone = tone;
            _disagree = 0;
            return tone;
        }

        if (tone == _cleanTone)
        {
            _disagree = 0;
            return _cleanTone;
        }

        _disagree++;
        if (_disagree * _quantumMs >= limit)
        {
            _cleanTone = tone;
            _disagree = 0;
        }

        return _cleanTone;
    }

    // Before a slow mark has been seen, the cold glitch is 0.3 of a 20 WPM
    // dit: about four blocks, longer than the gap inside a 50 WPM character.
    // Cap it at three blocks so that gap splits elements. Two blocks is
    // still a spike and does not.
    private double GlitchLimitMs()
    {
        double limit = _timing.GlitchMs;
        if (_timing.FastAcquisition && _quantumMs > 0)
        {
            double threeBlocks = 3.0 * _quantumMs;
            if (limit > threeBlocks) limit = threeBlocks;
        }

        return limit;
    }

    private void CloseMark()
    {
        double adjusted = AdjustedMark(_durationMs);
        double quality = _qualityCount == 0 ? 0 : _qualitySum / _qualityCount;
        _qualitySum = 0;
        _qualityCount = 0;
        // Random band-limited bursts can have a Morse-shaped envelope. They
        // do not sustain the tone's phase. Reject them before clock training;
        // short fragments wait for three supported marks or a very clean tone.
        if (quality < (_qualityAfterGlitch ? 0.75 : 0.60))
        {
            _supportedMarks = 0;
            _supportSum = 0;
            _pendingCount = _readyCount;
            ClearPartial();
            _held = null;
            return;
        }
        _supportedMarks++;
        _supportSum += quality;
        if (quality >= 0.995 || (quality >= 0.93 && _snrDb >= 15)
            || (_supportedMarks >= 3 && _supportSum / _supportedMarks >= 0.85))
            _strongSupport = true;
        if (_strongSupport) _readyCount = _pendingCount;
        if (!_timing.IsLocked)
        {
            if (adjusted >= _timing.CarrierMs || IsCarrierFragment(adjusted))
            {
                // Ten dits with no keying is a carrier. Before lock that ceiling
                // is ten dits at 5 WPM, so a slow dah is kept, but a lone mark
                // longer than that dah is the same carrier seen short. A second
                // piece of it, inside a few dits, is not a letter either.
                _carrierEcho = true;
                ClearPartial();
                _held = null;
                return;
            }

            _seenMark = true;
            if (!_live && _runCount >= RunCapacity - 2)
                Replay();
            if (!_live)
            {
                // Store the duration training just used. Replay must not rebuild
                // it from the latest rise, fall, and skew.
                PushRun(true, adjusted);
                _timing.ObserveElement(adjusted);
                _bufferedMarks++;
                if (_bufferedMarks == 2 && _timing.Reacquiring) SeedBufferedPair();
                if (_timing.IsLocked)
                    Replay();
                return;
            }

            AppendElement(adjusted, observe: true);
            return;
        }

        // Locked CarrierMs is ten dits of the fast fist. A 5 WPM dit is
        // already that long, so it must not be dropped on that ceiling.
        if (adjusted >= SlowCarrierMs)
        {
            AbandonSlowCandidate();
            ClearPartial();
            _held = null;
            return;
        }

        if (adjusted >= LongMarkDits * _timing.DitMs)
        {
            NoteSlowCandidate(adjusted);
            return;
        }

        AbandonSlowCandidate();
        _seenMark = true;
        AppendElement(adjusted, observe: true);
    }

    private void CloseSpace()
    {
        if (_holdingSlow)
        {
            // A dropout inside a carrier is not the key-up of a slower fist.
            if (_durationMs < _timing.DitMs)
            {
                AbandonSlowCandidate();
                return;
            }

            PushRun(false, _durationMs);
            return;
        }

        if (!_live && _runCount >= RunCapacity - 2)
            Replay();
        if (!_live)
        {
            if (_seenMark)
            {
                PushRun(false, _durationMs);
                _timing.ObserveSpace(_durationMs);
            }

            if (_timing.IsLocked)
                Replay();
            return;
        }

        TryEmitGap();
        // Letter and word boundaries keep moving after the dit locks.
        // The same window and the same clamps as before lock.
        if (_seenMark)
            _timing.ObserveSpace(_durationMs);
    }

    private void MaybeForceLock()
    {
        // A short send (a single K, then the tail) may never collect six
        // marks. Replay once the open space is a word gap so the tail still
        // flushes the letters that were buffered. Two marks have no 2-means
        // estimate: wait for that word gap, then classify from the gap.
        if (_bufferedMarks < 1 || _durationMs < _timing.WordGapThresholdMs)
            return;
        if (_bufferedMarks < 3)
            SeedBufferedPair();
        Replay();
    }

    private void NoteSlowCandidate(double adjustedMs)
    {
        if (_holdingSlow && _runCount > 0 && _runs[_runCount - 1].Tone)
        {
            // No key-up since the previous long mark. Still one carrier.
            AbandonSlowCandidate();
        }

        if (!_holdingSlow)
        {
            _holdingSlow = true;
            _runCount = 0;
            _bufferedMarks = 0;
            _slowMarks = 0;
        }

        PushRun(true, adjustedMs);
        _slowMarks++;
        _bufferedMarks = _slowMarks;
        _seenMark = true;
        ClearPartial();
        _held = null;
        if (_slowMarks >= SlowReacquireMarks)
            BeginSlowReacquire();
    }

    private void BeginSlowReacquire()
    {
        _holdingSlow = false;
        _timing.BeginReacquisition();
        _live = false;
        _seenMark = _slowMarks > 0;
        int marks = 0;
        for (int i = 0; i < _runCount; i++)
        {
            Run run = _runs[i];
            if (run.Tone)
            {
                _timing.ObserveElement(run.Ms);
                marks++;
            }
            else
            {
                _timing.ObserveSpace(run.Ms);
            }
        }

        _bufferedMarks = marks;
        _slowMarks = 0;
        if (_timing.IsLocked)
            Replay();
    }

    private void AbandonSlowCandidate()
    {
        if (!_holdingSlow) return;
        _holdingSlow = false;
        _runCount = 0;
        _bufferedMarks = 0;
        _slowMarks = 0;
    }

    private void SeedBufferedPair()
    {
        double first = 0;
        double second = 0;
        double gap = 0;
        int marks = 0;
        bool haveGap = false;
        for (int i = 0; i < _runCount; i++)
        {
            Run run = _runs[i];
            if (run.Tone)
            {
                if (marks == 0) first = run.Ms;
                else if (marks == 1) second = run.Ms;
                marks++;
            }
            else if (marks == 1 && !haveGap)
            {
                gap = run.Ms;
                haveGap = true;
            }
        }

        if (marks == 2 && haveGap)
            _timing.SeedFromGapRatio(first, gap, second);
    }

    private void Replay()
    {
        _live = true;
        int count = _runCount;
        double savedMs = _durationMs;
        bool savedTone = _tone;
        ClearPartial();
        _held = null;
        _charsInWord = 0;

        for (int i = 0; i < count; i++)
        {
            Run run = _runs[i];
            if (run.Tone)
            {
                FlushHeld();
                _letterEmitted = false;
                _wordEmitted = false;
                AppendElement(run.Ms, observe: false);
            }
            else
            {
                _durationMs = run.Ms;
                _letterEmitted = false;
                _wordEmitted = false;
                TryEmitGap();
                if (_letterEmitted && !_wordEmitted)
                    TryEmitGap();
            }
        }

        _runCount = 0;
        _bufferedMarks = 0;
        _tone = savedTone;
        _durationMs = savedMs;
        _letterEmitted = false;
        _wordEmitted = false;
    }

    private void AppendElement(double durationMs, bool observe)
    {
        if (durationMs >= _timing.CarrierMs)
        {
            ClearPartial();
            return;
        }

        // Once the dit is known, a mark of six dits or more is not a dah.
        // Emitting it prints T for every chopped piece of a steady carrier.
        if (_timing.IsLocked && durationMs >= LongMarkDits * _timing.DitMs)
        {
            ClearPartial();
            return;
        }

        bool dah = _timing.IsDah(durationMs);
        double center = dah ? _timing.DahCenterMs : _timing.DitMs;
        double error = Math.Abs(durationMs - center) / Math.Max(center, 1e-9);
        _fitSum += Math.Clamp(1.0 - error, 0.0, 1.0);
        _fitCount++;
        if (_patternLength < _pattern.Length)
            _pattern[_patternLength++] = dah ? '-' : '.';
        if (observe)
            _timing.ObserveElement(durationMs);
    }

    private void TryEmitGap()
    {
        if (!_letterEmitted
            && _patternLength > 0
            && _durationMs + _quantumMs >= _timing.LetterGapThresholdMs)
        {
            string pattern = new(_pattern, 0, _patternLength);
            string text = DecodePattern(pattern);
            float confidence = ConfidenceFor(text);
            ClearPartial();
            _letterEmitted = true;
            if (IsFragile(text) && _snrDb < FragileSnrDb && _charsInWord == 0)
                _held = new MorseDecodedSymbol(text, confidence);
            else
            {
                Enqueue(text, confidence);
                _charsInWord++;
            }
            return;
        }

        if (_letterEmitted
            && !_wordEmitted
            && _durationMs + _quantumMs >= _timing.WordGapThresholdMs)
        {
            _wordEmitted = true;
            if (_held is not null)
            {
                // Lone E/T/I with a word gap on both sides, and a weak tone.
                _held = null;
                _charsInWord = 0;
                return;
            }

            if (_charsInWord > 0)
            {
                Enqueue(" ", 1f);
                _charsInWord = 0;
            }
        }
    }

    private void FlushHeld()
    {
        if (_held is null) return;
        Enqueue(_held.Value.Text, _held.Value.Confidence);
        _held = null;
        _charsInWord++;
    }

    // Timing fit against the cluster centre, then SNR. 6 dB is a weak tone
    // and 18 dB is a clean one. A word space is not a guess and stays at 1.
    private float ConfidenceFor(string text)
    {
        double fit = _fitCount == 0 ? 0 : _fitSum / _fitCount;
        if (text == UnknownPlaceholder) fit *= 0.25;
        double snrFit = Math.Clamp((_snrDb - 6.0) / 12.0, 0.0, 1.0);
        return (float)Math.Clamp(fit * (0.5 + 0.5 * snrFit), 0.0, 1.0);
    }

    private void Enqueue(string text, float confidence)
    {
        if (_pendingCount >= _pending.Length) return;
        int index = (_pendingHead + _pendingCount) % _pending.Length;
        _pending[index] = new MorseDecodedSymbol(text, confidence);
        _pendingCount++;
        if (_strongSupport) _readyCount = _pendingCount;
    }

    private void PushRun(bool tone, double durationMs)
    {
        if (_runCount >= _runs.Length)
            return;
        _runs[_runCount++] = new Run(tone, durationMs);
    }

    private double AdjustedMark(double rawMs)
    {
        // No smoother: one analysis quantum is counted on the closing edge.
        // With a smoother, that quantum is inside the group delay, and the
        // same delay on both edges cancels. Only a window resize between
        // the edges still moves the length. A noise-gate attack on a loud
        // mark holds the key past that symmetric delay; the skew removes it.
        double corrected = _riseDelayMs > 0 || _fallDelayMs > 0
            ? rawMs + _riseDelayMs - _fallDelayMs
            : rawMs - _quantumMs;
        corrected -= _markSkewMs;
        return Math.Max(_quantumMs, corrected);
    }

    private void ClearPartial()
    {
        _patternLength = 0;
        _fitSum = 0;
        _fitCount = 0;
    }

    // A steady tone shorter than the pre-lock ceiling. No neighbouring
    // mark within a real gap has been kept, so this is not keying.
    // The carrier state clears on the first gap past FragmentGapMs, so a
    // mark after that pause is the next transmission.
    private bool IsCarrierFragment(double adjustedMs)
    {
        if (_timing.IsLocked) return false;
        if (_carrierEcho) return true;
        return adjustedMs > MorseTimingEstimator.MaxPreLockMarkMs;
    }

    private static bool IsFragile(string text) =>
        text is "E" or "T" or "I";
}
