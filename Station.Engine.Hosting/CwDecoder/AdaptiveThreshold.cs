// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>Noise-floor tracker with separate key-on and key-off thresholds.</summary>
internal sealed class AdaptiveThreshold
{
    private const double OnRatio = 12.0;
    private const double OffRatio = 5.0;
    private const double NoiseAlpha = 0.045;
    private const int WarmupBlocks = 6;
    private const double DigitalZeroPower = 1e-20;
    // 375 detector blocks are exactly two seconds at 256 samples / 48 kHz.
    private const int DigitalZeroReacquireBlocks = 375;
    // -120 dBFS power is below practical post-AGC receiver noise but keeps a
    // squelched digital-zero interval from collapsing the release threshold.
    private const double MinimumNoiseFloor = 1e-12;

    private double _noiseFloor;
    private double _lastPower;
    private double _snrDb;
    private int _blocks;
    private int _digitalZeroBlocks;
    private bool _hasNonZeroInput;

    public bool TonePresent { get; private set; }

    public double SnrDb => _snrDb;
    internal double NoiseFloor => _noiseFloor;

    public bool Update(double tonePower)
    {
        if (tonePower <= DigitalZeroPower)
        {
            if (_digitalZeroBlocks < DigitalZeroReacquireBlocks) _digitalZeroBlocks++;
            if (_blocks == 0) _noiseFloor = MinimumNoiseFloor;
            else _noiseFloor = Math.Max(_noiseFloor, MinimumNoiseFloor);
            TonePresent = false;
            return false;
        }

        tonePower = Math.Max(tonePower, MinimumNoiseFloor);
        _lastPower = tonePower;

        // Reacquire the real receiver floor instead of treating the first
        // post-squelch noise block as a key-down edge. A cold decoder does not
        // rebase, so leading silence cannot hide its first keyed element.
        bool reacquire = _hasNonZeroInput
            && _digitalZeroBlocks >= DigitalZeroReacquireBlocks;
        _digitalZeroBlocks = 0;
        _hasNonZeroInput = true;
        if (reacquire)
        {
            _noiseFloor = tonePower;
            _blocks = 1;
            return false;
        }

        if (_blocks++ == 0)
        {
            _noiseFloor = Math.Max(tonePower, MinimumNoiseFloor);
            return false;
        }

        if (_blocks <= WarmupBlocks)
        {
            _noiseFloor = Math.Max(
                MinimumNoiseFloor,
                _noiseFloor + 0.25 * (tonePower - _noiseFloor));
            return false;
        }

        double threshold = _noiseFloor * (TonePresent ? OffRatio : OnRatio);
        if (TonePresent)
        {
            if (tonePower < threshold) TonePresent = false;
        }
        else if (tonePower > threshold)
        {
            TonePresent = true;
        }

        if (TonePresent)
        {
            double ratio = _lastPower / Math.Max(_noiseFloor, MinimumNoiseFloor);
            _snrDb = Math.Clamp(10.0 * Math.Log10(Math.Max(ratio, 1e-12)), -30.0, 60.0);
        }

        // A keyed block must not pull the noise estimate toward the signal.
        // During key-up, the EMA follows AGC/QSB changes in roughly 120 ms.
        if (!TonePresent)
            _noiseFloor = Math.Max(
                MinimumNoiseFloor,
                _noiseFloor + NoiseAlpha * (tonePower - _noiseFloor));

        return TonePresent;
    }

    public void Reset()
    {
        _noiseFloor = 0;
        _lastPower = 0;
        _blocks = 0;
        _snrDb = 0;
        _digitalZeroBlocks = 0;
        _hasNonZeroInput = false;
        TonePresent = false;
    }
}
