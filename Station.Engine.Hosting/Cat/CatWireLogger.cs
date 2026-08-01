// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.Cat;

/// <summary>
/// Per-connection CAT wire logger with a fixed one-second emission budget.
/// Normal traffic is logged frame-for-frame; sustained poll/AI bursts are
/// summarized so diagnostics cannot amplify a busy CAT client without bound.
/// </summary>
internal sealed class CatWireLogger
{
    internal const int MaxFramesPerDirectionPerSecond = 20;

    private readonly ILogger _log;
    private readonly string _endpoint;
    private readonly LogLevel _level;
    private readonly Func<long> _stopwatchTicks;
    private readonly long _stopwatchFrequency;
    private readonly object _sync = new();
    private readonly DirectionState _rx = new();
    private readonly DirectionState _tx = new();

    public CatWireLogger(
        ILogger log,
        string endpoint,
        bool logAtInformation,
        Func<long>? stopwatchTicks = null,
        long? stopwatchFrequency = null)
    {
        _log = log;
        _endpoint = endpoint;
        _level = logAtInformation ? LogLevel.Information : LogLevel.Debug;
        _stopwatchTicks = stopwatchTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _stopwatchFrequency = stopwatchFrequency ?? System.Diagnostics.Stopwatch.Frequency;
    }

    public void Rx(string frame) => Write(Direction.Rx, frame);

    public void Tx(string frame) => Write(Direction.Tx, frame);

    public void FlushSuppressed()
    {
        if (!_log.IsEnabled(_level)) return;
        int rxSuppressed;
        int txSuppressed;
        lock (_sync)
        {
            rxSuppressed = TakeSuppressed(_rx);
            txSuppressed = TakeSuppressed(_tx);
        }
        LogSuppressed(Direction.Rx, rxSuppressed);
        LogSuppressed(Direction.Tx, txSuppressed);
    }

    private void Write(Direction direction, string frame)
    {
        if (!_log.IsEnabled(_level)) return;
        int suppressed = 0;
        bool emitFrame = false;
        lock (_sync)
        {
            var state = direction == Direction.Rx ? _rx : _tx;
            long now = _stopwatchTicks();
            if (!state.Started)
            {
                state.Started = true;
                state.WindowStart = now;
            }
            else if (now - state.WindowStart >= _stopwatchFrequency)
            {
                suppressed = TakeSuppressed(state);
                state.WindowStart = now;
                state.Emitted = 0;
            }

            if (state.Emitted >= MaxFramesPerDirectionPerSecond)
            {
                state.Suppressed++;
            }
            else
            {
                state.Emitted++;
                emitFrame = true;
            }
        }

        LogSuppressed(direction, suppressed);
        if (!emitFrame) return;
        if (direction == Direction.Rx)
            _log.Log(_level, "cat.rx endpoint={Endpoint} frame={Frame}", _endpoint, frame);
        else
            _log.Log(_level, "cat.tx endpoint={Endpoint} frame={Frame}", _endpoint, frame);
    }

    private static int TakeSuppressed(DirectionState state)
    {
        int suppressed = state.Suppressed;
        state.Suppressed = 0;
        return suppressed;
    }

    private void LogSuppressed(Direction direction, int suppressed)
    {
        if (suppressed <= 0) return;
        if (direction == Direction.Rx)
            _log.Log(
                _level,
                "cat.rx.suppressed endpoint={Endpoint} count={Count}",
                _endpoint,
                suppressed);
        else
            _log.Log(
                _level,
                "cat.tx.suppressed endpoint={Endpoint} count={Count}",
                _endpoint,
                suppressed);
    }

    private enum Direction
    {
        Rx,
        Tx,
    }

    private sealed class DirectionState
    {
        public bool Started;
        public long WindowStart;
        public int Emitted;
        public int Suppressed;
    }
}
