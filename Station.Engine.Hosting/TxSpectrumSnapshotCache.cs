// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>Digital TX analyzer pixels; this is not a post-amplifier RF measurement.</summary>
public sealed record TxSpectrumSnapshot(
    bool Available, string Source, long Generation, long Seq,
    double CaptureTimeUnixMs, long AgeMs, double CenterHz, float HzPerBin,
    int Width, float[] Bins, double CarrierHz, string Mode);

/// <summary>One DSP-thread producer, HTTP readers. Readers never consume native FFT flags.</summary>
public sealed class TxSpectrumSnapshotCache
{
    public const long MaxAgeMs = 400;
    public const long DemandLeaseMs = 1500;
    public const long CapturePeriodMs = 50;
    private readonly object _sync = new();
    private object? _context;
    private long _generation, _sequence, _demandUntil, _lastAttempt = long.MinValue;
    private long _capturedAt;
    private TxSpectrumSnapshot? _snapshot;

    public void Reset()
    {
        lock (_sync) { Clear(); _context = null; }
    }

    private void Clear()
    {
        _generation++;
        _snapshot = null;
        _lastAttempt = long.MinValue;
    }

    private void SetContext(object? context)
    {
        if (Equals(_context, context)) return;
        Clear();
        _context = context;
    }

    public TxSpectrumSnapshot Request(object? context, long nowMs)
    {
        lock (_sync)
        {
            SetContext(context);
            _demandUntil = nowMs + DemandLeaseMs;
            if (context is not null && _snapshot is not null && nowMs >= _capturedAt
                && nowMs - _capturedAt <= MaxAgeMs)
                // Give each HTTP serialization its own array, isolated from readers and the writer.
                return _snapshot with { AgeMs = nowMs - _capturedAt, Bins = (float[])_snapshot.Bins.Clone() };
            return new(false, "digital-tx", _generation, _sequence, 0, 0, 0, 0, 0, [], 0, "");
        }
    }

    public bool TryBeginCapture(object? context, long nowMs, out long generation)
    {
        lock (_sync)
        {
            SetContext(context);
            generation = _generation;
            if (context is null || nowMs >= _demandUntil) return false;
            if (_lastAttempt != long.MinValue && nowMs >= _lastAttempt && nowMs - _lastAttempt < CapturePeriodMs)
                return false;
            _lastAttempt = nowMs;
            return true;
        }
    }

    public void Publish(object context, ReadOnlySpan<float> nativeBins, long nowMs,
        double captureTimeUnixMs, double centerHz, float hzPerBin, long generation, double? carrierHz = null, string mode = "")
    {
        bool valid = !(nativeBins.Length < 2 || nativeBins.Length > 65536 || !float.IsFinite(hzPerBin)
            || hzPerBin <= 0 || !double.IsFinite(captureTimeUnixMs));
        for (int i = 0; i < nativeBins.Length; i++)
            if (!float.IsFinite(nativeBins[i])) { valid = false; break; }
        lock (_sync)
        {
            // A concurrent un-key or context change must not revive the previous over.
            if (generation != _generation || !Equals(context, _context) || nowMs >= _demandUntil) return;
            if (!valid) { _snapshot = null; return; }
            float[] bins = nativeBins.ToArray();
            Array.Reverse(bins);
            _capturedAt = nowMs;
            _snapshot = new(true, "digital-tx", _generation, ++_sequence,
                captureTimeUnixMs, 0, centerHz, hzPerBin, bins.Length, bins, carrierHz ?? centerHz, mode);
        }
    }
}
