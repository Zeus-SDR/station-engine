// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Dit length from the mark stream, and gap lengths from the space stream.
///
/// Marks are split with 2-means. The dit/dah boundary is the geometric mean
/// of the two centres, which stays between them when a heavy fist stretches
/// the dahs. Letter and word boundaries default to 2× and 5× the dit (the
/// gaps between a 1-dit element space, a 3-dit letter space, and a 7-dit
/// word space) and nudge toward the same geometric split of the space
/// clusters. A mark longer than about ten dits is a carrier and is ignored
/// for the estimate. Before lock, that ceiling is ten dits at the slowest
/// supported speed: the cold 20 WPM seed would call a 5 WPM dah a carrier.
/// A single cluster of slow dits sits in the same band as a run of dahs
/// near the cold seed. Intra-character gaps about as long as the marks are
/// dit evidence, so that cluster is not read as dahs. Two marks and a word
/// gap never reach 2-means; the gap-to-mark ratio classifies them instead.
/// During fast acquisition the training floor is a 50 WPM glitch. A 50 WPM
/// dit with the edges gone is about 16 ms, under the cold 18 ms floor, and
/// dropping it leaves only the dahs: the fist never locks, and a following
/// 5 WPM run is read as separate dahs.
/// </summary>
internal sealed class MorseTimingEstimator
{
    private const int WindowSize = 48;
    // 50 WPM and 5 WPM. Acquisition uses these while the cold 20 WPM seed
    // is still the dit.
    internal const double MinDitMs = 24.0;
    internal const double MaxDitMs = 240.0;
    internal const double GlitchFraction = 0.3;
    internal const double FastestGlitchMs = GlitchFraction * MinDitMs;
    internal const double CarrierDits = 10.0;
    // A 50 WPM dah is 3 × 24 ms. A longer mark is a slower fist, and the
    // acquisition glitch can stand down.
    internal const double FastMarkMs = 4.0 * MinDitMs;
    // A 5 WPM dah is 3 × 240 ms. The pre-lock carrier ceiling is ten of
    // those dits, so a steady tone of one or two seconds is still under it.
    // Anything past a slow dah, with no second element a few dits away,
    // is that tone and is not a letter.
    internal const double MaxPreLockMarkMs = 3.5 * MaxDitMs;
    private const double ClusterRatio = 1.45;
    // Element gaps of one fist. A fade stretches a single gap well past this
    // and must not drag the letter boundary down onto it.
    private const double SpaceClusterSpread = 1.25;

    private readonly double[] _marks = new double[WindowSize];
    private readonly double[] _spaces = new double[WindowSize];
    private readonly double[] _scratch = new double[WindowSize];
    private int _markCount;
    private int _markCursor;
    private int _spaceCount;
    private int _spaceCursor;
    private int _sinceMarkEstimate;
    private int _sinceSpaceEstimate;
    private double _ditMs;
    private double _dahCenterMs;
    private double _splitMs;
    private double _letterGapMs;
    private double _wordGapMs;
    private bool _twoClusters;
    private bool _fastAcquisition = true;
    private bool _reacquiring;

    public MorseTimingEstimator(double initialWpm = 20)
    {
        _ditMs = Math.Clamp(1200.0 / initialWpm, MinDitMs, MaxDitMs);
        ApplyDit(_ditMs, dahCenter: 3.0 * _ditMs, splitFromCenters: true);
    }

    public double DitMs => _ditMs;
    public double DahCenterMs => _dahCenterMs;
    public double DahThresholdMs => _splitMs;
    public double LetterGapThresholdMs => _letterGapMs;
    public double WordGapThresholdMs => _wordGapMs;
    public double ElementGapThresholdMs => _ditMs;

    /// <summary>
    /// Ten dits. After lock, ten of the learned dit. Before lock, ten dits
    /// at <see cref="MaxDitMs"/> so a real 5 WPM dah (720 ms) is not dropped
    /// against the cold 60 ms seed.
    /// </summary>
    public double CarrierMs => CarrierDits * (IsLocked ? _ditMs : MaxDitMs);

    public double GlitchMs => GlitchFraction * _ditMs;

