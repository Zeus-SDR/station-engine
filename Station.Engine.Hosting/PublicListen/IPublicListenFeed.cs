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
/// Engine → host seam for Public Listening (ADR-0010). The feed emits ONLY
/// serialized PublicSpectrum (0x41), PublicStationStatus (0x42) and
/// PublicAudioPcm (0x43) frames. Feed sinks are deliberately NOT
/// <c>IClientSink</c>s: they never enter <c>StreamingHub._clients</c>, so the
/// hub's LastClientDisconnected (UI-MOX release) behaviour is unaffected by
/// any number of listeners.
/// </summary>
public interface IPublicListenFeed
{
    /// <summary>Attach a sink; dispose the result to detach.</summary>
    IDisposable AttachSink(IPublicListenFeedSink sink);

    /// <summary>Union of what connected listeners want. Nothing unrequested is produced.</summary>
    void SetDemand(ListenerFeedDemandFlags demand);

    ListenerFeedDemandFlags Demand { get; }

    /// <summary>
    /// Operator-chosen spectrum shape: frames per second (1..15) and target
    /// bin count (512/1024/2048). Out-of-range values are clamped.
    /// </summary>
    void Configure(int spectrumFps, int spectrumBins);

    /// <summary>
    /// True while the connected radio can serve the wideband overview (stream
    /// 0xF0): a Protocol 2 connection with raw-ADC wideband snapshots. The host
    /// combines it with its ShareWideband switch for listener caps and the
    /// directory.
    /// </summary>
    bool WidebandCapable => false;

    /// <summary>
    /// Host switch for band conditions: on while Public Listening is enabled,
    /// listed in the directory and sharing wideband. The engine then measures
    /// band conditions from the wideband overview and runs the periodic band
    /// survey on a Protocol 2 radio.
    /// </summary>
    void SetBandSurvey(bool enabled)
    {
    }
}

public interface IPublicListenFeedSink
{
    /// <summary>
    /// A complete serialized 0x41/0x42/0x43 frame. Called on producer (DSP)
    /// threads — implementations must copy or enqueue and return immediately.
    /// The buffer may be shared by every sink; never mutate it.
    /// </summary>
    void OnFeedFrame(ReadOnlyMemory<byte> frame);
}
