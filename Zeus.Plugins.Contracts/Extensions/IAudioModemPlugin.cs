// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Plugins.Contracts.Extensions;

/// <summary>
/// Optional extension for a mode-specific audio modem hosted by a backend
/// plugin. The host owns mode selection and calls this seam from the existing
/// RX/TX audio insertion points.
///
/// <para>All members may be invoked concurrently from multiple host threads:
/// the realtime RX/TX audio tick, the bounded TX tail-drain worker, REST/control
/// threads, and plugin lifecycle activation/deactivation. Implementations must
/// serialize their own internal state and treat every call as re-entrant.</para>
///
/// <para><see cref="ProcessRx"/>, <see cref="ProcessTx"/>, and
/// <see cref="DrainTx"/> run on audio-sensitive paths. They must not allocate,
/// block, perform IO, or throw.</para>
/// </summary>
public interface IAudioModemPlugin
{
    /// <summary>True while the modem is engaged and should transform audio. Must be lock-free and non-throwing.</summary>
    bool Active { get; }

    /// <summary>True when the modem's required native/runtime support is loadable. Must be lock-free and non-throwing.</summary>
    bool NativeAvailable { get; }

    /// <summary>
    /// Reconcile plugin engagement from the host-owned RX mode byte. The SDK
    /// deliberately avoids exposing the host's RxMode enum. May race with audio
    /// callbacks and lifecycle shutdown; must serialize internal modem state.
    /// </summary>
    void SyncMode(byte rxModeByte);

    /// <summary>
    /// RX post-demod insert. Transform received modem audio to speech in place.
    /// Audio-sensitive: no allocation, blocking, IO, or exceptions.
    /// </summary>
    void ProcessRx(Span<float> block48k);

    /// <summary>
    /// TX mic insert. Transform speech to modem audio in place.
    /// Audio-sensitive: no allocation, blocking, IO, or exceptions.
    /// </summary>
    void ProcessTx(Span<float> block48k);

    /// <summary>
    /// Reset receiver-side decode state across MOX edges. May race with audio
    /// callbacks and lifecycle shutdown; must serialize internal modem state.
    /// </summary>
    void FlushRx();

    /// <summary>
    /// Drop pending TX modem state so the next over starts clean. May race with
    /// control callbacks and lifecycle shutdown; the host avoids calling it during
    /// an active tail drain, but implementations must still tolerate concurrency.
    /// </summary>
    void FlushTx();

    /// <summary>
    /// Finish the current TX frame and return queued 48 kHz samples. Runs on the
    /// bounded TX tail-drain worker and may race with control/lifecycle calls.
    /// </summary>
    int FinishTx();

    /// <summary>
    /// Drain queued TX modem samples into <paramref name="block48k"/>.
    /// Audio-sensitive: no allocation, blocking, IO, or exceptions.
    /// </summary>
    int DrainTx(Span<float> block48k);
}
