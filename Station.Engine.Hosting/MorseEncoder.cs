// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution. Morse table seeded from the
// Thetis CWX defaults (Project Files/Source/Console/cwx.cs, load_alpha at
// the morsedef.txt boot). Thetis is GPL-2.0+; preserving the lineage.

namespace Zeus.Server;

/// <summary>
/// One emitted CW timing symbol: a stretch of key-down (carrier on) or
/// key-up (silence). Durations are in milliseconds at the active WPM.
/// Concatenating the durations yields the full transmission timeline.
/// </summary>
public readonly record struct CwSymbol(bool KeyDown, int DurationMs);

internal readonly record struct CwSampleSymbol(bool KeyDown, int DurationSamples);

/// <summary>
/// Pure ASCII-text → key-down/key-up timing stream. No side effects, no IO,
/// bounded iterator state and no per-symbol heap allocation — safe to call
/// from any thread.
///
/// Timing follows the PARIS-method standard: unit = 1200 / WPM ms.
/// <list type="bullet">
///   <item>dit = 1 unit on</item>
///   <item>dah = 3 units on</item>
///   <item>intra-element gap (between elements of a char) = 1 unit off</item>
///   <item>inter-character gap = 3 units off</item>
///   <item>inter-word gap = 7 units off (absorbs the would-be inter-char gap)</item>
/// </list>
///
/// Inputs are case-folded to upper. Characters not present in the table
/// (and the Thetis-only macro escapes <c>" # $ &amp;</c>) are silently
/// skipped — same as Thetis treats undefined entries in morsedef.txt.
/// </summary>
public static class MorseEncoder
{
    /// <summary>Integer-millisecond PARIS unit for compatibility helpers.
    /// IQ playback uses sample-domain timing to preserve fractional units.</summary>
    public static int DitMs(int wpm) => 1200 / wpm;

    // ASCII 32..95, seeded from Thetis morsedef.txt defaults. Entries with
    // no defined pattern (slot is empty / whitespace-only) and the Thetis
    // macro escapes (loop ", long-dash #, long-space $, multi-digit &,
    // reserved _) are absent on purpose — we want a clean "skip unknown"
    // contract, not a partial port of Thetis's CWX special-char DSL.
    private static readonly Dictionary<char, string> Table = new()
    {
        // Punctuation + special procedural signals
        ['!'] = "...-.",   // [SN]
        ['%'] = ".-...",   // [AS]
        ['('] = "-.--.",   // [KN]
        ['*'] = "...-.-",  // [SK]
        ['+'] = ".-.-.",   // [AR]
        [','] = "--..--",
        ['-'] = "-....-",
        ['.'] = ".-.-.-",
        ['/'] = "-..-.",
        [':'] = "---...",
        [';'] = "-.-.-.",
        ['='] = "-...-",   // [BT]
        ['?'] = "..--..",
        ['@'] = ".--.-.",
        ['\\'] = "-...-.-", // [BK]

        // Digits
        ['0'] = "-----",
        ['1'] = ".----",
        ['2'] = "..---",
        ['3'] = "...--",
        ['4'] = "....-",
        ['5'] = ".....",
        ['6'] = "-....",
        ['7'] = "--...",
        ['8'] = "---..",
        ['9'] = "----.",

        // Letters
        ['A'] = ".-",
        ['B'] = "-...",
        ['C'] = "-.-.",
        ['D'] = "-..",
        ['E'] = ".",
        ['F'] = "..-.",
        ['G'] = "--.",
        ['H'] = "....",
        ['I'] = "..",
        ['J'] = ".---",
        ['K'] = "-.-",
        ['L'] = ".-..",
        ['M'] = "--",
        ['N'] = "-.",
        ['O'] = "---",
        ['P'] = ".--.",
        ['Q'] = "--.-",
        ['R'] = ".-.",
        ['S'] = "...",
        ['T'] = "-",
        ['U'] = "..-",
        ['V'] = "...-",
        ['W'] = ".--",
        ['X'] = "-..-",
        ['Y'] = "-.--",
        ['Z'] = "--..",
    };

    /// <summary>
    /// Lazily emit timing symbols for <paramref name="text"/> at
    /// <paramref name="wpm"/>. Iterating to the end is allocation-light:
    /// one tiny struct per dit/dah/gap.
    /// </summary>
    /// <param name="text">ASCII text. Lowercase is upcased; SPACE / TAB /
    /// LF / CR all collapse to a single word gap.</param>
    /// <param name="wpm">Words per minute. Must be ≥ 1; clamp at the call
    /// site if your UI permits zero/negative.</param>
    public static IEnumerable<CwSymbol> Encode(string text, int wpm)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (wpm < 1) throw new ArgumentOutOfRangeException(nameof(wpm), wpm, "WPM must be ≥ 1");

        int unit = DitMs(wpm);
        foreach (var symbol in EncodeUnits(text))
            yield return new CwSymbol(symbol.KeyDown, unit * symbol.Units);
    }

    /// <summary>
    /// Emit sample-domain timing with each cumulative boundary rounded to
    /// the nearest sample. Carrying the remainder prevents WPM truncation
    /// and drift over long messages; one Morse unit is exactly 6/(5*WPM) s.
    /// </summary>
    internal static IEnumerable<CwSampleSymbol> EncodeSamples(string text, int wpm, int rateHz)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (wpm < 1) throw new ArgumentOutOfRangeException(nameof(wpm));
        if (rateHz < 1) throw new ArgumentOutOfRangeException(nameof(rateHz));
        long denominator = 5L * wpm;
        long remainder = denominator / 2;
        foreach (var symbol in EncodeUnits(text))
        {
            long numerator = 6L * rateHz * symbol.Units + remainder;
            int samples = checked((int)(numerator / denominator));
            remainder = numerator % denominator;
            yield return new CwSampleSymbol(symbol.KeyDown, samples);
        }
    }

    private static IEnumerable<(bool KeyDown, int Units)> EncodeUnits(string text)
    {
        bool atWordStart = true;  // suppresses the leading inter-character gap

        foreach (char raw in text)
        {
            char c = char.ToUpperInvariant(raw);

            if (c is ' ' or '\t' or '\n' or '\r')
            {
                // Word gap absorbs the inter-character gap that would
                // otherwise precede the next char, so we mark atWordStart.
                // Suppress at the very start of the buffer — no point
                // emitting 7 units of silence before the first character.
                if (!atWordStart) yield return (false, 7);
                atWordStart = true;
                continue;
            }

            if (!Table.TryGetValue(c, out var pattern)) continue;

            // Inter-character gap. Skipped when this is the first
            // character of the buffer or of a fresh word (atWordStart).
            if (!atWordStart) yield return (false, 3);
            atWordStart = false;

            for (int i = 0; i < pattern.Length; i++)
            {
                if (i > 0) yield return (false, 1); // intra-element gap
                yield return (true, pattern[i] == '.' ? 1 : 3);
            }
        }
    }
}
