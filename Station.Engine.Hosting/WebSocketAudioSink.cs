// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Default <see cref="IRxAudioSink"/>: fans demodulated audio out to
/// connected WebSocket clients via <see cref="StreamingHub"/>. Bit-for-bit
/// equivalent of the pre-seam direct hub call — the wrapper exists so
/// desktop mode can register a native sink in its place without touching
/// the DSP tick.
/// </summary>
internal sealed class WebSocketAudioSink : IRxAudioSink
{
    private readonly StreamingHub _hub;

    public WebSocketAudioSink(StreamingHub hub) => _hub = hub;

    public void Publish(in AudioFrame frame) => _hub.Broadcast(in frame);
}
