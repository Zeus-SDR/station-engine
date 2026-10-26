// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Zeus.Contracts;
using Zeus.Server.Cat;
using Zeus.Server.Tci;

namespace Zeus.Server;

/// <summary>
/// Management service for CAT server configuration and status. Mirrors
/// <see cref="TciManagementService"/>. CAT owns its own TcpListener, so a live
/// rebind is technically possible, but for parity (and to keep the surface
/// minimal) port/bind changes are persisted and applied on the next restart —
/// the UI reports RequiresRestart, exactly like TCI. Hot-rebind is a documented
/// low-risk follow-up.
/// </summary>
public sealed class CatManagementService
{
    private readonly ILogger<CatManagementService> _log;
    private readonly CatServer _catServer;
    private readonly CatOptions _startupOptions;
    private readonly CatConfigStore _store;
    private readonly TciOptions _tciStartupOptions;
    private readonly TciConfigStore _tciStore;

    private CatRuntimeConfig _pendingConfig;

    public CatManagementService(
        ILogger<CatManagementService> log,
        CatServer catServer,
        IOptions<CatOptions> options,
        CatConfigStore store,
        IOptions<TciOptions> tciOptions,
        TciConfigStore tciStore)
    {
        _log = log;
        _catServer = catServer;
        _startupOptions = options.Value;
        _store = store;
        _tciStartupOptions = tciOptions.Value;
        _tciStore = tciStore;

        _pendingConfig = _store.Get() ?? new CatRuntimeConfig(
            Enabled: _startupOptions.Enabled,
            BindAddress: _startupOptions.BindAddress,
            Port: _startupOptions.Port,
            AutoReport: _startupOptions.AutoReport);
    }

    public CatStatus GetStatus()
    {
        var currentlyEnabled = _startupOptions.Enabled;
        var currentPort = _startupOptions.Port;
        var currentBindAddress = _startupOptions.BindAddress;
        var currentAutoReport = _startupOptions.AutoReport;
        var clientCount = _catServer.ClientCount;

        // The running CatServer is the listener for this port — probe-binding
        // the same port here always fails with "address already in use" and
        // surfaces a false-positive in the UI. Skip it; "is this port free to
        // switch to?" is answered by TestPort, which the panel calls before a
        // bind/port change. (Same lesson as TciManagementService.)
        bool portAvailable = true;
        string? error = PendingTciCollision();

        var requiresRestart = _pendingConfig.Enabled != currentlyEnabled
                            || _pendingConfig.Port != currentPort
                            || _pendingConfig.BindAddress != currentBindAddress
                            || _pendingConfig.AutoReport != currentAutoReport;

        return new CatStatus(
            CurrentlyEnabled: currentlyEnabled,
            CurrentPort: currentPort,
            CurrentBindAddress: currentBindAddress,
            CurrentAutoReport: currentAutoReport,
            PendingEnabled: _pendingConfig.Enabled,
            PendingPort: _pendingConfig.Port,
            PendingBindAddress: _pendingConfig.BindAddress,
            PendingAutoReport: _pendingConfig.AutoReport,
            ClientCount: clientCount,
            PortAvailable: portAvailable,
            RequiresRestart: requiresRestart,
            Error: error);
    }

    // CAT keeps the port at startup and TCI yields (StationControlPortConflict),
    // so warn here too: the operator may have just moved CAT onto TCI's port.
    private string? PendingTciCollision()
    {
        var tci = _tciStore.Get();
        var tciEnabled = tci?.Enabled ?? _tciStartupOptions.Enabled;
        var tciBindAddress = tci?.BindAddress ?? _tciStartupOptions.BindAddress;
        var tciPort = tci?.Port ?? _tciStartupOptions.Port;
        return StationControlPortConflict.Collides(
            tciEnabled, tciBindAddress, tciPort,
            _pendingConfig.Enabled, _pendingConfig.BindAddress, _pendingConfig.Port)
            ? StationControlPortConflict.PendingCollisionMessage("TCI", _pendingConfig.Port)
            : null;
    }

    public CatStatus SetConfig(CatRuntimeConfig config)
    {
        var normalized = new CatRuntimeConfig(
            Enabled: config.Enabled,
            BindAddress: string.IsNullOrWhiteSpace(config.BindAddress)
                ? "127.0.0.1"
                : config.BindAddress.Trim(),
            Port: config.Port is > 0 and < 65536 ? config.Port : 19090,
            AutoReport: config.AutoReport);

        _pendingConfig = normalized;
        try
        {
            _store.Set(normalized);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cat.config.persist failed");
        }

        _log.LogInformation(
            "cat.config.updated enabled={Enabled} bind={Bind} port={Port} autoReport={AutoReport} (restart required)",
            normalized.Enabled, normalized.BindAddress, normalized.Port, normalized.AutoReport);

        return GetStatus();
    }

    public CatTestResult TestPort(string bindAddress, int port)
        => TestPortCore(bindAddress, port, PortBindDiagnostics.ProbeTcp);

    internal static CatTestResult TestPortCore(
        string bindAddress,
        int port,
        Func<IPAddress, int, SocketError?> probe)
    {
        if (port is <= 0 or >= 65536)
            return new CatTestResult(Ok: false, Error: "Port must be between 1 and 65535");

        var addr = string.IsNullOrWhiteSpace(bindAddress) ? "127.0.0.1" : bindAddress.Trim();

        if (!PortBindDiagnostics.TryResolveBindAddress(addr, out var ip))
            return new CatTestResult(Ok: false, Error: $"Bind address '{addr}' is not a valid IP address, localhost, or *.");

        var error = probe(ip!, port);
        if (error is null or SocketError.Success)
            return new CatTestResult(Ok: true, Error: null);

        return new CatTestResult(Ok: false, Error: PortBindDiagnostics.Describe(error.Value, addr, port, "TCP"));
    }
}
