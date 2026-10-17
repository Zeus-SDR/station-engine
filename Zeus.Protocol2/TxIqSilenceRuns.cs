// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Protocol2;

// Single owner at each seam. Durations describe contiguous emitted sample
// content, not elapsed wall time; a missing packet is a timing diagnostic.
internal sealed class TxIqSilenceRuns
{
    private long _epoch = long.MinValue;
    private int _rate;
    private long _samples, _windowSamples, _windowZeros, _windowQuiet;
    private long _zeros, _quiet, _maxZeros, _maxQuiet, _initialQuiet;
    private int _resumptions;
    private bool _heardSignal;

    internal long Epoch => _epoch;
    internal bool HasSamples => _samples > 0;

    internal void Observe(long epoch, int sampleRate, int samples, bool exactZero, bool quiet)
    {
        if (sampleRate <= 0) return;
        if (epoch != _epoch || sampleRate != _rate)
        {
            _epoch = epoch;
            _rate = sampleRate;
            _samples = _windowSamples = _windowZeros = _windowQuiet = 0;
            _zeros = _quiet = _maxZeros = _maxQuiet = _initialQuiet = 0;
            _resumptions = 0;
            _heardSignal = false;
        }
        if (samples <= 0) return;
        _samples += samples;
        _windowSamples += samples;
        if (exactZero) _windowZeros += samples;
        if (quiet) _windowQuiet += samples;
        _zeros = exactZero ? _zeros + samples : 0;
        if (quiet)
        {
            _quiet += samples;
            if (!_heardSignal) _initialQuiet += samples;
        }
        else
        {
            if (_heardSignal && _quiet * 1000.0 / _rate >= 50) _resumptions++;
            _quiet = 0;
            _heardSignal = true;
        }
        _maxZeros = Math.Max(_maxZeros, _zeros);
        _maxQuiet = Math.Max(_maxQuiet, _quiet);
    }

    internal Window TakeWindow()
    {
        double Ms(long samples) => _rate > 0 ? samples * 1000.0 / _rate : 0;
        var window = new Window(_epoch, _windowSamples, _windowZeros, _windowQuiet,
            Ms(_samples), Ms(_zeros), Ms(_maxZeros), Ms(_quiet), Ms(_maxQuiet),
            Ms(_initialQuiet), _resumptions);
        _windowSamples = _windowZeros = _windowQuiet = 0;
        _maxZeros = _zeros;
        _maxQuiet = _quiet;
        _resumptions = 0;
        return window;
    }

    internal readonly record struct Window(long Epoch, long Samples, long ZeroSamples, long QuietSamples,
        double SampleTimeMs, double CurrentZeroMs, double MaxZeroMs, double CurrentQuietMs,
        double MaxQuietMs, double InitialQuietMs, int QuietResumptionsOver50Ms);
}
