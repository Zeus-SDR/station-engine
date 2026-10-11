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
    private readonly RollingQuantiles _quietNoise = new(64);
    private double _noise;
    private bool _haveSignal;
    public bool TonePresent { get; private set; }
    public double NoiseFloor { get; private set; }
    public double SnrDb { get; private set; }
    public bool HasMeasurement { get; private set; }
    /// <summary>
    /// True for the one update on which the mark rail first separated from
    /// the noise rail. Blocks before it were forced off while the rails were
    /// still being measured; <see cref = "Classify"/> can re-read them.
    /// </summary>
    public bool JustAcquired { get; private set; }
    /// <summary>The mark rail has separated from the noise rail since the last reset.</summary>
    public bool Acquired => _haveSignal;
    // Capped nonzero incoherent observations from the first 384 updates.
    // This is opening evidence, not a continuously sliding receiver floor.
    internal double OpeningQuietNoise => _quietNoise.Count >= 16 ? _quietNoise.Percentile(.90, interpolate: false) : NoiseFloor;
    internal double MarkRail => _high;

    private double _high;
    private double _heldMarkRail;
    // Half-life of about 0.5 s of 5.33 ms blocks. The held rail only bridges
    // the opening until the history has marks in it; a fading sender must
    // not stay judged against its first peak.
    private const double HeldRailDecay = 0.9927;
    public bool Update(double amplitude, double phaseQuality = 1, bool hasCoherentSupport = false)
    {
        JustAcquired = false;
        if (!double.IsFinite(amplitude) || amplitude < 0)
            amplitude = 0;
        _history.Push(amplitude);
        // An initially keyed receiver has no quiet lead-in. Its first low
        // rail can contain marks, cutting short dits and training a fast clock.
        // Incoherent, nonzero observations provide a separate quiet rail.
        if (_history.Count < History && amplitude > 1e-10 && phaseQuality < 0.60)
            _quietNoise.Push(amplitude);
        double low = Math.Max(_history.Percentile(0.25, interpolate: false), 1e-12);
        double high = _history.Percentile(0.80, interpolate: false);
        if (!_haveSignal)
            _noise = low;
        else if (hasCoherentSupport && _history.Count < History && _quietNoise.Count >= 16)
        {
            // Refine only the opening, with the FSM's coherent-support flag.
            // This flag can include a single very clean mark; it is not proof
            // of a complete Morse pattern, so the time and rail bounds matter.
            // The upper quiet percentile and 2:1 discrepancy leave a healthy
            // weak-signal floor alone; digital silence never enters this rail.
            double quiet = _quietNoise.Percentile(0.90, interpolate: false);
            if (quiet > 1e-10 && _noise > 2 * quiet)
                _noise = quiet;
        }

        low = Math.Max(_noise, 1e-12);
        NoiseFloor = low;
        if (_history.Count < 32 || high < 3.5 * low)
            return TonePresent = false;
        JustAcquired = !_haveSignal;
        _haveSignal = true;
        _heldMarkRail *= HeldRailDecay;
        _high = Math.Max(high, _heldMarkRail);
        return Classify(amplitude);
    }

    /// <summary>
    /// Key decision against the rails measured by the latest
    /// <see cref = "Update"/>, without adding <paramref name = "amplitude"/> to
    /// the history. Hysteresis carries across calls, so earlier blocks can be
    /// re-read in order before the current one.
    /// </summary>
    public bool Classify(double amplitude)
    {
        if (!_haveSignal)
            return TonePresent = false;
        if (!double.IsFinite(amplitude) || amplitude < 0)
            amplitude = 0;
        double low = NoiseFloor;
        double threshold = Math.Max(2.5 * low, (TonePresent ? 0.45 : 0.55) * _high);
        TonePresent = amplitude >= threshold;
        if (TonePresent)
        {
            SnrDb = 20 * Math.Log10(Math.Max(amplitude / low, 1));
            HasMeasurement = true;
        }

        return TonePresent;
    }

    /// <summary>
    /// Hold the mark rail at no less than <paramref name = "markLevel"/>,
    /// decaying with a half-life of about 0.5 s, until the next
    /// <see cref = "Reset"/>. At the moment of acquisition the 80th percentile
    /// has only just cleared the noise; a threshold that low keys noise peaks
    /// in the quiet before the sender and between its elements. The measured
    /// marks of the opening are the better rail until the history catches up.
    /// </summary>
    public void HoldMarkRail(double markLevel)
    {
        if (!double.IsFinite(markLevel) || markLevel <= 0)
            return;
        _heldMarkRail = Math.Max(_heldMarkRail, markLevel);
        _high = Math.Max(_high, _heldMarkRail);
    }

    public void Reset()
    {
        _history.Reset();
        _quietNoise.Reset();
        _noise = 0;
        _haveSignal = false;
        _high = 0;
        _heldMarkRail = 0;
        JustAcquired = false;
        TonePresent = false;
        NoiseFloor = 0;
        SnrDb = 0;
        HasMeasurement = false;
    }
}
