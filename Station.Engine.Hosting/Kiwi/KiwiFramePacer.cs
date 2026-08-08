// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server;

/// <summary>
/// Paces the Kiwi waterfall/spectrum rows into a steady 30 Hz display stream.
///
/// Hardware DDC frames arrive at a constant ~30 Hz, so every spectrum surface
/// (panadapter trace, 2D/3D waterfall, detached slice windows) is built around
/// a smooth, even frame cadence. A KiwiSDR's W/F rows arrive at the server's
/// own rate — measured 8–16 fps depending on station load — with wide
/// inter-arrival jitter (60–260 ms on a healthy station). Broadcasting each
/// row as it lands makes the waterfall scroll in lumps and the trace jump in
/// bursts next to the hardware panadapter: the "kinda jittery" symptom.
///
/// The pacer re-times the stream: rows are PUSHED as they arrive, and a 30 Hz
/// tick emits an exponential moving average (τ ≈ <see cref="TauMs"/> ms) of
/// the bin array, the row centre, and the Hz/pixel. Content glides to each new
/// row in ~τ instead of stepping, and every consumer downstream sees hardware-
/// like cadence. The very first row snaps (no ramp up from black), and when no
/// row has arrived for <see cref="StallMs"/> ms emission pauses outright —
/// repeating rows through a dead stream would paint a fake "live" waterfall
/// while the reconnect loop runs; a frozen display plus the honest status pill
/// is the correct failure presentation.
/// </summary>
internal sealed class KiwiFramePacer : IDisposable
{
    // Tick cadence: 30 Hz to match the hardware display stream.
    internal const int TickMs = 33;
    // EMA time constant. ~100 ms converges a step in ~3τ (0.3 s) — fast enough
    // to track real signal motion, slow enough to swallow the 60–260 ms row
    // arrival jitter measured on healthy stations.
    internal const double TauMs = 100;
    // No row for this long → stop emitting (stream stalled; a drop/reconnect
    // is imminent or already running).
    internal const long StallMs = 500;

    private readonly int _width;
    private readonly Func<long> _clockMs;
    private readonly object _gate = new();
    private PeriodicTimer? _timer;
    private Task? _loop;

    private float[]? _latest;
    private long _latestCenterHz;
    private double _latestHzPerPixel;
    private long _latestPushMs = -1;

    private float[]? _smoothed;
    private double _smoothedCenterHz;
    private double _smoothedHzPerPixel;
    private long _lastTickMs = -1;

    /// <summary>Raised on every tick while the stream is fresh. The array is a
    /// per-emission copy — consumers may hold it without tearing.</summary>
    public Action<float[], long, double>? FrameReady;

    public KiwiFramePacer(int width, Func<long>? clockMs = null)
    {
        _width = width;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync().ConfigureAwait(false))
                Tick(_clockMs());
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch (ObjectDisposedException) { /* disposed */ }
    }

    /// <summary>Offer the newest decoded row (already resampled to the display
    /// width). Cheap: keeps the reference; the engine allocates a fresh array
    /// per row.</summary>
    public void Push(float[] db, long centerHz, double hzPerPixel)
    {
        if (db.Length != _width) return;
        lock (_gate)
        {
            _latest = db;
            _latestCenterHz = centerHz;
            _latestHzPerPixel = hzPerPixel;
            _latestPushMs = _clockMs();
        }
    }

    private void Tick(long nowMs)
    {
        float[] emit;
        long emitCenterHz;
        double emitHzPerPixel;
        lock (_gate)
        {
            if (_latest is null || _latestPushMs < 0) return;
            if (nowMs - _latestPushMs > StallMs) return; // frozen, not fake

            if (_smoothed is null)
            {
                // First row: snap — never ramp up from an empty/black array.
                _smoothed = (float[])_latest.Clone();
                _smoothedCenterHz = _latestCenterHz;
                _smoothedHzPerPixel = _latestHzPerPixel;
                _lastTickMs = nowMs;
            }
            else
            {
                long dt = Math.Clamp(nowMs - _lastTickMs, 1, 100);
                _lastTickMs = nowMs;
                double alpha = 1.0 - Math.Exp(-dt / TauMs);
                var latest = _latest;
                var smoothed = _smoothed;
                for (int i = 0; i < smoothed.Length; i++)
                    smoothed[i] += (latest[i] - smoothed[i]) * (float)alpha;
                _smoothedCenterHz += (_latestCenterHz - _smoothedCenterHz) * alpha;
                _smoothedHzPerPixel += (_latestHzPerPixel - _smoothedHzPerPixel) * alpha;
            }

            emit = (float[])_smoothed.Clone();
            // Sub-Hz remnants of the centre EMA are invisible; round so the
            // frontend anchor stays on integer Hz like every other frame source.
            emitCenterHz = (long)Math.Round(_smoothedCenterHz);
            emitHzPerPixel = _smoothedHzPerPixel;
        }
        FrameReady?.Invoke(emit, emitCenterHz, emitHzPerPixel);
    }

    /// <summary>Test seam: drive one tick with an explicit clock reading.</summary>
    internal void TickForTest(long nowMs) => Tick(nowMs);

    public void Dispose()
    {
        var timer = _timer;
        _timer = null;
        timer?.Dispose();
    }
}
