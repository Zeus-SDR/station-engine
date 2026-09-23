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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Dsp;

/// <summary>
/// Peak-hold for the PureSignal observed-TX-peak readout. The radio streams
/// PS feedback blocks continuously, keyed or not, and each block covers only
/// ~5 ms of TX reference. A per-block reading therefore collapses to zero in
/// every speech gap and the moment the operator unkeys, which leaves nothing
/// to set HW peak from. This holds the highest block peak for
/// <see cref="HoldMs"/>, then lets it fall exponentially while keyed so a
/// lowered drive is still reflected. While unkeyed the value is frozen: the
/// silent blocks the radio keeps sending never erase the last over's peak.
/// </summary>
/// <remarks>
/// Not thread-safe; the owner serialises access (WdspDspEngine holds
/// <c>_psLock</c>). Time is caller-supplied milliseconds so decay is
/// independent of the P1/P2 feedback block rate and testable.
/// </remarks>
internal sealed class PsObservedPeakHold
{
    /// <summary>How long a new peak is held before it may decay.</summary>
    public const long HoldMs = 2000;

    /// <summary>Exponential decay time constant after the hold (≈ 8.7 dB/s).</summary>
    public const double DecayTauMs = 1000.0;

    private double _held;
    private long _heldSinceMs;
    private long _lastObserveMs;

    /// <summary>The held peak magnitude; 0 until a non-zero block is seen.</summary>
    public double Value => _held;

    /// <summary>Fold one feedback block's peak magnitude into the hold.</summary>
    public void Observe(double blockPeak, bool keyed, long nowMs)
    {
        if (!double.IsFinite(blockPeak) || blockPeak < 0) blockPeak = 0;

        if (blockPeak >= _held)
        {
            _held = blockPeak;
            _heldSinceMs = nowMs;
        }
        else if (!keyed)
        {
            // Freeze, and restart the hold so the previous over's peak stays
            // readable for HoldMs into the next over before it can fall.
            _heldSinceMs = nowMs;
        }
        else
        {
            long decayFromMs = Math.Max(_lastObserveMs, _heldSinceMs + HoldMs);
            if (nowMs > decayFromMs)
            {
                _held *= Math.Exp(-(nowMs - decayFromMs) / DecayTauMs);
                _held = Math.Max(_held, blockPeak);
            }
        }

        _lastObserveMs = nowMs;
    }

    /// <summary>Drop any held peak (PS arm / disarm).</summary>
    public void Reset()
    {
        _held = 0;
        _heldSinceMs = 0;
        _lastObserveMs = 0;
    }
}
