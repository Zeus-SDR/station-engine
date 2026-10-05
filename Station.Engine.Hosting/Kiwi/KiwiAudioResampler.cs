// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>
/// Streaming band-limited resampler for the KiwiSDR SND stream (native ~12 kHz,
/// or 20.25 kHz on wideband firmware) up to the 48 kHz RX mix bus.
///
/// <para>Replaces a linear interpolator whose sinc² response left the spectral
/// images of the 12 kHz stream (12 kHz ± f, i.e. 6–12 kHz and up) only
/// ~10–30 dB down — an audible metallic sheen on the Kiwi that the hardware
/// receivers don't have. This uses the shared <see cref="WindowedSincKernel"/>
/// (96-tap Blackman-Harris, cutoff 0.475·input rate), so the images sit at the
/// kernel's stopband instead. Cost is 96 MACs per output sample (~4.6 M/s at
/// 48 kHz) and 48 input samples of group delay (4 ms at 12 kHz).</para>
///
/// <para>Single-owner: <see cref="Process"/> runs on the Kiwi SND receive
/// thread only. <see cref="RequestReset"/> may be called from any thread; the
/// reset is applied at the start of the next <see cref="Process"/>, so stream
/// state is never mutated under a concurrent call.</para>
///
/// <para>The fractional read position is kept relative to the start of the
/// retained input window and rebased every call, so it never grows with session
/// length — an always-on Kiwi can run for days without counter overflow or loss
/// of phase precision.</para>
/// </summary>
internal sealed class KiwiAudioResampler
{
    private const int Taps = WindowedSincKernel.Taps;
    private const int Half = WindowedSincKernel.Half;
    private const int PhaseCount = WindowedSincKernel.PhaseCount;

    private readonly int _outRateHz;
    private WindowedSincKernel? _kernel;
    private int _kernelInRateHz;
    private int _inRateHz;
    private double _step;
    private float[] _in = new float[4096];
    private int _inLen;
    // Input position (index into _in) of the next output sample's centre.
    private double _pos;
    private float[] _out = new float[8192];
    private int _resetRequested;

    public KiwiAudioResampler(int outRateHz)
    {
        if (outRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(outRateHz));
        _outRateHz = outRateHz;
    }

    /// <summary>Discard stream history (e.g. on reconnect). Thread-safe; applied
    /// on the next <see cref="Process"/> call.</summary>
    public void RequestReset() => Volatile.Write(ref _resetRequested, 1);

    /// <summary>Resample one block. The returned span aliases an internal buffer
    /// and is valid only until the next call. An input already at the output
    /// rate is returned unchanged.</summary>
    public ReadOnlySpan<float> Process(ReadOnlySpan<float> input, int inRateHz)
    {
        if (Interlocked.Exchange(ref _resetRequested, 0) != 0) _inRateHz = 0;
        if (input.IsEmpty) return ReadOnlySpan<float>.Empty;
        if (inRateHz <= 0) inRateHz = 12_000;
        if (inRateHz != _inRateHz) ResetForRate(inRateHz);
        if (inRateHz == _outRateHz) return input;

        Append(input);

        var kernel = _kernel!;
        int n = 0;
        while (true)
        {
            int center = (int)Math.Floor(_pos);
            int phase = (int)Math.Round((_pos - center) * PhaseCount);
            if (phase == PhaseCount)
            {
                phase = 0;
                center++;
            }

            int first = center - Half + 1;
            if (first + Taps > _inLen) break;

            var coeffs = kernel.Coefficients.AsSpan(phase * Taps, Taps);
            var window = _in.AsSpan(first, Taps);
            double sum = 0;
            for (int tap = 0; tap < Taps; tap++)
                sum += window[tap] * coeffs[tap];

            if (n == _out.Length) Array.Resize(ref _out, _out.Length * 2);
            _out[n++] = (float)sum;
            _pos += _step;
        }

        // Drop input no future output can reach: the next output's first tap
        // is at floor(_pos) - Half + 1. Rebase _pos so it stays small.
        int keepFrom = Math.Min((int)Math.Floor(_pos) - Half + 1, _inLen);
        if (keepFrom > 0)
        {
            _in.AsSpan(keepFrom, _inLen - keepFrom).CopyTo(_in);
            _inLen -= keepFrom;
            _pos -= keepFrom;
        }

        return _out.AsSpan(0, n);
    }

    private void ResetForRate(int inRateHz)
    {
        _inRateHz = inRateHz;
        _step = inRateHz / (double)_outRateHz;
        if (inRateHz != _outRateHz && _kernelInRateHz != inRateHz)
        {
            _kernel = WindowedSincKernel.ForRates(inRateHz, _outRateHz);
            _kernelInRateHz = inRateHz;
        }

        // Prime with Half-1 samples of silence so the first output is centred
        // on the first real input sample with a full (zero) left history.
        _inLen = Half - 1;
        Array.Clear(_in, 0, _inLen);
        _pos = Half - 1;
    }

    private void Append(ReadOnlySpan<float> input)
    {
        int required = _inLen + input.Length;
        if (required > _in.Length)
        {
            int size = _in.Length;
            while (size < required) size *= 2;
            Array.Resize(ref _in, size);
        }
        input.CopyTo(_in.AsSpan(_inLen));
        _inLen += input.Length;
    }
}
