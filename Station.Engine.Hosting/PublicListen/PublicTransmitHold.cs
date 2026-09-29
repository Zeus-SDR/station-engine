// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.PublicListen;

/// <summary>
/// "The station is on the air" latch for Public Listening (invariant 1 in
/// docs/designs/public-listening.md). Held while ANY transmit signal is set —
/// MOX, TUN, two-tone, hardware PTT, the radio-keyed flag or the DSP's RX
/// suppression latch — and for a fixed <see cref="Tail"/> after the last one
/// drops. The tail is deliberately independent of the operator's
/// <c>TxPostTxRxMuteDelayMs</c> (which may be 0): listeners must never hear
/// the TX→RX turnaround, the operator's own over, or the key-up transient.
///
/// Signals are polled on every <see cref="IsHeld"/> read (producer threads)
/// and edges can also be pushed with <see cref="NoteTransmitEdge"/> so a key-down
/// shorter than a poll interval still starts the tail.
///
/// <see cref="IsHeld"/> answers "is the station on the air NOW". Audio that was
/// CAPTURED while keyed but reaches the tap later (a sidecar poll backlog, a
/// DSP output ring) is caught by <see cref="WasCapturedWhileHeld"/>, which
/// compares the block's own capture time against the wall-clock
/// <see cref="LastKeyedUnixMs"/> plus <see cref="Tail"/>.
/// </summary>
public sealed class PublicTransmitHold
{
    /// <summary>Minimum silence after the last transmit signal drops.</summary>
    public static readonly TimeSpan DefaultTail = TimeSpan.FromMilliseconds(400);

    private const long Never = long.MinValue;

    private readonly Func<bool> _isTransmitting;
    private readonly TimeProvider _time;
    private int _edgeKeyed;
    private long _lastKeyedTimestamp = Never;
    private long _lastKeyedUnixMs = Never;

    public PublicTransmitHold(Func<bool> isTransmitting, TimeProvider? time = null, TimeSpan? tail = null)
    {
        _isTransmitting = isTransmitting ?? throw new ArgumentNullException(nameof(isTransmitting));
        _time = time ?? TimeProvider.System;
        Tail = tail ?? DefaultTail;
        if (Tail < TimeSpan.FromMilliseconds(300))
            throw new ArgumentOutOfRangeException(nameof(tail), "Public Listening transmit tail must be at least 300 ms.");
    }

    public TimeSpan Tail { get; }

    /// <summary>
    /// True while transmitting and for <see cref="Tail"/> afterwards. A signal
    /// source that throws is treated as transmitting (fail closed).
    /// </summary>
    public bool IsHeld
    {
        get
        {
            long now = _time.GetTimestamp();
            bool keyed;
            try { keyed = Volatile.Read(ref _edgeKeyed) != 0 || _isTransmitting(); }
            catch { keyed = true; }
            if (keyed)
            {
                StampKeyed(now);
                return true;
            }

            long last = Interlocked.Read(ref _lastKeyedTimestamp);
            return last != Never && _time.GetElapsedTime(last, now) < Tail;
        }
    }

    /// <summary>
    /// Push a transmit edge (e.g. RadioService.MoxChanged). Both edges stamp the
    /// latch so the tail always runs from the most recent key activity.
    /// </summary>
    public void NoteTransmitEdge(bool keyed)
    {
        Volatile.Write(ref _edgeKeyed, keyed ? 1 : 0);
        StampKeyed(_time.GetTimestamp());
    }

    /// <summary>
    /// Record that a transmit signal the polled sources cannot see (e.g. the
    /// Protocol 3 sidecar's TX-IQ egress) is active right now. Starts/extends
    /// the tail exactly like a polled keyed signal.
    /// </summary>
    public void NoteKeyed() => StampKeyed(_time.GetTimestamp());

    /// <summary>
    /// Wall-clock (Unix ms, from the same <see cref="TimeProvider"/>) of the most
    /// recent moment any transmit signal was observed; <see cref="long.MinValue"/>
    /// when the station has never keyed. Reading it polls the signals first, so
    /// an ongoing transmission always reports "now".
    /// </summary>
    public long LastKeyedUnixMs
    {
        get
        {
            _ = IsHeld;
            return Interlocked.Read(ref _lastKeyedUnixMs);
        }
    }

    /// <summary>
    /// True when audio whose OLDEST sample was captured at
    /// <paramref name="captureUnixMs"/> (wall clock, Unix ms) falls inside a
    /// transmission or its <see cref="Tail"/>: <c>capture ≤ LastKeyedUnixMs + Tail</c>.
    /// A non-finite capture time is treated as captured now.
    /// </summary>
    public bool WasCapturedWhileHeld(double captureUnixMs)
    {
        long last = LastKeyedUnixMs;
        if (last == Never) return false;
        if (!double.IsFinite(captureUnixMs))
            captureUnixMs = (_time.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalMilliseconds;
        return captureUnixMs <= last + Tail.TotalMilliseconds;
    }

    private void StampKeyed(long timestamp)
    {
        Interlocked.Exchange(ref _lastKeyedTimestamp, timestamp);
        Interlocked.Exchange(ref _lastKeyedUnixMs, _time.GetUtcNow().ToUnixTimeMilliseconds());
    }
}
