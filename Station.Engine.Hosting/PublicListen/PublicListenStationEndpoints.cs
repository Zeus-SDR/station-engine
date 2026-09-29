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

using System.Net;
using System.Net.WebSockets;
using Zeus.Contracts;

namespace Zeus.Server.PublicListen;

/// <summary>Body of <c>PUT /api/station/public-listen/config</c>; omitted fields keep their value.</summary>
public sealed record PublicListenEngineConfigRequest(int? SpectrumFps = null, int? SpectrumBins = null, bool? BandSurvey = null);

/// <summary>Body of <c>PUT /api/station/public-listen/guests/pool</c>.</summary>
public sealed record PublicListenGuestPoolRequest(int Size);

/// <summary>
/// One entry of <c>PUT /api/station/public-listen/guests/leases</c>. Enum
/// fields are names (<see cref="RxMode"/>, <see cref="GuestAgcMode"/>,
/// <see cref="GuestNrMode"/>, <see cref="GuestNbMode"/>); the engine normalizes
/// the settings again. The Phase 4 DSP fields are optional (an older host
/// omits them and gets the Phase 3 defaults).
/// </summary>
public sealed record PublicListenGuestLeaseDto(
    int Slot,
    long Generation,
    long Hz,
    string Mode,
    int FilterLowHz,
    int FilterHighHz,
    string Agc,
    string Nr,
    double? AgcTopDb = null,
    string? Nb = null,
    bool? Anf = null,
    bool? Snb = null,
    bool? Squelch = null,
    double? SquelchDb = null,
    bool? Apf = null,
    int? EmnrGainMethod = null,
    int? EmnrNpeMethod = null,
    bool? EmnrAeRun = null,
    double? Nr4ReductionAmount = null,
    double? Nr4SmoothingFactor = null,
    int? NnrModel = null,
    double? NnrMaskFloorDb = null,
    double? NnrAlpha = null,
    double? NnrKneeDb = null,
    double? NnrTauSeconds = null,
    double? NnrMaxGainDb = null,
    double? NnrAttackMs = null,
    double? NnrReleaseMs = null,
    bool? EqEnabled = null,
    int? EqPreampDb = null,
    int? EqLowDb = null,
    int? EqMidDb = null,
    int? EqHighDb = null,
    int DisplaySpanHz = 0, long DisplayCenterHz = 0)
{
    public bool TryToRequest(out GuestLeaseRequest request)
    {
        request = default;
        if (!PublicListenReceiverWire.TryToSettings(
                Hz, Mode, FilterLowHz, FilterHighHz, Agc, Nr, AgcTopDb, Nb, Anf, Snb, Squelch, SquelchDb, Apf, EmnrGainMethod, EmnrNpeMethod, EmnrAeRun, Nr4ReductionAmount, Nr4SmoothingFactor, NnrModel, NnrMaskFloorDb, NnrAlpha, NnrKneeDb, NnrTauSeconds, NnrMaxGainDb, NnrAttackMs, NnrReleaseMs, EqEnabled, EqPreampDb, EqLowDb, EqMidDb, EqHighDb, out var settings))
            return false;
        request = new GuestLeaseRequest(Slot, Generation, (settings with { DisplaySpanHz = DisplaySpanHz, DisplayCenterHz = DisplayCenterHz }).Normalize());
        return true;
    }
}

/// <summary>
/// One entry of <c>PUT /api/station/public-listen/vrx/receivers</c> (Phase 4):
/// the same receiver fields as a guest lease.
/// </summary>
public sealed record PublicListenVrxDto(
    int Slot,
    long Generation,
    long Hz,
    string Mode,
    int FilterLowHz,
    int FilterHighHz,
    string Agc,
    string Nr,
    double? AgcTopDb = null,
    string? Nb = null,
    bool? Anf = null,
    bool? Snb = null,
    bool? Squelch = null,
    double? SquelchDb = null,
    bool? Apf = null,
    int? EmnrGainMethod = null,
    int? EmnrNpeMethod = null,
    bool? EmnrAeRun = null,
    double? Nr4ReductionAmount = null,
    double? Nr4SmoothingFactor = null,
    int? NnrModel = null,
    double? NnrMaskFloorDb = null,
    double? NnrAlpha = null,
    double? NnrKneeDb = null,
    double? NnrTauSeconds = null,
    double? NnrMaxGainDb = null,
    double? NnrAttackMs = null,
    double? NnrReleaseMs = null,
    bool? EqEnabled = null,
    int? EqPreampDb = null,
    int? EqLowDb = null,
    int? EqMidDb = null,
    int? EqHighDb = null)
{
    public bool TryToRequest(out VrxRequest request)
    {
        request = default;
        if (!PublicListenReceiverWire.TryToSettings(
                Hz, Mode, FilterLowHz, FilterHighHz, Agc, Nr, AgcTopDb, Nb, Anf, Snb, Squelch, SquelchDb, Apf, EmnrGainMethod, EmnrNpeMethod, EmnrAeRun, Nr4ReductionAmount, Nr4SmoothingFactor, NnrModel, NnrMaskFloorDb, NnrAlpha, NnrKneeDb, NnrTauSeconds, NnrMaxGainDb, NnrAttackMs, NnrReleaseMs, EqEnabled, EqPreampDb, EqLowDb, EqMidDb, EqHighDb, out var settings))
            return false;
        request = new VrxRequest(Slot, Generation, settings);
        return true;
    }
}

