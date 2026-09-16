// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Narrow frequency-acquisition bank for fixed 256-sample CW audio blocks.
///
/// The detector starts at the operator's receive pitch, then considers a
/// bounded set of nearby bins. A new center is only accepted after several
/// consecutive winners, so a brief click or adjacent station cannot retune
/// the decoder on a single block. This is deliberately a first-principles
/// implementation rather than a port of another receiver's CW DSP.
/// </summary>
internal sealed class GoertzelDetector
{
    // 256 samples at 48 kHz is 5.33 ms: short enough to resolve 50 WPM key
    // edges while retaining ample processing gain for a narrow CW tone.
    public const int BlockSize = 256;
    private const int SearchStepHz = 25;
    private const int SearchHalfWidthSteps = 5;
    private const int LockConfirmationBlocks = 8;
    private const double PowerSmoothing = 0.22;

    private readonly int _sampleRateHz;
    private readonly double[] _window = new double[BlockSize];
    private readonly double[] _coefficients = new double[(SearchHalfWidthSteps * 2) + 1];
    private readonly double[] _frequenciesHz = new double[(SearchHalfWidthSteps * 2) + 1];
    private readonly double[] _instantPowers = new double[(SearchHalfWidthSteps * 2) + 1];
    private readonly double[] _smoothedPowers = new double[(SearchHalfWidthSteps * 2) + 1];
    private int _lockedIndex = SearchHalfWidthSteps;
    private int _pendingIndex = -1;
    private int _pendingBlocks;
    private bool _havePower;
    private double _configuredCenterFrequencyHz;

    public GoertzelDetector(int sampleRateHz, double centerFrequencyHz)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        _sampleRateHz = sampleRateHz;
        for (int i = 0; i < BlockSize; i++)
            _window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / (BlockSize - 1)));
        Retune(centerFrequencyHz);
    }

    /// <summary>The currently stable acquired tone center.</summary>
    public double TrackedFrequencyHz => _frequenciesHz[_lockedIndex];

    public void Retune(double centerFrequencyHz)
    {
        if (centerFrequencyHz <= 0 || centerFrequencyHz >= _sampleRateHz / 2.0)
            throw new ArgumentOutOfRangeException(nameof(centerFrequencyHz));

        _configuredCenterFrequencyHz = centerFrequencyHz;
        double highestFrequency = (_sampleRateHz / 2.0) - 1.0;
        for (int i = 0; i < _frequenciesHz.Length; i++)
        {
            int step = i - SearchHalfWidthSteps;
            double frequency = Math.Clamp(centerFrequencyHz + (step * SearchStepHz), 1.0, highestFrequency);
            _frequenciesHz[i] = frequency;
            _coefficients[i] = 2.0 * Math.Cos(2.0 * Math.PI * frequency / _sampleRateHz);
        }

        Array.Clear(_instantPowers);
        Array.Clear(_smoothedPowers);
        _lockedIndex = SearchHalfWidthSteps;
        _pendingIndex = -1;
        _pendingBlocks = 0;
        _havePower = false;
    }

    /// <summary>Drop the acquired offset and return to the configured pitch.</summary>
    public void Reset() => Retune(_configuredCenterFrequencyHz);

    public double DetectPower(ReadOnlySpan<float> samples)
    {
        if (samples.Length != BlockSize)
            throw new ArgumentException($"A block must contain exactly {BlockSize} samples.", nameof(samples));

        int bestIndex = _lockedIndex;
        double bestPower = double.NegativeInfinity;
        for (int candidate = 0; candidate < _coefficients.Length; candidate++)
        {
            double power = DetectPowerAt(samples, _coefficients[candidate]);
            _instantPowers[candidate] = power;
            _smoothedPowers[candidate] = _havePower
                ? _smoothedPowers[candidate] + (PowerSmoothing * (power - _smoothedPowers[candidate]))
                : power;
            if (_smoothedPowers[candidate] > bestPower)
            {
                bestPower = _smoothedPowers[candidate];
                bestIndex = candidate;
            }
        }

        _havePower = true;
        TrackStableCandidate(bestIndex);
        return _instantPowers[_lockedIndex];
    }

    private double DetectPowerAt(ReadOnlySpan<float> samples, double coefficient)
    {
        double s1 = 0;
        double s2 = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double s0 = samples[i] * _window[i] + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }
        double power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return Math.Max(power / (BlockSize * BlockSize), 1e-20);
    }

    private void TrackStableCandidate(int bestIndex)
    {
        if (bestIndex == _lockedIndex)
        {
            _pendingIndex = -1;
            _pendingBlocks = 0;
            return;
        }

        if (bestIndex == _pendingIndex)
            _pendingBlocks++;
        else
        {
            _pendingIndex = bestIndex;
            _pendingBlocks = 1;
        }

        if (_pendingBlocks < LockConfirmationBlocks) return;
        _lockedIndex = _pendingIndex;
        _pendingIndex = -1;
        _pendingBlocks = 0;
    }
}
