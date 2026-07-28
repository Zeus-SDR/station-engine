// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

namespace Zeus.Server;

/// <summary>
/// Band noise-floor tracker for Auto-AGC-T — a faithful port of Thetis's
/// <c>processNoiseFloor</c> (display.cs, [2.10.3.9]) and its fast-attack
/// semantics, which is the estimator Thetis's auto-AGC timer consumes
/// (console.cs <c>tmrAutoAGC_Tick</c>).
///
/// Per feed sample:
///  1. Gate: only bins quieter than (running mean + 2 dB) count as noise
///     (Thetis: <c>max_copy &lt; currentAverage</c>, the "+2 so we dont include
///     samples close to our current average" gate).
///  2. If at least 15% of the valid bins pass the gate (Thetis
///     <c>_NFsensitivity</c> default 3 → width·3/20), the frame estimate is the
///     linear-power mean of those bins, and the running mean moves halfway to
///     it in the power domain (Thetis's 2-tap <c>(new+old)/2</c> smoothing).
///     Otherwise the band is signal-saturated and the estimate walks UP
///     (Thetis: +1 dB/frame ≈ 30 dB/s at its ~30 fps display rate; the 5 Hz
///     meter feed here uses 6 dB/tick for the same slew).
///  3. The published floor lers toward the running mean with a 2 s attack
///     (Thetis <c>AttackTimeInMS</c> default 2000), normalised by feed rate so
///     the wall-clock time constant matches Thetis at any tick rate.
///
/// Fast-attack (Thetis <c>FastAttackNoiseFloorRX1</c>, set on band change,
/// &gt;0.5 MHz VFO jump, preamp/attenuator step): the lerp snaps instantly to
/// the running mean, and publishing is suppressed — Thetis only raises
/// <c>IsNoiseFloorGoodRX1</c> when NOT in fast-attack — until the estimate has
/// converged within 1 dB AND at least 1 s has elapsed (Thetis uses
/// max(1 s, FFT fill time); the 1 s floor dominates in practice).
///
/// Cold start: Thetis seeds its running mean at −200 dBm and walks up to the
/// band (~1.6 s at its frame rate; ~16 s at our 5 Hz feed). We instead seed
/// directly from the first frame — same fixed point, no slow crawl.
///
/// NOT thread-safe: drive it from the single meter tick only.
/// </summary>
internal sealed class AutoAgcNoiseFloorTracker
{
    // Thetis gate: bins below (running mean + 2 dB) (display.cs:5242).
    private const double GateAboveMeanDb = 2.0;
    // Thetis requireSamples = width · (_NFsensitivity / 20), default 3 → 15%.
    private const double QuietBinFraction = 3.0 / 20.0;
    // Thetis AttackTimeInMSForRX1 default (display.cs:4644).
    private const double AttackSeconds = 2.0;
    // Thetis up-walk +1 dB per display frame (~30 fps) ⇒ ~30 dB/s; at the 5 Hz
    // meter feed that is 6 dB per tick.
    private const double UpWalkDbPerTick = 6.0;
    // Thetis clears fast-attack when |binAvg − lerp| < 1 dB and ≥ 1 s elapsed.
    private const double FastAttackConvergedDb = 1.0;
    private const long FastAttackMinMs = 1000;
    // Bins at or below this are the sanitiser's invalid sentinel (−200 pinned);
    // mirrors the predicate TryNoiseFloorFromDisplayBins used.
    private const float MinValidBinDb = -190.0f;

    private readonly double _feedFps;
    private double _binAverage = double.NaN;   // Thetis m_fFFTBinAverage
    private double _lerpAverage = double.NaN;  // Thetis m_fLerpAverage
    private bool _fastAttack;
    private long _fastAttackSinceMs;

    public AutoAgcNoiseFloorTracker(double feedFps = 5.0)
    {
        _feedFps = feedFps;
    }

    /// <summary>The settled floor estimate (Thetis NoiseFloorRX1). Valid only
    /// when <see cref="IsGood"/>; during fast-attack Thetis withholds the
    /// reading and the consumer holds its last AGC threshold.</summary>
    public double FloorDbm { get; private set; } = double.NaN;

    /// <summary>Thetis IsNoiseFloorGoodRX1: false while fast-attacking or
    /// before the first sample.</summary>
    public bool IsGood { get; private set; }

    /// <summary>Enter fast-attack (band change / big VFO jump / preamp or
    /// attenuator step / auto re-engaged): the old band's running mean is
    /// discarded (the next sample cold-seeds from the new band), the lerp
    /// snaps to it, and publishing holds off until settled. NOTE: Thetis keeps
    /// its running mean across fast-attack and lets the 2-tap drain it, which
    /// converges in ~10 frames — 0.3 s at its 30 fps display rate but a full
    /// 2 s at our 5 Hz feed, longer than the 1 s publish gate. Re-seeding is
    /// the rate-compensated equivalent: the estimate is fully re-seeded well
    /// inside the gate instead of publishing half-converged.</summary>
    public void FastAttack(long nowMs)
    {
        _fastAttack = true;
        _fastAttackSinceMs = nowMs;
        _binAverage = double.NaN;
        _lerpAverage = double.NaN;
        IsGood = false;
    }

