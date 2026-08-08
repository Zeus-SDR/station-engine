// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server;

/// <summary>
/// Deterministic signal detector for the wideband spectrum path. Runs on the
/// analyzer's linear bin-power spectrum once per display frame and produces
/// sub-bin-accurate signal markers: refined center frequency, occupied
/// bandwidth, peak level, and SNR against an adaptively estimated noise
/// floor.
///
/// Pipeline per call:
///   1. Per-bin temporal EMA in linear power (cross-frame Welch-style
///      averaging) so detection works on a stable spectrum, not a single
///      snapshot.
///   2. Robust noise floor: iterative k-sigma clipping in the dB domain
///      rejects signals and outliers, converging on the true floor even in
///      a crowded band.
///   3. Hysteresis segmentation (enter floor+10 dB, exit floor+6 dB) with
///      short-gap bridging and linear sub-bin edge interpolation, so
///      bandwidth edges land between bins instead of quantizing to them.
///   4. Per-region refinement: parabolic sub-bin peak interpolation and a
///      floor-subtracted power centroid for marker placement.
///   5. Temporal tracking with confirm/hold hysteresis: a signal must be
///      seen twice before it is reported and survives a few missed frames
///      before it is dropped, so noise spikes never surface and real
///      signals ride through momentary fades.
///
/// All state is preallocated; steady-state calls allocate nothing. Not
/// thread-safe: each analysis worker owns its instance.
/// </summary>
internal sealed class WidebandSignalDetector
{
    public const int MaxTrackedSignals = 32;

    private const int MaxRegionsPerFrame = 512;
    // Temporal averaging time constant for the detection spectrum. At the
    // Saturn 100 ms frame cadence this is ~8 frames of averaging depth —
    // deep enough to pin the floor, shallow enough that a signal appearing
    // or disappearing is reflected within ~0.5 s.
    private const double AveragingTauMs = 400.0;
    private const double MinFrameIntervalMs = 10.0;
    private const double MaxFrameIntervalMs = 1_000.0;
    private const double MinPower = 1e-24;
    // Noise-floor estimation: three rounds of 2.5-sigma clipping in dB
    // converge tightly from below without being pulled up by strong signals.
    private const int FloorClipIterations = 3;
    private const double FloorClipSigma = 2.5;
    // Hysteresis: a bin must clear the floor by this much to open a region,
    // but the region only closes once power falls to within ExitAboveFloorDb
    // of the floor — the classic double-threshold scheme that stops edges
    // from chattering on noise wiggle.
    private const double EnterAboveFloorDb = 10.0;
    private const double ExitAboveFloorDb = 6.0;
    // Regions separated by at most this many bins of above-exit-threshold
    // spectrum are one signal (window mainlobes and modulated carriers dip
    // between peak bins).
    private const int MaxBridgeGapBins = 2;
    // Bins 0..4 are the analyzer's DC-suppression zone; never detect there.
    private const int FirstDetectableBin = 5;
    // Tracking: report after this many consecutive sightings, drop after
    // this many consecutive misses.
    private const int MinSightingsToReport = 2;
    private const int MaxMissesToKeep = 5;
    // Parameter smoothing once a track is matched to a fresh measurement.
    private const double TrackBlendAlpha = 0.5;

    private readonly int _maxBins;
    private readonly double[] _avgPower;
    private readonly double[] _db;
    private readonly bool[] _clipped;
    private readonly Region[] _regions = new Region[MaxRegionsPerFrame];
    private readonly Track[] _tracks = new Track[MaxTrackedSignals];
    private int _activeBins;
    private double _binHz;
    private bool _averagingValid;

    public WidebandSignalDetector(int maxBins)
    {
        if (maxBins < 64 || maxBins > (WidebandSpectrumAnalyzer.AnalysisFftSize / 2) + 1)
            throw new ArgumentOutOfRangeException(nameof(maxBins));
        _maxBins = maxBins;
        _avgPower = new double[maxBins];
        _db = new double[maxBins];
        _clipped = new bool[maxBins];
    }

