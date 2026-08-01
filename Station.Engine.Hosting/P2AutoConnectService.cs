// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zeus.Contracts;
using Zeus.Dsp.Wdsp;
using P2Discovery = Zeus.Protocol2.Discovery.IRadioDiscovery;

namespace Zeus.Server;

internal sealed record Protocol2ConnectionProbe(
    bool Busy,
    HpsdrBoardKind BoardKind,
    string? Firmware);

internal interface IProtocol2ConnectionConnector
{
    bool IsConnected { get; }
    bool IsConnectReady { get; }
    Task<Protocol2ConnectionProbe?> ProbeAsync(
        IPEndPoint endpoint,
        CancellationToken cancellationToken);
    Task<int> ConnectAsync(
        IPEndPoint endpoint,
        int sampleRateKhz,
        HpsdrBoardKind boardKind,
        string? firmware,
        bool sampleRateExplicit,
        CancellationToken cancellationToken);
}

internal sealed class Protocol2ConnectionConnector(
    RadioService radio,
    DspPipelineService dsp,
    WdspWisdomInitializer wisdom,
    P2Discovery discovery) : IProtocol2ConnectionConnector
{
    public bool IsConnected => radio.IsConnected;

    public bool IsConnectReady =>
        wisdom.Phase is not (WisdomPhase.Idle or WisdomPhase.Building);

    public async Task<Protocol2ConnectionProbe?> ProbeAsync(
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        var probe = await discovery.ProbeEndpointAsync(
            endpoint,
            TimeSpan.FromMilliseconds(700),
            cancellationToken).ConfigureAwait(false);
        return probe is null
            ? null
            : new Protocol2ConnectionProbe(
                probe.Details.Busy,
                probe.Board,
                probe.FirmwareString);
    }

    public Task<int> ConnectAsync(
        IPEndPoint endpoint,
        int sampleRateKhz,
        HpsdrBoardKind boardKind,
        string? firmware,
        bool sampleRateExplicit,
        CancellationToken cancellationToken) =>
        dsp.ConnectP2Async(
            endpoint,
            sampleRateKhz,
            numAdc: 2,
            cancellationToken,
            boardKind,
            firmware,
            sampleRateExplicit);
}

internal interface IP2AutoConnectControl
{
    bool ManualDisconnectRequested { get; }
    void DisableForManualDisconnect();
}

internal sealed class P2AutoConnectControl : IP2AutoConnectControl
{
    private int _manualDisconnectRequested;

    public bool ManualDisconnectRequested =>
        Volatile.Read(ref _manualDisconnectRequested) != 0;

    public void DisableForManualDisconnect() =>
        Interlocked.Exchange(ref _manualDisconnectRequested, 1);
}

internal static class Protocol2ConnectionServiceCollectionExtensions
{
    public static IServiceCollection AddProtocol2ConnectionServices(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<
            IProtocol2ConnectionConnector,
            Protocol2ConnectionConnector>();
        services.TryAddSingleton<IP2AutoConnectControl, P2AutoConnectControl>();
        return services;
    }
}

internal static class Protocol2ConnectionEndpoint
{
    public static bool TryParse(string? raw, out IPEndPoint endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim();
        if (IPAddress.TryParse(value, out var address))
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            endpoint = new IPEndPoint(address, 1024);
            return true;
        }

        if (!IPEndPoint.TryParse(value, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork
            || parsed.Port is < 1 or > 65_535)
            return false;

        endpoint = parsed;
        return true;
    }
}

internal readonly record struct P2AutoConnectTiming(
    TimeSpan PollInterval,
    IReadOnlyList<TimeSpan> FailureBackoffs)
{
    public static P2AutoConnectTiming Production { get; } = new(
        TimeSpan.FromSeconds(2),
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        ]);
}

internal sealed class P2AutoConnectOptions(string endpoint)
{
    public string Endpoint { get; } = endpoint;
}

internal class P2AutoConnectService : BackgroundService
{
    internal const string EndpointEnvironmentVariable = "ZEUS_P2_AUTOCONNECT_ENDPOINT";
    private const int SampleRateKhz = 48;

    private readonly P2AutoConnectOptions _options;
    private readonly IProtocol2ConnectionConnector _connector;
    private readonly IP2AutoConnectControl _control;
    private readonly ILogger<P2AutoConnectService> _log;
    private readonly P2AutoConnectTiming _timing;
    private bool _endpointErrorLogged;
    private int _failureBackoffIndex;

    public P2AutoConnectService(
        P2AutoConnectOptions options,
        IProtocol2ConnectionConnector connector,
        IP2AutoConnectControl control,
        ILogger<P2AutoConnectService> log)
        : this(options, connector, control, log, P2AutoConnectTiming.Production)
    {
    }

    internal P2AutoConnectService(
        P2AutoConnectOptions options,
        IProtocol2ConnectionConnector connector,
        IP2AutoConnectControl control,
        ILogger<P2AutoConnectService> log,
        P2AutoConnectTiming timing)
    {
        _options = options;
        _connector = connector;
        _control = control;
        _log = log;
        _timing = timing;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TryResolveEndpoint(out var endpoint)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(endpoint, stoppingToken).ConfigureAwait(false);
                _failureBackoffIndex = 0;
                await DelayAsync(_timing.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var delay = NextFailureBackoff();
                _log.LogWarning(
                    ex,
                    "p2.autoconnect cycle failed endpoint={Endpoint}; retrying in {Delay}",
                    endpoint,
                    delay);
                try
                {
                    await DelayAsync(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal bool TryResolveEndpoint(out IPEndPoint endpoint)
    {
        if (Protocol2ConnectionEndpoint.TryParse(_options.Endpoint, out endpoint))
            return true;

        if (!_endpointErrorLogged)
        {
            _log.LogError(
                "p2.autoconnect disabled; {Variable} value '{Endpoint}' is not a valid IPv4 endpoint",
                EndpointEnvironmentVariable,
                _options.Endpoint);
            _endpointErrorLogged = true;
        }

        return false;
    }

    internal async Task RunCycleAsync(
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        if (_control.ManualDisconnectRequested
            || _connector.IsConnected
            || !_connector.IsConnectReady)
            return;

        var probe = await _connector.ProbeAsync(endpoint, cancellationToken)
            .ConfigureAwait(false);
        if (_control.ManualDisconnectRequested || probe is null || probe.Busy)
            return;

        var boardKind = probe.BoardKind == HpsdrBoardKind.Unknown
            ? HpsdrBoardKind.OrionMkII
            : probe.BoardKind;
        _log.LogInformation(
            "p2.autoconnect connecting endpoint={Endpoint} rateKhz={RateKhz} board={Board}",
            endpoint,
            SampleRateKhz,
            boardKind);
        await _connector.ConnectAsync(
            endpoint,
            SampleRateKhz,
            boardKind,
            probe.Firmware,
            sampleRateExplicit: false,
            cancellationToken).ConfigureAwait(false);
    }

    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    private TimeSpan NextFailureBackoff()
    {
        var index = Math.Min(
            _failureBackoffIndex,
            _timing.FailureBackoffs.Count - 1);
        if (_failureBackoffIndex < _timing.FailureBackoffs.Count - 1)
            _failureBackoffIndex++;
        return _timing.FailureBackoffs[index];
    }
}
