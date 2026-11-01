// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

internal static class ExternalFeedbackSampleRate
{
    internal const int TxHz = 48_000;
}

/// <summary>
/// Keeps a two-tone's envelope from repeating on whole samples at the
/// reference rate. 700/1900 Hz repeats every 40 samples at 48 kHz, so calcc
/// sees only about 20 distinct amplitudes and its curve fits fail. The upper
/// tone moves by the fewest hertz that make the pattern take more than
/// <see cref="ShortestRepeatPeriods"/> periods to repeat.
/// </summary>
internal static class ExternalFeedbackTwoTone
{
    internal const int ShortestRepeatPeriods = 8;
    internal const double MinimumPhaseStepSamples = 0.05;
    internal const int MaximumShiftHz = 25;

    internal static double SpreadUpperTone(double freq1Hz, double freq2Hz)
    {
        if (!double.IsFinite(freq1Hz) || !double.IsFinite(freq2Hz) || freq1Hz == freq2Hz) return freq2Hz;
        double direction = freq2Hz > freq1Hz ? 1 : -1;
        for (int shift = 0; shift <= MaximumShiftHz; shift++)
        {
            foreach (int sign in shift == 0 ? [1] : new[] { 1, -1 })
            {
                double candidate = freq2Hz + direction * sign * shift;
                if (Math.Abs(candidate - freq1Hz) < 1) continue;
                if (Spreads(candidate - freq1Hz)) return candidate;
            }
        }
        return freq2Hz;
    }

    internal static bool Spreads(double separationHz)
    {
        double period = ExternalFeedbackSampleRate.TxHz / Math.Abs(separationHz);
        for (int periods = 1; periods <= ShortestRepeatPeriods; periods++)
        {
            double span = period * periods;
            if (Math.Abs(span - Math.Round(span)) < MinimumPhaseStepSamples) return false;
        }
        return true;
    }
}

