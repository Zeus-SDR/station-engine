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

using Microsoft.Extensions.DependencyInjection.Extensions;
using Zeus.Contracts;

namespace Zeus.Server.PublicListen;

public static class PublicListenFeedServiceCollectionExtensions
{
    /// <summary>
    /// Register the engine-side listener feed. Lazy: nothing is constructed,
    /// hooked into the hub or produced until a host component (the Desktop
    /// host's PublicListenService) resolves <see cref="IPublicListenFeed"/>,
    /// so hosts without Public Listening are unaffected.
    /// </summary>
    public static IServiceCollection AddPublicListenFeed(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var radio = sp.GetRequiredService<RadioService>();
            var tx = sp.GetRequiredService<TxService>();
            var pipeline = sp.GetRequiredService<DspPipelineService>();
            var ptt = sp.GetService<ExternalPttService>();

            // Every signal that means "our RF is on the air". Cheap volatile
            // reads first; TxService/RadioService reads take small locks.
            var hold = new PublicTransmitHold(() =>
                pipeline.RxAudioSuppressedForTx
                || pipeline.IsRadioKeyed
                || radio.IsMox
                || tx.IsMoxOn
                || tx.IsTunOn
                || tx.IsTwoToneOn
                || (ptt?.IsKeyed ?? false));
            radio.MoxChanged += hold.NoteTransmitEdge;

            var feed = new PublicListenFeed(
                hold,
                () => BuildStatus(radio, pipeline.PublicOperatorDbm),
                TimeProvider.System,
                sp.GetService<ILogger<PublicListenFeed>>(),
                widebandCapable: () => pipeline.PublicWidebandCapable);
            sp.GetRequiredService<StreamingHub>().SetListenerTap(feed);
            return feed;
        });
        services.TryAddSingleton<IPublicListenFeed>(sp => sp.GetRequiredService<PublicListenFeed>());
        // Phase 3 guest receivers: the lease book lives in the DSP pipeline,
        // which owns the guest WDSP channels and the Protocol 2 guest DDCs.
        services.TryAddSingleton<IGuestReceiverPool>(sp => sp.GetRequiredService<DspPipelineService>().GuestPool);
        // Phase 4 virtual receivers: WDSP channels on the operator's RX1 IQ,
        // owned by the DSP pipeline like the guests.
        services.TryAddSingleton<IVirtualReceiverPool>(sp => sp.GetRequiredService<DspPipelineService>().VrxPool);
        // Zeus Link: the product host reaches the feed over the loopback
        // /ws?feed=listener socket and /api/station/public-listen/* instead of
        // in-process. Lazy like the feed itself.
        services.TryAddSingleton(sp => new PublicListenFeedSocketHub(
            sp.GetRequiredService<IPublicListenFeed>(),
            sp.GetService<IGuestReceiverPool>(),
            () => sp.GetRequiredService<DspPipelineService>().PublicBandConditions,
            sp.GetService<ILogger<PublicListenFeedSocketHub>>(),
            sp.GetService<IVirtualReceiverPool>()));
        return services;
    }

    /// <summary>
    /// Construct the listener feed now, so its hub tap and DSP hooks are live
    /// before any host connects. The standalone engine (Zeus Link) calls this
    /// at startup: there the product host reaches the feed only through the
    /// loopback feed socket, so nothing in-process would ever resolve it. The
    /// feed stays idle until a listener demand arrives.
    /// </summary>
    public static IPublicListenFeed ActivatePublicListenFeed(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<IPublicListenFeed>();
    }

    internal static PublicStationStatusDto BuildStatus(RadioService radio, double operatorDbm = double.NaN)
    {
        var state = radio.Snapshot();
        string protocol = !radio.IsConnected
            ? "none"
            : radio.IsProtocol3Active ? "P3"
            : radio.IsProtocol2Active ? "P2"
            : "P1";
        return new PublicStationStatusDto(
            PublicStationStatusCodec.CurrentVersion,
            Tx: false,
            Protocol: protocol,
            Op: new PublicOperatorDto(
                state.VfoHz,
                state.Mode.ToString(),
                state.FilterLowHz,
                state.FilterHighHz,
                radio.IsConnected && double.IsFinite(operatorDbm) ? operatorDbm : null),
            Wideband: false,
            Listeners: 0,
            MaxListeners: 0);
    }
}
