// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

// External receiver audio (docs/designs/external-receiver-audio-port.md).
// A product-operated radio's finished receive audio enters here, and only
// here, while the engine has no radio of its own. It reaches only the
// per-receiver ReceiverAudioAvailable event (CW decoder, WSPR decoder, TCI)
// and the product capture leases on both RX taps: external audio has no
// local DSP or plugin chain, so the processed and data taps get the same
// block. It never raises
// RxAudioAvailable and never reaches the speaker/browser broadcast, the
// recorder's monitor injection, plugins, VSTs or WDSP. Nothing in the tick
// calls this method.
public partial class DspPipelineService
{
    private readonly ExternalReceiverPort? _externalReceiverPort;
    private readonly object _externalReceiverAudioGate = new();
    private readonly float[] _externalReceiverAudioBuf =
        new float[ExternalReceiverPort.MaxSamplesPerBlock];

    /// <summary>
    /// Publishes one block of external receiver audio (48 kHz mono float32).
    /// Does nothing unless the external receiver port is active for
    /// <paramref name="receiver"/> and its context is not transmitting.
    /// </summary>
    public void PublishExternalReceiverAudio(int receiver, ReadOnlySpan<float> samples)
    {
        var port = _externalReceiverPort;
        if (port is null
            || samples.IsEmpty
            || samples.Length > _externalReceiverAudioBuf.Length
            || !port.TryGetContext(receiver, out var context)
            || context.Transmitting)
            return;

        lock (_externalReceiverAudioGate)
        {
            samples.CopyTo(_externalReceiverAudioBuf);
            var block = new ReadOnlyMemory<float>(_externalReceiverAudioBuf, 0, samples.Length);
            ReceiverAudioAvailable?.Invoke(receiver, AudioOutputRateHz, block);
            _productPluginAudio?.PublishRxAudio(receiver, AudioOutputRateHz, block.Span);
            _productPluginAudio?.PublishRxDataAudio(receiver, AudioOutputRateHz, block.Span);
        }
    }
}