    /// <summary>
    /// Runs detection on one frame of linear bin power. Writes up to
    /// <see cref="MaxTrackedSignals"/> markers (sorted by SNR, strongest
    /// first) and returns the count written. Frequencies are absolute:
    /// bin k sits at k · <paramref name="binHz"/>.
    /// </summary>
    public int Detect(
        ReadOnlySpan<double> binPower,
        double binHz,
        double frameIntervalMs,
        Span<WidebandSignalMarker> markers)
    {
        if (binPower.Length > _maxBins)
            throw new ArgumentException($"Bin count must not exceed {_maxBins}.", nameof(binPower));
        if (!double.IsFinite(binHz) || binHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(binHz), "Bin width must be finite and positive.");

        frameIntervalMs = Math.Clamp(frameIntervalMs, MinFrameIntervalMs, MaxFrameIntervalMs);
        int bins = binPower.Length;
        if (bins != _activeBins || Math.Abs(binHz - _binHz) > binHz * 1e-9)
        {
            // Resolution changed (zoom-driven resegmentation or a different
            // capture length): restart averaging and tracking rather than
            // blending spectra at two bin widths.
            _activeBins = bins;
            _binHz = binHz;
            _averagingValid = false;
            Array.Clear(_tracks);
        }

        double alpha = 1.0 - Math.Exp(-frameIntervalMs / AveragingTauMs);
        for (int bin = 0; bin < bins; bin++)
        {
            double p = binPower[bin];
            if (!double.IsFinite(p) || p < 0.0) p = 0.0;
            _avgPower[bin] = _averagingValid
                ? _avgPower[bin] * (1.0 - alpha) + p * alpha
                : p;
            _db[bin] = 10.0 * Math.Log10(Math.Max(_avgPower[bin], MinPower));
        }
        _averagingValid = true;

        double floorDb = EstimateNoiseFloor(bins);
        int regionCount = SegmentRegions(bins, floorDb);

        int emitted = UpdateTracksAndEmit(regionCount, floorDb, markers);
        return emitted;
    }

    private double EstimateNoiseFloor(int bins)
    {
        Array.Clear(_clipped, 0, bins);
        double mean = 0.0;
        for (int iteration = 0; iteration < FloorClipIterations; iteration++)
        {
            double sum = 0.0;
            int count = 0;
            for (int bin = FirstDetectableBin; bin < bins; bin++)
            {
                if (_clipped[bin]) continue;
                sum += _db[bin];
                count++;
            }
            if (count == 0) return -240.0;
            mean = sum / count;

            if (iteration == FloorClipIterations - 1) break;

            double variance = 0.0;
            for (int bin = FirstDetectableBin; bin < bins; bin++)
            {
                if (_clipped[bin]) continue;
                double d = _db[bin] - mean;
                variance += d * d;
            }
            double std = Math.Sqrt(variance / count);
            double cutoff = mean + FloorClipSigma * std;
            for (int bin = FirstDetectableBin; bin < bins; bin++)
            {
                if (!_clipped[bin] && _db[bin] > cutoff)
                    _clipped[bin] = true;
            }
        }
        return mean;
    }

