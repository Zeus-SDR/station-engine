// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), and contributors.

using System.Diagnostics;

namespace Zeus.Protocol1;

/// <summary>
/// Radio-clock-slaved pacing state for non-MOX EP2 speaker-audio packets.
/// Credits accrue from the RX receive loop as it processes the radio's EP6
/// packet flow, so they track the radio's clock rate. The TX loop
/// spends those credits at the codec cadence and may borrow a bounded amount
/// while buffered audio exists, allowing an inline DSP tick to stall the RX
/// thread without immediately starving the radio. Catch-up credits repay that
/// debt; they never cause back-to-back EP2 sends.
/// </summary>
internal sealed class P1AudioEgressPacer
{
    internal const int SamplesPerPacket = 2 * ControlFrame.IqSamplesPerUsbFrame;
    internal const int MaxBorrowedPackets = 128; // 336 ms at 48 kHz
    internal static readonly long PacketIntervalTicks = Math.Max(
        1,
        (long)Math.Round(Stopwatch.Frequency * SamplesPerPacket / 48_000.0));

    private long _credits;
    private long _nextSendTicks;

    internal long Credits => Interlocked.Read(ref _credits);
    internal bool Active => Volatile.Read(ref _nextSendTicks) != 0;

    internal void AddCredit()
    {
        while (true)
        {
            long current = Interlocked.Read(ref _credits);
            long next = Math.Min(MaxBorrowedPackets, current + 1);
            if (Interlocked.CompareExchange(ref _credits, next, current) == current) return;
        }
    }

    /// <summary>
    /// Return the monotonic delay until one packet may be sent. Null means the
    /// pacer needs another radio credit before it can start or continue.
    /// </summary>
    internal TimeSpan? DelayUntilSend(long nowTicks)
    {
        if (_nextSendTicks == 0)
        {
            if (Credits <= 0) return null;
            _nextSendTicks = nowTicks;
        }

        if (Credits <= -MaxBorrowedPackets) return null;
        long remaining = _nextSendTicks - nowTicks;
        return remaining <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(remaining / (double)Stopwatch.Frequency);
    }

    internal void RecordSend(long nowTicks)
    {
        Interlocked.Decrement(ref _credits);

        // Never catch up with a burst. If the worker woke late, re-anchor the
        // next slot to the actual send time; radio credits still enforce the
        // long-term average and bound drift in either direction.
        long scheduled = _nextSendTicks + PacketIntervalTicks;
        _nextSendTicks = scheduled > nowTicks ? scheduled : nowTicks + PacketIntervalTicks;
    }

    internal void Reset()
    {
        Interlocked.Exchange(ref _credits, 0);
        _nextSendTicks = 0;
    }
}
