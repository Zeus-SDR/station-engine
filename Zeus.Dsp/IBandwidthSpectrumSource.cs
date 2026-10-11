// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Dsp;

/// <summary>Full FFT bins for the narrow live display, independent of the main display pixel grid.</summary>
public interface IBandwidthSpectrumSource
{
    bool TryGetBandwidthSpectrum(int channelId, bool tx, Span<float> bins,
        long demandUntilTickMs, out RxSnrSpectrumInfo info);
}
