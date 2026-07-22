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

using System.Buffers;
using System.Threading.Channels;

namespace Zeus.Protocol2;

internal readonly record struct RevisionedTxIqPacket(byte[] Buffer, long SafetyRevision);

/// <summary>
/// Owns the pooled buffers queued to the synchronous Protocol-2 TX-IQ sender.
/// Queue-removal helpers return dropped packets before reporting completion so
/// reset and shutdown drains cannot strand rentals.
/// </summary>
internal sealed class TxIqPacketPool
{
    private readonly ArrayPool<byte> _pool;
    private readonly int _packetLength;

    public TxIqPacketPool(int packetLength, ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(packetLength);
        _packetLength = packetLength;
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    public byte[] Rent() => _pool.Rent(_packetLength);

    public void Return(byte[]? packet)
    {
        if (packet is not null) _pool.Return(packet);
    }

    public bool TryDrop(ChannelReader<RevisionedTxIqPacket> reader, Action onRemoved)
    {
        if (!reader.TryRead(out var packet)) return false;
        try
        {
            onRemoved();
        }
        finally
        {
            Return(packet.Buffer);
        }
        return true;
    }

    public int Drain(ChannelReader<RevisionedTxIqPacket> reader, Action onRemoved)
    {
        int count = 0;
        while (TryDrop(reader, onRemoved)) count++;
        return count;
    }
}
