// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.Sockets;

namespace Station.AudioIpc.Spike;

/// <summary>
/// Cross-process readiness edge. Windows uses a named auto-reset event. Unix
/// uses a one-byte AF_UNIX datagram: this avoids Darwin's variadic sem_open
/// creation ABI and its unusually restrictive semaphore naming rules while
/// retaining eventfd-style wakeups rather than polling the shared ring.
/// </summary>
internal sealed class CrossProcessSignal : IDisposable
{
    private static readonly byte[] WakeByte = [1];
    private readonly string _name;
    private readonly EventWaitHandle? _windowsEvent;
    private readonly Socket? _unixSocket;
    private readonly UnixDomainSocketEndPoint? _unixEndpoint;
    private readonly bool _receives;
    private bool _disposed;

    private CrossProcessSignal(string name, EventWaitHandle windowsEvent)
    {
        _name = name;
        _windowsEvent = windowsEvent;
    }

    private CrossProcessSignal(string name, Socket unixSocket, UnixDomainSocketEndPoint endpoint, bool receives)
    {
        _name = name;
        _unixSocket = unixSocket;
        _unixEndpoint = endpoint;
        _receives = receives;
    }

    public static CrossProcessSignal Create(string name, bool receives) => OpenCore(name, receives, create: true);

    public static CrossProcessSignal Open(string name, bool receives, TimeSpan timeout)
    {
        if (OperatingSystem.IsWindows()) return OpenCore(name, receives, create: false);

        // A sender may start before the receiving endpoint is bound. Waiting
        // here makes first use deterministic; receivers bind immediately.
        if (!receives)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!File.Exists(name) && DateTime.UtcNow < deadline) Thread.Sleep(10);
            if (!File.Exists(name)) throw new IOException($"Timed out opening IPC signal {name}.");
        }
        return OpenCore(name, receives, create: false);
    }

    private static CrossProcessSignal OpenCore(string name, bool receives, bool create)
    {
        if (OperatingSystem.IsWindows())
        {
            var evt = create
                ? new EventWaitHandle(false, EventResetMode.AutoReset, name, out _)
                : EventWaitHandle.OpenExisting(name);
            return new CrossProcessSignal(name, evt);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(name);
        if (receives)
        {
            try { File.Delete(name); } catch (IOException) { }
            socket.Bind(endpoint);
        }
        else
        {
            // A paused or wedged peer can fill its receive queue. Signaling
            // must fail open, never block the engine audio callback.
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
            _unixSocket!.SendTo(WakeByte, SocketFlags.None, _unixEndpoint!);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Wait()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowsEvent is not null)
        {
            _windowsEvent.WaitOne();
            return;
        }
        Span<byte> value = stackalloc byte[1];
        _unixSocket!.Receive(value, SocketFlags.None);
    }

    public bool Wait(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowsEvent is not null) return _windowsEvent.WaitOne(timeout);
        var micros = (int)Math.Clamp(timeout.TotalMilliseconds * 1000, 1, int.MaxValue);
        if (!_unixSocket!.Poll(micros, SelectMode.SelectRead)) return false;
        Span<byte> value = stackalloc byte[1];
        _unixSocket.Receive(value, SocketFlags.None);
        return true;
    }

    public void Drain()
    {
        if (_disposed) return;
        if (_windowsEvent is not null)
        {
            while (_windowsEvent.WaitOne(0)) { }
            return;
        }
        Span<byte> value = stackalloc byte[1];
        while (_unixSocket!.Poll(0, SelectMode.SelectRead)) _unixSocket.Receive(value, SocketFlags.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _windowsEvent?.Dispose();
        _unixSocket?.Dispose();
        if (_receives && !OperatingSystem.IsWindows())
            try { File.Delete(_name); } catch (IOException) { }
    }
}
