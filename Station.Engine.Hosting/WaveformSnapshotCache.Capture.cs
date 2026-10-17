// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

public sealed partial class WaveformSnapshotCache
{
    private WaveformCaptureSession? _session;
    internal bool IsCaptureActive
    {
        get
        {
            var session = Volatile.Read(ref _session);
            return session is not null && Volatile.Read(ref session.Status) == "capturing";
        }
    }
    private Timer? _captureExpiry;
    private int _startingCapture;
    private void ExpireCapture(object? unused)
    {
        lock (_sync)
        {
            if (_session is null) { _captureExpiry?.Dispose(); _captureExpiry = null; return; }
            long now = Environment.TickCount64;
            _session.Check(_session.Context, now, Volatile.Read(ref _missed) != 0);
            if (_session.Expired(now))
            { _session = null; _captureExpiry?.Dispose(); _captureExpiry = null; }
            else _captureExpiry?.Change(Math.Max(1, _session.Status == "capturing"
                ? _session.Started + _session.Duration + 2001 - now
                : _session.Finished + WaveformCaptureSession.RetentionMs - now), Timeout.Infinite);
        }
    }
    public WaveformCaptureMetadata? StartCapture(object? context, long now, int receiver,
        bool tx, int durationMs, string mode, out string? error, bool microphone = false, bool monitor = false)
    {
        // Large sample storage is allocated outside the producer lock and DSP thread.
        if (context is null) { error = "Signal source is unavailable"; return null; }
        if (durationMs is not (1000 or 3000 or 5000 or 10000)) { error = "Unsupported duration"; return null; }
        if (Interlocked.CompareExchange(ref _startingCapture, 1, 0) != 0)
        { error = "A capture is already starting"; return null; }
        try
        {
            lock (_sync)
            {
                _session?.Check(context, now, Volatile.Read(ref _missed) != 0);
                if (_session?.Status == "capturing") { error = "A capture is already running"; return null; }
            }
            var pending = new WaveformCaptureSession(context, receiver, tx, durationMs, mode, 0, now, microphone, monitor);
            lock (_sync)
            {
                _session?.Check(context, now, Volatile.Read(ref _missed) != 0);
                if (_session?.Status == "capturing") { error = "A capture is already running"; return null; }
                SetContext(context);
                if (Interlocked.Exchange(ref _missed, 0) != 0)
                { _count = _write = 0; _endSampleIndex = 0; _generation++; }
                pending.Generation = _generation;
                _session = pending;
                _captureExpiry?.Dispose();
                _captureExpiry = new Timer(ExpireCapture, null, durationMs + 2001, Timeout.Infinite);
                _until = Math.Max(_until, now + durationMs + 2000);
                error = null; return _session.Metadata();
            }
        }
        finally { Volatile.Write(ref _startingCapture, 0); }
    }
    public WaveformCaptureMetadata? GetCapture(string id, object? context, long now)
    {
        lock (_sync)
        {
            if (_session?.Id != id) return null;
            _session.Check(context, now, Volatile.Read(ref _missed) != 0);
            if (_session.Expired(now)) { _session = null; return null; }
            return _session.Metadata();
        }
    }
    public byte[]? GetCaptureBytes(string id)
    {
        // Completed buffers are immutable. Serialization holds no producer lock.
        WaveformCaptureSession? session;
        lock (_sync) session = _session?.Id == id ? _session : null;
        return session?.Bytes();
    }
    public bool CancelCapture(string id, long now)
    {
        lock (_sync)
        {
            if (_session?.Id != id) return false;
            _session.Stop("cancelled", null, now);
            _session = null;
            _captureExpiry?.Dispose(); _captureExpiry = null;
            _until = Math.Min(_until, now + LeaseMs);
            return true;
        }
    }
    public WaveformCaptureMetadata? FindCapture(string id)
    {
        lock (_sync) return _session?.Id == id ? _session.Metadata() : null;
    }
}
