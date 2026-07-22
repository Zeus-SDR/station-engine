// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Receives demodulated audio frames produced by DspPipelineService.Tick.
/// Both the regular RX path and the TX-monitor path publish through this
/// seam, so an implementation handles whatever audio reaches the operator's
/// speakers.
///
/// Default registration is <see cref="WebSocketAudioSink"/>, which fans out
/// to connected WS clients via <c>StreamingHub.Broadcast(in AudioFrame)</c>.
/// Desktop mode swaps in a native miniaudio sink in place of the WebSocket
/// fan-out (Phase 2b).
///
/// Publish runs on the DSP tick thread and must not block or allocate
/// beyond what the existing Broadcast already does — see
/// <c>StreamingHub.Broadcast(in AudioFrame)</c> for the cost model.
/// </summary>
public interface IRxAudioSink
{
    void Publish(in AudioFrame frame);

    /// <summary>
    /// Publish a frame that is EXEMPT from the operator's RX master mute
    /// (<see cref="RxAudioMuteState"/>). Used only for local monitor audio the
    /// operator explicitly asked to hear — Recorder playback and TX Monitor
    /// preview — so a muted radio still lets them monitor their own audio on the
    /// PC output. The DEFAULT is a no-op so exempt audio never leaks to the
    /// WebSocket fan-out or the onboard-speaker sinks; only
    /// <see cref="NativeAudioSink"/> (the desktop PC output the operator hears)
    /// overrides it. Real RX audio never travels this lane — the pipeline keeps it
    /// on <see cref="Publish"/>, which stays muted. Runs on the DSP tick thread
    /// under the same cost model as <see cref="Publish"/>.
    /// </summary>
    void PublishExempt(in AudioFrame frame) { }
}
