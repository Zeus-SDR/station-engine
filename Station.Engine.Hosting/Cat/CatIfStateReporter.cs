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
/// Emits MOX-edge IF frames. MOX assertion is immediate; each MOX release is
/// deferred by a bounded interval so a following Fake It dial restore can land
/// first. Deferrals are per falling edge and are not coalesced, so a burst of
/// releases can emit one frame each — harmless, because every frame is built
/// from live radio state at the instant it is sent, never from state captured
/// at the MOX edge. For the same reason a release immediately followed by a
/// re-key reports the current (transmitting) state rather than a stale receive
/// edge.
/// </summary>
internal sealed class CatIfStateReporter : IDisposable
{
    private readonly object _sync = new();
    private readonly RadioService _radio;
    private readonly ILogger _log;
    private readonly Action<string> _send;
    private readonly int _fallingEdgeDelayMs;
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    public CatIfStateReporter(
        RadioService radio,
        int fallingEdgeDelayMs,
        Action<string> send,
        ILogger log)
    {
        _radio = radio;
        _fallingEdgeDelayMs = Math.Max(0, fallingEdgeDelayMs);
        _send = send;
        _log = log;
    }

    public void Report(bool moxOn)
    {
        try
        {
            CancellationToken cancellationToken;
            lock (_sync)
            {
                if (_disposed) return;
                if (moxOn || _fallingEdgeDelayMs == 0)
                {
                    SendCurrent();
                    return;
                }
                cancellationToken = _disposeCts.Token;
            }

            _ = ReportFallingEdgeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            LogFailure(ex, deferred: false);
        }
    }

    private async Task ReportFallingEdgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_fallingEdgeDelayMs, cancellationToken);
            lock (_sync)
            {
                if (_disposed) return;
                SendCurrent();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogFailure(ex, deferred: true);
        }
    }

    private void SendCurrent()
    {
        var state = _radio.SnapshotCatIfState();
        _send(CatProtocol.Response(
            "IF",
            CatProtocol.BuildIfBody(
                state.VfoHz,
                state.Mode,
                state.Mox,
                split: false)));
    }

    private void LogFailure(Exception ex, bool deferred)
    {
        try
        {
            _log.LogDebug(
                ex,
                "cat.if.report.failed deferred={Deferred}",
                deferred);
        }
        catch
        {
            // CAT diagnostics must never escape into the radio MOX transition.
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _disposeCts.Cancel();
        }
        _disposeCts.Dispose();
    }
}
