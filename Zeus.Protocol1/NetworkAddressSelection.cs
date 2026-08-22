// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Zeus.Protocol1;

internal readonly record struct LocalIpv4Address(
    IPAddress Address,
    IPAddress Mask,
    bool IsTunnel = false,
    string NicIdentity = "",
    string NicDescription = "",
    int? Ipv4InterfaceIndex = null);

// Test-visible snapshot of a network interface, so the tunnel-tagging projection
// in Protocol1Client.SelectLocalIpv4Addresses can be exercised without live NICs.
internal readonly record struct NicSnapshot(
    string Name,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    IReadOnlyList<(IPAddress Address, IPAddress? Mask)> UnicastAddresses,
    string Description = "",
    int? Ipv4InterfaceIndex = null,
    string Identity = "");

internal enum LocalAddressSelectionRule
{
    Reachability,
    RouteAgreement,
    FirstPhysical,
    TunnelFallback,
    None,
}

internal sealed record LocalAddressSelection(
    LocalIpv4Address? Candidate,
    int SubnetMatchCount,
    IReadOnlyList<string> RejectedNicDisplays,
    LocalAddressSelectionRule Rule,
    bool MatchesRouteProbe,
    bool ReachabilityDisagreesWithRoute)
{
    public IPAddress? Address => Candidate?.Address;
    public string NicDisplay => Candidate is null ? "(none)" : DisplayNic(Candidate.Value);
    public string RuleName => Rule switch
    {
        LocalAddressSelectionRule.Reachability => "reachability",
        LocalAddressSelectionRule.RouteAgreement => "route-agreement",
        LocalAddressSelectionRule.FirstPhysical => "first-physical",
        LocalAddressSelectionRule.TunnelFallback => "tunnel-fallback",
        _ => "none",
    };

    internal static string DisplayNic(LocalIpv4Address candidate)
    {
        var description = string.IsNullOrWhiteSpace(candidate.NicDescription)
            ? "(unknown adapter)"
            : candidate.NicDescription;
        var index = candidate.Ipv4InterfaceIndex?.ToString() ?? "unknown";
        return $"{description} (ifIndex={index})";
    }
}

internal static class NetworkAddressSelection
{
    public const int MaxReachabilityProbeCandidates = 8;

