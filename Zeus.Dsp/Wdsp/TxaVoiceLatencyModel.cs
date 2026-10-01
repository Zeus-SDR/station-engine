// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Dsp.Wdsp;

/// <summary>
/// Upper bound on how long a syllable fed into WDSP TXA takes to reach the IQ
/// output, used by the voice unkey tail to decide how much silence it must
/// clock through TXA before quiet output really means "speech has left".
/// The bound is checked against real libwdsp in
/// <c>TxaVoiceLatencyModelTests</c> for both TXA profiles (P1 48/48/48 and
/// P2 48/96/192) and every operator-selectable stage that adds delay.
/// </summary>
public static class TxaVoiceLatencyModel
{
    /// <summary>TXA input/DSP buffer size for both Zeus TXA profiles.</summary>
    public const int TxaDspSize = 512;

    // Fixed TXA lag outside the bandpass FIRs (iobuff handoff, leveler/ALC,
    // input/output resamplers, P2 CFIR). Measured 20-31 ms on libwdsp.
    internal const int PipelineAllowanceMs = 35;

    // TXA.c create_cfcomp: fixed 16384-point FFT with 4x overlap. A sample
    // leaves the CFC (fsize - dsp_size) samples after it entered.
    internal const int CfcFftSize = 16384;

    /// <summary>
    /// Milliseconds from a sample entering TXA until its output can first
    /// appear. Linear-phase TX bandpass stages each add (nc-1)/2 samples of
    /// group delay at the DSP rate. bp0 always runs; bp1 runs with the
    /// compressor and bp2 with CESSB (TXA.c TXASetupBPFilters). Minimum-phase
    /// stages are allowed two DSP blocks. The CFC adds its FFT frame.
    /// </summary>
    public static int EstimateOnsetMs(
        FilterPhaseMode phase,
        BandpassWindow window,
        int dspRateHz,
        bool compressorEnabled,
        bool cessbEnabled,
        bool cfcEnabled)
    {
        if (dspRateHz <= 0) dspRateHz = 48_000;
        int filterMs;
        if (phase == FilterPhaseMode.Linear)
        {
            int stages = 1 + (compressorEnabled ? 1 : 0)
                + (compressorEnabled && cessbEnabled ? 1 : 0);
            int nc = WdspDspEngine.ResolveBandpassNc(window, TxaDspSize);
            filterMs = stages * (int)Math.Ceiling((nc - 1) * 500.0 / dspRateHz);
        }
        else
        {
            filterMs = (int)Math.Ceiling(2.0 * TxaDspSize * 1000.0 / dspRateHz);
        }
        int cfcMs = cfcEnabled
            ? (int)Math.Ceiling((CfcFftSize - TxaDspSize) * 1000.0 / dspRateHz)
            : 0;
        return filterMs + cfcMs + PipelineAllowanceMs;
    }
}
