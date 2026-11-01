// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Dsp;

/// <summary>Borrowed completed digital TX output, including applied correction. The handler must not retain the span.</summary>
public delegate void TxWaveformHandler(int sampleRateHz, ReadOnlySpan<float> interleavedIq);

public interface ITxWaveformSource
{
    void SetTxWaveformTap(TxWaveformHandler? handler, long demandUntilTickMs);

    /// <summary>
    /// Adds an independent completed-output observer. Disposing the returned
    /// lease removes only this observer; the display/capture tap remains intact.
    /// </summary>
    IDisposable SubscribeTxWaveform(TxWaveformHandler handler);
}