    /// <summary>
    /// Shortest mark or gap that trains the estimate. The decoder's own glitch
    /// cap stays on <see cref="GlitchMs"/>: lowering that would split a
    /// two-block dropout. Only the training floor drops, and only while the
    /// fist can still be 50 WPM.
    /// </summary>
    private double TrainingSpikeMs => FastAcquisition ? FastestGlitchMs : GlitchMs;

    public double Wpm => 1200.0 / _ditMs;
    public bool IsLocked { get; private set; }

    /// <summary>
    /// True until a mark longer than a 50 WPM dah shows this is a slower
    /// fist, or the dit locks. The smoother and the glitch stay short
    /// enough for a 24 ms element while this is set.
    /// </summary>
    public bool FastAcquisition => _fastAcquisition && !IsLocked;

    internal bool Reacquiring => _reacquiring && !IsLocked;

    public bool IsDah(double durationMs) =>
        durationMs >= _splitMs && durationMs < CarrierMs;

    public bool IsCarrier(double durationMs) => durationMs >= CarrierMs;

    public void ObserveElement(double durationMs)
    {
        if (!double.IsFinite(durationMs) || durationMs <= 0) return;
        // A single long carrier must not become the dah cluster, and a
        // spike must not become the dit. Both are rejected before the window.
        // The cold floor is 18 ms, and the adjusted 50 WPM dit is shorter.
        if (durationMs < TrainingSpikeMs || durationMs > CarrierMs) return;
        if (durationMs >= FastMarkMs && !_reacquiring)
            _fastAcquisition = false;

        _marks[_markCursor] = durationMs;
        _markCursor = (_markCursor + 1) % WindowSize;
        if (_markCount < WindowSize) _markCount++;

        bool early = _markCount <= 8;
        if (early || ++_sinceMarkEstimate >= 4)
        {
            _sinceMarkEstimate = 0;
            ReestimateMarks();
        }
    }

    public void ObserveSpace(double durationMs)
    {
        if (!double.IsFinite(durationMs) || durationMs <= 0) return;
        if (durationMs < TrainingSpikeMs || durationMs > 15.0 * _ditMs) return;

        _spaces[_spaceCursor] = durationMs;
        _spaceCursor = (_spaceCursor + 1) % WindowSize;
        if (_spaceCount < WindowSize) _spaceCount++;
        if (++_sinceSpaceEstimate >= 4)
        {
            _sinceSpaceEstimate = 0;
            ReestimateSpaces();
        }
    }

    public void Reset(double initialWpm = 20)
    {
        Array.Clear(_marks);
        Array.Clear(_spaces);
        _markCount = 0;
        _markCursor = 0;
        _spaceCount = 0;
        _spaceCursor = 0;
        _sinceMarkEstimate = 0;
        _sinceSpaceEstimate = 0;
        _twoClusters = false;
        _fastAcquisition = true;
        _reacquiring = false;
        IsLocked = false;
        _ditMs = Math.Clamp(1200.0 / initialWpm, MinDitMs, MaxDitMs);
        ApplyDit(_ditMs, dahCenter: 3.0 * _ditMs, splitFromCenters: true);
    }

    /// <summary>
    /// Drop a locked fast fist so the next marks are a new acquisition.
    /// The dit already learned stays put: a 240 ms element is outside the
    /// centre/3 band of a 24 ms dit, and clearing it back to the 20 WPM
    /// seed would call that element a dah again.
    /// </summary>
    internal void BeginReacquisition(bool allowFaster = false)
    {
        Array.Clear(_marks);
        Array.Clear(_spaces);
        _markCount = 0;
        _markCursor = 0;
        _spaceCount = 0;
        _spaceCursor = 0;
        _sinceMarkEstimate = 0;
        _sinceSpaceEstimate = 0;
        _twoClusters = false;
        _fastAcquisition = allowFaster;
        _reacquiring = allowFaster;
        IsLocked = false;
    }

