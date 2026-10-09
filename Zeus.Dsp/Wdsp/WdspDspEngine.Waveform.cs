// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Dsp.Wdsp;

public sealed partial class WdspDspEngine : ITxWaveformSource
{
    private TxWaveformHandler? _waveformTap;
    private long _waveformDemandUntil;

    public void SetTxWaveformTap(TxWaveformHandler? handler, long demandUntilTickMs)
    {
        Volatile.Write(ref _waveformTap, handler);
        Volatile.Write(ref _waveformDemandUntil, demandUntilTickMs);
    }
}
