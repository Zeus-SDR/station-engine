// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// FM repeater access tone burst (1750 Hz et al.): a sine with ~5 ms
/// raised-cosine ramps that REPLACES the mic audio in the keyed TX stream for
/// its duration (the mic is muted while it plays). Armed by
/// <see cref="TxService"/> at the start of an FM over or on demand, rendered
/// block-by-block by <see cref="TxAudioIngest"/> on the mic hot path, so it
/// is paced by the real TX audio clock and never keys the transmitter itself.
///
/// <para>Level. The burst is injected at the mic, ahead of the WDSP panel
/// (mic) gain, with the speech processors bypassed the same way as the roger
/// beep (SetTxRogerBeepBypass: leveler / compressor / CFC / CESSB / phase
/// rotator off). What remains before the FM modulator is the panel gain, the
/// ALC (a limiter whose output target is 1.0 FS) and FM pre-emphasis. The
/// sample amplitude is therefore <see cref="TargetAlcPeak"/> divided by the
/// linear mic gain (capped at 1.0 FS), so the tone arrives at the ALC at
/// -0.9 dBFS whatever the operator's mic gain is: just under the ALC target,
/// so the ALC does not gain-ride or clip it into harmonics. WDSP fmmod maps
/// a 1.0 FS modulator input to the full configured deviation (scaled by
/// 1/(1+ctcss_level) when CTCSS is on). WDSP's FM pre-emphasis (emph.c,
/// fc_mults curve 0 with g0 = -20log10(fHigh/fLow)) has unity gain at the TX
/// audio high cut and f/fHigh below it, so with pre-emphasis on the tone
/// reaches the modulator at 0.9 x 1750/3000 = 0.53 FS: about 2.6 kHz of a
/// 5 kHz deviation — typical 1750 Hz burst deviation, and the most the chain
/// allows, since the ALC caps the level ahead of the post-limiter
/// pre-emphasis. With pre-emphasis off (flat TX) it is 0.9 of the deviation
/// (about 4.5 kHz). The roger beep uses a fixed 0.60 for a 1 kHz courtesy
/// tone; a burst must be as strong as the chain permits to open a repeater.</para>
///
/// <para>Thread-safety: all members are called under TxAudioIngest's
/// <c>_sync</c> (or before the ingest exists); not independently locked.</para>
/// </summary>
internal sealed class FmToneBurstGenerator
{
    internal const float TargetAlcPeak = 0.9f;
    internal const double RampMs = 5.0;

    private readonly int _sampleRate;
    private double _phase;
    private double _phaseStep;
    private int _totalSamples;
    private int _position;
    private int _rampSamples;
    private float _amplitude;

    public FmToneBurstGenerator(int sampleRate) => _sampleRate = sampleRate;

    /// <summary>True from <see cref="Arm"/> until the last burst sample has
    /// been rendered or <see cref="Cancel"/>.</summary>
    public bool IsActive => _totalSamples > 0 && _position < _totalSamples;

    /// <summary>True once rendering has started (at least one block).</summary>
    public bool Started => _position > 0;

    public float Amplitude => _amplitude;

    /// <summary>Arm a burst of <paramref name="toneHz"/> for
    /// <paramref name="durationMs"/>. <paramref name="micGainDb"/> is the
    /// effective TX mic (panel) gain the tone will pass through.</summary>
    public void Arm(double toneHz, int durationMs, double micGainDb)
    {
        _phase = 0.0;
        _phaseStep = 2.0 * Math.PI * toneHz / _sampleRate;
        _totalSamples = Math.Max(1, (int)((long)_sampleRate * durationMs / 1000));
        _position = 0;
        _rampSamples = Math.Max(1, Math.Min((int)(_sampleRate * RampMs / 1000.0), _totalSamples / 2));
        _amplitude = AmplitudeFor(micGainDb);
    }

    public void Cancel()
    {
        _totalSamples = 0;
        _position = 0;
    }

    /// <summary>Sample amplitude that reaches the ALC at
    /// <see cref="TargetAlcPeak"/> after <paramref name="micGainDb"/> of
    /// panel gain, capped to full scale.</summary>
    internal static float AmplitudeFor(double micGainDb)
    {
        double micLinear = double.IsFinite(micGainDb) ? Math.Pow(10.0, micGainDb / 20.0) : 1.0;
        if (micLinear <= 0) micLinear = 1.0;
        return (float)Math.Min(1.0, TargetAlcPeak / micLinear);
    }

    /// <summary>Overwrite <paramref name="block"/> with the next slice of the
    /// burst (mic muted). Samples past the end of the burst are zeroed so no
    /// mic audio leaks into the final burst block. No-op when inactive.</summary>
    public void Render(Span<float> block)
    {
        if (!IsActive) return;
        for (int i = 0; i < block.Length; i++)
        {
            if (_position >= _totalSamples)
            {
                block[i] = 0f;
                continue;
            }
            block[i] = (float)(Math.Sin(_phase) * _amplitude * Envelope(_position));
            _phase += _phaseStep;
            if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
            _position++;
        }
    }

    // Raised-cosine attack / release over _rampSamples.
    private double Envelope(int n)
    {
        int fromEnd = _totalSamples - 1 - n;
        int edge = Math.Min(n, fromEnd);
        if (edge >= _rampSamples) return 1.0;
        return 0.5 - 0.5 * Math.Cos(Math.PI * edge / _rampSamples);
    }
}
