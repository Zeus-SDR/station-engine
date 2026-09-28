// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
namespace Zeus.Server;

/// <summary>
/// Two-level decision for the coherent envelope. A slowly measured mark rail
/// keeps random in-mark dips from moving the decision threshold. The low rail
/// is noise; a Rayleigh noise envelope alone has insufficient rail separation.
/// </summary>
internal sealed class MatchedToneThreshold
{
    private const int History = 384;
    private readonly RollingQuantiles _history = new(History);
    private double _noise;
    private bool _haveSignal;
    public bool TonePresent { get; private set; }
    public double NoiseFloor { get; private set; }
    public double SnrDb { get; private set; }
    public bool HasMeasurement { get; private set; }

    public bool Update(double amplitude)
    {
        if (!double.IsFinite(amplitude) || amplitude < 0) amplitude = 0;
        _history.Push(amplitude);
        double low = Math.Max(_history.Percentile(0.25, interpolate: false), 1e-12);
        double high = _history.Percentile(0.80, interpolate: false);
        if (!_haveSignal) _noise = low;
        low = Math.Max(_noise, 1e-12);
        NoiseFloor = low;
        if (_history.Count < 32 || high < 3.5 * low)
            return TonePresent = false;
        _haveSignal = true;
        double threshold = Math.Max(2.5 * low, (TonePresent ? 0.45 : 0.55) * high);
        TonePresent = amplitude >= threshold;
        if (TonePresent)
        {
            SnrDb = 20 * Math.Log10(Math.Max(amplitude / low, 1));
            HasMeasurement = true;
        }
        return TonePresent;
    }

    public void Reset()
    {
        _history.Reset();
        _noise = 0;
        _haveSignal = false;
        TonePresent = false;
        NoiseFloor = 0;
        SnrDb = 0;
        HasMeasurement = false;
    }
}
