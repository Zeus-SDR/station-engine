// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Net;
using Zeus.Contracts;
using Zeus.Dsp.Wdsp;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;
using P1Radio = Zeus.Protocol1.Discovery.DiscoveredRadio;
using P2Discovery = Zeus.Protocol2.Discovery.IRadioDiscovery;
using P2Radio = Zeus.Protocol2.Discovery.DiscoveredRadio;

namespace Zeus.Server;

internal sealed record ReclaimRadioRequest(
    string? Endpoint,
    string? Protocol,
    int SampleRate = 192_000,
    byte? BoardId = null);
internal sealed record P1ConnectionIdentity(P1Radio? Probe, HpsdrBoardKind BoardKind);
internal enum P2TakeoverPreflight
{
    Ready,
    AlreadyConnected,
    ConnectedElsewhere,
    DspNotReady,
}

/// <summary>Maps engine-owned radio discovery and P1/P2 lifecycle routes.</summary>
public static class RadioConnectionEndpoints
{
    public static IEndpointRouteBuilder MapRadioConnectionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var log = endpoints.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Zeus.Server.RadioConnectionEndpoints");

        endpoints.MapGet("/api/radios", async (
            IRadioDiscovery p1Discovery,
            P2Discovery p2Discovery,
            IRadioDiscoveryExtension extension,
            HttpContext ctx) =>
        {
            var timeout = TimeSpan.FromMilliseconds(1500);
            var p1Task = p1Discovery.DiscoverAsync(timeout, ctx.RequestAborted);
            var p2Task = p2Discovery.DiscoverAsync(timeout, ctx.RequestAborted);
            var extensionTask = extension.ExtendAsync(p1Task, p2Task, ctx.RequestAborted);
            await Task.WhenAll(p1Task, p2Task, extensionTask).ConfigureAwait(false);

            var additions = extensionTask.Result.Protocol2Details;
            var p1Infos = p1Task.Result.Select(MapP1);
            var p2Infos = p2Task.Result.Select(r => MapP2(
                r,
                additions.TryGetValue(r.Ip, out var details) ? details : null));
            return p1Infos
                .Concat(p2Infos)
                .Concat(extensionTask.Result.AdditionalRadios)
                .ToArray();
        });