    /// <summary>
    /// Two marks never reach 2-means. The element gap is about one dit, so
    /// a gap near the shorter mark makes that mark the dit and a mark a few
    /// times longer the dah. Equal marks with a gap of the same length are
    /// dits; a gap near a third of them is a pair of dahs.
    /// </summary>
    internal void SeedFromGapRatio(double firstMarkMs, double gapMs, double secondMarkMs)
    {
        if (IsLocked) return;
        if (!(firstMarkMs > 0) || !(secondMarkMs > 0) || !(gapMs > 0)) return;

        double shortMark = Math.Min(firstMarkMs, secondMarkMs);
        double longMark = Math.Max(firstMarkMs, secondMarkMs);
        if (longMark >= shortMark * 1.45)
        {
            double gapRatio = gapMs / shortMark;
            if (gapRatio < 0.4 || gapRatio > 2.2) return;
            ApplyDit(shortMark, longMark, splitFromCenters: true);
            return;
        }

        double center = (firstMarkMs + secondMarkMs) * 0.5;
        double ratio = gapMs / center;
        if (ratio >= 0.5 && ratio <= 1.8)
            ApplyDit(center, 3.0 * center, splitFromCenters: true);
        else if (ratio >= 2.0 && ratio <= 4.5 && center <= 1.8 * _ditMs)
            // Equal short marks separated by a letter gap are dits. A pair
            // of dahs with a word gap must retain the previous dit instead.
            ApplyDit(center, 3.0 * center, splitFromCenters: true);
        else if (ratio >= 0.2 && ratio <= 0.45
                 && center >= 2.0 * _ditMs && center <= 5.5 * _ditMs)
            ApplyDit(center / 3.0, center, splitFromCenters: true);
    }

    private void ReestimateMarks()
    {
        if (_markCount < 3) return;
        CopyWindow(_marks, _markCount, _scratch);
        int n = _markCount;
        Array.Sort(_scratch, 0, n);

        double low = _scratch[n / 4];
        double high = _scratch[(n * 3) / 4];
        bool separated = high >= low * ClusterRatio;
        if (separated)
        {
            for (int iter = 0; iter < 8; iter++)
            {
                double boundary = Math.Sqrt(Math.Max(low, 1e-9) * Math.Max(high, 1e-9));
                double sumLow = 0, sumHigh = 0;
                int nLow = 0, nHigh = 0;
                for (int i = 0; i < n; i++)
                {
                    double value = _scratch[i];
                    if (value < boundary)
                    {
                        sumLow += value;
                        nLow++;
                    }
                    else
                    {
                        sumHigh += value;
                        nHigh++;
                    }
                }

                if (nLow == 0 || nHigh == 0)
                {
                    separated = false;
                    break;
                }

                low = sumLow / nLow;
                high = sumHigh / nHigh;
                if (low > high)
                    (low, high) = (high, low);
            }
        }

        _twoClusters = separated && high >= low * ClusterRatio;
        double dit;
        double dah;
        double center = _scratch[n / 2];
        if (_twoClusters)
        {
            dit = low;
            dah = high;
        }
        else if (!IsLocked && GapsMatchMark(center))
        {
            // Cold 8 WPM dits are 150 ms, inside the 2–5.5× band of the
            // 20 WPM seed, so centre/3 would call them dahs. The gaps
            // inside the character are about one dit, the same length as
            // the marks. A dah run's element gap is about a third of the
            // mark; that case keeps the centre/3 reading below.
            dit = center;
            dah = 3.0 * center;
        }
        else if (center >= 2.0 * _ditMs && center <= 5.5 * _ditMs)
        {
            // One cluster of dahs (a run of T) sits near 3× the running dit.
            // Treat that centre as the dah and keep the dit at one third,
            // so the run is not re-read as a string of E.
            dah = center;
            dit = center / 3.0;
        }
        else
        {
            dit = center;
            dah = 3.0 * center;
        }

        ApplyDit(dit, dah, splitFromCenters: true);
        ReestimateSpaces();
        UpdateLock();
    }

