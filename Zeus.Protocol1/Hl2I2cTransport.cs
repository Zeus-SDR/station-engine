// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;

namespace Zeus.Protocol1;

public interface IHl2I2cTransport
{
    Task<uint> ReadAsync(byte address, byte register, CancellationToken cancellationToken);
    Task WriteAsync(byte address, byte register, byte value, CancellationToken cancellationToken);
}

/// <summary>One acknowledged bus-2 transaction at a time, carried by the existing EP2 loop.</summary>
internal sealed class Hl2I2cTransport : IHl2I2cTransport
{
    private sealed class Request(uint command)
    {
        public uint Command { get; } = command;
        public bool Sent { get; set; }
        public TaskCompletionSource<uint> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _sync = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly TimeSpan _timeout;
    private Request? _pending;
    private bool _available;
    private long _generation;
    private long _lastSendMs = long.MinValue;

    internal Hl2I2cTransport(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromMilliseconds(500);

    internal void SetAvailable(bool available)
    {
        lock (_sync)
        {
            _generation++;
            _available = available;
            _pending?.Completion.TrySetException(new IOException("HL2 I²C connection ended."));
            _pending = null;
            _lastSendMs = long.MinValue;
        }
    }

    public Task<uint> ReadAsync(byte address, byte register, CancellationToken cancellationToken) =>
        ExchangeAsync(address, register, 0, read: true, cancellationToken);

    public async Task WriteAsync(byte address, byte register, byte value, CancellationToken cancellationToken) =>
        _ = await ExchangeAsync(address, register, value, read: false, cancellationToken).ConfigureAwait(false);

    private async Task<uint> ExchangeAsync(byte address, byte register, byte value, bool read, CancellationToken ct)
    {
        if (address > 0x7F) throw new ArgumentOutOfRangeException(nameof(address));
        long generation;
        lock (_sync) generation = _generation;
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        Request? request = null;
        try
        {
            lock (_sync)
            {
                if (!_available || generation != _generation)
                    throw new IOException("HL2 I²C is unavailable; reconnect the radio.");
                ct.ThrowIfCancellationRequested();
                request = new Request(((read ? 7u : 6u) << 24) | ((uint)(address | 0x80) << 16) | ((uint)register << 8) | value);
                _pending = request;
            }
            return await request.Completion.Task.WaitAsync(_timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (request is not null && ReferenceEquals(_pending, request))
                {
                    // The protocol has no transaction ID. After an unanswered
                    // send, a late response must never satisfy a later read.
                    if (request.Sent && !request.Completion.Task.IsCompleted) _available = false;
                    _pending = null;
                }
            }
            _serial.Release();
        }
    }

    internal bool TryWritePacket(Span<byte> packet, ControlFrame.CcRegister firstRegister, long nowMs)
    {
        if (packet.Length != ControlFrame.PacketLength
            || firstRegister is not (ControlFrame.CcRegister.RxFreq or ControlFrame.CcRegister.TxFreq)) return false;
        // Eight-byte Metis header plus three-byte USB-frame sync precede C0.
        return TryWriteControl(packet.Slice(11, 5), nowMs);
    }

    internal bool TryWriteControl(Span<byte> control, long nowMs)
    {
        if (control.Length < 5) return false;
        lock (_sync)
        {
            if (!_available || _pending is not { Sent: false } request) return false;
            if (_lastSendMs != long.MinValue && nowMs - _lastSendMs < 10) return false;
            control[0] = (byte)(0xFA | (control[0] & 1)); // RQST, bus 2, preserve MOX.
            BinaryPrimitives.WriteUInt32BigEndian(control[1..], request.Command);
            request.Sent = true;
            _lastSendMs = nowMs;
            return true;
        }
    }

    internal void AcceptPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 1032 || packet[0] != 0xEF || packet[1] != 0xFE || packet[2] != 1 || packet[3] != 6) return;
        for (int offset = 8; offset <= 520; offset += 512)
        {
            if (packet[offset] != 0x7F || packet[offset + 1] != 0x7F || packet[offset + 2] != 0x7F) return;
        }
        AcceptControl(packet.Slice(11, 5));
        AcceptControl(packet.Slice(523, 5));
    }

    internal void AcceptControl(ReadOnlySpan<byte> control)
    {
        if (control.Length < 5 || (control[0] & 0x80) == 0) return;
        int address = (control[0] >> 1) & 0x3F;
        uint data = BinaryPrimitives.ReadUInt32BigEndian(control[1..]);
        lock (_sync)
        {
            if (!_available || _pending is not { Sent: true } request) return;
            if (address == 0x3F && data == request.Command)
                request.Completion.TrySetException(new IOException("HL2 I²C bus was busy."));
            else if (address == 0x3D && ((request.Command >> 24) == 7 || data == request.Command))
                request.Completion.TrySetResult(data);
        }
    }
}