    /// <summary>Full reset (auto toggled off): forget the band entirely.</summary>
    public void Reset()
    {
        _binAverage = double.NaN;
        _lerpAverage = double.NaN;
        _fastAttack = false;
        _fastAttackSinceMs = 0;
        FloorDbm = double.NaN;
        IsGood = false;
    }

    /// <summary>
    /// Feed one panadapter snapshot (display-dB bins, invalid pinned at −200).
    /// <paramref name="bins"/> is a scratch buffer and may be mutated.
    /// </summary>
    public void AddBins(Span<float> bins, long nowMs)
    {
        // Compact the valid bins, accumulating the full-span power mean for the
        // cold seed (Thetis seeds at −200 and walks up; we seed at the first
        // frame's mean — same fixed point without the multi-second crawl).
        int valid = 0;
        double allPowerSum = 0.0;
        for (int i = 0; i < bins.Length; i++)
        {
            float v = bins[i];
            if (!float.IsFinite(v) || v <= MinValidBinDb) continue;
            bins[valid++] = v;
            allPowerSum += Math.Pow(10.0, v / 10.0);
        }
        if (valid == 0) return;

        double frameEstimate;
        if (double.IsNaN(_binAverage))
        {
            // Cold seed: ungated full-span mean, then fall through so the first
            // gated pass refines it immediately.
            frameEstimate = 10.0 * Math.Log10(allPowerSum / valid + 1e-60);
            _binAverage = frameEstimate;
            _lerpAverage = frameEstimate;
        }

        // Thetis gate: only bins quieter than (running mean + 2 dB) are noise.
        double gate = _binAverage + GateAboveMeanDb;
        double quietPowerSum = 0.0;
        int quietCount = 0;
        for (int i = 0; i < valid; i++)
        {
            double v = bins[i];
            if (v < gate)
            {
                quietPowerSum += Math.Pow(10.0, v / 10.0);
                quietCount++;
            }
        }

        int requireSamples = Math.Max(1, (int)(valid * QuietBinFraction));
        if (quietCount >= requireSamples)
        {
            frameEstimate = 10.0 * Math.Log10(quietPowerSum / quietCount + 1e-60);
            AddFrameEstimate(frameEstimate, nowMs);
        }
        else
        {
            // Band too crowded to find quiet bins: walk the estimate up until
            // the gate re-opens (Thetis +1 dB/frame).
            AddFrameEstimate(null, nowMs);
        }
    }

    /// <summary>
    /// Feed a scalar floor proxy (S-meter fallback for a sustained spectrum
    /// outage). No gating is possible without bins; the 2-tap + lerp still
    /// apply so the fallback cannot step the gain.
    /// </summary>
    public void AddScalar(double dbm, long nowMs)
    {
        if (!double.IsFinite(dbm)) return;
        if (double.IsNaN(_binAverage))
        {
            _binAverage = dbm;
            _lerpAverage = dbm;
        }
        AddFrameEstimate(dbm, nowMs);
    }

    /// <summary>Thetis processNoiseFloor body: 2-tap power smoothing (or the
    /// up-walk when <paramref name="frameEstimate"/> is null), then the attack-
    /// time lerp, fast-attack clearing, and publish gating.</summary>
    private void AddFrameEstimate(double? frameEstimate, long nowMs)
    {
        if (frameEstimate is double est)
        {
            // 2-tap in the power domain: mean of (new, old) linear powers.
            double newLinear = Math.Pow(10.0, est / 10.0);
            double oldLinear = Math.Pow(10.0, _binAverage / 10.0);
            _binAverage = 10.0 * Math.Log10((newLinear + oldLinear) * 0.5 + 1e-60);
        }
        else
        {
            _binAverage += UpWalkDbPerTick;
        }
        _binAverage = Math.Clamp(_binAverage, -200.0, 200.0);

        // Lerp toward the running mean with the attack time constant (Thetis:
        // framesInAttack = fps·attackSec + 1; fast-attack ⇒ 1 ⇒ instant snap).
        int framesInAttack = _fastAttack ? 1 : (int)(_feedFps * AttackSeconds) + 1;
        _lerpAverage += (_binAverage - _lerpAverage) / framesInAttack;

        // Thetis clears fast-attack once converged AND the settle delay has
        // passed; only then is the floor published again.
        if (_fastAttack &&
            Math.Abs(_binAverage - _lerpAverage) < FastAttackConvergedDb &&
            nowMs - _fastAttackSinceMs >= FastAttackMinMs)
        {
            _fastAttack = false;
        }

        if (_fastAttack)
        {
            IsGood = false;
        }
        else
        {
            FloorDbm = _lerpAverage;
            IsGood = true;
        }
    }
}
