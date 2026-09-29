// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers.Binary;

namespace Zeus.Protocol2;

/// <summary>
/// Folds back-to-back Protocol-2 hi-priority status payloads (UDP 1025, the
/// bytes after the 4-byte sequence header) into one pending payload so the
/// RX loop can dispatch the newest telemetry instead of replaying a socket
/// backlog one packet at a time.
///
/// Why: Saturn P2_app sends a fresh hi-priority message on every ADC overflow
/// detection with no sleep (OutHighPriority.c, "any overflow causes a new
/// message to be sent out"). TX leakage during high-power TUN overloads ADC0
/// continuously, the flood outruns the per-packet subscriber fan-out on the
/// shared RX thread, and the TX power meter / ADC protection read telemetry
/// that is a second or more stale (G2 bench, 2026-09-28).
///
/// Merge rules mirror what Thetis keeps from the same stream
/// (ChannelMaster/network.c case 0): every field is latest-wins except
/// <list type="bullet">
///   <item>byte 1 ADC overload bits — OR'd across the merged packets (Thetis
///   ORs until <c>getAndResetADC_Overload()</c> reads them);</item>
///   <item>bytes 35..38 ADC0/ADC1 max magnitude — max across the merged
///   packets, matching the firmware's own peak-hold per message.</item>
/// </list>
/// A payload whose status byte 0 (PTT / dot / dash / PLL / sidetone) or user
/// digital-input byte 55 differs from the pending one is never merged: the
/// caller flushes first, so every input edge is still delivered in order.
/// Single-threaded — owned by the RX loop.
/// </summary>
internal sealed class HiPriStatusCoalescer
{
    private const int StatusByteOffset = 0;
    private const int OverloadByteOffset = 1;
    private const int Adc0MagnitudeOffset = 35;
    private const int Adc1MagnitudeOffset = 37;
    private const int UserDigitalInOffset = 55;

    private readonly byte[] _pending;
    private readonly long _maxHoldTicks;
    private int _length;
    private int _packets;
    private long _firstPacketTicks;

    /// <param name="capacity">Largest payload the RX buffer can hold.</param>
    /// <param name="maxHoldTicks">
    /// Longest a pending payload may wait for the socket queue to drain, in
    /// <see cref="System.Diagnostics.Stopwatch"/> ticks, measured from the
    /// first packet folded into it. Bounds edge / overload latency when the
    /// socket never quite empties (e.g. under heavy IQ load).
    /// </param>
    public HiPriStatusCoalescer(int capacity, long maxHoldTicks)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxHoldTicks < 0) throw new ArgumentOutOfRangeException(nameof(maxHoldTicks));
        _pending = new byte[capacity];
        _maxHoldTicks = maxHoldTicks;
    }

    public bool HasPending => _packets > 0;

    /// <summary>Number of radio packets folded into <see cref="Pending"/>.</summary>
    public int PendingPacketCount => _packets;

    public ReadOnlySpan<byte> Pending => _pending.AsSpan(0, _length);

    /// <summary>
    /// True when <paramref name="payload"/> can be folded into the pending
    /// payload without hiding an input edge. False when nothing is pending.
    /// </summary>
    public bool CanMerge(ReadOnlySpan<byte> payload) =>
        _packets > 0
        && payload.Length == _length
        && payload[StatusByteOffset] == _pending[StatusByteOffset]
        && (_length <= UserDigitalInOffset
            || payload[UserDigitalInOffset] == _pending[UserDigitalInOffset]);

    /// <summary>
    /// Start a pending payload, or fold into the existing one. The caller
    /// must flush first when <see cref="CanMerge"/> is false.
    /// </summary>
    public void Add(ReadOnlySpan<byte> payload, long nowTicks)
    {
        if (payload.Length == 0 || payload.Length > _pending.Length)
            throw new ArgumentOutOfRangeException(nameof(payload));

        if (_packets == 0)
        {
            payload.CopyTo(_pending);
            _length = payload.Length;
            _firstPacketTicks = nowTicks;
            _packets = 1;
            return;
        }

        if (!CanMerge(payload))
            throw new InvalidOperationException("Flush the pending hi-priority payload before adding one with a different input state.");

        byte overload = (byte)(_pending[OverloadByteOffset] | payload[OverloadByteOffset]);
        ushort adc0 = MaxBeU16(payload, Adc0MagnitudeOffset);
        ushort adc1 = MaxBeU16(payload, Adc1MagnitudeOffset);

        payload.CopyTo(_pending);
        _pending[OverloadByteOffset] = overload;
        WriteBeU16IfPresent(Adc0MagnitudeOffset, adc0);
        WriteBeU16IfPresent(Adc1MagnitudeOffset, adc1);
        _packets++;
    }

    /// <summary>
    /// True when the pending payload has waited <c>maxHoldTicks</c> or longer
    /// and must be dispatched even though more datagrams are queued.
    /// </summary>
    public bool HeldTooLong(long nowTicks) =>
        _packets > 0 && nowTicks - _firstPacketTicks >= _maxHoldTicks;

    public void Clear()
    {
        _packets = 0;
        _length = 0;
    }

    private ushort MaxBeU16(ReadOnlySpan<byte> payload, int offset)
    {
        if (_length < offset + 2) return 0;
        ushort pending = BinaryPrimitives.ReadUInt16BigEndian(_pending.AsSpan(offset, 2));
        ushort incoming = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
        return Math.Max(pending, incoming);
    }

    private void WriteBeU16IfPresent(int offset, ushort value)
    {
        if (_length >= offset + 2)
            BinaryPrimitives.WriteUInt16BigEndian(_pending.AsSpan(offset, 2), value);
    }
}
