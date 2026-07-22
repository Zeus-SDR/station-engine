// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Protocol2;

internal enum RxSilenceRecoveryAction
{
    None,
    AttemptRestart,
    Blocked,
    Exhausted
}

internal readonly record struct RxSilenceRecoveryDecision(
    RxSilenceRecoveryAction Action,
    int Attempt,
    int MaxAttempts,
    long SilenceMs);

internal readonly record struct RxSilenceRecoverySuccess(int Attempts, long SilenceMs);

internal sealed class RxSilenceRecoveryPolicy
{
    private readonly int _silenceBeforeRecoveryMs;
    private readonly int _attemptSpacingMs;
    private readonly int _maxAttempts;
    private long _lastPacketMs;
    private long _lastAttemptMs;
    private long _lastBlockedLogMs;
    private int _attempts;
    private bool _exhaustedReported;

    public RxSilenceRecoveryPolicy(
        long startMs,
        int silenceBeforeRecoveryMs,
        int attemptSpacingMs,
        int maxAttempts)
    {
        if (silenceBeforeRecoveryMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(silenceBeforeRecoveryMs));
        if (attemptSpacingMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptSpacingMs));
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _lastPacketMs = startMs;
        _silenceBeforeRecoveryMs = silenceBeforeRecoveryMs;
        _attemptSpacingMs = attemptSpacingMs;
        _maxAttempts = maxAttempts;
    }

    public int Attempts => _attempts;

    public RxSilenceRecoveryDecision Tick(long nowMs, bool streamingExpected, bool recoveryBlocked)
    {
        if (!streamingExpected)
            return None(nowMs);

        long silenceMs = Math.Max(0, nowMs - _lastPacketMs);
        if (silenceMs < _silenceBeforeRecoveryMs)
            return None(silenceMs);

        if (recoveryBlocked)
        {
            if (_lastBlockedLogMs == 0 || nowMs - _lastBlockedLogMs >= _attemptSpacingMs)
            {
                _lastBlockedLogMs = nowMs;
                return new RxSilenceRecoveryDecision(
                    RxSilenceRecoveryAction.Blocked,
                    _attempts,
                    _maxAttempts,
                    silenceMs);
            }
            return None(silenceMs);
        }

        if (_attempts >= _maxAttempts)
        {
            if (nowMs - _lastAttemptMs < _attemptSpacingMs)
                return None(silenceMs);
            if (_exhaustedReported)
                return None(silenceMs);
            _exhaustedReported = true;
            return new RxSilenceRecoveryDecision(
                RxSilenceRecoveryAction.Exhausted,
                _attempts,
                _maxAttempts,
                silenceMs);
        }

        if (_attempts == 0 || nowMs - _lastAttemptMs >= _attemptSpacingMs)
        {
            _attempts++;
            _lastAttemptMs = nowMs;
            return new RxSilenceRecoveryDecision(
                RxSilenceRecoveryAction.AttemptRestart,
                _attempts,
                _maxAttempts,
                silenceMs);
        }

        return None(silenceMs);
    }

    public RxSilenceRecoverySuccess? RecordPacket(long nowMs)
    {
        long silenceMs = Math.Max(0, nowMs - _lastPacketMs);
        int attempts = _attempts;
        bool recovered = attempts > 0 || _exhaustedReported;
        _lastPacketMs = nowMs;
        _lastAttemptMs = 0;
        _lastBlockedLogMs = 0;
        _attempts = 0;
        _exhaustedReported = false;
        return recovered ? new RxSilenceRecoverySuccess(attempts, silenceMs) : null;
    }

    private static RxSilenceRecoveryDecision None(long silenceMs) =>
        new(RxSilenceRecoveryAction.None, 0, 0, silenceMs);
}
