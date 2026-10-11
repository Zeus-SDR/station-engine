// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
namespace Zeus.Server;
/// <summary>Bounded complete-run evidence; it never uses decoded text or callsign priors.</summary>
internal sealed class CwTimingFitness
{
    readonly double[] marks = new double[16], gaps = new double[16], qualities = new double[16], snrs = new double[16];
    int mc, mi, gc, gi;
    public bool SingleMarkCluster
    {
        get
        {
            if (mc == 0)
                return false;
            double lo = double.PositiveInfinity, hi = 0;
            for (int i = 0; i < mc; i++)
            {
                lo = Math.Min(lo, marks[i]);
                hi = Math.Max(hi, marks[i]);
            }

            return hi <= 1.25 * lo;
        }
    }

    public int Marks => mc;

    public bool StrongShortMark
    {
        get
        {
            for (int i = 0; i < mc; i++)
                if (marks[i] > 0 && marks[i] < 120 && qualities[i] >= .93 && snrs[i] >= 15)
                    return true;
            return false;
        }
    }

    public bool MayBeSlow
    {
        get
        {
            for (int i = 0; i < mc; i++)
                if (marks[i] >= 120 && marks[i] < 840 && qualities[i] >= .93 && snrs[i] >= 15)
                    return true;
            return false;
        }
    }

    public bool NoteMark(double ms, double quality, double snr)
    {
        if (quality < .60 || ms < 7.2 || !double.IsFinite(ms))
            return false;
        marks[mi] = ms;
        qualities[mi] = quality;
        snrs[mi] = snr;
        mi = (mi + 1) % 16;
        mc = Math.Min(16, mc + 1);
        return true;
    }

    public void NoteGap(double ms)
    {
        if (ms <= 0 || !double.IsFinite(ms))
            return;
        gaps[gi] = ms;
        gi = (gi + 1) % 16;
        gc = Math.Min(16, gc + 1);
    }

    public double Fit(double dit, double quantum, out bool mixed)
    {
        mixed = false;
        if (mc < 3 || gc < 2)
            return double.PositiveInfinity;
        int dots = 0, dashes = 0, g = 0;
        double me = 0, ge = 0;
        for (int i = 0; i < mc; i++)
        {
            double r = marks[i] / dit;
            if (Math.Abs(r - 1) <= .4)
                dots++;
            if (Math.Abs(r - 3) <= .8)
                dashes++;
            double e = Math.Max(0, Math.Min(Math.Abs(marks[i] - dit), Math.Abs(marks[i] - 3 * dit)) - quantum) / dit;
            me += e * e;
        }

        for (int i = 0; i < gc; i++)
        {
            double r = gaps[i] / dit;
            if (r < .55 || r > 1.6)
                continue;
            double e = Math.Max(0, Math.Abs(gaps[i] - dit) - quantum) / dit;
            ge += e * e;
            g++;
        }

        mixed = dots > 0 && dashes > 0 && g >= 2;
        return g < 2 ? double.PositiveInfinity : Math.Sqrt(.5 * (me / mc + ge / g));
    }

    // Fit m = u*d + b for complete marks (u is 1 or 3), and g = d - b
    // for repeated intra-element gaps. The mark/gap groups have equal weight,
    // so long text runs cannot overwhelm spacing evidence. Signed b describes
    // timing geometry; it does not identify a particular receiver filter.
    // Both mark classes, positive corrected spaces and one-block residual
    // allowance keep a carrier or noise fragments from providing a free clock.
    public bool TryJoint(double quantum, double maxSkew, out double dit, out double skew, out double fit, double minimumDit = 50)
    {
        dit = skew = 0;
        fit = double.PositiveInfinity;
        if (mc < 4 || gc < 2)
            return false;
        double lo = double.PositiveInfinity, hi = 0;
        for (int i = 0; i < mc; i++)
        {
            lo = Math.Min(lo, marks[i]);
            hi = Math.Max(hi, marks[i]);
        }

        int dots = 0, dashes = 0;
        for (int step = 0; step < 8; step++)
        {
            double a = 0, b = 0;
            dots = dashes = 0;
            for (int i = 0; i < mc; i++)
            {
                if (Math.Abs(marks[i] - lo) <= Math.Abs(marks[i] - hi))
                {
                    a += marks[i];
                    dots++;
                }
                else
                {
                    b += marks[i];
                    dashes++;
                }
            }

            if (dots < 2 || dashes < 1)
                return false;
            lo = a / dots;
            hi = b / dashes;
        }

        double unitMean = 0, unitSquareMean = 0, markMean = 0, unitMarkMean = 0, gapMean = 0;
        int goodGaps = 0;
        for (int i = 0; i < mc; i++)
        {
            double unit = Math.Abs(marks[i] - lo) <= Math.Abs(marks[i] - hi) ? 1 : 3;
            unitMean += unit;
            unitSquareMean += unit * unit;
            markMean += marks[i];
            unitMarkMean += unit * marks[i];
        }

        for (int i = 0; i < gc; i++)
            if (gaps[i] >= Math.Max(2 * quantum, lo - 2 * maxSkew) && gaps[i] <= lo + 2 * maxSkew)
            {
                gapMean += gaps[i];
                goodGaps++;
            }

        if (goodGaps < 2)
            return false;
        gapMean /= goodGaps;
        unitMean /= mc;
        unitSquareMean /= mc;
        markMean /= mc;
        unitMarkMean /= mc;
        double aa = unitSquareMean + 1, ab = unitMean - 1, bb = 2, yd = unitMarkMean + gapMean, ys = markMean - gapMean, det = aa * bb - ab * ab;
        dit = (yd * bb - ys * ab) / det;
        skew = (aa * ys - ab * yd) / det;
        if (dit < minimumDit || dit > 240 || Math.Abs(skew) > maxSkew || dit - skew < 2 * quantum)
            return false;
        double me = 0, ge = 0;
        for (int i = 0; i < mc; i++)
        {
            double error = Math.Max(0, Math.Min(Math.Abs(marks[i] - (dit + skew)), Math.Abs(marks[i] - (3 * dit + skew))) - quantum) / dit;
            me += error * error;
        }

        for (int i = 0; i < gc; i++)
            if (gaps[i] >= Math.Max(2 * quantum, lo - 2 * maxSkew) && gaps[i] <= lo + 2 * maxSkew)
            {
                double error = Math.Max(0, Math.Abs(gaps[i] - (dit - skew)) - quantum) / dit;
                ge += error * error;
            }

        if (goodGaps < 2)
            return false;
        fit = Math.Sqrt(.5 * (me / mc + ge / goodGaps));
        return fit < .2;
    }

    public void Reset()
    {
        mc = mi = gc = gi = 0;
    }
}
