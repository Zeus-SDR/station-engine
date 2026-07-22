// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Dsp.Wdsp;

/// <summary>
/// Processes one realtime pre-WDSP TX-audio block.
/// </summary>
/// <param name="input">Mic-monaural float32, length = <paramref name="frames"/>.</param>
/// <param name="output">Caller-owned buffer the plugin writes; length = <paramref name="frames"/>.</param>
/// <param name="frames">Block size in frames. Currently matches the TXA input block.</param>
/// <param name="channels">Always 1 in the current TX path.</param>
/// <param name="sampleRate">Always 48000 in the current TX path.</param>
public delegate void TxAudioPluginHandler(
    ReadOnlySpan<float> input,
    Span<float> output,
    int frames,
    int channels,
    int sampleRate);
