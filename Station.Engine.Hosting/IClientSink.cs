// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>
/// A consumer of <see cref="StreamingHub"/>'s broadcast fan-out. Implemented by
/// the WebSocket <c>ClientSession</c> and by the remote-access WebRTC sink
/// (<c>RemoteFrameSink</c>), so a remote session rides the exact same fan-out as
/// <c>/ws</c> clients — the Broadcast methods are unchanged, and when no remote
/// sink is attached the <c>/ws</c> path behaves identically to before.
///
/// Only the two members the broadcast loops touch are abstracted.
/// </summary>
public interface IClientSink
{
    /// <summary>Whether this consumer wants display frames (used to skip the heavy serialize).</summary>
    bool WantsDisplay { get; }

    /// <summary>Enqueue a serialized frame. Must be non-blocking (callers run on the DSP thread).</summary>
    bool TryEnqueue(byte[] payload);

    /// <summary>
    /// Whether this consumer should receive CW text for <paramref name="receiver"/>.
    /// A sink that has not named a subscription still receives RX1, and never
    /// another receiver's copy.
    /// </summary>
    bool AcceptsCwDecodedText(int receiver) => receiver == 0;
}