    public static bool IsTunnelInterface(string name, NetworkInterfaceType type)
        => type == NetworkInterfaceType.Tunnel ||
           (type == NetworkInterfaceType.Unknown &&
            (name.StartsWith("utun", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("tun", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("wg", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("zt", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("ppp", StringComparison.OrdinalIgnoreCase)));
    // Deliberately NOT keyed on NetworkInterfaceType.Ppp: a PPP link (dial-up,
    // cellular modem) is a real network path, not an overlay riding on top of
    // one. Ranking it behind a physical NIC would demote a legitimate route.
    // The "ppp" name prefix above only fires for the rare Unknown-typed case.

    // Deliberately mirrored in Zeus.Protocol2. The protocol assemblies cannot
    // share this without introducing a new cross-protocol project dependency.
    public static LocalAddressSelection SelectLocalAddressForSubnet(
        IPAddress radioIp,
        IEnumerable<LocalIpv4Address> localAddresses,
        IPAddress? routeProbeAddress,
        IPAddress? reachableAddress = null)
    {
        var matches = FindSubnetMatches(radioIp, localAddresses);

        int chosenIndex = -1;
        var rule = LocalAddressSelectionRule.None;

        // 1. A well-formed reply received on a candidate-bound socket proves
        // reachability on that link and outranks routing-table heuristics.
        if (ShouldProbeReachability(matches.Count) &&
            reachableAddress?.AddressFamily == AddressFamily.InterNetwork &&
            !reachableAddress.Equals(IPAddress.Any))
        {
            chosenIndex = matches.FindIndex(candidate =>
                candidate.Address.Equals(reachableAddress));
            if (chosenIndex >= 0) rule = LocalAddressSelectionRule.Reachability;
        }

        // 2. Prefer source/egress agreement regardless of NIC type. This does
        // not reopen #1039: when binding utun produced EHOSTUNREACH, the kernel's
        // route was the physical NIC, so the probe answer still selects physical.
        if (chosenIndex < 0 &&
            routeProbeAddress?.AddressFamily == AddressFamily.InterNetwork &&
            !routeProbeAddress.Equals(IPAddress.Any))
        {
            chosenIndex = matches.FindIndex(candidate =>
                candidate.Address.Equals(routeProbeAddress));
            if (chosenIndex >= 0) rule = LocalAddressSelectionRule.RouteAgreement;
        }

        // 3. Preserve the established first-physical-subnet-match behavior
        // when the route probe has no usable matching answer.
        if (chosenIndex < 0)
        {
            chosenIndex = matches.FindIndex(candidate => !candidate.IsTunnel);
            if (chosenIndex >= 0) rule = LocalAddressSelectionRule.FirstPhysical;
        }

        // 4. Preserve radio-over-VPN support when only a tunnel matches.
        if (chosenIndex < 0)
        {
            chosenIndex = matches.FindIndex(candidate => candidate.IsTunnel);
            if (chosenIndex >= 0) rule = LocalAddressSelectionRule.TunnelFallback;
        }

        // 5. No subnet match: the caller retains its IPAddress.Any fallback.
        LocalIpv4Address? chosen = chosenIndex >= 0 ? matches[chosenIndex] : null;
        var rejected = matches
            .Where(candidate => chosen is null ||
                !NicIdentity(candidate).Equals(NicIdentity(chosen.Value), StringComparison.Ordinal))
            .Select(LocalAddressSelection.DisplayNic)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new LocalAddressSelection(
            chosen,
            matches.Count,
            rejected,
            rule,
            chosen is not null && routeProbeAddress is not null &&
                chosen.Value.Address.Equals(routeProbeAddress),
            ShouldProbeReachability(matches.Count) &&
                reachableAddress is not null && routeProbeAddress is not null &&
                !reachableAddress.Equals(routeProbeAddress));
    }

    public static List<LocalIpv4Address> FindSubnetMatches(
        IPAddress radioIp,
        IEnumerable<LocalIpv4Address> localAddresses)
    {
        var matches = new List<LocalIpv4Address>();
        if (radioIp.AddressFamily != AddressFamily.InterNetwork) return matches;
        foreach (var local in localAddresses)
        {
            if (local.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (local.Mask.AddressFamily != AddressFamily.InterNetwork) continue;
            if (local.Mask.Equals(IPAddress.Any)) continue;
            if (SameSubnet(radioIp, local.Address, local.Mask)) matches.Add(local);
        }
        return matches;
    }

    public static bool ShouldProbeReachability(int subnetMatchCount) => subnetMatchCount > 1;

    public static IReadOnlyList<LocalIpv4Address> GetReachabilityProbeCandidates(
        IEnumerable<LocalIpv4Address> matches,
        int maxCandidates = MaxReachabilityProbeCandidates)
    {
        if (maxCandidates <= 0) return [];
        // Apply the fan-out cap only after the stable physical/tunnel partition,
        // so virtual adapters cannot crowd the radio-facing NIC out of the wave.
        return matches
            .OrderBy(candidate => candidate.IsTunnel ? 1 : 0)
            .Take(maxCandidates)
            .ToArray();
    }

    public static IPAddress? SelectReachableAddress(
        IReadOnlyList<LocalIpv4Address> candidates,
        IEnumerable<IPAddress> responderAddresses,
        IPAddress? routeProbeAddress)
    {
        var responders = responderAddresses.ToHashSet();
        // Reply enumeration reflects scheduler timing, not link suitability.
        // Prefer a responding route candidate, then stable candidate order.
        if (routeProbeAddress is not null && responders.Contains(routeProbeAddress) &&
            candidates.Any(candidate => candidate.Address.Equals(routeProbeAddress)))
        {
            return routeProbeAddress;
        }

        foreach (var candidate in candidates)
        {
            if (responders.Contains(candidate.Address)) return candidate.Address;
        }

        return null;
    }

    private static string NicIdentity(LocalIpv4Address candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.NicIdentity)) return candidate.NicIdentity;
        return $"{candidate.NicDescription}\u001f{candidate.Ipv4InterfaceIndex?.ToString() ?? "unknown"}";
    }

    public static IPAddress? FindLocalAddressForSubnet(
        IPAddress radioIp,
        IEnumerable<LocalIpv4Address> localAddresses,
        IPAddress? routeProbeAddress = null,
        IPAddress? reachableAddress = null)
        => SelectLocalAddressForSubnet(radioIp, localAddresses, routeProbeAddress, reachableAddress).Address;

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
