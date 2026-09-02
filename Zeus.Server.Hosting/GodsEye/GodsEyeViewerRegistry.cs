// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.GodsEye;
#else
namespace Zeus.Server.GodsEye;
#endif

public sealed class GodsEyeViewerRegistry : IDisposable
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _leases = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private CancellationTokenSource _changed = new();
    private bool _disposed;

    public GodsEyeViewerRegistry(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _timer = new Timer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool HasViewers { get { lock (_sync) return _leases.Count > 0; } }
    public CancellationToken ChangedToken { get { lock (_sync) return _changed.Token; } }

    public void Open(string id) => Renew(id);
    public void Heartbeat(string id) => Renew(id);

    public void Close(string id)
    {
        lock (_sync)
        {
            if (!_leases.Remove(id)) return;
            ScheduleLocked();
            if (_leases.Count == 0) PulseLocked();
        }
    }

    private void Renew(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return;
        lock (_sync)
        {
            if (_leases.Count >= 256 && !_leases.ContainsKey(id)) return;
            var wasEmpty = _leases.Count == 0;
            _leases[id] = _time.GetUtcNow() + LeaseDuration;
            ScheduleLocked();
            if (wasEmpty) PulseLocked();
        }
    }

    private void Expire()
    {
        lock (_sync)
        {
            if (_disposed) return;
            var now = _time.GetUtcNow();
            foreach (var id in _leases.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
                _leases.Remove(id);
            ScheduleLocked();
            if (_leases.Count == 0) PulseLocked();
        }
    }

    private void ScheduleLocked()
    {
        if (_leases.Count == 0) { _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
        var due = _leases.Values.Min() - _time.GetUtcNow();
        _timer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void PulseLocked()
    {
        _changed.Cancel();
        _changed.Dispose();
        _changed = new CancellationTokenSource();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            _changed.Cancel();
            _changed.Dispose();
        }
    }
}
