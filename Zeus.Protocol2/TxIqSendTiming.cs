// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace Zeus.Protocol2;

// Single owner: the TX sender. Epoch changes identify explicit stream resets,
// rather than mistaking an empty producer queue during a stall for an unkey.
internal sealed class TxIqSendTiming
{
    private long? _lastTimestamp;
    private long _epoch;
    private long _maxGapTicks;
    private int _gapsOver10Ms;

    internal void Record(long timestamp, long epoch)
    {
        if (_lastTimestamp is { } previous && epoch == _epoch)
        {
            long gap = timestamp - previous;
            _maxGapTicks = Math.Max(_maxGapTicks, gap);
            if (gap > Stopwatch.Frequency / 100) _gapsOver10Ms++;
        }
        _lastTimestamp = timestamp;
        _epoch = epoch;
    }

    internal (long MaxGapUs, int GapsOver10Ms) TakeWindow()
    {
        var window = ((long)(_maxGapTicks * 1_000_000.0 / Stopwatch.Frequency), _gapsOver10Ms);
        _maxGapTicks = 0;
        _gapsOver10Ms = 0;
        return window;
    }
}
