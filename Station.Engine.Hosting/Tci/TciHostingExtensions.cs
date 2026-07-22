// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Zeus.Server.Tci;

/// <summary>Shared Kestrel binding and request branch for the TCI listener.</summary>
public static class TciHostingExtensions
{
    public static void ConfigureTciListener(
        this KestrelServerOptions kestrel,
        bool enabled,
        string bindAddress,
        int port)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        if (!enabled)
            return;

        if (bindAddress is "0.0.0.0" or "*" or "")
            kestrel.ListenAnyIP(port);
        else if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
            kestrel.ListenLocalhost(port);
        else if (IPAddress.TryParse(bindAddress, out var tciIp))
        {
            // A specific persisted IP can go stale (DHCP renumber, new
            // router): Kestrel would then throw WSAEADDRNOTAVAIL at host
            // start and the app would never open. Validate it against the
            // machine's current addresses; on any doubt, leave TCI off and
            // let the operator fix the setting — never bind wider than the
            // address they chose (TCI has no auth; transport is the
            // security boundary), and never take the host down for it.
            IPAddress[]? localAddresses = null;
            try
            {
                localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                    .Select(unicastAddress => unicastAddress.Address)
                    .Where(address => address is not null)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"tci.bind.local-address-enumeration.failed address={tciIp}: {ex.Message} — TCI not started");
            }

            if (localAddresses is not null && IsLocalOrLoopback(tciIp, localAddresses))
            {
                kestrel.Listen(tciIp, port);
            }
            else if (localAddresses is not null)
            {
                Console.Error.WriteLine(
                    $"tci.bind.stale address={tciIp} not on any local interface — TCI not started; fix the bind address in Settings > TCI");
            }
        }
        else
        {
            Console.Error.WriteLine(
                $"tci.bind.invalid address={bindAddress} — TCI not started; fix the bind address in Settings > TCI");
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
}
