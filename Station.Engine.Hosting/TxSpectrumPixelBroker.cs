// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>Shares the destructive native TX pan freshness flag between display
/// consumers. Pixels stay in full-width native order without display calibration.</summary>
public sealed class TxSpectrumPixelBroker
{
    private readonly object _sync = new();
    private object? _context;
    private float[]? _raw;
    private long _generation, _version, _mainVersion, _capturedAt;

    public void Reset()
    {
        lock (_sync) { _generation++; _context = null; _raw = null; _mainVersion = _version; }
    }

    private void SetContext(object context)
    {
        if (Equals(context, _context)) return;
        _generation++; _context = context; _raw = null; _mainVersion = _version;
    }

    private void Store(float[] pixels, long nowMs)
    {
        for (int i = 0; i < pixels.Length; i++)
            if (!float.IsFinite(pixels[i])) { _raw = null; return; }
        if (_raw?.Length != pixels.Length) _raw = new float[pixels.Length];
        pixels.CopyTo(_raw, 0);
        _capturedAt = nowMs;
        _version++;
    }

    public bool ReadMain(object context, float[] target, long nowMs, Func<float[], bool> nativeRead) =>
        ReadMain(context, target, nowMs, nativeRead, out _);

    public bool ReadMain(object context, float[] target, long nowMs, Func<float[], bool> nativeRead,
        out bool nativeFresh)
    {
        nativeFresh = false;
        lock (_sync)
        {
            SetContext(context);
            long generation = _generation;
            // Preserve the original main reader's priority for the newest native frame.
            if (nativeRead(target))
            {
                nativeFresh = true;
                if (generation == _generation && Equals(context, _context))
                { Store(target, nowMs); _mainVersion = _version; }
                return true;
            }
            if (generation != _generation || !Equals(context, _context) || _raw?.Length != target.Length
                || _mainVersion == _version || nowMs < _capturedAt
                || nowMs - _capturedAt > TxSpectrumSnapshotCache.MaxAgeMs) return false;
            _raw.CopyTo(target, 0);
            _mainVersion = _version;
            return true;
        }
    }

    public bool ReadDedicated(object context, float[] target, long nowMs, Func<float[], bool> nativeRead)
    {
        lock (_sync)
        {
            SetContext(context);
            long generation = _generation;
            if (!nativeRead(target) || generation != _generation || !Equals(context, _context)) return false;
            // Leave the main cursor unchanged: it has not observed this native frame yet.
            Store(target, nowMs);
            return true;
        }
    }
}
