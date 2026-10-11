// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

public sealed record BandwidthSpectrumSnapshot(bool Available, string Source, int ReceiverIndex,
    float[] Bins, int Width, double CenterHz, long CarrierHz, string Mode, double HzPerBin,
    long Seq, long Generation, long AgeMs, long CaptureTimeUnixMs, string LevelReference);

internal sealed class BandwidthSpectrumCache
{
    internal const int LeaseMs = 2000;
    internal const int StaleMs = 750;
    private readonly object _gate = new();
    private readonly float[] _bins = new float[16_384];
    private object? _context;
    private long _demandUntil, _updatedAt, _captureUnixMs, _seq, _generation, _nativeGeneration, _warmupUntil;
    private int _rate, _count;
    private double _center;

    internal long DemandUntil => Volatile.Read(ref _demandUntil);

    internal void Publish(object context, ReadOnlySpan<float> nativeDescendingBins, int rate,
        long generation, double center, long now, long captureUnixMs, double offsetDb)
    {
        // HTTP copies can never make a real-time producer wait.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (!Equals(_context, context) || _nativeGeneration != generation || _rate != rate)
            {
                _context = context; _nativeGeneration = generation; _generation++; _rate = rate; _count = 0;
                _warmupUntil = now + (long)Math.Ceiling(1000.0 * nativeDescendingBins.Length / rate);
            }
            if (now < _warmupUntil) return;
            _count = Math.Min(_bins.Length, nativeDescendingBins.Length);
            for (int i = 0; i < _count; i++)
            {
                float value = nativeDescendingBins[_count - 1 - i];
                _bins[i] = float.IsFinite(value) ? (float)(value + offsetDb) : -200f;
            }
            _center = center; _updatedAt = now; _captureUnixMs = captureUnixMs; _seq++;
        }
        finally { Monitor.Exit(_gate); }
    }

    internal BandwidthSpectrumSnapshot Request(object? context, bool tx, int receiver,
        long carrier, string mode, long now, double minOffset, double maxOffset)
    {
        Volatile.Write(ref _demandUntil, context is null ? 0 : now + LeaseMs);
        lock (_gate)
        {
            var empty = new BandwidthSpectrumSnapshot(false, tx ? "digital-tx" : "rx-native",
                receiver, [], 0, carrier, carrier, mode, 0, _seq, _generation,
                Math.Max(0, now - _updatedAt), _captureUnixMs, "native-display");
            if (context is null || !Equals(context, _context) || _count == 0
                || now - _updatedAt > StaleMs) return empty;
            double step = (double)_rate / _count;
            double start = _center - _rate / 2.0;
            // Clip in floating point before conversion so a distant view cannot
            // overflow an integer index. Each returned value remains a real bin.
            int first = (int)Math.Clamp(Math.Floor((carrier + minOffset - start) / step), 0, _count);
            int end = (int)Math.Clamp(Math.Ceiling((carrier + maxOffset - start) / step), first, _count);
            int width = end - first;
            if (width == 0) return empty;
            var bins = _bins.AsSpan(first, width).ToArray();
            return empty with { Available = true, Bins = bins, Width = width,
                CenterHz = start + (first + width / 2.0) * step, HzPerBin = step };
        }
    }
}
