// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>Single-bin tone-power detector for fixed 256-sample blocks.</summary>
internal sealed class GoertzelDetector
{
    // 256 samples at 48 kHz is 5.33 ms: short enough to resolve 50 WPM key
    // edges while retaining ample processing gain for a narrow CW tone.
    public const int BlockSize = 256;

    private readonly int _sampleRateHz;
    private readonly double[] _window = new double[BlockSize];
    private double _coefficient;

    public GoertzelDetector(int sampleRateHz, double centerFrequencyHz)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        _sampleRateHz = sampleRateHz;
        for (int i = 0; i < BlockSize; i++)
            _window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / (BlockSize - 1)));
        Retune(centerFrequencyHz);
    }

    public void Retune(double centerFrequencyHz)
    {
        if (centerFrequencyHz <= 0 || centerFrequencyHz >= _sampleRateHz / 2.0)
            throw new ArgumentOutOfRangeException(nameof(centerFrequencyHz));
        _coefficient = 2.0 * Math.Cos(2.0 * Math.PI * centerFrequencyHz / _sampleRateHz);
    }

    public double DetectPower(ReadOnlySpan<float> samples)
    {
        if (samples.Length != BlockSize)
            throw new ArgumentException($"A block must contain exactly {BlockSize} samples.", nameof(samples));

        double s1 = 0;
        double s2 = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double s0 = samples[i] * _window[i] + (_coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        double power = s1 * s1 + s2 * s2 - _coefficient * s1 * s2;
        return Math.Max(power / (BlockSize * BlockSize), 1e-20);
    }
}