        // Take over a Busy radio. Discovery reports status 0x03 when another
        // client owns the radio; the UI normally disables Connect for those.
        // This is outward-facing and deliberate: the Connect panel gates it
        // behind an explicit confirmation because the stop can interrupt an
        // active transmission. P2 only continues after stable Idle proves the
        // old stream is abandoned; a live controller makes the attempt fail.
        endpoints.MapPost("/api/radios/reclaim", async (
            ReclaimRadioRequest req,
            RadioReclaimService reclaim,
            IProtocol2ConnectionConnector p2Connection,
            RadioService radio,
            DspPipelineService dsp,
            HttpContext ctx) =>
        {
            if (!TryParseIpEndpoint(req.Endpoint ?? string.Empty, out var ipEndpoint))
                return Results.BadRequest(new { error = $"Invalid endpoint '{req.Endpoint}'." });

            var isP2 = string.Equals(req.Protocol, "P2", StringComparison.OrdinalIgnoreCase);
            log.LogInformation(
                "api.radios.reclaim ip={Ip} protocol={Proto}",
                ipEndpoint.Address,
                isP2 ? "P2" : "P1");

            if (!isP2)
            {
                await reclaim.ReclaimAsync(
                        ipEndpoint.Address,
                        isProtocol2: false,
                        ct: ctx.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Ok(new { freed = true });
            }

            // Keep the stop/verify/connect handoff under the same lifecycle gate.
            // This closes the local TOCTOU window: no competing API request can
            // connect between the final fail-closed probe and the first P2 start.
            radio.NotifyOperatorConnectionAction();
            await radio.RadioLifecycleGate.WaitAsync(ctx.RequestAborted).ConfigureAwait(false);
            try
            {
                var currentState = radio.Snapshot();
                var preflight = ClassifyP2TakeoverPreflight(
                    radio.IsConnected,
                    currentState.ConnectedProtocol,
                    currentState.Endpoint,
                    ipEndpoint.Address,
                    p2Connection.IsConnectReady);
                if (preflight == P2TakeoverPreflight.AlreadyConnected)
                {
                    // A stale Busy row or a repeated confirmation must never
                    // send run=0 to Zeus's own active P2 session.
                    return Results.Ok(new
                    {
                        freed = false,
                        connected = true,
                        alreadyConnected = true,
                        protocol = "P2",
                        endpoint = currentState.Endpoint,
                        sampleRateKhz = Math.Max(1, currentState.SampleRate / 1000),
                    });
                }

                if (preflight == P2TakeoverPreflight.ConnectedElsewhere)
                {
                    // ConnectP2CoreAsync rejects a second local connection. Do
                    // that preflight before run=0 so a queued/stale takeover
                    // cannot interrupt its target and then discover that this
                    // Zeus instance is already committed to another radio.
                    return Results.Conflict(new
                    {
                        error =
                            $"Zeus is already connected via {currentState.ConnectedProtocol ?? "another protocol"}. " +
                            "Disconnect it before taking over a different radio.",
                    });
                }

                // Do not interrupt the incumbent unless this Zeus instance can
                // proceed immediately once exclusivity is confirmed.
                if (preflight == P2TakeoverPreflight.DspNotReady)
                {
                    return Results.Json(
                        new { error = "DSP is preparing FFTW plans — try again in a moment." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                await reclaim.ReclaimAsync(
                        ipEndpoint.Address,
                        isProtocol2: true,
                        ct: ctx.RequestAborted)
                    .ConfigureAwait(false);

                var stableIdle = await WaitForStableP2IdleAsync(
                    cancellationToken => p2Connection.ProbeAsync(ipEndpoint, cancellationToken),
                    TimeSpan.FromMilliseconds(250),
                    samples: 3,
                    cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
                var finalProbe = stableIdle
                    ? await p2Connection.ProbeAsync(ipEndpoint, ctx.RequestAborted).ConfigureAwait(false)
                    : null;
                if (finalProbe is null || finalProbe.Busy)
                {
                    log.LogWarning(
                        "api.radios.reclaim P2 radio {Ip} did not remain Idle; refusing second-master connect",
                        ipEndpoint.Address);
                    return Results.Json(
                        new
                        {
                            error =
                                "The radio stream stopped, but exclusive control could not be confirmed. " +
                                "The other controller may still be active; disconnect it there before trying again.",
                            busy = true,
                            reclaimable = false,
                        },
                        statusCode: StatusCodes.Status409Conflict);
                }

                var boardKind = req.BoardId is byte rawBoardId
                    ? MapBoardByteP2(rawBoardId)
                    : finalProbe.BoardKind;
                var rateKhz = ResolveP2RateKhz(req.SampleRate);
                rateKhz = await dsp.ConnectP2WithLifecycleGateHeldAsync(
                    ipEndpoint,
                    rateKhz,
                    numAdc: 2,
                    ct: ctx.RequestAborted,
                    boardKind: boardKind,
                    firmware: finalProbe.Firmware,
                    sampleRateExplicit: true).ConfigureAwait(false);
                return Results.Ok(new
                {
                    freed = true,
                    connected = true,
                    protocol = "P2",
                    endpoint = req.Endpoint,
                    sampleRateKhz = rateKhz,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "api.radios.reclaim P2 takeover failed");
                return Results.Problem(ex.Message, statusCode: 500);
            }
            finally
            {
                radio.RadioLifecycleGate.Release();
            }
        });

        endpoints.MapPost("/api/connect", async (
            ConnectRequest req,
            RadioService radio,
            WdspWisdomInitializer wisdom,
            IRadioDiscovery p1Discovery,
            HttpContext ctx) =>
        {
            log.LogInformation(
                "api.connect endpoint={Ep} rate={Rate} preamp={Pre} atten={Atten}",
                req.Endpoint,
                req.SampleRate,
                req.PreampOn,
                req.Atten);

            // WDSPwisdom must finish before OpenChannel, otherwise FFTW runs its slow
            // per-size planner on the pipeline thread and RX packets pile up until
            // the radio drops. The UI keeps Connect disabled during build; this is
            // the server-side guard for non-UI callers (curl, older clients). A
            // Failed bake must NOT block connect: the operator gets a working DSP
            // on the slow path instead of a station they can never open.
            if (wisdom.Phase is WisdomPhase.Idle or WisdomPhase.Building)
                return Results.Json(
                    new { error = "DSP is preparing FFTW plans — try again in a moment." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            if (!TryValidateSampleRate(req.SampleRate, out var rateErr))
                return Results.BadRequest(new { error = rateErr });
            if (req.Atten is int a && !TryValidateAttenDb(a, out var attenErr))
                return Results.BadRequest(new { error = attenErr });

            if (req.PreampOn is bool preamp) radio.SetPreamp(preamp);
            if (req.Atten is int atten) radio.SetAttenuator(new HpsdrAtten(atten));

            // Best-effort identity and firmware capture. The radio is not yet
            // connected, so it normally answers this short discovery probe.
            // Fail-open: discovery failure must never prevent the connection.
            var identity = await ResolveP1ConnectionIdentityAsync(
                req.BoardId,
                req.Endpoint,
                p1Discovery,
                log,
                ctx.RequestAborted).ConfigureAwait(false);
            var probe = identity.Probe;
            var firmware = probe?.FirmwareString;
            if (TryParseIpEndpoint(req.Endpoint, out var firmwareEndpoint)
                && !CanBypassBusyProbe(isP2: false, req.Force)
                && probe?.Details.Busy == true)
            {
                log.LogWarning(
                    "api.connect REFUSED — P1 radio {Ip} reports BUSY (another controller owns it)",
                    firmwareEndpoint.Address);
                return Results.Json(
                    new
                    {
                        error =
                            "This radio is already in use by another controller. Use Reclaim to take exclusive control, then connect again.",
                        busy = true,
                        reclaimable = true,
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                var state = await radio.ConnectAsync(
                    req.Endpoint,
                    req.SampleRate,
                    ctx.RequestAborted,
                    identity.BoardKind,
                    firmware,
                    probe?.Mac).ConfigureAwait(false);
                return Results.Ok(state);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        endpoints.MapPost("/api/connect/p2", async (
            ConnectRequest req,
            RadioService radio,
            WdspWisdomInitializer wisdom,
            IProtocol2ConnectionConnector p2Connection,
            HttpContext ctx) =>
        {
            log.LogInformation(
                "api.connect.p2 endpoint={Ep} rate={Rate} force={Force}",
                req.Endpoint,
                req.SampleRate,
                req.Force);

            // Same guard as /api/connect: block only while the bake is
            // pending; a Failed bake must not lock the operator out.
            if (wisdom.Phase is WisdomPhase.Idle or WisdomPhase.Building)
                return Results.Json(
                    new { error = "DSP is preparing FFTW plans — try again in a moment." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            if (!TryParseIpEndpoint(req.Endpoint, out var ipEndpoint))
                return Results.BadRequest(new { error = $"Invalid endpoint '{req.Endpoint}'." });

            var currentState = radio.Snapshot();
            if (string.Equals(
                    currentState.ConnectedProtocol,
                    "P2",
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(currentState.Endpoint)
                && TryParseIpEndpoint(currentState.Endpoint, out var currentEndpoint)
                && currentEndpoint.Address.Equals(ipEndpoint.Address))
            {
                return Results.Ok(new
                {
                    protocol = "P2",
                    endpoint = currentState.Endpoint ?? req.Endpoint,
                    sampleRateKhz = Math.Max(1, currentState.SampleRate / 1000),
                    alreadyConnected = true,
                });
            }

            // HARDWARE-SAFETY GUARD (relay chatter / PSU brown-out).
            // Before opening the relay-bearing high-priority stream, unicast-probe
            // the radio and refuse to connect if it reports Busy — i.e. another
            // controller (a co-located saturn-go / p2app stack, or another
            // Zeus/Thetis client) is already driving it. Two masters publishing
            // DIFFERENT band/antenna/ALEX-relay selections make the FPGA flip the
            // BPF/LPF/T-R relay matrix every packet; the relay inrush can brown
            // out a shared PSU and reboot the host (observed 2026-06-23 on a
            // co-located CM5 Saturn all-in-one). A single Zeus master is fine —
            // the danger requires a second, disagreeing controller. The operator
            // can reclaim an abandoned stream deliberately. Reclaim sends run=0,
            // requires the radio to remain Idle beyond the normal keepalive
            // cadence, and then re-connects through this same guard. A live old
            // controller therefore reasserts Busy and the takeover aborts. The
            // probe fails OPEN for ordinary connects: a radio that
            // doesn't answer the probe is NOT blocked, so this never strands a
            // legitimate connect. The probe must NOT weaken the co-located
            // ephemeral-port bind — it only reads discovery, it doesn't touch the
            // connect socket.
            // Probe result is reused after the busy-gate to capture the
            // firmware version for the diagnostics snapshot.
            Protocol2ConnectionProbe? probe = null;
            if (!CanBypassBusyProbe(isP2: true, req.Force))
            {
                try
                {
                    probe = await p2Connection.ProbeAsync(
                        ipEndpoint,
                        ctx.RequestAborted).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "api.connect.p2 busy-probe failed — allowing connect");
                }

                if (probe?.Busy == true)
                {
                    log.LogWarning(
                        "api.connect.p2 REFUSED — radio {Ip} reports BUSY (another controller owns it); refusing second-master connect",
                        ipEndpoint.Address);
                    return Results.Json(
                        new
                        {
                            error =
                                "This radio is already in use by another controller. Connecting Zeus as a " +
                                "second master would make the band/antenna/T-R relays chatter and can brown " +
                                "out the radio. Stop the other controller (e.g. saturn-go / another client), " +
                                "or use Take over to safely clear an abandoned radio stream.",
                            busy = true,
                            reclaimable = true,
                        },
                        statusCode: StatusCodes.Status409Conflict);
                }
            }

            var rateKhz = ResolveP2RateKhz(req.SampleRate);

            // Plumb the discovered board byte through so RadioService can
            // surface the real board kind instead of defaulting to OrionMkII
            // for every P2 connection (issue #171 — Brick2 is Hermes/0x01 on P2).
            var boardKind = req.BoardId is byte b
                ? MapBoardByteP2(b)
                : HpsdrBoardKind.Unknown;

            try
            {
                // Firmware version for the "Report a problem" diagnostic snapshot.
                rateKhz = await p2Connection.ConnectAsync(
                    ipEndpoint,
                    rateKhz,
                    boardKind,
                    probe?.Firmware,
                    sampleRateExplicit: true,
                    ctx.RequestAborted).ConfigureAwait(false);
                return Results.Ok(new
                {
                    protocol = "P2",
                    endpoint = req.Endpoint,
                    sampleRateKhz = rateKhz,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "api.connect.p2 failed");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        endpoints.MapPost("/api/disconnect/p2", async (
            DspPipelineService dsp,
            IP2AutoConnectControl p2AutoConnect,
            HttpContext ctx) =>
        {
            log.LogInformation("api.disconnect.p2");
            p2AutoConnect.DisableForManualDisconnect();
            await dsp.DisconnectP2Async(ctx.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { status = "disconnected" });
        });

        endpoints.MapPost("/api/disconnect", async (
            RadioService radio,
            IExternalRadioSidecar sidecar,
            DspPipelineService dsp,
            IP2AutoConnectControl p2AutoConnect,
            HttpContext ctx) =>
        {
            log.LogInformation("api.disconnect");
            p2AutoConnect.DisableForManualDisconnect();
            if (radio.IsProtocol3Active)
            {
                await sidecar.DisconnectAsync(ctx.RequestAborted).ConfigureAwait(false);
                radio.MarkProtocol3Disconnected();
                dsp.DisconnectP3TxEngine();
                return Results.Ok(radio.Snapshot());
            }
            if (radio.IsProtocol2Active)
            {
                await dsp.DisconnectP2Async(ctx.RequestAborted).ConfigureAwait(false);
                return Results.Ok(radio.Snapshot());
            }
            return Results.Ok(await radio.DisconnectAsync(ctx.RequestAborted).ConfigureAwait(false));
        });

        return endpoints;
    }

    internal static bool CanBypassBusyProbe(bool isP2, bool force) =>
        force && !isP2;

    internal static P2TakeoverPreflight ClassifyP2TakeoverPreflight(
        bool isConnected,
        string? connectedProtocol,
        string? connectedEndpoint,
        IPAddress targetAddress,
        bool isConnectReady)
    {
        ArgumentNullException.ThrowIfNull(targetAddress);

        if (isConnected
            && string.Equals(connectedProtocol, "P2", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(connectedEndpoint)
            && TryParseIpEndpoint(connectedEndpoint, out var currentEndpoint)
            && currentEndpoint.Address.Equals(targetAddress))
        {
            return P2TakeoverPreflight.AlreadyConnected;
        }

        if (isConnected) return P2TakeoverPreflight.ConnectedElsewhere;
        return isConnectReady
            ? P2TakeoverPreflight.Ready
            : P2TakeoverPreflight.DspNotReady;
    }

    internal static int ResolveP2RateKhz(int sampleRate) => sampleRate switch
    {
        48_000 => 48,
        96_000 => 96,
        192_000 => 192,
        384_000 => 384,
        768_000 => 768,      // P2 only (ANAN G2)
        1_536_000 => 1536,   // P2 only (ANAN G2)
        _ => 192,
    };

    internal static async Task<bool> WaitForStableP2IdleAsync(
        Func<CancellationToken, Task<Protocol2ConnectionProbe?>> probe,
        TimeSpan interval,
        int samples,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (samples < 1) throw new ArgumentOutOfRangeException(nameof(samples));

        for (var sample = 0; sample < samples; sample++)
        {
            var result = await probe(cancellationToken).ConfigureAwait(false);
            // Reclaim is deliberately fail-closed: unlike an ordinary connect,
            // silence cannot prove that the incumbent stream was released.
            if (result is null || result.Busy) return false;
            if (sample + 1 < samples && interval > TimeSpan.Zero)
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static RadioInfo MapP1(P1Radio radio) => new(
        MacAddress: radio.Mac.ToString(),
        IpAddress: radio.Ip.ToString(),
        BoardId: radio.Board.ToString(),
        FirmwareVersion: radio.FirmwareString,
        Busy: radio.Details.Busy,
        Details: BuildP1Details(radio));

    private static RadioInfo MapP2(
        P2Radio radio,
        IReadOnlyDictionary<string, string>? additionalDetails)
    {
        var details = BuildP2Details(radio);
        if (additionalDetails is not null)
        {
            foreach (var entry in additionalDetails)
                details[entry.Key] = entry.Value;
        }
        return new RadioInfo(
            MacAddress: radio.Mac.ToString(),
            IpAddress: radio.Ip.ToString(),
            BoardId: radio.Board.ToString(),
            FirmwareVersion: radio.FirmwareString,
            Busy: radio.Details.Busy,
            Details: details);
    }

    private static IReadOnlyDictionary<string, string> BuildP1Details(P1Radio radio)
    {
        var details = new Dictionary<string, string>
        {
            ["protocol"] = "P1",
            ["rawBoardId"] = $"0x{radio.Details.RawBoardId:X2}",
            ["firmwareCode"] = radio.FirmwareVersion.ToString(),
            ["gatewareBuild"] = radio.Details.GatewareBuild.ToString(),
            ["rawReplyHex"] = Convert.ToHexString(radio.Details.RawReply),
        };
        if (radio.Details.FixedIpEnabled) details["fixedIpEnabled"] = "true";
        if (radio.Details.FixedIpOverridesDhcp) details["fixedIpOverridesDhcp"] = "true";
        if (radio.Details.MacAddressModified) details["macAddressModified"] = "true";
        if (radio.Details.FixedIpAddress is { } ip) details["fixedIpAddress"] = ip.ToString();
        if (radio.Details.HermesLite2MinorVersion is { } minor)
            details["hl2MinorVersion"] = minor.ToString();
        return details;
    }

    private static Dictionary<string, string> BuildP2Details(P2Radio radio)
    {
        var details = new Dictionary<string, string>
        {
            ["protocol"] = "P2",
            ["rawBoardId"] = $"0x{radio.Details.RawBoardId:X2}",
            ["firmwareCode"] = radio.FirmwareVersion.ToString(),
            ["protocolSupported"] = radio.Details.ProtocolSupported.ToString(),
            ["numReceivers"] = radio.Details.NumReceivers.ToString(),
            ["mercuryVersion0"] = radio.Details.MercuryVersion0.ToString(),
            ["mercuryVersion1"] = radio.Details.MercuryVersion1.ToString(),
            ["mercuryVersion2"] = radio.Details.MercuryVersion2.ToString(),
            ["mercuryVersion3"] = radio.Details.MercuryVersion3.ToString(),
            ["pennyVersion"] = radio.Details.PennyVersion.ToString(),
            ["metisVersion"] = radio.Details.MetisVersion.ToString(),
            ["rawReplyHex"] = Convert.ToHexString(radio.Details.RawReply),
        };
        if (radio.Details.BetaVersion != 0)
            details["betaVersion"] = radio.Details.BetaVersion.ToString();
        return details;
    }

    internal static bool TryParseIpEndpoint(string raw, out IPEndPoint endpoint) =>
        Protocol2ConnectionEndpoint.TryParse(raw, out endpoint);

    internal static async Task<P1ConnectionIdentity> ResolveP1ConnectionIdentityAsync(
        byte? rawBoardId,
        string endpoint,
        IRadioDiscovery discovery,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        P1Radio? probe = null;
        if (TryParseIpEndpoint(endpoint, out var ipEndpoint))
        {
            probe = await TryProbeP1Async(
                discovery,
                ipEndpoint,
                TimeSpan.FromMilliseconds(400),
                logger,
                cancellationToken).ConfigureAwait(false);

            // Only shared wire byte 0x06 needs more evidence. Retry immediately
            // with one longer receive window; all unambiguous boards retain the
            // existing single-probe latency.
            if (rawBoardId == 0x06 && probe is null)
            {
                probe = await TryProbeP1Async(
                    discovery,
                    ipEndpoint,
                    TimeSpan.FromMilliseconds(1000),
                    logger,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var boardKind = ResolveP1BoardKind(rawBoardId, probe);
        if (rawBoardId == 0x06 && probe is null)
        {
            logger.LogWarning(
                "api.connect P1 identity could not be confirmed for ambiguous board byte 0x06 at {Endpoint}; falling back to HermesLite2",
                endpoint);
        }

        return new P1ConnectionIdentity(probe, boardKind);
    }

    private static async Task<P1Radio?> TryProbeP1Async(
        IRadioDiscovery discovery,
        IPEndPoint endpoint,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var found = await discovery.DiscoverAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            return found.FirstOrDefault(d => d.Ip.Equals(endpoint.Address));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "api.connect P1 identity probe failed for {Endpoint}; connection remains fail-open",
                endpoint);
            return null;
        }
    }

    internal static HpsdrBoardKind ResolveP1BoardKind(byte? rawBoardId, P1Radio? probe)
    {
        if (probe is not null)
            return probe.Board;

        // A bare 0x06 cannot distinguish HL2 from a revised ANAN-10E. When
        // probing succeeds both are classified correctly. When it fails, keep
        // the pre-change HL2 behavior so a working HL2 never loses TX; guessing
        // HermesII would send an incompatible 8-bit drive value to HL2 firmware.
        if (rawBoardId == 0x06)
            return HpsdrBoardKind.HermesLite2;

        return rawBoardId is byte raw
            ? ReplyParser.ClassifyBoard(raw, codeVersion: null)
            : HpsdrBoardKind.Unknown;
    }

    // Protocol 2 uses different wire numbering for the dual-ADC family
    // (Angelia/Orion/OrionMkII). This P2-only map mirrors its reply parser;
    // reusing the P1 classifier here would recreate issue #780.
    private static HpsdrBoardKind MapBoardByteP2(byte raw) => raw switch
    {
        0x00 => HpsdrBoardKind.Metis,        // Atlas
        0x01 => HpsdrBoardKind.Hermes,
        0x02 => HpsdrBoardKind.HermesII,
        0x03 => HpsdrBoardKind.Angelia,      // ANAN-100D (P2 wire)
        0x04 => HpsdrBoardKind.Orion,        // ANAN-200D (P2 wire — issue #780)
        0x05 => HpsdrBoardKind.OrionMkII,    // ANAN-7000DLE / 8000DLE (P2 wire)
        0x06 => HpsdrBoardKind.HermesLite2,
        0x0A => HpsdrBoardKind.OrionMkII,    // Saturn / ANAN-G2
        0x14 => HpsdrBoardKind.HermesC10,    // ANAN-G2E
        _ => HpsdrBoardKind.Unknown,
    };

    private static bool TryValidateSampleRate(int rate, out string error)
    {
        if (rate is 48_000 or 96_000 or 192_000 or 384_000 or 768_000 or 1_536_000)
        {
            error = string.Empty;
            return true;
        }
        error =
            $"sampleRate must be one of {{48000, 96000, 192000, 384000, 768000, 1536000}}, got {rate}.";
        return false;
    }

    private static bool TryValidateAttenDb(int db, out string error)
    {
        if (db >= HpsdrAtten.MinDb && db <= HpsdrAtten.MaxDb)
        {
            error = string.Empty;
            return true;
        }
        error = $"atten must be in {HpsdrAtten.MinDb}..{HpsdrAtten.MaxDb} dB, got {db}.";
        return false;
    }
}
