// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 2 of the License, or (at your option)
// any later version. See the LICENSE file at the root of this repository.

using System.Threading.Channels;

namespace Zeus.Protocol2;

internal readonly record struct RevisionedTxIqPacket(byte[] Buffer, long SafetyRevision, int PeakQuantized = 0);

/// <summary>
/// Per-client packet storage. Returning a sent packet must not enter the shared
/// array pool, whose partition locks can block the dedicated sender.
/// </summary>
internal sealed class TxIqPacketPool
{
    // More than the largest queue cap (256), the producer and sender, and
    // concurrent reset/stale-drop owners. Cache pressure never drops IQ.
    internal const int Capacity = 512;
    private readonly byte[]?[] _available = new byte[Capacity][];
    private readonly int _packetLength;
    private readonly Func<int, byte[]> _allocate;
    private int _rentTicket, _returnTicket;

    public TxIqPacketPool(int packetLength, Func<int, byte[]>? allocate = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(packetLength);
        _packetLength = packetLength;
        _allocate = allocate ?? (static length => new byte[length]);
        for (int i = 0; i < Capacity; i++) _available[i] = _allocate(packetLength);
    }

    public byte[] Rent()
    {
        int start = Interlocked.Increment(ref _rentTicket);
        for (int offset = 0; offset < Capacity; offset++)
        {
            int slot = unchecked(start + offset) & (Capacity - 1);
            if (Interlocked.Exchange(ref _available[slot], null) is { } packet) return packet;
        }
        // Exhaustion preserves packet delivery. Normal bounded queues fit in
        // the preallocated cache; no shared pool is consulted even here.
        return _allocate(_packetLength);
    }

    public void Return(byte[]? packet)
    {
        if (packet is null) return;
        int start = Interlocked.Increment(ref _returnTicket);
        for (int offset = 0; offset < Capacity; offset++)
        {
            int slot = unchecked(start + offset) & (Capacity - 1);
            if (Interlocked.CompareExchange(ref _available[slot], packet, null) is null) return;
        }
        // A full cache drops only this reusable storage, never queued IQ.
        // Bound the sender's work instead of waiting for another owner.
    }

    public bool TryDrop(ChannelReader<RevisionedTxIqPacket> reader, Action onRemoved)
    {
        if (!reader.TryRead(out var packet)) return false;
        try { onRemoved(); }
        finally { Return(packet.Buffer); }
        return true;
    }

    public int Drain(ChannelReader<RevisionedTxIqPacket> reader, Action onRemoved)
    {
        int count = 0;
        while (TryDrop(reader, onRemoved)) count++;
        return count;
    }
}
