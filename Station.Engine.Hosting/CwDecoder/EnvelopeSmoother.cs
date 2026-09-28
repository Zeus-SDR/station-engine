// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Moving average on the Goertzel amplitude.
///
/// One 5.3 ms block of noise is an exponential power draw, so a per-block
/// threshold chatters at the SNRs where a dit is still obvious by ear.
/// Averaging about 0.3 dit of amplitude (clamped to 1..8 blocks) puts the
/// decision on the dit, not on the block. A 50 WPM dit is about four blocks,
/// so the floor is one block: two blocks of average, plus the quiet samples
/// already in the window, trim the rise and bridge the element gap. The
/// average is linear-phase:
/// both edges move by the same group delay, <see cref="GroupDelayBlocks"/>,
/// and a mark keeps its length. Callers subtract the change in that delay
/// when the window is resized between the two edges.
/// </summary>
internal sealed class EnvelopeSmoother
{
    public const int MinLength = 1;
    public const int MaxLength = 8;
    public const double DitFraction = 0.30;

    private readonly double[] _ring = new double[MaxLength];
    private int _count;
    private int _cursor;
    private int _length = 3;

    public int Length => _length;

    /// <summary>Delay of the causal average, in blocks. (N − 1) / 2.</summary>
    public double GroupDelayBlocks => (_length - 1) * 0.5;

    public void SetDit(double ditMs, double blockMs)
    {
        if (!(ditMs > 0) || !(blockMs > 0)) return;
        double target = DitFraction * ditMs / blockMs;
        int n = (int)Math.Round(target, MidpointRounding.AwayFromZero);
        if (n < MinLength) n = MinLength;
        if (n > MaxLength) n = MaxLength;
        // An odd window lands its midpoint on a block. 2 stays even: it is
        // the window for a 30–40 ms dit. 8 is the slow-dit cap.
        if (n > 2 && n < MaxLength && (n & 1) == 0)
        {
            int down = n - 1;
            int up = n + 1;
            n = Math.Abs(down - target) <= Math.Abs(up - target) ? down : up;
        }

        _length = n;
    }

    public double Push(double amplitude)
    {
        if (!double.IsFinite(amplitude) || amplitude < 0) amplitude = 0;
        _ring[_cursor] = amplitude;
        _cursor++;
        if (_cursor == MaxLength) _cursor = 0;
        if (_count < MaxLength) _count++;
        return Average();
    }

    /// <summary>Drop stored samples. The window length stays.</summary>
    public void Clear()
    {
        Array.Clear(_ring);
        _count = 0;
        _cursor = 0;
    }

    public void Reset()
    {
        Clear();
        _length = 3;
    }

    private double Average()
    {
        int n = _length;
        if (n > _count) n = _count;
        if (n <= 0) return 0;
        double sum = 0;
        int index = _cursor - 1;
        if (index < 0) index = MaxLength - 1;
        for (int i = 0; i < n; i++)
        {
            sum += _ring[index];
            index--;
            if (index < 0) index = MaxLength - 1;
        }

        return sum / n;
    }
}
