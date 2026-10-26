// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Dsp;

/// <summary>
/// Linear gain applied only when publishing processed TX PCM to an external
/// transmitter. Operator monitor playback is never scaled. Measured with
/// OfflinePreview USB 1 kHz at −6 dBFS, mic gain 0 dB, and
/// leveler/CFC/compressor/phase-rotator/EQ off. 1.0 means the monitor demod
/// peak already matches TXA IQ magnitude within 1 dB.
/// </summary>
public static class ProcessedTxPublish
{
    public static readonly float CompensationGain = 1f;

    /// <summary>
    /// DSB's monitor peak sat at 0.4613 against the USB input-referred peak of
    /// 0.6742 (−3.30 dB) for the same 1 kHz tone at −6 dBFS. This is that
    /// ratio. USB and every other mode stay at <see cref="CompensationGain"/>.
    /// </summary>
    public static readonly float DsbCompensationGain = 1.461521f;

    /// <summary>
    /// Publish-path gain for one TX mode. USB already matches TXA IQ at 1.
    /// DSB's monitor demod is quieter, so only that mode is scaled, and only
    /// on this publish path.
    /// </summary>
    public static float GainFor(RxMode mode) =>
        mode == RxMode.DSB ? DsbCompensationGain : CompensationGain;

    /// <summary>
    /// ALC makeup for an external processed stream. With SSB leveler off, the
    /// normal 3 dB ceiling leaves quiet speech below useful radio drive. Let
    /// ALC make it up dynamically, while preserving an explicit zero and every
    /// other mode. Never changes the stored operator configuration.
    /// </summary>
    public static TxLevelingConfig LevelingFor(
        RxMode mode,
        bool processedTxStreamRequested,
        TxLevelingConfig configured)
    {
        if (!processedTxStreamRequested
            || configured.LevelerEnabled
            || configured.AlcMaxGainDb <= 0.0
            || mode is not (RxMode.LSB or RxMode.USB or RxMode.DSB))
            return configured;
        return configured with { AlcMaxGainDb = Math.Max(configured.AlcMaxGainDb, 15.0) };
    }

    public static void Apply(Span<float> samples) => Apply(samples, RxMode.USB);

    public static void Apply(Span<float> samples, RxMode mode)
    {
        float gain = GainFor(mode);
        if (gain == 1f) return;
        for (int i = 0; i < samples.Length; i++)
            samples[i] *= gain;
    }
}
