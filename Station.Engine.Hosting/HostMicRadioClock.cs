// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
namespace Zeus.Server;

// Packet-clock credits only: neither DSP nor allocation runs on the UDP thread.
internal sealed class HostMicRadioClock
{
    internal const int MaximumCredits = 960;
    internal const int MaximumAgeMs = 80;
    private readonly object _sync = new();
    private bool _connected, _seen, _everSeen;
    private uint _sequence;
    private long _last;
    private int _credits, _epoch;
    internal event Action? Ready;
    internal void Connect(bool connected)
    {
        lock (_sync)
        {
            _connected = connected;
            _seen = false;
            _everSeen = false;
            _credits = 0;
            _epoch++;
        }
        Ready?.Invoke();
    }
    internal void Accept(uint sequence, int samples) => Accept(sequence, samples, Stopwatch.GetTimestamp());
    internal void Accept(uint sequence, int samples, long now)
    {
        lock (_sync)
        {
            if (!_connected || samples != 64) return;
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
            }
            _seen = true;
            _everSeen = true;
            _sequence = sequence;
            _last = now;
            _credits += elapsedSamples;
        }
        Ready?.Invoke();
    }
    internal (bool Connected, bool Running, int Epoch, int Samples) Take(long now)
    {
        lock (_sync)
        {
            if (_seen && Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds > MaximumAgeMs)
            {
                _seen = false;
                _credits = 0;
                _epoch++;
            }
            int samples = _credits;
            _credits = 0;
            return (_connected && _everSeen, _seen, _epoch, samples);
        }
    }
}
