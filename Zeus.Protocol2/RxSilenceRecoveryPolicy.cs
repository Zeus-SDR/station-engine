// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Protocol2;

internal enum RxSilenceRecoveryAction
{
    None,
    AttemptRestart,
    Blocked,
    Exhausted,
    Displaced
}

internal readonly record struct RxSilenceRecoveryDecision(
    RxSilenceRecoveryAction Action,
    int Attempt,
    int MaxAttempts,
    long SilenceMs);

internal readonly record struct RxSilenceRecoverySuccess(int Attempts, long SilenceMs);

internal sealed class RxSilenceRecoveryPolicy
{
    // A restart hands the radio's stream back to this host, and a second live
    // controller gets it back by the same route once its own 3 s silence
    // watchdog fires. IQ therefore runs for about one watchdog period after each
    // restart (issue 3607: restarts exactly 6.012 s apart). The window brackets
    // that signature; a shorter blip or a longer healthy run stays a network
    // fault and keeps the ordinary retry path.
    internal const int DefaultDisplacedMinRunMs = 2000;
    internal const int DefaultDisplacedMaxRunMs = 5000;

    private readonly int _silenceBeforeRecoveryMs;
    private readonly int _attemptSpacingMs;
    private readonly int _maxAttempts;
    private readonly int _displacedMinRunMs;
    private readonly int _displacedMaxRunMs;
    private readonly long _noYieldUntilMs;
    private long _lastPacketMs;
    private long _lastAttemptMs;
    private long _lastRestartMs;
    private long _lastBlockedLogMs;
    private int _attempts;
    private bool _exhaustedReported;

    public RxSilenceRecoveryPolicy(
        long startMs,
        int silenceBeforeRecoveryMs,
        int attemptSpacingMs,
        int maxAttempts,
        int displacedMinRunMs = DefaultDisplacedMinRunMs,
        int displacedMaxRunMs = DefaultDisplacedMaxRunMs,
        long noYieldUntilMs = 0)
    {
        if (silenceBeforeRecoveryMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(silenceBeforeRecoveryMs));
        if (attemptSpacingMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptSpacingMs));
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (displacedMinRunMs <= 0 || displacedMaxRunMs <= displacedMinRunMs)
            throw new ArgumentOutOfRangeException(nameof(displacedMaxRunMs));
        _lastPacketMs = startMs;
        _silenceBeforeRecoveryMs = silenceBeforeRecoveryMs;
        _attemptSpacingMs = attemptSpacingMs;
        _maxAttempts = maxAttempts;
        _displacedMinRunMs = displacedMinRunMs;
        _displacedMaxRunMs = displacedMaxRunMs;
        _noYieldUntilMs = noYieldUntilMs;
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

        // A session that just took the radio over keeps restarting for the hold
        // window: giving up here would hand the radio back to the old controller.
        bool holdingTakeover = nowMs < _noYieldUntilMs;

        if (!holdingTakeover && WasDisplacedAfterRestart())
        {
            if (_exhaustedReported)
                return None(silenceMs);
            _exhaustedReported = true;
            return new RxSilenceRecoveryDecision(
                RxSilenceRecoveryAction.Displaced,
                _attempts,
                _maxAttempts,
                silenceMs);
        }

        if (!holdingTakeover && _attempts >= _maxAttempts)
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
            _lastRestartMs = nowMs;
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

    private bool WasDisplacedAfterRestart()
    {
        if (_lastRestartMs == 0) return false;
        long iqRunAfterRestartMs = _lastPacketMs - _lastRestartMs;
        return iqRunAfterRestartMs >= _displacedMinRunMs
            && iqRunAfterRestartMs < _displacedMaxRunMs;
    }

    private static RxSilenceRecoveryDecision None(long silenceMs) =>
        new(RxSilenceRecoveryAction.None, 0, 0, silenceMs);
}
