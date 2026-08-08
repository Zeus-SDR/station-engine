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

    private readonly MorseTimingEstimator _timing;
    private readonly char[] _pattern = new char[8];
    private int _patternLength;
    private bool _initialized;
    private bool _tone;
    private double _durationMs;
    private double _fitSum;
    private int _fitCount;
    private bool _letterEmitted;
    private bool _wordEmitted;
    private double _quantumMs;

    public MorseFsm(MorseTimingEstimator timing) => _timing = timing;

    internal static IReadOnlyDictionary<string, string> CharacterTable => Table;

    internal static string DecodePattern(string pattern) =>
        Table.TryGetValue(pattern, out string? text) ? text : UnknownPlaceholder;

    public bool Process(bool tone, double blockDurationMs, out MorseDecodedSymbol symbol)
    {
        symbol = default;
        _quantumMs = blockDurationMs;
        if (!_initialized)
        {
            _initialized = true;
            _tone = tone;
            _durationMs = blockDurationMs;
            return false;
        }

        if (tone == _tone)
        {
            _durationMs += blockDurationMs;
            return !tone && TryEmitGap(out symbol);
        }

        if (_tone)
        {
            // A block detector reports the edge after one complete analysis
            // quantum. Remove that fixed latency before clustering; otherwise
            // dits grow while the complementary key-up gaps shrink.
            AppendElement(Math.Max(blockDurationMs, _durationMs - blockDurationMs));
            _tone = false;
            _durationMs = blockDurationMs;
            return false;
        }

        bool emitted = TryEmitGap(out symbol);
        _tone = true;
        _durationMs = blockDurationMs;
        _letterEmitted = false;
        _wordEmitted = false;
        return emitted;
    }

    private void AppendElement(double durationMs)
    {
        bool dah = _timing.IsDah(durationMs);
        double target = dah ? 3.0 * _timing.DitMs : _timing.DitMs;
        double error = Math.Abs(durationMs - target) / Math.Max(target, 1e-9);
        _fitSum += Math.Clamp(1.0 - error, 0.0, 1.0);
        _fitCount++;
        if (_patternLength < _pattern.Length)
            _pattern[_patternLength++] = dah ? '-' : '.';
        _timing.ObserveElement(durationMs);
    }

    private bool TryEmitGap(out MorseDecodedSymbol symbol)
    {
        symbol = default;
        if (!_letterEmitted
            && _patternLength > 0
            && _durationMs + _quantumMs >= _timing.LetterGapThresholdMs)
        {
            string pattern = new(_pattern, 0, _patternLength);
            string text = DecodePattern(pattern);
            double fit = _fitCount == 0 ? 0 : _fitSum / _fitCount;
            if (text == UnknownPlaceholder) fit *= 0.25;
            symbol = new MorseDecodedSymbol(text, (float)Math.Clamp(fit, 0, 1));
            _patternLength = 0;
            _fitSum = 0;
            _fitCount = 0;
            _letterEmitted = true;
            return true;
        }

        if (_letterEmitted
            && !_wordEmitted
            && _durationMs + _quantumMs >= _timing.WordGapThresholdMs)
        {
            _wordEmitted = true;
            symbol = new MorseDecodedSymbol(" ", 1f);
            return true;
        }
        return false;
    }

    public void Reset()
    {
        _patternLength = 0;
        _initialized = false;
        _tone = false;
        _durationMs = 0;
        _fitSum = 0;
        _fitCount = 0;
        _letterEmitted = false;
        _wordEmitted = false;
        _quantumMs = 0;
    }
}
