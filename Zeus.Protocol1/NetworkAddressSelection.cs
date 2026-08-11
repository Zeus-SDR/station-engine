// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Zeus.Protocol1;

internal readonly record struct LocalIpv4Address(IPAddress Address, IPAddress Mask, bool IsTunnel = false);

// Test-visible snapshot of a network interface, so the tunnel-tagging projection
// in Protocol1Client.SelectLocalIpv4Addresses can be exercised without live NICs.
internal readonly record struct NicSnapshot(
    string Name,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    IReadOnlyList<(IPAddress Address, IPAddress? Mask)> UnicastAddresses);

internal static class NetworkAddressSelection
{
    public static bool IsTunnelInterface(string name, NetworkInterfaceType type)
        => type == NetworkInterfaceType.Tunnel ||
           (type == NetworkInterfaceType.Unknown &&
            name.StartsWith("utun", StringComparison.OrdinalIgnoreCase));

    public static IPAddress? FindLocalAddressForSubnet(
        IPAddress radioIp,
        IEnumerable<LocalIpv4Address> localAddresses)
    {
        if (radioIp.AddressFamily != AddressFamily.InterNetwork) return null;
        // Prefer a physical (non-tunnel) interface. Discovery excludes tunnel
        // interfaces entirely (RadioDiscoveryService.GetSocketPlans), so a radio
        // is always seen via a real NIC; if the connect socket then binds to an
        // overlapping VPN/utun address that merely appears earlier in enumeration
        // order, every datagram fails with EHOSTUNREACH ("No route to host").
        // Only fall back to a tunnel match when no physical interface matches, so
        // a genuine radio-over-VPN topology still connects.
        IPAddress? tunnelMatch = null;
        foreach (var local in localAddresses)
        {
            if (local.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (local.Mask.AddressFamily != AddressFamily.InterNetwork) continue;
            if (local.Mask.Equals(IPAddress.Any)) continue;
            if (!SameSubnet(radioIp, local.Address, local.Mask)) continue;
            if (local.IsTunnel)
            {
                tunnelMatch ??= local.Address;
                continue;
            }
            return local.Address;
        }
        return tunnelMatch;
    }

    public static bool IsLinkLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var ab = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        var mb = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++)
            if ((ab[i] & mb[i]) != (bb[i] & mb[i])) return false;
        return true;
    }
}
