// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// SPE Expert 1.5K Taurus amplifier support. This file is GPL-3.0-or-later
// (see Station.Engine.Hosting/SpeTaurus/SOURCE.md); the rest of the engine is
// GPL-2.0-or-later, whose "or later" option permits the combination. The
// resulting engine binary is distributed as GPL-3.0-or-later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.IO.Ports;
using System.Net.Sockets;

namespace Zeus.Server.SpeTaurus;

internal interface ISpeTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    Task OpenAsync(SpeTaurusConfig config, CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    Task CloseAsync();
}

internal sealed class SpeSerialTransport : ISpeTransport
{
    private SerialPort? _port;

    public bool IsOpen => _port?.IsOpen == true;

    public Task OpenAsync(SpeTaurusConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(config.PortName))
            throw new InvalidOperationException("Select a serial port before connecting.");
        var port = new SerialPort(config.PortName, config.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = config.ResponseTimeoutMs,
            WriteTimeout = config.ResponseTimeoutMs,
        };
        try
        {
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            _port = port;
            return Task.CompletedTask;
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var port = _port ?? throw new IOException("Serial port is not connected.");
        // Some platform serial drivers ignore async cancellation. Keep their
        // pending write target private, bound our wait, then let the service
        // close the port without risking a late write into a reused caller
        // buffer.
        var scratch = new byte[buffer.Length];
        var read = await port.BaseStream.ReadAsync(scratch, CancellationToken.None)
            .AsTask()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        scratch.AsMemory(0, read).CopyTo(buffer);
        return read;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var port = _port ?? throw new IOException("Serial port is not connected.");
        var copy = bytes.ToArray();
        await port.BaseStream.WriteAsync(copy, CancellationToken.None)
            .AsTask()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await port.BaseStream.FlushAsync(CancellationToken.None)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task CloseAsync()
    {
        Interlocked.Exchange(ref _port, null)?.Dispose();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

internal sealed class SpeTcpTransport : ISpeTransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public bool IsOpen => _client?.Connected == true && _stream is not null;

    public async Task OpenAsync(SpeTaurusConfig config, CancellationToken cancellationToken)
    {
        if (!SpeTaurusService.IsValidHost(config.BridgeHost))
            throw new InvalidOperationException("Enter a valid bridge DNS name or IPv4 address.");
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(config.ConnectTimeoutMs);
            await client.ConnectAsync(config.BridgeHost, config.BridgePort, timeout.Token)
                .ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        (_stream ?? throw new IOException("TCP serial bridge is not connected."))
            .ReadAsync(buffer, cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new IOException("TCP serial bridge is not connected.");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CloseAsync()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
        Interlocked.Exchange(ref _client, null)?.Dispose();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

internal static class SpeSerialPorts
{
    internal static IReadOnlyList<string> List()
    {
        try { return SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch { return []; }
    }
}
