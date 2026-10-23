// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace Zeus.Protocol2;

// Single owner: the TX sender. Epoch changes identify explicit stream resets,
// rather than mistaking an empty producer queue during a stall for an unkey.
internal sealed class TxIqSendTiming
{
    private long? _lastTimestamp;
    private readonly long[] _phaseMaxTicks = new long[4];
    private long _phaseTicksSinceLastSend;
    private long _maxUnaccountedTicks;
    private long _unaccountedAtMaxGapTicks;
    private long _maxAttributedGapTicks;
    private long _lastGcPauseTicks;
    private long _gcCorrelationMaxGapTicks;
    private long _gcPauseDeltaAtMaxGapTicks;

    internal enum Phase { Input, Pacing, SendGate, Datagram }

    internal void RecordPhase(Phase phase, long elapsedTicks, long epoch)
    {
        // Ignore the initial queue wait and explicit unkey/rekey idle time.
        // A failed first send can report a window before Record observes reset.
        if (epoch != _epoch)
        {
            Array.Clear(_phaseMaxTicks);
            _lastTimestamp = null;
            _phaseTicksSinceLastSend = 0;
            _epoch = epoch;
        }
        if (_lastTimestamp is null) return;
        int index = (int)phase;
        _phaseMaxTicks[index] = Math.Max(_phaseMaxTicks[index], elapsedTicks);
        _phaseTicksSinceLastSend += Math.Max(0, elapsedTicks);
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
        => Record(timestamp, epoch, GC.GetTotalPauseDuration().Ticks);

    internal void Record(long timestamp, long epoch, long totalPauseTicks)
    {
        if (_lastTimestamp is { } previous && epoch == _epoch)
        {
            long gap = timestamp - previous;
            if (gap > _gcCorrelationMaxGapTicks)
            {
                _gcCorrelationMaxGapTicks = gap;
                _gcPauseDeltaAtMaxGapTicks = Math.Max(0, totalPauseTicks - _lastGcPauseTicks);
            }
            // Phases are disjoint and accumulated for this exact successful
            // send interval, including retries. Window maxima cannot be summed.
            long unaccounted = Math.Max(0, gap - _phaseTicksSinceLastSend);
            _maxUnaccountedTicks = Math.Max(_maxUnaccountedTicks, unaccounted);
            if (gap > _maxAttributedGapTicks)
            {
                _maxAttributedGapTicks = gap;
                _unaccountedAtMaxGapTicks = unaccounted;
            }
            _maxGapTicks = Math.Max(_maxGapTicks, gap);
            if (gap > Stopwatch.Frequency / 100) _gapsOver10Ms++;
        }
        if (_lastTimestamp is not null && epoch != _epoch) Array.Clear(_phaseMaxTicks);
        _lastTimestamp = timestamp;
        _lastGcPauseTicks = totalPauseTicks;
        _phaseTicksSinceLastSend = 0;
        _epoch = epoch;
    }

    // Process-wide counter correlation, not time attributable to this sender.
    // Counter/timestamp reads are not atomic and pauses may overlap measured
    // phases. Never subtract this delta from the unaccounted duration.
    internal (long MaxGapUs, long GcPauseDeltaUsAtMaxGap) TakeGcCorrelation()
    {
        var result = (Microseconds(_gcCorrelationMaxGapTicks), _gcPauseDeltaAtMaxGapTicks / 10);
        _gcCorrelationMaxGapTicks = 0;
        _gcPauseDeltaAtMaxGapTicks = 0;
        return result;
    }

    internal (long MaxGapUs, int GapsOver10Ms) TakeWindow()
    {
        var window = ((long)(_maxGapTicks * 1_000_000.0 / Stopwatch.Frequency), _gapsOver10Ms);
        _maxGapTicks = 0;
        _gapsOver10Ms = 0;
        return window;
    }

    internal (long MaxUnaccountedUs, long UnaccountedAtMaxGapUs) TakeUnaccounted()
    {
        var result = (Microseconds(_maxUnaccountedTicks), Microseconds(_unaccountedAtMaxGapTicks));
        _maxUnaccountedTicks = 0;
        _unaccountedAtMaxGapTicks = 0;
        _maxAttributedGapTicks = 0;
        // Reporting can occur during a gap. Retain its accumulated phases.
        return result;
    }
}
