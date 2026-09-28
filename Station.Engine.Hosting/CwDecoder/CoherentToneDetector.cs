// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

/// <summary>
/// Sliding complex matched filter. Integrating before taking magnitude retains
/// phase information and reduces noise bandwidth to Fs/N, unlike averaging
/// magnitudes. The fixed window delays both edges by the same amount.
/// </summary>
internal sealed class CoherentToneDetector
{
    private readonly bool _windowed;
    public CoherentToneDetector(bool windowed = false) => _windowed = windowed;
    private const int Capacity = 6;
    private int _length = 6;
    private readonly double[] _real = new double[Capacity];
    private readonly double[] _imag = new double[Capacity];
    private int _cursor;
    private int _filled;
    public bool IsReady => _filled >= _length;
    private double _oscReal = 1;
    private double _oscImag;
    private double _stepReal = 1;
    private double _stepImag;
    private double _oscillatorHz;
    private int _sampleRate;
    private double[] _window = [];
    private float[] _samples = [];
    private readonly double[] _rotationReal = new double[17 * Capacity];
    private readonly double[] _rotationImag = new double[17 * Capacity];
    private double _frequency;

    public double GroupDelayBlocks => (_length - 1) * 0.5;

    // 32 ms gives 31.25 Hz ENBW; 21.3 ms gives 46.875 Hz. The latter fits
    // inside a 40–50 WPM dit. The caller changes this only during silence.
    public void SetDit(double ditMs) =>
        _length = ditMs < 33 ? 4 : 6;

    public double TrackedFrequencyHz { get; private set; }
    public double Coherence { get; private set; }
    private double _phaseCoherence;
    public double PhaseCoherence => IsReady ? _phaseCoherence : 0;

    public double Process(ReadOnlySpan<float> samples, double hz, int sampleRate, bool measurePhase = true)
    {
        if (_window.Length != samples.Length || _sampleRate != sampleRate) Reset();
        Configure(hz, sampleRate, samples.Length);
        if (Math.Abs(hz - _frequency) > 0.5)
        {
            _frequency = hz;
            TrackedFrequencyHz = hz;
            _oscReal = 1;
            _oscImag = 0;
            // A search step is not a new signal. Reproject the actual recent
            // audio at this frequency rather than introducing zero padding.
            for (int age = _filled; age > 0; age--)
            {
                int index = (_cursor - age + Capacity) % Capacity;
                Mix(_samples.AsSpan(index * samples.Length, samples.Length), index);
            }
        }
        samples.CopyTo(_samples.AsSpan(_cursor * samples.Length, samples.Length));
        Mix(samples, _cursor);
        _filled = Math.Min(_filled + 1, Capacity);
        _cursor = (_cursor + 1) % Capacity;
        double sumReal = 0, sumImag = 0, energy = 0;
        for (int i = 0; i < _length; i++)
        {
            int index = (_cursor - _length + Capacity + i) % Capacity;
            sumReal += _real[index];
            sumImag += _imag[index];
            energy += _real[index] * _real[index] + _imag[index] * _imag[index];
        }
        double phasePower = 0;
        int bestOffset = 0;
        double magnitudeSum = 0;
        for (int i = 0; i < _length; i++)
        {
            int index = (_cursor - _length + Capacity + i) % Capacity;
            magnitudeSum += Math.Sqrt(_real[index] * _real[index] + _imag[index] * _imag[index]);
        }
        // The wide detector can copy a tone within its main lobe before its
        // search moves. Allow that residual phase slope (100 Hz either way).
        // The narrow path needs only the 25 Hz search-grid half step. Weight
        // by magnitude so a short dit's quiet edges do not count as equally
        // strong incoherent samples.
        for (int offset = _windowed ? -8 : -1; measurePhase && IsReady && offset <= (_windowed ? 8 : 1); offset++)
        {
            double r = 0, q = 0;
            for (int i = 0; i < _length; i++)
            {
                int index = (_cursor - _length + Capacity + i) % Capacity;
                int rotation = (offset + 8) * Capacity + i;
                double c = _rotationReal[rotation], t = _rotationImag[rotation];
                r += _real[index] * c - _imag[index] * t;
                q += _real[index] * t + _imag[index] * c;
            }
            double candidatePower = r * r + q * q;
            if (candidatePower > phasePower)
            {
                phasePower = candidatePower;
                bestOffset = offset;
            }
        }
        _phaseCoherence = IsReady && magnitudeSum > 1e-12 ? Math.Sqrt(phasePower) / magnitudeSum : 0;
        // Hold the last supported tone through quiet/noisy gaps. The residual
        // phase slope describes the copied tone, not the requested centre.
        if (PhaseCoherence >= 0.95) TrackedFrequencyHz = _frequency + bestOffset * 12.5;
        double power = sumReal * sumReal + sumImag * sumImag;
        Coherence = energy > 1e-20 ? power / (_length * energy) : 0;
        // Match the Hann bin's coherent gain (sum(window)/256).
        return Math.Sqrt(power) / _length * (127.5 / 256);
    }

    private void Mix(ReadOnlySpan<float> samples, int index)
    {
        double real = 0, imag = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double sample = samples[i] * (_windowed ? _window[i] : 1);
            real += sample * _oscReal;
            imag += sample * _oscImag;
            double nextReal = _oscReal * _stepReal - _oscImag * _stepImag;
            _oscImag = _oscImag * _stepReal + _oscReal * _stepImag;
            _oscReal = nextReal;
        }
        // Bound roundoff during continuous listening without per-sample trig.
        double scale = 1 / Math.Sqrt(_oscReal * _oscReal + _oscImag * _oscImag);
        _oscReal *= scale;
        _oscImag *= scale;
        _real[index] = real / samples.Length;
        _imag[index] = imag / samples.Length;
    }

    private void Configure(double hz, int sampleRate, int blockSize)
    {
        if (hz != _oscillatorHz || sampleRate != _sampleRate)
        {
            double step = 2 * Math.PI * hz / sampleRate;
            _stepReal = Math.Cos(step);
            _stepImag = Math.Sin(step);
            _oscillatorHz = hz;
        }
        if (_window.Length != blockSize || _sampleRate != sampleRate)
        {
            _window = new double[blockSize];
            _samples = new float[Capacity * blockSize];
            for (int i = 0; i < blockSize; i++)
                _window[i] = blockSize > 1 ? 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (blockSize - 1)) : 1;
            for (int offset = _windowed ? -8 : -1; offset <= (_windowed ? 8 : 1); offset++)
            {
                double turn = 2 * Math.PI * (offset * 12.5) * blockSize / sampleRate;
                for (int i = 0; i < Capacity; i++)
                {
                    int index = (offset + 8) * Capacity + i;
                    _rotationReal[index] = Math.Cos(turn * i);
                    _rotationImag[index] = Math.Sin(turn * i);
                }
            }
        }
        _sampleRate = sampleRate;
    }

    public void Reset()
    {
        Array.Clear(_real);
        Array.Clear(_imag);
        _cursor = 0;
        _filled = 0;
        _length = 6;
        _oscReal = 1;
        _oscImag = 0;
        TrackedFrequencyHz = _frequency;
        Coherence = 0;
        _phaseCoherence = 0;
    }
}
