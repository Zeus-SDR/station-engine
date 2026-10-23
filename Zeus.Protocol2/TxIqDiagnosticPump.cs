// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Protocol2;

/// <summary>One realtime producer; serialized, best-effort background diagnostics.</summary>
internal sealed class TxIqDiagnosticPump<T> : IDisposable where T : struct
{
    internal const int Capacity = 32;
    private readonly T[] _items = new T[Capacity];
    private readonly Action<T> _consume;
    private readonly Action<long> _reportDrops;
    private readonly object _lifecycle = new();
    private Timer? _timer;
    private long _written, _read, _dropped, _reportedDrops;
    private int _draining;
    private volatile bool _disposed;

    internal TxIqDiagnosticPump(Action<T> consume, Action<long> reportDrops)
    {
        _consume = consume;
        _reportDrops = reportDrops;
    }

    internal long Dropped => Interlocked.Read(ref _dropped);
    internal long Pending => Volatile.Read(ref _written) - Volatile.Read(ref _read);

    // Called at sender startup, never by the steady-state publication path.
    internal void Start()
    {
        lock (_lifecycle)
        {
            if (!_disposed && _timer is null)
                _timer = new Timer(static state => ((TxIqDiagnosticPump<T>)state!).Drain(),
                    this, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        }
    }

    internal bool TryPublish(in T item)
    {
        var written = _written;
        if (_disposed || written - Volatile.Read(ref _read) >= Capacity)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }
        _items[(int)(written % Capacity)] = item;
        Volatile.Write(ref _written, written + 1);
        return true;
    }

    // Also the deterministic test seam. A blocked provider never holds a queue lock.
    internal void Drain()
    {
        if (_disposed || Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        try
        {
            for (var count = 0; count < Capacity && !_disposed; count++)
            {
                var read = _read;
                if (read == Volatile.Read(ref _written)) break;
                var index = (int)(read % Capacity);
                var item = _items[index];
                _items[index] = default;
                Volatile.Write(ref _read, read + 1);
                try { _consume(item); } catch { /* Diagnostics cannot stop the sender. */ }
            }
            var dropped = Dropped;
            if (!_disposed && dropped != _reportedDrops)
            {
                _reportedDrops = dropped;
                try { _reportDrops(dropped); } catch { }
            }
        }
        finally { Volatile.Write(ref _draining, 0); }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            _timer?.Dispose(); // Does not join a callback blocked in a provider.
            _timer = null;
        }
    }
}
