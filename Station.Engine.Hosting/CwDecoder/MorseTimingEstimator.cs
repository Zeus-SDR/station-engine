// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>Rolling lower-cluster estimate of the sender's dit duration.</summary>
internal sealed class MorseTimingEstimator
{
    private const int WindowSize = 48;
    private const double MinDitMs = 24.0;
    private const double MaxDitMs = 240.0;
    private readonly double[] _durations = new double[WindowSize];
    private readonly double[] _scratch = new double[WindowSize];
    private int _count;
    private int _cursor;
    private int _sinceEstimate;
    private double _ditMs;

    public MorseTimingEstimator(double initialWpm = 20)
    {
        _ditMs = Math.Clamp(1200.0 / initialWpm, MinDitMs, MaxDitMs);
    }

    public double DitMs => _ditMs;
    public double DahThresholdMs => 1.5 * _ditMs;
    public double LetterGapThresholdMs => 3.0 * _ditMs;
    public double WordGapThresholdMs => 5.5 * _ditMs;
    public double ElementGapThresholdMs => 1.2 * _ditMs;
    public double Wpm => 1200.0 / _ditMs;

    public bool IsDah(double durationMs) => durationMs >= DahThresholdMs;

    public void ObserveElement(double durationMs)
    {
        if (!double.IsFinite(durationMs) || durationMs <= 0) return;
        durationMs = Math.Clamp(durationMs, MinDitMs * 0.35, MaxDitMs * 4.0);
        _durations[_cursor] = durationMs;
        _cursor = (_cursor + 1) % WindowSize;
        if (_count < WindowSize) _count++;

        // Fast lower-cluster tracking gives a cold decoder useful timing in
        // the first character; the periodic clustering below rejects dahs.
        if (durationMs < DahThresholdMs)
            _ditMs = Math.Clamp(_ditMs + 0.28 * (durationMs - _ditMs), MinDitMs, MaxDitMs);

        if (++_sinceEstimate >= 6)
        {
            _sinceEstimate = 0;
            Reestimate();
        }
    }

    private void Reestimate()
    {
        if (_count < 3) return;
        Array.Copy(_durations, _scratch, _count);
        Array.Sort(_scratch, 0, _count);

        // The lower timing cluster is dits. Split at the largest adjacent
        // ratio; with only dits present, use the lower two-thirds median.
        int split = Math.Max(1, (_count * 2) / 3);
        double bestRatio = 1.0;
        for (int i = 1; i < _count; i++)
        {
            double ratio = _scratch[i] / Math.Max(_scratch[i - 1], 1e-9);
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                split = i;
            }
        }
        if (bestRatio < 1.45) split = Math.Max(1, (_count * 2) / 3);

        double median = _scratch[(split - 1) / 2];
        _ditMs = Math.Clamp((_ditMs * 0.35) + (median * 0.65), MinDitMs, MaxDitMs);
    }

    public void Reset(double initialWpm = 20)
    {
        Array.Clear(_durations);
        _count = 0;
        _cursor = 0;
        _sinceEstimate = 0;
        _ditMs = Math.Clamp(1200.0 / initialWpm, MinDitMs, MaxDitMs);
    }
}