    private int SegmentRegions(int bins, double floorDb)
    {
        double enterDb = floorDb + EnterAboveFloorDb;
        double exitDb = floorDb + ExitAboveFloorDb;
        int regionCount = 0;
        int bin = FirstDetectableBin;
        while (bin < bins && regionCount < MaxRegionsPerFrame)
        {
            if (_db[bin] <= enterDb)
            {
                bin++;
                continue;
            }

            // Find the last enter-threshold bin of this signal, bridging
            // dips of up to MaxBridgeGapBins bins between peaks (window
            // mainlobes and modulated carriers dip below the enter
            // threshold mid-signal).
            int lastStrong = bin;
            int scan = bin + 1;
            while (scan < bins && scan <= lastStrong + MaxBridgeGapBins + 1)
            {
                if (_db[scan] > enterDb) lastStrong = scan;
                scan++;
            }

            // Grow the region outward to the exit threshold on both sides.
            int lowStart = bin;
            while (lowStart > FirstDetectableBin && _db[lowStart - 1] > exitDb)
                lowStart--;
            int runEnd = lastStrong + 1;
            while (runEnd < bins && _db[runEnd] > exitDb)
                runEnd++;

            // Sub-bin edges: interpolate where the spectrum crosses the exit
            // threshold between the last above-exit bin and its neighbor.
            double lowEdge = lowStart;
            if (lowStart > 0 && _db[lowStart - 1] <= exitDb)
                lowEdge = lowStart - ThresholdCrossingFraction(_db[lowStart - 1], _db[lowStart], exitDb);
            double highEdge = runEnd;
            if (runEnd < bins && runEnd > 0 && _db[runEnd] <= exitDb)
                highEdge = (runEnd - 1) + ThresholdCrossingFraction(_db[runEnd], _db[runEnd - 1], exitDb);

            // Peak and parabolic sub-bin refinement in the dB domain.
            int peakBin = lowStart;
            for (int k = lowStart + 1; k < runEnd; k++)
            {
                if (_db[k] > _db[peakBin]) peakBin = k;
            }
            double refinedPeak = peakBin;
            if (peakBin > lowStart && peakBin < runEnd - 1)
            {
                double left = _db[peakBin - 1];
                double center = _db[peakBin];
                double right = _db[peakBin + 1];
                double denominator = left - (2.0 * center) + right;
                if (denominator < 0.0)
                {
                    double delta = 0.5 * (left - right) / denominator;
                    refinedPeak = peakBin + Math.Clamp(delta, -0.5, 0.5);
                }
            }

            // Floor-subtracted power centroid: places the marker on the
            // energy center for wide or asymmetric signals where the peak
            // bin is off-center.
            double floorPower = Math.Pow(10.0, floorDb / 10.0);
            double weightedSum = 0.0;
            double weightTotal = 0.0;
            for (int k = lowStart; k < runEnd; k++)
            {
                double w = Math.Max(0.0, _avgPower[k] - floorPower);
                weightedSum += k * w;
                weightTotal += w;
            }
            // Narrow signals (parabolic-refined) belong on the refined peak;
            // wide signals belong on the energy centroid.
            double widthBins = highEdge - lowEdge;
            double centroid = weightTotal > 0.0 ? weightedSum / weightTotal : refinedPeak;
            double centerBin = widthBins <= 4.0 ? refinedPeak : centroid;

            _regions[regionCount++] = new Region(
                centerBin,
                lowEdge,
                highEdge,
                _db[peakBin]);
            bin = runEnd;
        }
        return regionCount;
    }

    private static double ThresholdCrossingFraction(double outsideDb, double insideDb, double thresholdDb)
    {
        double span = insideDb - outsideDb;
        if (span <= 0.0) return 0.5;
        return Math.Clamp((thresholdDb - outsideDb) / span, 0.0, 1.0);
    }

