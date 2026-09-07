// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace Zeus.Protocol2;

// Single owner: the TX sender. Epoch changes identify explicit stream resets,
// rather than mistaking an empty producer queue during a stall for an unkey.
internal sealed class TxIqSendTiming
{
    private long? _lastTimestamp;
    private readonly long[] _phaseMaxTicks = new long[4];

    internal enum Phase { Input, Pacing, SendGate, Datagram }

    internal void RecordPhase(Phase phase, long elapsedTicks, long epoch)
    {
        // Ignore the initial queue wait and explicit unkey/rekey idle time.
        // A failed first send can report a window before Record observes reset.
        if (epoch != _epoch)
        {
            Array.Clear(_phaseMaxTicks);
            _lastTimestamp = null;
            _epoch = epoch;
        }
        if (_lastTimestamp is null) return;
        int index = (int)phase;
        _phaseMaxTicks[index] = Math.Max(_phaseMaxTicks[index], elapsedTicks);
    }

    internal (long InputUs, long PacingUs, long SendGateUs, long DatagramUs) TakePhases()
    {
        var result = (Microseconds(_phaseMaxTicks[0]), Microseconds(_phaseMaxTicks[1]),
            Microseconds(_phaseMaxTicks[2]), Microseconds(_phaseMaxTicks[3]));
        Array.Clear(_phaseMaxTicks);
        return result;
    }

    internal static long Microseconds(long ticks) => (long)(ticks * 1_000_000.0 / Stopwatch.Frequency);

    internal static void RecordMaximum(ref long maximum, long elapsedTicks)
    {
        long previous = Volatile.Read(ref maximum);
        while (elapsedTicks > previous)
        {
            long observed = Interlocked.CompareExchange(ref maximum, elapsedTicks, previous);
            if (observed == previous) return;
            previous = observed;
        }
    }

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
        if (_lastTimestamp is not null && epoch != _epoch) Array.Clear(_phaseMaxTicks);
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
