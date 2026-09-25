// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Sums a keyed Morse tone into 48 kHz TX audio blocks, sample-accurately,
/// from the <see cref="MorseEncoder"/> timing stream. Each key-down element
/// gets raised-cosine rise/fall ramps (same 5 ms shaping as
/// <c>CwEngine</c>) so the ID has no key clicks. The mixer only ever ADDS to
/// the block it is handed; it never generates audio on its own clock, so it
/// can only reach the air inside a transmission the operator started.
///
/// Not thread-safe: the owner (<see cref="CwIdService"/>) serialises calls.
/// </summary>
internal sealed class CwIdToneMixer
{
    public const int SampleRate = 48000;
    private const double RampMs = 5.0;

    private CwSymbol[] _symbols = Array.Empty<CwSymbol>();
    private int _index;
    private int _posInSymbol;
    private int _symbolSamples;
    private int _rampSamples;
    private double _phase;
    private double _phaseInc;
    private float _amplitude;

    public bool Active => _index < _symbols.Length;

    /// <summary>Total length of the ID in samples at the current settings.
    /// Diagnostic / test aid.</summary>
    public long TotalSamples { get; private set; }

    public void Begin(string text, int wpm, int toneHz, double levelDb)
    {
        ArgumentNullException.ThrowIfNull(text);
        _symbols = MorseEncoder.Encode(text, wpm).ToArray();
        _index = 0;
        _posInSymbol = 0;
        _phase = 0;
        _phaseInc = 2 * Math.PI * toneHz / SampleRate;
        _amplitude = (float)Math.Pow(10, levelDb / 20.0);
        _rampSamples = (int)(RampMs * SampleRate / 1000.0);
        TotalSamples = 0;
        foreach (var s in _symbols) TotalSamples += SamplesFor(s);
        _symbolSamples = _symbols.Length > 0 ? SamplesFor(_symbols[0]) : 0;
    }

    public void Cancel()
    {
        _symbols = Array.Empty<CwSymbol>();
        _index = 0;
        _posInSymbol = 0;
    }

    /// <summary>Add the next <paramref name="block"/>.Length samples of the ID
    /// onto <paramref name="block"/>, clamped to ±1. Returns true when the ID
    /// finished inside this block (the mixer is then inactive).</summary>
    public bool MixInto(Span<float> block)
    {
        if (!Active) return false;
        for (int i = 0; i < block.Length; i++)
        {
            var sym = _symbols[_index];
            if (sym.KeyDown)
            {
                float env = Envelope(_posInSymbol, _symbolSamples, _rampSamples);
                float v = block[i] + _amplitude * env * (float)Math.Sin(_phase);
                block[i] = Math.Clamp(v, -1f, 1f);
                _phase += _phaseInc;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            }

            if (++_posInSymbol >= _symbolSamples)
            {
                _posInSymbol = 0;
                if (++_index >= _symbols.Length) return true;
                _symbolSamples = SamplesFor(_symbols[_index]);
            }
        }
        return false;
    }

    private static int SamplesFor(CwSymbol s) => (int)((long)s.DurationMs * SampleRate / 1000);

    internal static float Envelope(int pos, int length, int ramp)
    {
        int r = Math.Min(ramp, length / 2);
        if (r <= 0) return 1f;
        if (pos < r) return (float)(0.5 - 0.5 * Math.Cos(Math.PI * pos / r));
        int fromEnd = length - 1 - pos;
        if (fromEnd < r) return (float)(0.5 - 0.5 * Math.Cos(Math.PI * fromEnd / r));
        return 1f;
    }
}
