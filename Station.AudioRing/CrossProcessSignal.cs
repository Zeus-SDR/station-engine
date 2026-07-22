// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Douglas J. Cerrato (KB2UKA)

using System.Net;
using System.Net.Sockets;

namespace Station.AudioRing;

/// <summary>
/// Cross-process readiness edge. Windows uses a named auto-reset event. Unix
/// uses a one-byte AF_UNIX datagram, avoiding Darwin's variadic sem_open ABI
/// and restrictive named-semaphore rules while retaining event-style wakeups.
/// </summary>
internal sealed class CrossProcessSignal : IDisposable
{
    private static readonly byte[] WakeByte = [1];
    private readonly string _name;
    private readonly EventWaitHandle? _windowsEvent;
    private readonly Socket? _unixSocket;
    private readonly SocketAddress? _unixSocketAddress;
    private readonly bool _receives;
    private int _sendFaulted;
    private bool _disposed;

    private CrossProcessSignal(string name, EventWaitHandle windowsEvent)
    {
        _name = name;
        _windowsEvent = windowsEvent;
    }

    private CrossProcessSignal(
        string name,
        Socket unixSocket,
        UnixDomainSocketEndPoint endpoint,
        bool receives)
    {
        _name = name;
        _unixSocket = unixSocket;
        _unixSocketAddress = receives ? null : endpoint.Serialize();
        _receives = receives;
    }

    public static CrossProcessSignal Create(string name, bool receives) =>
        OpenCore(name, receives, create: true);

    public static CrossProcessSignal Open(string name, bool receives, TimeSpan timeout)
    {
        if (OperatingSystem.IsWindows())
            return OpenCore(name, receives, create: false);

        if (!receives)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!File.Exists(name) && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            if (!File.Exists(name))
                throw new IOException($"Timed out opening audio-ring signal {name}.");
        }

        return OpenCore(name, receives, create: false);
    }

    private static CrossProcessSignal OpenCore(string name, bool receives, bool create)
    {
        if (OperatingSystem.IsWindows())
        {
            var value = create
                ? new EventWaitHandle(false, EventResetMode.AutoReset, name, out _)
                : EventWaitHandle.OpenExisting(name);
            return new CrossProcessSignal(name, value);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(name);
        if (receives)
        {
            TryDelete(name);
            socket.Bind(endpoint);
        }
        else
        {
            // A dead or wedged peer can fill its receive queue. Audio producers
            // must fail open instead of waiting for signaling capacity.
            socket.Blocking = false;
        }

        return new CrossProcessSignal(name, socket, endpoint, receives);
    }

    public bool TrySet()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowsEvent is not null)
        {
            _windowsEvent.Set();
            return true;
        }

        try
        {
            if (Volatile.Read(ref _sendFaulted) != 0
                || !_unixSocket!.Poll(0, SelectMode.SelectWrite))
            {
                return false;
            }

            _unixSocket.SendTo(WakeByte.AsSpan(), SocketFlags.None, _unixSocketAddress!);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is
            SocketError.WouldBlock or
            SocketError.IOPending or
            SocketError.NoBufferSpaceAvailable)
        {
            // A queued wake already exists. Keep the attachment healthy while
            // the receiver drains its datagram backlog.
            return true;
        }
        catch (SocketException)
        {
            Volatile.Write(ref _sendFaulted, 1);
            return false;
        }
    }

    public bool Wait(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowsEvent is not null)
            return _windowsEvent.WaitOne(timeout);

        var microseconds = (int)Math.Clamp(timeout.TotalMilliseconds * 1_000, 1, int.MaxValue);
        if (!_unixSocket!.Poll(microseconds, SelectMode.SelectRead))
            return false;
        Span<byte> value = stackalloc byte[1];
        _unixSocket.Receive(value, SocketFlags.None);
        while (_unixSocket.Poll(0, SelectMode.SelectRead))
            _unixSocket.Receive(value, SocketFlags.None);
        return true;
    }

    public void Drain()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowsEvent is not null)
        {
            while (_windowsEvent.WaitOne(0))
            {
            }
            return;
        }

        Span<byte> value = stackalloc byte[1];
        while (_unixSocket!.Poll(0, SelectMode.SelectRead))
            _unixSocket.Receive(value, SocketFlags.None);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _windowsEvent?.Dispose();
        _unixSocket?.Dispose();
        if (_receives && !OperatingSystem.IsWindows())
            TryDelete(_name);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
