// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Sockets;

namespace Zeus.Protocol1;

internal readonly record struct LocalIpv4Address(IPAddress Address, IPAddress Mask);

internal static class NetworkAddressSelection
{
    public static IPAddress? FindLocalAddressForSubnet(
        IPAddress radioIp,
        IEnumerable<LocalIpv4Address> localAddresses)
    {
        if (radioIp.AddressFamily != AddressFamily.InterNetwork) return null;
        foreach (var local in localAddresses)
        {
            if (local.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (local.Mask.AddressFamily != AddressFamily.InterNetwork) continue;
            if (local.Mask.Equals(IPAddress.Any)) continue;
            if (SameSubnet(radioIp, local.Address, local.Mask)) return local.Address;
        }
        return null;
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
