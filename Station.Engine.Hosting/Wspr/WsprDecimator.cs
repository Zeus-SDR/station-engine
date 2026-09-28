// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Streaming 48 kHz → 12 kHz decimator for WSPR capture: a linear-phase
/// Blackman-windowed low-pass FIR (cutoff 4.8 kHz, stopband past 6 kHz) and
/// keep-one-in-four. WSPR occupies 1400–1600 Hz of audio, so the passband is
/// flat and aliasing from 6–24 kHz is attenuated well below the decoder's noise
/// floor. State carries across calls so block boundaries are seamless.
/// </summary>
internal sealed class WsprDecimator
{
    internal const int Factor = 4;
    private const int Taps = 64;
    private static readonly float[] Coefficients = Design();

    // Circular history of the last Taps input samples.
    private readonly float[] _history = new float[Taps];
    private int _head;
    private int _phase;

    /// <summary>Decimates <paramref name="input"/>; returns the samples written to <paramref name="output"/>.</summary>
    /// <remarks><paramref name="output"/> must hold at least <c>input.Length / 4 + 1</c> samples.</remarks>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int written = 0;
        for (int i = 0; i < input.Length; i++)
        {
            _history[_head] = input[i];
            _head = (_head + 1) % Taps;
            if (++_phase < Factor) continue;
            _phase = 0;

            // _head now points at the oldest sample.
            float acc = 0f;
            int index = _head;
            for (int k = 0; k < Taps; k++)
            {
                acc += Coefficients[k] * _history[index];
                if (++index == Taps) index = 0;
            }
            output[written++] = acc;
        }
        return written;
    }

    public void Reset()
    {
        Array.Clear(_history);
        _head = 0;
        _phase = 0;
    }

    private static float[] Design()
    {
        const double sampleRate = 48_000.0;
        const double cutoffHz = 4_800.0;
        var h = new double[Taps];
        double fc = cutoffHz / sampleRate;
        double centre = (Taps - 1) / 2.0;
        double sum = 0;
        for (int n = 0; n < Taps; n++)
        {
            double x = n - centre;
            double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            double window = 0.42
                - 0.5 * Math.Cos(2 * Math.PI * n / (Taps - 1))
                + 0.08 * Math.Cos(4 * Math.PI * n / (Taps - 1));
            h[n] = sinc * window;
            sum += h[n];
        }
        var result = new float[Taps];
        for (int n = 0; n < Taps; n++) result[n] = (float)(h[n] / sum);
        return result;
    }
}
