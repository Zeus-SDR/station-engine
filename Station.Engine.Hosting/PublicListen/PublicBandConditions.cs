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
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.PublicListen;

/// <summary>One amateur band segment. <see cref="Band"/> is the directory key ("40", "6", …).</summary>
public readonly record struct PublicBandEdge(string Band, long LowHz, long HighHz);

/// <summary>
/// The amateur band segments Public Listening reports conditions for — the
/// single table shared by the station measurement and (by key) the broker's
/// allow-list. IARU-style HF edges plus 6 m; a band is measured only when it
/// lies wholly inside the analysed span.
/// </summary>
public static class PublicBandPlan
{
    public static readonly IReadOnlyList<PublicBandEdge> Bands =
    [
        new("160", 1_800_000, 2_000_000),
        new("80", 3_500_000, 4_000_000),
        new("60", 5_330_000, 5_410_000),
        new("40", 7_000_000, 7_300_000),
        new("30", 10_100_000, 10_150_000),
        new("20", 14_000_000, 14_350_000),
        new("17", 18_068_000, 18_168_000),
        new("15", 21_000_000, 21_450_000),
        new("12", 24_890_000, 24_990_000),
        new("10", 28_000_000, 29_700_000),
        new("6", 50_000_000, 54_000_000),
    ];
}

/// <summary>
/// Smoothed conditions on one band. <see cref="NoiseDbm"/> is the wideband
/// analyzer's display dB (dB relative to ADC full scale, since the overview is
/// not RF-calibrated) — "dBm" only in the directory contract's field name.
/// </summary>
public sealed record PublicBandCondition(string Band, double SnrDb, double NoiseDbm, DateTimeOffset UpdatedUtc);

/// <summary>Thread-safe snapshot of the measured band conditions.</summary>
public sealed record PublicBandConditions(DateTimeOffset? UpdatedUtc, IReadOnlyList<PublicBandCondition> Bands)
{
    public static readonly PublicBandConditions Empty = new(null, []);
}

/// <summary>
/// Band conditions measured from the Public Listening wideband overview
/// (docs/designs/public-listening.md, "Band conditions"). For each band in
/// <see cref="PublicBandPlan"/>: noise = the <see cref="NoisePercentile"/> of the
/// band's pixels, signal = the <see cref="SignalPercentile"/>, SNR = signal −
/// noise, each smoothed by a time-based EMA (<see cref="EmaTimeConstant"/>, so
/// ~30 s of frames settle it). Callers ingest only frames that were NOT taken
/// under the transmit hold. <see cref="Ingest"/> runs on the wideband analyzer
/// worker; <see cref="Snapshot"/> from any thread.
/// </summary>
internal sealed class PublicBandConditionsTracker
{
    public const double NoisePercentile = 0.20;
    public const double SignalPercentile = 0.98;
    public static readonly TimeSpan EmaTimeConstant = TimeSpan.FromSeconds(10);

    /// <summary>A band narrower than this many pixels is not measured.</summary>
    public const int MinBandPixels = 3;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly BandState[] _states = new BandState[PublicBandPlan.Bands.Count];
    private float[] _scratch = [];

