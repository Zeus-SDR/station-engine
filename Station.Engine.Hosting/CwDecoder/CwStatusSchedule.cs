// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// When an active decoder should emit an empty-text status frame. Text copy
/// keeps the 10 Hz <see cref="CwBroadcastCadence"/>. This clock is the slow
/// heartbeat, plus an immediate send when the decoder wakes or the tone moves.
/// </summary>
internal sealed class CwStatusSchedule(long ticksPerSecond)
{
    private readonly long _ticksPerSecond = Math.Max(1, ticksPerSecond);
    private readonly long _periodTicks = Math.Max(1, ticksPerSecond);
    private long _lastSentTicks = long.MinValue;
    private int _pitchHz = int.MinValue;
    private int _locked = int.MinValue;

    public bool ToneChanged(int pitchHz, bool locked)
    {
        int bit = locked ? 1 : 0;
        return pitchHz != _pitchHz || bit != _locked;
    }

    public bool HeartbeatDue(long nowTicks)
    {
        if (_lastSentTicks == long.MinValue) return true;
        return nowTicks - _lastSentTicks >= _periodTicks;
    }

    public void MarkSent(long nowTicks, int pitchHz, bool locked)
    {
        _lastSentTicks = nowTicks;
        _pitchHz = pitchHz;
        _locked = locked ? 1 : 0;
    }

    public void Reset()
    {
        _lastSentTicks = long.MinValue;
        _pitchHz = int.MinValue;
        _locked = int.MinValue;
    }

    /// <summary>Milliseconds until the next heartbeat. 0 means it is due now.</summary>
    public int MillisecondsUntilHeartbeat(long nowTicks)
    {
        if (_lastSentTicks == long.MinValue) return 0;
        long elapsed = nowTicks - _lastSentTicks;
        if (elapsed >= _periodTicks) return 0;
        long remain = _periodTicks - elapsed;
        long ms = (remain * 1000 + _ticksPerSecond - 1) / _ticksPerSecond;
        if (ms < 1) return 1;
        if (ms > int.MaxValue) return int.MaxValue;
        return (int)ms;
    }
}
