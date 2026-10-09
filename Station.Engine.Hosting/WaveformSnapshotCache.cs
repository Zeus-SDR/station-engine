// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

public sealed record WaveformSnapshot(bool Available, string Source, string Kind,
    int ReceiverIndex, int SampleRateHz, int Channels, long Seq, long Generation,
    double CaptureTimeUnixMs, long AgeMs, float[] Samples, double DurationMs,
    double WindowMs, string AmplitudeReference, string Mode, double CarrierHz, string SignalStage);

/// <summary>Bounded contiguous samples; request readers never consume the producer.</summary>
public sealed class WaveformSnapshotCache
{
    public const int MaxFrames = 16384;
    public const long LeaseMs = 1500;
    public const long MaxAgeMs = 400;
    private readonly object _sync = new();
    private readonly float[] _ring = new float[MaxFrames * 2];
    private object? _context;
    private int _count, _write, _rate, _channels;
    private long _generation, _seq, _until, _capture;
    private double _captureUnix;
    private int _missed;

    public bool IsDemanded(long nowMs) => nowMs < Volatile.Read(ref _until);

    private void SetContext(object? context)
    {
        if (Equals(context, _context)) return;
        _context = context; _count = _write = _rate = _channels = 0; _generation++;
    }

    public void Append(object? context, int rate, int channels, ReadOnlySpan<float> samples,
        long nowMs, double unixMs)
    {
        if (!Monitor.TryEnter(_sync)) { Interlocked.Exchange(ref _missed, 1); return; }
        try
        {
            SetContext(context);
            if (context is null || nowMs >= _until || rate is < 8000 or > 768000 || channels is < 1 or > 2
                || !double.IsFinite(unixMs)
                || samples.Length == 0 || samples.Length % channels != 0) return;
            double blockMs = samples.Length * 1000.0 / (rate * channels);
            if (_rate != rate || _channels != channels || Interlocked.Exchange(ref _missed, 0) != 0
                || (_count > 0 && (nowMs < _capture || nowMs - _capture > Math.Max(60, blockMs * 2 + 20))))
            { _count = _write = 0; _generation++; }
            for (int i = 0; i < samples.Length; i++)
                if (!float.IsFinite(samples[i])) { _count = _write = 0; _generation++; return; }
            _rate = rate; _channels = channels;
            int capacity = MaxFrames * channels;
            int start = Math.Max(0, samples.Length - capacity);
            var tail = samples[start..];
            int first = Math.Min(tail.Length, capacity - _write);
            tail[..first].CopyTo(_ring.AsSpan(_write));
            tail[first..].CopyTo(_ring);
            _write = (_write + tail.Length) % capacity;
            _count = Math.Min(capacity, _count + tail.Length);
            _capture = nowMs; _captureUnix = unixMs; _seq++;
        }
        finally { Monitor.Exit(_sync); }
    }

    public WaveformSnapshot Request(object? context, long nowMs, int receiver, bool tx,
        double windowMs, string mode, double carrier)
    {
        lock (_sync)
        {
            SetContext(context);
            if (nowMs >= _until && _count > 0) { _count = _write = 0; _generation++; }
            _until = nowMs + LeaseMs;
            windowMs = double.IsFinite(windowMs) ? Math.Clamp(windowMs, 5, 100) : 50;
            bool valid = context is not null && _count > 0 && nowMs >= _capture && nowMs - _capture <= MaxAgeMs;
            float[] result = [];
            double duration = 0;
            if (valid)
            {
                int frames = Math.Min(_count / _channels, (int)Math.Ceiling(_rate * windowMs / 1000));
                result = new float[frames * _channels];
                int capacity = MaxFrames * _channels;
                int start = (_write - result.Length + capacity) % capacity;
                int first = Math.Min(result.Length, capacity - start);
                _ring.AsSpan(start, first).CopyTo(result);
                _ring.AsSpan(0, result.Length - first).CopyTo(result.AsSpan(first));
                duration = frames * 1000.0 / _rate;
            }
            return new(valid, tx ? "digital-tx" : "rx-demodulated", tx ? "complex" : "real",
                receiver, valid ? _rate : 0, tx ? 2 : 1, _seq, _generation,
                valid ? _captureUnix : 0, valid ? nowMs - _capture : 0, result,
                duration, duration, "relative", mode, carrier, tx ? "tx-output" : "rx-post-demod");
        }
    }
}
