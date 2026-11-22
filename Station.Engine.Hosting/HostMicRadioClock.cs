// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
namespace Zeus.Server;

// Packet-clock credits only: neither DSP nor allocation runs on the UDP thread.
internal sealed class HostMicRadioClock(Func<long>? timestamp = null)
{
    internal const int MaximumCredits = 960;
    internal const int MaximumAgeMs = 80;
    internal const int FallbackGapMs = 20;
    internal const int RecoveryMs = 500;
    private const int MinimumRecoverySamples = 21_600; // 90% of 48 kHz over RecoveryMs.
    private readonly object _sync = new();
    private readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    private bool _connected, _seen, _everSeen;
    private uint _sequence;
    private long _last;
    private long? _healthySince;
    private int _recoverySamples;
    private bool _fallback;
    private int _credits, _epoch;
    internal event Action? Ready;
    internal void Connect(bool connected)
    {
        lock (_sync)
        {
            _connected = connected;
            _seen = false;
            _everSeen = false;
            _fallback = false;
            _healthySince = null;
            _recoverySamples = 0;
            _credits = 0;
            _epoch++;
        }
        Ready?.Invoke();
    }
    internal void Accept(uint sequence, int samples) => Accept(sequence, samples, _timestamp());
    internal void Accept(uint sequence, int samples, long now)
    {
        lock (_sync)
        {
            if (!_connected || samples != 64) return;
            if (_everSeen && Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds > FallbackGapMs)
                StartFallback();
            bool expired = _seen && Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds > MaximumAgeMs;
            if (_seen && !expired && unchecked((int)(sequence - _sequence)) <= 0) return;
            int elapsedSamples = _seen && !expired
                ? (int)Math.Min((long)unchecked(sequence - _sequence) * samples, MaximumCredits + 1L)
                : samples;
            if (expired || elapsedSamples > MaximumCredits || _credits + elapsedSamples > MaximumCredits)
            {
                _credits = 0;
                _epoch++;
                elapsedSamples = samples;
                if (_fallback) StartFallback();
            }
            _seen = true;
            _everSeen = true;
            _sequence = sequence;
            _last = now;
            _credits += elapsedSamples;
            if (_fallback)
            {
                _healthySince ??= now;
                _recoverySamples += samples;
                if (Stopwatch.GetElapsedTime(_healthySince.Value, now).TotalMilliseconds >= RecoveryMs)
                {
                    if (_recoverySamples >= MinimumRecoverySamples)
                        _fallback = false;
                    else
                        StartFallback();
                }
            }
        }
        Ready?.Invoke();
    }
    internal (bool Connected, bool Running, int Epoch, int Samples, bool UseCaptureClock) Take() => Take(_timestamp());

    internal (bool Connected, bool Running, int Epoch, int Samples, bool UseCaptureClock) Take(long now)
    {
        lock (_sync)
        {
            if (_everSeen && Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds > FallbackGapMs)
                StartFallback();
            if (_seen && Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds > MaximumAgeMs)
            {
                _seen = false;
                _credits = 0;
                _epoch++;
            }
            int samples = _credits;
            _credits = 0;
            return (_connected && _everSeen, _seen, _epoch, samples, _fallback);
        }
    }

    private void StartFallback()
    {
        _fallback = true;
        _healthySince = null;
        _recoverySamples = 0;
    }
}
