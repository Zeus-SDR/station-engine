// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.Sockets;

namespace Zeus.Protocol1;

internal enum RxFailureDecision
{
    Continue,
    StartSoftRestart,
    Disconnect,
    Exit
}

internal sealed class RxFailurePolicy
{
    private readonly int _transientFailureBudget;
    private int _consecutiveTransientFailures;
    private bool _softRestartStarted;

    public RxFailurePolicy(int transientFailureBudget)
    {
        if (transientFailureBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(transientFailureBudget));
        _transientFailureBudget = transientFailureBudget;
    }

    public int ConsecutiveTransientFailures => _consecutiveTransientFailures;

    public RxFailureDecision RecordSocketFailure(SocketError error, bool cancellationRequested)
    {
        if (cancellationRequested || error is SocketError.Interrupted or SocketError.OperationAborted)
            return RxFailureDecision.Exit;

        if (error is not (SocketError.TimedOut or SocketError.ConnectionReset))
            return RxFailureDecision.Disconnect;

        _consecutiveTransientFailures++;
        if (_consecutiveTransientFailures < _transientFailureBudget)
            return RxFailureDecision.Continue;

        if (!_softRestartStarted)
        {
            _softRestartStarted = true;
            return RxFailureDecision.StartSoftRestart;
        }

        return RxFailureDecision.Disconnect;
    }

    public void RecordSuccess()
    {
        _consecutiveTransientFailures = 0;
        _softRestartStarted = false;
    }
}
