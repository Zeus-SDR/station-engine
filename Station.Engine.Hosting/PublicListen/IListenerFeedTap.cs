// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server.PublicListen;

/// <summary>
/// Producer-side hook the <see cref="StreamingHub"/> and the DSP pipeline call
/// to hand RX1 display rows and RX1 audio to Public Listening (ADR-0010).
/// Implemented by <see cref="PublicListenFeed"/>. Every call runs on a
/// producer (DSP / sidecar poll) thread, so implementations quantize or copy
/// and return — they never block and never throw into the operator path.
///
/// This is deliberately NOT an <c>IClientSink</c>: nothing reached through it
/// can enter <c>StreamingHub._clients</c>, see operator frames other than the
/// RX1 display row it is handed, or affect <c>LastClientDisconnected</c>.
/// </summary>
internal interface IListenerFeedTap
{
    /// <summary>True while a listener wants the operator-view spectrum. One volatile read.</summary>
    bool WantsOperatorView { get; }

    /// <summary>True while a listener wants listen-along RX1 audio. One volatile read.</summary>
    bool WantsListenAlong { get; }

    /// <summary>An RX1 (RxId 0) display frame about to be broadcast to operator clients.</summary>
    void OfferDisplayFrame(in DisplayFrame frame);

    /// <summary>A serialized RX1 DisplayFrame published by a product adapter (Protocol 3 sidecar).</summary>
    void OfferDisplayFrameBytes(ReadOnlySpan<byte> payload);

    /// <summary>
    /// RX1 demodulated audio taken before any mute, mix, sidetone, monitor or
    /// recorder inject. <paramref name="appliedAfGainDb"/> is the operator AF
    /// gain already applied to these samples; the feed divides it out.
    /// <paramref name="silence"/> forces a silent block (audio modem active,
    /// sidecar TX mute, …) while keeping the listener's audio clock running.
    /// <paramref name="captureUnixMs"/> is the wall-clock (Unix ms) capture time
    /// of the OLDEST sample in the block, or a conservative (earlier) estimate;
    /// a block captured at or before the last keyed moment plus the transmit
    /// tail is silenced even if the station has since un-keyed. NaN = "now".
    /// Callers wrap this in try/catch: a listener fault must never stop
    /// operator audio.
    /// </summary>
    void OfferRx1Audio(
        ReadOnlySpan<float> interleaved,
        int channels,
        int sampleRateHz,
        double appliedAfGainDb,
        bool silence,
        double captureUnixMs = double.NaN);

    /// <summary>
    /// A transmit signal the engine's own sources cannot see (the Protocol 3
    /// sidecar's TX-IQ egress) is active now: extend the transmit hold.
    /// </summary>
    void NoteTransmitting();

    /// <summary>True while a listener wants the wideband overview (stream 0xF0). One volatile read.</summary>
    bool WantsWideband => false;

    /// <summary>True while <see cref="PublicTransmitHold"/> is held: producers skip analysis.</summary>
    bool IsTransmitHeld => false;

    /// <summary>
    /// True while the host reports band conditions to the directory (Public
    /// Listening on, listed, sharing wideband): the overview measures them and
    /// the band survey may pulse wideband demand. One volatile read.
    /// </summary>
    bool BandSurveyEnabled => false;

    /// <summary>
    /// One full-span wideband overview row (dB per pixel) from the listener's
    /// own analyzer — never the operator's. The feed max-decimates it to the
    /// overview bin count and emits it as stream 0xF0, or emits a data-free
    /// TransmitHold frame while the transmit hold is held (the row is not read
    /// then, only its width).
    /// </summary>
    void OfferWidebandOverview(ReadOnlySpan<float> rowDb, double tsUnixMs, long centerHz, float hzPerPixel)
    {
    }

    /// <summary>
    /// Per-slot rate gate for a guest receiver's panadapter (stream 0xE0+slot),
    /// at the listener spectrum fps. True = the caller may read the guest
    /// analyzer and offer one row now.
    /// </summary>
    bool TryBeginGuestSpectrum(int slot) => false;

    /// <summary>
    /// One guest receiver display row (dB per pixel) from the guest's OWN
    /// WDSP analyzer. Emitted as 0x41 with stream id 0xE0+slot, or as a
    /// data-free TransmitHold frame while the station transmits (the row is
    /// then not read, only its <paramref name="width"/>).
    /// </summary>
    void OfferGuestSpectrum(int slot, ReadOnlySpan<float> rowDb, int width, double tsUnixMs, long centerHz, float hzPerPixel)
    {
    }

    /// <summary>
    /// A guest receiver's demodulated mono audio (fixed 0 dB AF gain), emitted
    /// as 0x43 with stream id 0xE0+slot. Silenced exactly like listen-along:
    /// while <see cref="PublicTransmitHold"/> is held and for any block whose
    /// capture time falls inside a transmission or its tail.
    /// </summary>
    void OfferGuestAudio(int slot, ReadOnlySpan<float> mono, int sampleRateHz, double captureUnixMs = double.NaN)
    {
    }

    /// <summary>
    /// A virtual receiver's demodulated mono audio (Phase 4, fixed 0 dB AF
    /// gain), emitted as 0x43 with stream id 0xC0+slot. Silenced exactly like
    /// listen-along and guest audio: while <see cref="PublicTransmitHold"/> is
    /// held and for any block whose capture time falls inside a transmission
    /// or its tail.
    /// </summary>
    void OfferVrxAudio(int slot, ReadOnlySpan<float> mono, int sampleRateHz, double captureUnixMs = double.NaN)
    {
    }
}
