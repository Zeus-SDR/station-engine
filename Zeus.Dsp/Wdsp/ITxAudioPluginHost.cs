// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Dsp.Wdsp;

/// <summary>
/// A DSP engine that can host the realtime TX Audio Suite insert point.
/// </summary>
public interface ITxAudioPluginHost
{
    void SetTxAudioPluginHandler(TxAudioPluginHandler? handler);
    bool HasTxAudioPluginHandler { get; }
}
