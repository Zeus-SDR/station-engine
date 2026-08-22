// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Default <see cref="IRxAudioSink"/>: fans demodulated audio out to
/// connected WebSocket clients via <see cref="StreamingHub"/>. Bit-for-bit
/// equivalent of the pre-seam direct hub call — the wrapper exists so
/// desktop mode can register a native sink in its place without touching
/// the DSP tick.
///
/// Mute / exempt split: while the operator's RX master mute
/// (<see cref="RxAudioMuteState"/>) is on, band frames are dropped here so
/// WebSocket clients hear nothing (the browser keeps its decoder running and
/// simply receives no band audio). The exempt lane always broadcasts: it
/// carries only operator-requested local monitor playback (Recorder playback,
/// TX Monitor preview), which must stay audible while muted.
/// </summary>
internal sealed class WebSocketAudioSink : IRxAudioSink
{
    private readonly StreamingHub _hub;
    private readonly RxAudioMuteState _muteState;

    public WebSocketAudioSink(StreamingHub hub, RxAudioMuteState muteState)
    {
        _hub = hub;
        _muteState = muteState;
    }

    public void Publish(in AudioFrame frame)
    {
        _hub.RecordRxAudioFrame();
        if (_muteState.IsMuted) return;
        _hub.Broadcast(in frame);
    }

    public void PublishExempt(in AudioFrame frame) => _hub.Broadcast(in frame);
}