    public PublicBandConditionsTracker(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Measure one overview row (dB per pixel; pixel <c>i</c> covers
    /// <c>[start + i·hzPerPixel, start + (i+1)·hzPerPixel)</c> with
    /// <c>start = centerHz − width·hzPerPixel/2</c>) and fold it into the EMA.
    /// </summary>
    public void Ingest(ReadOnlySpan<float> rowDb, long centerHz, float hzPerPixel)
    {
        if (rowDb.IsEmpty || !(hzPerPixel > 0f)) return;
        if (_scratch.Length < rowDb.Length) _scratch = new float[rowDb.Length];

        long nowTs = _time.GetTimestamp();
        var nowUtc = _time.GetUtcNow();
        lock (_gate)
        {
            for (int i = 0; i < _states.Length; i++)
            {
                var edge = PublicBandPlan.Bands[i];
                if (!TryMeasure(rowDb, centerHz, hzPerPixel, edge.LowHz, edge.HighHz, _scratch,
                        out double noise, out double signal))
                    continue;

                ref var state = ref _states[i];
                if (!state.Valid)
                {
                    state.Noise = noise;
                    state.Signal = signal;
                    state.Valid = true;
                }
                else
                {
                    double dt = _time.GetElapsedTime(state.LastTimestamp, nowTs).TotalSeconds;
                    double alpha = 1.0 - Math.Exp(-Math.Max(0.0, dt) / EmaTimeConstant.TotalSeconds);
                    state.Noise += (noise - state.Noise) * alpha;
                    state.Signal += (signal - state.Signal) * alpha;
                }
                state.LastTimestamp = nowTs;
                state.UpdatedUtc = nowUtc;
            }
        }
    }

    public PublicBandConditions Snapshot()
    {
        lock (_gate)
        {
            List<PublicBandCondition>? bands = null;
            DateTimeOffset? latest = null;
            for (int i = 0; i < _states.Length; i++)
            {
                var state = _states[i];
                if (!state.Valid) continue;
                (bands ??= []).Add(new PublicBandCondition(
                    PublicBandPlan.Bands[i].Band,
                    Math.Max(0.0, state.Signal - state.Noise),
                    state.Noise,
                    state.UpdatedUtc));
                if (latest is null || state.UpdatedUtc > latest) latest = state.UpdatedUtc;
            }
            return bands is null ? PublicBandConditions.Empty : new PublicBandConditions(latest, bands);
        }
    }

    /// <summary>
    /// Noise (low percentile) and signal (high percentile) of the row pixels
    /// whose centre falls inside [lowHz, highHz]. False when the band is not
    /// wholly inside the row's span or covers fewer than <see cref="MinBandPixels"/>.
    /// </summary>
    internal static bool TryMeasure(
        ReadOnlySpan<float> rowDb,
        long centerHz,
        float hzPerPixel,
        long lowHz,
        long highHz,
        Span<float> scratch,
        out double noiseDb,
        out double signalDb)
    {
        noiseDb = signalDb = 0;
        double startHz = centerHz - (rowDb.Length * (double)hzPerPixel / 2.0);
        double endHz = startHz + (rowDb.Length * (double)hzPerPixel);
        if (lowHz < startHz || highHz > endHz) return false;

        int first = Math.Max(0, (int)Math.Ceiling(((lowHz - startHz) / hzPerPixel) - 0.5));
        int last = Math.Min(rowDb.Length - 1, (int)Math.Floor(((highHz - startHz) / hzPerPixel) - 0.5));
        int count = last - first + 1;
        if (count < MinBandPixels || scratch.Length < count) return false;

        var values = scratch[..count];
        int n = 0;
        foreach (float v in rowDb.Slice(first, count))
        {
            if (float.IsFinite(v)) values[n++] = v;
        }
        if (n < MinBandPixels) return false;
        values = values[..n];
        values.Sort();
        noiseDb = Percentile(values, NoisePercentile);
        signalDb = Percentile(values, SignalPercentile);
        return true;
    }

    /// <summary>Linear-interpolated percentile (0..1) of an ascending-sorted span.</summary>
    internal static double Percentile(ReadOnlySpan<float> sorted, double fraction)
    {
        if (sorted.IsEmpty) return double.NaN;
        double rank = Math.Clamp(fraction, 0.0, 1.0) * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = Math.Min(sorted.Length - 1, lo + 1);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }

    private struct BandState
    {
        public bool Valid;
        public double Noise;
        public double Signal;
        public long LastTimestamp;
        public DateTimeOffset UpdatedUtc;
    }
}

/// <summary>
/// The periodic "band survey" (docs/designs/public-listening.md, "Band
/// conditions"): while the station is listed in the directory, shares the
/// wideband overview and is on a Protocol 2 radio, pulse public wideband
/// demand for <see cref="Duration"/> every <see cref="Interval"/> so the band
/// conditions stay fresh with no listener connected. The pulse goes through the
/// normal debounced public demand, so the transport runs for about
/// Duration + 30 s. No pulse while transmitting; listener wideband demand
/// already measures, so it restarts the interval instead. Thread-safe.
/// </summary>
internal sealed class PublicBandSurvey
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(8);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private bool _surveyed;
    private long _lastStartTimestamp;
    private bool _pulsing;

    public PublicBandSurvey(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// True while a survey pulse wants the wideband transport. The first pulse
    /// starts as soon as the station becomes eligible.
    /// </summary>
    public bool Update(bool eligible, bool listenersDriving, bool transmitting)
    {
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            if (!eligible)
            {
                _surveyed = false;
                _pulsing = false;
                return false;
            }
            if (listenersDriving)
            {
                // Listeners keep the overview (and the measurement) running.
                _surveyed = true;
                _lastStartTimestamp = now;
                _pulsing = false;
                return false;
            }
            if (transmitting)
            {
                _pulsing = false;
                return false;
            }
            if (_pulsing)
            {
                if (_time.GetElapsedTime(_lastStartTimestamp, now) < Duration) return true;
                _pulsing = false;
            }
            if (_surveyed && _time.GetElapsedTime(_lastStartTimestamp, now) < Interval) return false;

            _surveyed = true;
            _pulsing = true;
            _lastStartTimestamp = now;
            return true;
        }
    }
}
