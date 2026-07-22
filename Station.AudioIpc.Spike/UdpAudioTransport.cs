// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Sockets;

namespace Station.AudioIpc.Spike;

public sealed class UdpAudioClient : IAudioBlockRoundTripClient
{
    private readonly Socket _socket;
    private readonly ulong _sessionToken;
    private readonly byte[] _send = new byte[AudioIpcProtocol.PacketBytes];
    private readonly byte[] _receive = new byte[AudioIpcProtocol.PacketBytes];
    private readonly float[] _decoded = new float[AudioIpcProtocol.SamplesPerBlock];
    private long _sequence;

    private UdpAudioClient(AudioIpcEndpoint endpoint, Socket socket)
    {
        Endpoint = endpoint;
        _socket = socket;
        _sessionToken = AudioIpcProtocol.SessionToken(endpoint.SessionId);
    }

    public AudioIpcEndpoint Endpoint { get; }

    public static UdpAudioClient Create()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        var endpoint = new AudioIpcEndpoint(AudioIpcTransportKind.Udp,
            $"zph-{Environment.ProcessId}-{Guid.NewGuid():N}", UdpPort: port);
        socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        if (OperatingSystem.IsWindows())
        {
            const int SioUdpConnReset = -1744830452;
            try { socket.IOControl(SioUdpConnReset, [0], null); } catch (SocketException) { }
        }
        return new UdpAudioClient(endpoint, socket);
    }

    public bool TryRoundTrip(ReadOnlySpan<float> input, Span<float> output, TimeSpan timeout, out long roundTripTicks)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        AudioIpcProtocol.WritePacket(_send, sequence, _sessionToken, input);
        var started = AudioIpcProtocol.Timestamp();
        try
        {
            _socket.Send(_send, SocketFlags.None, out var sendError);
            if (sendError != SocketError.Success)
            {
                roundTripTicks = AudioIpcProtocol.ElapsedTicks(started);
                return false;
            }

            var timeoutMicros = (int)Math.Clamp(timeout.TotalMilliseconds * 1000, 1, int.MaxValue);
            while (_socket.Poll(timeoutMicros, SelectMode.SelectRead))
            {
                var received = _socket.Receive(_receive, SocketFlags.None, out var receiveError);
                if (receiveError != SocketError.Success)
                {
                    roundTripTicks = AudioIpcProtocol.ElapsedTicks(started);
                    return false;
                }
                if (received == AudioIpcProtocol.PacketBytes &&
                    AudioIpcProtocol.TryReadPacket(_receive, _sessionToken, out var responseSequence, _decoded) &&
                    responseSequence == sequence)
                {
                    _decoded.AsSpan().CopyTo(output);
                    roundTripTicks = AudioIpcProtocol.ElapsedTicks(started);
                    return true;
                }

                var elapsedMicros = AudioIpcProtocol.TicksToMilliseconds(AudioIpcProtocol.ElapsedTicks(started)) * 1000;
                timeoutMicros = (int)Math.Max(0, timeout.TotalMilliseconds * 1000 - elapsedMicros);
                if (timeoutMicros == 0) break;
            }
        }
        catch (SocketException) { }

        roundTripTicks = AudioIpcProtocol.ElapsedTicks(started);
        return false;
    }

    public void Dispose() => _socket.Dispose();
}

public sealed class UdpAudioServer : IAudioBlockServer
{
    private readonly Socket _socket;
    private readonly ulong _sessionToken;
    private readonly byte[] _packet = new byte[AudioIpcProtocol.PacketBytes];
    private readonly float[] _block = new float[AudioIpcProtocol.SamplesPerBlock];

    public UdpAudioServer(AudioIpcEndpoint endpoint)
    {
        _sessionToken = AudioIpcProtocol.SessionToken(endpoint.SessionId);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, endpoint.UdpPort));
    }

    public void Run(float gain, CancellationToken cancellationToken)
    {
        EndPoint source = new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_socket.Poll(50_000, SelectMode.SelectRead)) continue;
            var received = _socket.ReceiveFrom(_packet, SocketFlags.None, ref source);
            if (received != AudioIpcProtocol.PacketBytes ||
                !AudioIpcProtocol.TryReadPacket(_packet, _sessionToken, out var sequence, _block))
                continue;

            if (gain != 1f)
                for (var i = 0; i < _block.Length; i++) _block[i] *= gain;
            AudioIpcProtocol.WritePacket(_packet, sequence, _sessionToken, _block);
            _socket.SendTo(_packet, SocketFlags.None, source);
        }
    }

    public void Dispose() => _socket.Dispose();
}
