// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server;

/// <summary>
/// Real-input FFT specialized for power-spectrum analysis. Packs N real
/// samples into an N/2-point complex transform and splits the result into the
/// N-point real spectrum, so it does roughly half the work of the naive
/// full-complex approach for the same output. Twiddle factors and the
/// bit-reversal permutation are precomputed once per instance; steady-state
/// calls allocate nothing.
///
/// Not thread-safe: each analysis worker owns its instance.
/// </summary>
internal sealed class WidebandRealFft
{
    public const int MinSize = 512;
    public const int MaxSize = 32_768;

    private readonly int _size;
    private readonly int _half;
    private readonly int[] _bitReverse;
    private readonly double[] _twiddleCos; // cos(2πk/N_complex), k < N_complex/2
    private readonly double[] _twiddleSin;
    private readonly double[] _re;
    private readonly double[] _im;

    public WidebandRealFft(int size)
    {
        if (size < MinSize || size > MaxSize || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size),
                $"Size must be a power of two between {MinSize} and {MaxSize}.");

        _size = size;
        _half = size / 2;

        int bits = 0;
        for (int n = _half; n > 1; n >>= 1) bits++;
        _bitReverse = new int[_half];
        for (int i = 0; i < _half; i++)
        {
            int v = i;
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r = (r << 1) | (v & 1);
                v >>= 1;
            }
            _bitReverse[i] = r;
        }

        _twiddleCos = new double[_half / 2];
        _twiddleSin = new double[_half / 2];
        for (int k = 0; k < _half / 2; k++)
        {
            double angle = -2.0 * Math.PI * k / _half;
            _twiddleCos[k] = Math.Cos(angle);
            _twiddleSin[k] = Math.Sin(angle);
        }

        _re = new double[_half];
        _im = new double[_half];
    }

    /// <summary>Transform size in real samples.</summary>
    public int Size => _size;

    /// <summary>
    /// Computes the single-sided power spectrum of <paramref name="realInput"/>
    /// into <paramref name="powerOut"/> (length N/2 + 1). The input is copied
    /// internally; the caller's span is not modified. No scaling is applied —
    /// powerOut[k] = |X[k]|² with X the unscaled DFT — so callers keep their
    /// existing calibration math.
    /// </summary>
    public void ForwardPower(ReadOnlySpan<double> realInput, Span<double> powerOut)
    {
        if (realInput.Length != _size)
            throw new ArgumentException($"Input must be exactly {_size} samples.", nameof(realInput));
        if (powerOut.Length < _half + 1)
            throw new ArgumentException($"Output must hold at least {_half + 1} bins.", nameof(powerOut));

        // Pack even/odd samples as the complex sequence z[n] = x[2n] + i·x[2n+1].
        for (int i = 0; i < _half; i++)
        {
            _re[i] = realInput[2 * i];
            _im[i] = realInput[(2 * i) + 1];
        }

        ComplexFftInPlace();

        // Split the M-point complex spectrum Z into the N-point real spectrum.
        // With A = Z[k], B = conj(Z[M-k]):
        //   E[k] = (A + B)/2        (even-sample transform)
        //   O[k] = -i·(A - B)/2     (odd-sample transform)
        //   X[k] = E[k] + W_N^k·O[k],   W_N^k = exp(-i·π·k/M)
        // The formulas are symmetric in k ↔ M-k, so one pass covers every
        // interior bin 1..M-1, including the self-conjugate bin k = M/2.
        // Only bins 0..N/2 are produced (single-sided spectrum).
        for (int k = 1; k < _half; k++)
        {
            int mk = _half - k;
            double eRe = (_re[k] + _re[mk]) * 0.5;
            double eIm = (_im[k] - _im[mk]) * 0.5;
            double oRe = (_im[k] + _im[mk]) * 0.5;
            double oIm = (_re[mk] - _re[k]) * 0.5;

            double ang = -Math.PI * k / _half;
            double wRe = Math.Cos(ang);
            double wIm = Math.Sin(ang);

            double xRe = eRe + (wRe * oRe - wIm * oIm);
            double xIm = eIm + (wRe * oIm + wIm * oRe);
            powerOut[k] = xRe * xRe + xIm * xIm;
        }

        // DC and Nyquist (k = N/2) come straight from Z[0].
        double dc = _re[0] + _im[0];
        double nyq = _re[0] - _im[0];
        powerOut[0] = dc * dc;
        powerOut[_half] = nyq * nyq;
    }

    private void ComplexFftInPlace()
    {
        for (int i = 0; i < _half; i++)
        {
            int j = _bitReverse[i];
            if (i >= j) continue;
            (_re[i], _re[j]) = (_re[j], _re[i]);
            (_im[i], _im[j]) = (_im[j], _im[i]);
        }

        for (int len = 2; len <= _half; len <<= 1)
        {
            int halfLen = len >> 1;
            int twiddleStride = _half / len;
            for (int i = 0; i < _half; i += len)
            {
                int tw = 0;
                int limit = i + halfLen;
                for (int even = i; even < limit; even++)
                {
                    int odd = even + halfLen;
                    double wRe = _twiddleCos[tw];
                    double wIm = _twiddleSin[tw];
                    tw += twiddleStride;

                    double oddRe = _re[odd] * wRe - _im[odd] * wIm;
                    double oddIm = _re[odd] * wIm + _im[odd] * wRe;
                    double evenRe = _re[even];
                    double evenIm = _im[even];
                    _re[even] = evenRe + oddRe;
                    _im[even] = evenIm + oddIm;
                    _re[odd] = evenRe - oddRe;
                    _im[odd] = evenIm - oddIm;
                }
            }
        }
    }
}
