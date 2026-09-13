// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
using System.Net.Sockets;
using System.Text;
#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.Aprs;
#else
namespace Zeus.Server.Aprs;
#endif

public interface IAprsConnection : IAsyncDisposable
{
    ValueTask<string?> ReadAsync(CancellationToken ct);
    ValueTask LoginAsync(string login, CancellationToken ct);
}
public interface IAprsConnector { Task<IAprsConnection> ConnectAsync(CancellationToken ct); }
public sealed class AprsConnector : IAprsConnector
{
    public const string Host = "rotate.aprs2.net";
    public async Task<IAprsConnection> ConnectAsync(CancellationToken ct)
    {
        var client = new TcpClient();
        try { await client.ConnectAsync(Host, 14580, ct).ConfigureAwait(false); return new Connection(client); }
        catch { client.Dispose(); throw; }
    }
    private sealed class Connection(TcpClient client) : IAprsConnection
    {
        private readonly NetworkStream _stream = client.GetStream();
        private readonly byte[] _buffer = new byte[4096];
        private int _offset, _count;
        public async ValueTask<string?> ReadAsync(CancellationToken ct)
        {
            var line = new StringBuilder(128);
            var overflow = false;
            while (true)
            {
                if (_offset == _count)
                {
                    _count = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false); _offset = 0;
                    if (_count == 0) return null;
                }
                var b = _buffer[_offset++];
                if (b == '\n') { if (overflow) return ""; return line.ToString().TrimEnd('\r'); }
                if (line.Length < 511) line.Append((char)b); else overflow = true;
            }
        }
        public async ValueTask LoginAsync(string login, CancellationToken ct) =>
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(login + "\r\n"), ct).ConfigureAwait(false);
        public ValueTask DisposeAsync() { client.Dispose(); return ValueTask.CompletedTask; }
    }
}

