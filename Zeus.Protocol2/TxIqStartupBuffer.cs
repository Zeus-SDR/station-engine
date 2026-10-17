// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Protocol2;

// Sender-owned startup reserve. Waiting for one source cadence lets a partial
// first WDSP burst build a lead without increasing the radio FIFO target.
internal sealed class TxIqStartupBuffer
{
    internal const int MaximumWaitMs = 20;
    private long _epoch = long.MinValue;
    private long _started;
    private bool _released;

    internal int WaitMilliseconds(long epoch, long now, long frequency, bool bypass)
    {
        if (epoch != _epoch)
        {
            _epoch = epoch;
            _started = now;
            _released = false;
        }
        if (_released) return 0;
        double remainingMs = MaximumWaitMs - (now - _started) * 1000.0 / frequency;
        if (bypass || remainingMs <= 0)
        {
            _released = true;
            return 0;
        }
        return Math.Max(1, (int)Math.Ceiling(remainingMs));
    }
}
