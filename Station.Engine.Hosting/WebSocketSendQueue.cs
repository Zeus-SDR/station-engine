// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using Zeus.Contracts;

namespace Zeus.Server;

internal enum SendQueueChange
{
    Enqueued,
    Coalesced,
    DroppedOldest,
    DroppedIncoming,
    Completed,
}

internal readonly record struct SendQueueResult(
    SendQueueChange Change,
    MsgType? DroppedType = null);

/// <summary>
/// Bounded multi-producer/single-consumer websocket queue. Display frames are
/// full snapshots, so a newer snapshot replaces the queued snapshot for the
/// same receiver stream in place. When full, high-rate telemetry is discarded
/// before control-plane frames so chat and state edges are not displaced by
/// audio, display, or meter traffic.
/// </summary>
internal sealed class WebSocketSendQueue
{
    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly LinkedList<byte[]> _items = new();
    private readonly SemaphoreSlim _available = new(0);
    private bool _completed;

    public WebSocketSendQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public SendQueueResult Enqueue(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_sync)
        {
            if (_completed) return new(SendQueueChange.Completed);

            if (TryGetDisplayStream(payload, out byte stream))
            {
                for (var node = _items.First; node is not null; node = node.Next)
                {
                    if (TryGetDisplayStream(node.Value, out byte queuedStream) && queuedStream == stream)
                    {
                        node.Value = payload;
                        return new(SendQueueChange.Coalesced);
                    }
                }
            }

            MsgType? droppedType = null;
            if (_items.Count == _capacity)
            {
                var discardable = FindOldestDiscardable();
                if (discardable is null && IsDiscardable(payload))
                    return new(SendQueueChange.DroppedIncoming, FrameType(payload));

                var dropped = discardable ?? _items.First!;
                droppedType = FrameType(dropped.Value);
                _items.Remove(dropped);
            }

            _items.AddLast(payload);
            if (droppedType is null)
            {
                _available.Release();
                return new(SendQueueChange.Enqueued);
            }

            return new(SendQueueChange.DroppedOldest, droppedType);
        }
    }

    public async ValueTask<byte[]?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (_items.First is { } first)
                {
                    _items.RemoveFirst();
                    return first.Value;
                }

                if (_completed) return null;
            }
        }
    }

    public void Complete()
    {
        lock (_sync)
        {
            if (_completed) return;
            _completed = true;
            _available.Release();
        }
    }

    internal IReadOnlyList<byte[]> Snapshot()
    {
        lock (_sync) return _items.ToArray();
    }

    private static MsgType? FrameType(byte[] payload) =>
        payload.Length == 0 ? null : (MsgType)payload[0];

    private LinkedListNode<byte[]>? FindOldestDiscardable()
    {
        for (var node = _items.First; node is not null; node = node.Next)
        {
            if (IsDiscardable(node.Value)) return node;
        }

        return null;
    }

    private static bool IsDiscardable(byte[] payload) => FrameType(payload) is
        MsgType.DisplayFrame or
        MsgType.AudioPcm or
        MsgType.TxMeters or
        MsgType.TxMetersV2 or
        MsgType.PsMeters or
        MsgType.RxMeter or
        MsgType.RxMetersV2 or
        MsgType.PaTemp or
        MsgType.MicPeak or
        MsgType.DiagnosticsHealth;

    private static bool TryGetDisplayStream(byte[] payload, out byte stream)
    {
        if (payload.Length > WireFormat.HeaderSize && payload[0] == (byte)MsgType.DisplayFrame)
        {
            stream = payload[WireFormat.HeaderSize];
            return true;
        }

        stream = 0;
        return false;
    }
}
