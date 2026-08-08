// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA),
//                    Christian Suarez (N9WAR), and contributors.

using System.Net;
using System.Net.Sockets;

namespace Zeus.Server;

internal static class PortBindDiagnostics
{
    internal static bool TryResolveBindAddress(string bindAddress, out IPAddress? address)
    {
        if (bindAddress is "0.0.0.0" or "*" or "")
        {
            address = IPAddress.Any;
            return true;
        }

        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.Loopback;
            return true;
        }

        return IPAddress.TryParse(bindAddress, out address);
    }

    internal static SocketError? ProbeTcp(IPAddress address, int port)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(address, port));
            return null;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode;
        }
    }

    internal static string Describe(SocketError error, string bindAddress, int port, string protocol) => error switch
    {
        SocketError.AddressAlreadyInUse => $"Port {port} is already in use on {bindAddress}",
        SocketError.AccessDenied =>
            $"Access was denied while binding {protocol} port {port} on {bindAddress}. " +
            $"On Windows, the port may be reserved by Windows; check excluded ranges with " +
            $"'netsh interface ipv4 show excludedportrange protocol={protocol.ToLowerInvariant()}'. " +
            "If WinNAT owns the reservation, restart it with 'net stop winnat' then 'net start winnat'.",
        SocketError.AddressNotAvailable =>
            $"Bind address {bindAddress} is not available on this computer. Check that it belongs to an active network adapter.",
        _ => $"Could not bind {protocol} port {port} on {bindAddress} ({error}).",
    };
}