    private int UpdateTracksAndEmit(int regionCount, double floorDb, Span<WidebandSignalMarker> markers)
    {
        // Match regions to live tracks by center proximity.
        for (int t = 0; t < _tracks.Length; t++)
        {
            ref var track = ref _tracks[t];
            if (!track.Active) continue;

            int best = -1;
            double bestDistance = double.MaxValue;
            for (int r = 0; r < regionCount; r++)
            {
                if (_regions[r].Matched) continue;
                double tolerance = Math.Max(3.0, (track.HighBin - track.LowBin) * 0.25);
                double distance = Math.Abs(_regions[r].CenterBin - track.CenterBin);
                if (distance <= tolerance && distance < bestDistance)
                {
                    best = r;
                    bestDistance = distance;
                }
            }

            if (best >= 0)
            {
                ref readonly var region = ref _regions[best];
                track.CenterBin = Blend(track.CenterBin, region.CenterBin);
                track.LowBin = Blend(track.LowBin, region.LowBin);
                track.HighBin = Blend(track.HighBin, region.HighBin);
                track.PeakDb = Blend(track.PeakDb, region.PeakDb);
                track.FloorDb = floorDb;
                track.Sightings++;
                track.Misses = 0;
                _regions[best].Matched = true;
            }
            else
            {
                track.Misses++;
                if (track.Misses > MaxMissesToKeep)
                    track = default;
            }
        }

        // Unmatched regions seed new tracks in free slots.
        for (int r = 0; r < regionCount; r++)
        {
            if (_regions[r].Matched) continue;
            for (int t = 0; t < _tracks.Length; t++)
            {
                if (_tracks[t].Active) continue;
                _tracks[t] = new Track
                {
                    Active = true,
                    CenterBin = _regions[r].CenterBin,
                    LowBin = _regions[r].LowBin,
                    HighBin = _regions[r].HighBin,
                    PeakDb = _regions[r].PeakDb,
                    FloorDb = floorDb,
                    Sightings = 1,
                    Misses = 0,
                };
                _regions[r].Matched = true;
                break;
            }
        }

        // Emit confirmed tracks, strongest SNR first. Selection-sort into the
        // output span; at most MaxTrackedSignals candidates so this is cheap.
        int emitted = 0;
        int capacity = Math.Min(markers.Length, MaxTrackedSignals);
        while (emitted < capacity)
        {
            int strongest = -1;
            double strongestSnr = double.MinValue;
            for (int t = 0; t < _tracks.Length; t++)
            {
                ref readonly var track = ref _tracks[t];
                if (!track.Active || track.Reported || track.Sightings < MinSightingsToReport)
                    continue;
                double snr = track.PeakDb - track.FloorDb;
                if (snr > strongestSnr)
                {
                    strongest = t;
                    strongestSnr = snr;
                }
            }
            if (strongest < 0) break;

            ref var chosen = ref _tracks[strongest];
            chosen.Reported = true;
            double snrDb = chosen.PeakDb - chosen.FloorDb;
            markers[emitted++] = new WidebandSignalMarker(
                CenterHz: chosen.CenterBin * _binHz,
                LowHz: chosen.LowBin * _binHz,
                HighHz: chosen.HighBin * _binHz,
                PeakDb: chosen.PeakDb,
                NoiseFloorDb: chosen.FloorDb,
                SnrDb: snrDb,
                Confidence: Math.Clamp(0.4 + 0.15 * chosen.Sightings, 0.0, 1.0));
        }

        // Clear per-frame flags for the next call.
        for (int t = 0; t < _tracks.Length; t++)
        {
            if (_tracks[t].Active) _tracks[t].Reported = false;
        }
        return emitted;
    }

    private static double Blend(double current, double measurement) =>
        current * (1.0 - TrackBlendAlpha) + measurement * TrackBlendAlpha;

    private struct Region
    {
        public Region(double centerBin, double lowBin, double highBin, double peakDb)
        {
            CenterBin = centerBin;
            LowBin = lowBin;
            HighBin = highBin;
            PeakDb = peakDb;
            Matched = false;
        }

        public double CenterBin;
        public double LowBin;
        public double HighBin;
        public double PeakDb;
        public bool Matched;
    }

    private struct Track
    {
        public bool Active;
        public double CenterBin;
        public double LowBin;
        public double HighBin;
        public double PeakDb;
        public double FloorDb;
        public int Sightings;
        public int Misses;
        public bool Reported;
    }
}

/// <summary>
/// One detected wideband signal. Frequencies are absolute Hz in the
/// wideband ADC baseband (0 Hz .. sampleRate/2); levels are dB against the
/// analyzer's calibrated reference.
/// </summary>
internal readonly record struct WidebandSignalMarker(
    double CenterHz,
    double LowHz,
    double HighHz,
    double PeakDb,
    double NoiseFloorDb,
    double SnrDb,
    double Confidence);