    private void ReestimateSpaces()
    {
        _letterGapMs = 2.0 * _ditMs;
        _wordGapMs = 5.0 * _ditMs;
        if (_spaceCount < 4) return;

        CopyWindow(_spaces, _spaceCount, _scratch);
        double sumElement = 0, sumLetter = 0, sumWord = 0;
        double elementMin = double.MaxValue, letterMin = double.MaxValue, wordMin = double.MaxValue;
        double elementMax = 0, letterMax = 0, wordMax = 0;
        int nElement = 0, nLetter = 0, nWord = 0;
        for (int i = 0; i < _spaceCount; i++)
        {
            double gap = _scratch[i];
            double units = gap / _ditMs;
            if (units < 2.0)
                TakeGap(gap, ref sumElement, ref nElement, ref elementMin, ref elementMax);
            else if (units < 4.5)
                TakeGap(gap, ref sumLetter, ref nLetter, ref letterMin, ref letterMax);
            else if (units < 12.0)
                TakeGap(gap, ref sumWord, ref nWord, ref wordMin, ref wordMax);
        }

        // Geometric boundary of the element-space and letter-space clusters.
        // For a 1-dit and a 3-dit cluster that lands near 1.7 dit; the clamp
        // keeps it on the 2× side of a sloppy fist.
        if (Tight(nElement, elementMin, elementMax) && Tight(nLetter, letterMin, letterMax))
        {
            double boundary = Math.Sqrt((sumElement / nElement) * (sumLetter / nLetter));
            _letterGapMs = Math.Clamp(boundary, 1.6 * _ditMs, 2.6 * _ditMs);
        }

        if (Tight(nLetter, letterMin, letterMax) && Tight(nWord, wordMin, wordMax))
        {
            double boundary = Math.Sqrt((sumLetter / nLetter) * (sumWord / nWord));
            _wordGapMs = Math.Clamp(boundary, 4.0 * _ditMs, 6.5 * _ditMs);
        }
    }

    // Intra-character gaps of a dit run match the mark. A gap near a third
    // of the mark is the element space inside a dah, which is dah evidence.
    private bool GapsMatchMark(double markCenter)
    {
        if (!(markCenter > 0) || _spaceCount < 1) return false;
        int near = 0;
        int third = 0;
        int n = _spaceCount < WindowSize ? _spaceCount : WindowSize;
        for (int i = 0; i < n; i++)
        {
            double gap = _spaces[i];
            if (!(gap > 0)) continue;
            double ratio = gap / markCenter;
            if (ratio >= 0.55 && ratio <= 1.7) near++;
            else if (ratio >= 0.2 && ratio <= 0.45) third++;
        }

        return near > 0 && third == 0;
    }

    private static void TakeGap(double gap, ref double sum, ref int count, ref double min, ref double max)
    {
        sum += gap;
        count++;
        if (gap < min) min = gap;
        if (gap > max) max = gap;
    }

    private static bool Tight(int count, double min, double max) =>
        count >= 2 && min > 0 && max <= min * SpaceClusterSpread;

    private void ApplyDit(double dit, double dahCenter, bool splitFromCenters)
    {
        _ditMs = Math.Clamp(dit, MinDitMs, MaxDitMs);
        _dahCenterMs = Math.Max(dahCenter, _ditMs);
        _splitMs = splitFromCenters
            ? Math.Sqrt(_ditMs * Math.Max(_dahCenterMs, _ditMs))
            : 1.732 * _ditMs;
        // Keep the boundary strictly between the centres when they differ.
        if (_dahCenterMs > _ditMs * 1.2)
            _splitMs = Math.Sqrt(_ditMs * _dahCenterMs);
        _letterGapMs = 2.0 * _ditMs;
        _wordGapMs = 5.0 * _ditMs;
    }

    private void UpdateLock()
    {
        if (IsLocked) return;
        if (_markCount >= 6 && _twoClusters && _dahCenterMs >= _ditMs * 1.7)
            IsLocked = true;
        else if (_markCount >= 8)
            IsLocked = true;
    }

    private static void CopyWindow(double[] source, int count, double[] dest)
    {
        // Unfilled slots sit at the front of the ring. Once it is full every
        // slot is a sample; 2-means does not care about order.
        Array.Copy(source, dest, count < WindowSize ? count : WindowSize);
    }
}
