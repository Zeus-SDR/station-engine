// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Zeus.Server.Tci;

/// <summary>Shared Kestrel binding and request branch for the TCI listener.</summary>
public static class TciHostingExtensions
{
    internal static TciListenerBinding ResolveTciListener(
        bool enabled,
        string bindAddress,
        int port,
        Func<IReadOnlyCollection<IPAddress>>? getLocalAddresses = null)
    {
        if (!enabled)
            return new(false, bindAddress, port, Address: null, Error: null);

        if (port is <= 0 or >= 65536)
            return new(false, bindAddress, port, Address: null, Error: "TCI port must be between 1 and 65535. TCI listener is inactive.");

        if (bindAddress is "0.0.0.0" or "*" or "")
            return ProbePort(bindAddress, port, IPAddress.Any);

        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
            return ProbePort(bindAddress, port, IPAddress.Loopback);

        if (!IPAddress.TryParse(bindAddress, out var tciIp))
        {
            return new(
                false,
                bindAddress,
                port,
                Address: null,
                Error: $"Bind address '{bindAddress}' is not a valid IP address, localhost, or *. TCI listener is inactive.");
        }

        IReadOnlyCollection<IPAddress> localAddresses;
        try
        {
            localAddresses = (getLocalAddresses ?? GetLocalAddresses)();
        }
        catch (Exception ex)
        {
            return new(
                false,
                bindAddress,
                port,
                Address: null,
                Error: $"Could not verify whether bind address {bindAddress} is available on this computer: {ex.Message}. TCI listener is inactive.");
        }

        if (IsLocalOrLoopback(tciIp, localAddresses))
            return ProbePort(bindAddress, port, tciIp);

        return new(
            false,
            bindAddress,
            port,
            Address: null,
            Error: PortBindDiagnostics.Describe(SocketError.AddressNotAvailable, bindAddress, port, "TCP")
                + " TCI listener is inactive; fix the bind address in Settings > TCI.");
    }

    private static TciListenerBinding ProbePort(string bindAddress, int port, IPAddress address)
    {
        // TCI is optional. Do not let a stale or occupied TCI endpoint prevent
        // the primary station engine from starting. Keep the configured choice
        // so management status can explain why TCI is inactive after launch.
        var error = address.Equals(IPAddress.Any) && Socket.OSSupportsIPv6
            ? ProbeDualModeWildcard(port)
            : PortBindDiagnostics.ProbeTcp(address, port);
        if (error is null or SocketError.Success)
            return new(true, bindAddress, port, address, Error: null);

        return new(false, bindAddress, port, Address: null,
            Error: PortBindDiagnostics.Describe(error.Value, bindAddress, port, "TCP")
                + " TCI listener is inactive; choose another port in Settings > TCI.");
    }

    private static SocketError? ProbeDualModeWildcard(int port)
    {
        // Kestrel's ListenAnyIP uses an IPv6 dual-mode socket when available.
        // An IPv4-only probe can miss a conflicting [::] listener.
        try
        {
            using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
            {
                DualMode = true,
            };
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            socket.Listen(1);
            return null;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode;
        }
    }

    public static void ConfigureTciListener(
        this KestrelServerOptions kestrel,
        bool enabled,
        string bindAddress,
        int port)
        => ConfigureTciListener(kestrel, ResolveTciListener(enabled, bindAddress, port));

    internal static void ConfigureTciListener(
        this KestrelServerOptions kestrel,
        TciListenerBinding listener)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        ArgumentNullException.ThrowIfNull(listener);
        if (!listener.IsActive)
        {
            if (listener.Error is not null)
                Console.Error.WriteLine($"tci.bind.rejected error={listener.Error}");
            return;
        }

        if (listener.BindAddress is "0.0.0.0" or "*" or "")
        {
            kestrel.ListenAnyIP(listener.Port);
        }
        else if (string.Equals(listener.BindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            kestrel.ListenLocalhost(listener.Port);
        }
        else
        {
            kestrel.Listen(listener.Address!, listener.Port);
        }
    }

    public static IApplicationBuilder UseTciServer(
        this IApplicationBuilder app,
        bool enabled,
        int port)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!enabled)
            return app;

        app.UseWhen(
            context => context.Connection.LocalPort == port,
            branch => branch.Run(context =>
                context.RequestServices.GetRequiredService<TciServer>().AcceptAsync(context)));
        return app;
    }

    // True when the candidate can be bound on this machine without a stale-
    // address failure: loopback, a current local unicast address (compared by
    // address bytes so an IPv6 scope id never causes a false negative), or a
    // wildcard (Any/IPv6Any) — wildcards bind regardless of interface state,
    // so they are deliberately accepted here despite the narrower name.
    internal static bool IsLocalOrLoopback(
        IPAddress candidate,
        IReadOnlyCollection<IPAddress> localAddresses) =>
        candidate.Equals(IPAddress.Any) ||
        candidate.Equals(IPAddress.IPv6Any) ||
        IPAddress.IsLoopback(candidate) ||
        localAddresses.Any(localAddress =>
            candidate.GetAddressBytes().SequenceEqual(localAddress.GetAddressBytes()));

    private static IReadOnlyCollection<IPAddress> GetLocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicastAddress => unicastAddress.Address)
            .ToArray();
}

public sealed record TciListenerBinding(
    bool IsActive,
    string BindAddress,
    int Port,
    IPAddress? Address,
    string? Error);
