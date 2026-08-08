// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>Accumulating 10 Hz deadline, expressed in caller-owned clock ticks.</summary>
internal sealed class CwBroadcastCadence(long ticksPerSecond)
{
    private readonly long _periodTicks = Math.Max(1, ticksPerSecond / 10);
    private long _nextDeadline;
    private bool _initialized;

    public void Reset(long nowTicks)
    {
        _nextDeadline = nowTicks;
        _initialized = true;
    }

    public bool TryTake(long nowTicks)
    {
        if (!_initialized) Reset(nowTicks);
        if (nowTicks < _nextDeadline) return false;

        _nextDeadline += _periodTicks;
        // Clamp only genuine wake-up slips. Ordinary cadence always advances
        // from the previous deadline, so processing time cannot alias 10 Hz
        // down to a slower rate.
        if (nowTicks - _nextDeadline > _periodTicks)
            _nextDeadline = nowTicks + _periodTicks;
        return true;
    }

    /// <summary>
    /// Consume a deadline for a caller that must emit immediately. Advancing
    /// from the later of the accumulated deadline and the current time makes
    /// the forced emission part of the same 10 Hz budget.
    /// </summary>
    public void ConsumeForced(long nowTicks)
    {
        if (!_initialized) Reset(nowTicks);
        _nextDeadline = Math.Max(_nextDeadline, nowTicks) + _periodTicks;
    }
}