/// <summary>Body of <c>PUT /api/station/public-listen/vrx/pool</c>.</summary>
public sealed record PublicListenVrxPoolRequest(int Size);

internal static class PublicListenReceiverWire
{
    /// <summary>Parse one host receiver entry (enum names) into normalized engine settings.</summary>
    public static bool TryToSettings(
        long hz,
        string mode,
        int filterLowHz,
        int filterHighHz,
        string agc,
        string nr,
        double? agcTopDb,
        string? nb,
        bool? anf,
        bool? snb,
        bool? squelch,
        double? squelchDb,
        bool? apf,
        int? emnrGainMethod,
        int? emnrNpeMethod,
        bool? emnrAeRun,
        double? nr4ReductionAmount,
        double? nr4SmoothingFactor,
        int? nnrModel,
        double? nnrMaskFloorDb,
        double? nnrAlpha,
        double? nnrKneeDb,
        double? nnrTauSeconds,
        double? nnrMaxGainDb,
        double? nnrAttackMs,
        double? nnrReleaseMs,
        bool? eqEnabled,
        int? eqPreampDb,
        int? eqLowDb,
        int? eqMidDb,
        int? eqHighDb,
        out GuestReceiverSettings settings)
    {
        settings = null!;
        if (!Enum.TryParse<RxMode>(mode, ignoreCase: true, out var rxMode) || !Enum.IsDefined(rxMode)) return false;
        if (!Enum.TryParse<GuestAgcMode>(agc, ignoreCase: true, out var agcMode) || !Enum.IsDefined(agcMode)) return false;
        if (!Enum.TryParse<GuestNrMode>(nr, ignoreCase: true, out var nrMode) || !Enum.IsDefined(nrMode)) return false;
        var nbMode = GuestNbMode.Off;
        if (nb is not null && (!Enum.TryParse(nb, ignoreCase: true, out nbMode) || !Enum.IsDefined(nbMode))) return false;
        settings = new GuestReceiverSettings(
            hz,
            rxMode,
            filterLowHz,
            filterHighHz,
            agcMode,
            nrMode,
            agcTopDb ?? GuestReceiverSettings.DefaultAgcTopDb,
            nbMode,
            anf ?? false,
            snb ?? false,
            squelch ?? false,
            squelchDb ?? GuestReceiverSettings.DefaultSquelchDb,
            apf ?? false,
            emnrGainMethod ?? 2,
            emnrNpeMethod ?? 0,
            emnrAeRun ?? true,
            nr4ReductionAmount ?? 10,
            nr4SmoothingFactor ?? 0,
            nnrModel ?? 0,
            nnrMaskFloorDb ?? -25,
            nnrAlpha ?? 1,
            nnrKneeDb ?? 10,
            nnrTauSeconds ?? 2,
            nnrMaxGainDb ?? 12,
            nnrAttackMs ?? 0,
            nnrReleaseMs ?? 0,
            eqEnabled ?? false,
            eqPreampDb ?? 0,
            eqLowDb ?? 0,
            eqMidDb ?? 0,
            eqHighDb ?? 0).Normalize();
        return true;
    }
}

