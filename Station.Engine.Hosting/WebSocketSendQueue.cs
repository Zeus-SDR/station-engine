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
    private const int MaxConsecutiveTelemetryBypasses = 4;
    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly LinkedList<byte[]> _items = new();
    private readonly SemaphoreSlim _available = new(0);
    private int _consecutiveTelemetryBypasses;
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
                var incomingType = FrameType(payload);
                // Audible PCM is realtime data, not ordinary telemetry. Prefer
                // throwing away a replaceable display/meter snapshot first.
                // When a low-priority snapshot arrives to a queue containing
                // only PCM and control edges, discard the new snapshot instead
                // of punching a hole in RX audio.
                var discardable = FindOldestLowPriorityTelemetry();
                if (discardable is null && incomingType is { } pcmType && IsRealtimePcm(pcmType))
                    discardable = FindOldestType(pcmType);
                if (discardable is null &&
                    (IsLowPriorityTelemetry(incomingType) || IsRealtimePcm(incomingType)))
                    return new(SendQueueChange.DroppedIncoming, incomingType);

                var dropped = discardable ??
                    FindOldestAudio() ??
                    FindOldestType(MsgType.NativeMicPcm) ??
                    _items.First!;
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
                    // Once the socket falls behind, sending a replaceable
                    // display/meter snapshot before already-queued PCM extends
                    // the audible gap. Skip only when PCM directly follows the
                    // leading telemetry run: control/product ordering is never
                    // crossed.
                    var next = first;
                    if (_consecutiveTelemetryBypasses < MaxConsecutiveTelemetryBypasses)
                        next = FirstPcmAfterLeadingTelemetry(first);

                    if (ReferenceEquals(next, first))
                        _consecutiveTelemetryBypasses = 0;
                    else
                        _consecutiveTelemetryBypasses++;

                    _items.Remove(next);
                    return next.Value;
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

    public int RemoveType(MsgType type)
    {
        int removed = 0;
        lock (_sync)
        {
            for (var node = _items.First; node is not null;)
            {
                var next = node.Next;
                if (FrameType(node.Value) == type)
                {
                    _items.Remove(node);
                    removed++;
                    // Keep the semaphore count aligned when the consumer has
                    // not already claimed this item's permit.
                    _available.Wait(0);
                }
                node = next;
            }
        }
        return removed;
    }

    internal IReadOnlyList<byte[]> Snapshot()
    {
        lock (_sync) return _items.ToArray();
    }

    private static MsgType? FrameType(byte[] payload) =>
        payload.Length == 0 ? null : (MsgType)payload[0];

    private LinkedListNode<byte[]>? FindOldestLowPriorityTelemetry()
    {
        for (var node = _items.First; node is not null; node = node.Next)
        {
            var queuedType = FrameType(node.Value);
            if (IsLowPriorityTelemetry(queuedType)) return node;
        }

        return null;
    }

    private LinkedListNode<byte[]>? FindOldestAudio() => FindOldestType(MsgType.AudioPcm);

    private LinkedListNode<byte[]>? FindOldestType(MsgType type)
    {
        for (var node = _items.First; node is not null; node = node.Next)
        {
            if (FrameType(node.Value) == type) return node;
        }
        return null;
    }

    private static LinkedListNode<byte[]> FirstPcmAfterLeadingTelemetry(
        LinkedListNode<byte[]> first)
    {
        if (!IsLowPriorityTelemetry(FrameType(first.Value))) return first;
        var node = first.Next;
        while (node is not null && IsLowPriorityTelemetry(FrameType(node.Value)))
            node = node.Next;

        return node is not null && IsRealtimePcm(FrameType(node.Value)) ? node : first;
    }

    private static bool IsRealtimePcm(MsgType? type) =>
        type is MsgType.AudioPcm or MsgType.NativeMicPcm;

    private static bool IsLowPriorityTelemetry(MsgType? type) => type is
        MsgType.DisplayFrame or
        MsgType.TxMeters or
        MsgType.TxMetersV2 or
        MsgType.PsMeters or
        MsgType.RxMeter or
        MsgType.RxMetersV2 or
        MsgType.RxMetersV2Secondary or
        MsgType.RxSignalQuality or
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
