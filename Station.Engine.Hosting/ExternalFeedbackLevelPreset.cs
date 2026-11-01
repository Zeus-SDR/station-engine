// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>
/// Predicts calcc's feedback level from the first strong pairs of an arm,
/// before calcc has fitted anything. calcc reports 256 × hw_scale / rx_scale:
/// the feedback envelope at the HW peak over the HW peak, which is 256 × the
/// feedback/reference gain at the top. The least-squares gain over samples
/// above <see cref="StrongShareOfHwPeak"/> of the HW peak matched calcc's own
/// level within a few counts on the 2026-10-31 bench captures (157.7 against
/// 156, 159 against 154), and read 5,790 on a cold start whose first fit
/// reported 7,703.
/// </summary>
internal sealed class ExternalFeedbackLevelPreset
{
    internal const double StrongShareOfHwPeak = 0.6;
    internal const int StrongSamples = 1_000;
    // Below this the feedback is missing, not quiet; the servo's stall
    // detection owns that case.
    private const double NoFeedbackLevel = 1;

    private double _crossSum;
    private double _referenceSum;
    private int _count;

    /// <summary>True once the level has been predicted; one prediction per arm.</summary>
    internal bool Done { get; private set; }

    /// <summary>
    /// Adds pairs; returns the predicted level once, when enough strong pairs
    /// are in, and null before and after. Held pairs (zero reference) are skipped.
    /// </summary>
    internal double? Add(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
        ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ, double hwPeak)
    {
        if (Done || hwPeak <= 0) return null;
        double floor = StrongShareOfHwPeak * hwPeak;
        for (int i = 0; i < txI.Length; i++)
        {
            double tx = Math.Sqrt(txI[i] * (double)txI[i] + txQ[i] * (double)txQ[i]);
            if (tx < floor) continue;
            double rx = Math.Sqrt(rxI[i] * (double)rxI[i] + rxQ[i] * (double)rxQ[i]);
            _crossSum += rx * tx;
            _referenceSum += tx * tx;
            _count++;
        }
        if (_count < StrongSamples) return null;
        Done = true;
        return 256.0 * _crossSum / _referenceSum;
    }

    /// <summary>
    /// The attenuation that moves <paramref name="level"/> to the auto-attenuate
    /// target, or null when it is already inside the servo's window, when no
    /// feedback is arriving, or when the clamped value would not change.
    /// </summary>
    internal static int? AttenuationFor(double level, int currentDb, int maximumDb)
    {
        if (!double.IsFinite(level) || level < NoFeedbackLevel) return null;
        if (level >= PsAutoAttenuateService.FeedbackLowThreshold &&
            level <= PsAutoAttenuateService.FeedbackHighThreshold)
            return null;
        int step = (int)Math.Round(20 * Math.Log10(level / PsAutoAttenuateService.IdealFeedback),
            MidpointRounding.AwayFromZero);
        int db = Math.Clamp(currentDb + step, 0, maximumDb);
        return db == currentDb ? null : db;
    }
}