/// <summary>
/// Private station-side Public Listening surface for the separately launched
/// Zeus Link product host (docs/designs/public-listening.md, "Engine ↔ host
/// seam"). Mapped only by StationEngine, never by the Desktop host; every route
/// (and the <c>/ws?feed=listener</c> socket) sits behind the station bearer
/// token (<see cref="StationAccessTokenAuthorization"/>, failing closed without
/// one) plus a loopback / no-Origin check here. Nothing here touches the
/// operator's receivers, PureSignal or any <c>StreamingHub</c> client.
/// </summary>
public static class PublicListenStationEndpoints
{
    public const string RoutePrefix = "/api/station/public-listen";
    private const int MaxBodyBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapPublicListenStationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet($"{RoutePrefix}/status", (HttpContext context, PublicListenFeedSocketHub hub) =>
            IsTrustedServiceRequest(context)
                ? Results.Ok(hub.Status())
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        endpoints.MapPut($"{RoutePrefix}/config", async (HttpContext context, PublicListenFeedSocketHub hub) =>
        {
            if (!IsTrustedServiceRequest(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var request = await ReadBodyAsync<PublicListenEngineConfigRequest>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "invalid config" });
            hub.Configure(request.SpectrumFps, request.SpectrumBins, request.BandSurvey);
            return Results.Ok(hub.Status());
        });

        endpoints.MapPut($"{RoutePrefix}/guests/pool", async (HttpContext context, PublicListenFeedSocketHub hub) =>
        {
            if (!IsTrustedServiceRequest(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var request = await ReadBodyAsync<PublicListenGuestPoolRequest>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "invalid pool size" });
            hub.SetPoolSize(Math.Clamp(request.Size, 0, PublicStreamId.MaxGuestSlots));
            return Results.Ok(hub.GuestCaps());
        });

        endpoints.MapPut($"{RoutePrefix}/guests/leases", async (HttpContext context, PublicListenFeedSocketHub hub) =>
        {
            if (!IsTrustedServiceRequest(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var request = await ReadBodyAsync<PublicListenGuestLeaseDto[]>(context).ConfigureAwait(false);
            if (request is null || request.Length > PublicStreamId.MaxGuestSlots)
                return Results.BadRequest(new { error = "invalid leases" });
            var leases = new List<GuestLeaseRequest>(request.Length);
            foreach (var dto in request)
            {
                if (dto is null || !dto.TryToRequest(out var lease))
                    return Results.BadRequest(new { error = "invalid lease" });
                leases.Add(lease);
            }
            hub.SetLeases(leases);
            return Results.Ok(new { leases = leases.Count });
        });

        endpoints.MapPut($"{RoutePrefix}/vrx/pool", async (HttpContext context, PublicListenFeedSocketHub hub) =>
        {
            if (!IsTrustedServiceRequest(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var request = await ReadBodyAsync<PublicListenVrxPoolRequest>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "invalid pool size" });
            hub.SetVrxPoolSize(Math.Clamp(request.Size, 0, PublicStreamId.MaxVrxSlots));
            return Results.Ok(hub.VrxCaps());
        });

        endpoints.MapPut($"{RoutePrefix}/vrx/receivers", async (HttpContext context, PublicListenFeedSocketHub hub) =>
        {
            if (!IsTrustedServiceRequest(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var request = await ReadBodyAsync<PublicListenVrxDto[]>(context).ConfigureAwait(false);
            if (request is null || request.Length > PublicStreamId.MaxVrxSlots)
                return Results.BadRequest(new { error = "invalid receivers" });
            var receivers = new List<VrxRequest>(request.Length);
            foreach (var dto in request)
            {
                if (dto is null || !dto.TryToRequest(out var receiver))
                    return Results.BadRequest(new { error = "invalid receiver" });
                receivers.Add(receiver);
            }
            hub.SetVrxReceivers(receivers);
            return Results.Ok(new { receivers = receivers.Count });
        });

        endpoints.MapGet($"{RoutePrefix}/guests/{{slot:int}}/signal", (
            HttpContext context,
            int slot,
            long generation,
            PublicListenFeedSocketHub hub) =>
        {
            if (!IsTrustedServiceRequest(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return hub.TryGetSignalDbm(slot, generation, out var dbm) && double.IsFinite(dbm)
                ? Results.Ok(new { slot, generation, dbm = Math.Round(dbm, 1) })
                : Results.NotFound(new { slot, generation });
        });

        return endpoints;
    }

    /// <summary>
    /// Serve <c>/ws?feed=listener</c>: the restricted listener feed socket (see
    /// <see cref="PublicListenFeedSocketHub"/>). Loopback and Origin-less only;
    /// the station bearer token is enforced by middleware before this runs.
    /// </summary>
    public static async Task HandleFeedSocketAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!IsTrustedServiceRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var hub = context.RequestServices.GetRequiredService<PublicListenFeedSocketHub>();
        if (hub.ActiveSockets >= PublicListenFeedSocketHub.MaxSockets)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        try
        {
            await hub.RunAsync(socket, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The host went away; RunAsync already cleared its demand.
        }
    }

    /// <summary>True for <c>?feed=…</c> requests on <c>/ws</c> (the feed socket, not an operator client).</summary>
    public static bool IsFeedSocketRequest(HttpRequest request) =>
        request.Path.Equals("/ws", StringComparison.OrdinalIgnoreCase)
        && request.Query.ContainsKey("feed");

    internal static bool IsTrustedServiceRequest(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        return address is not null
            && IPAddress.IsLoopback(address)
            && context.Request.Headers.Origin.Count == 0;
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext context) where T : class
    {
        if (context.Request.ContentLength is > MaxBodyBytes) return null;
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
