// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA),
//                    Christian Suarez (N9WAR), and contributors.

using System.Net;
using Zeus.Contracts;
using Zeus.Server.Cat;
using Zeus.Server.Tci;

namespace Zeus.Server;

/// <summary>
/// Detects the network CAT and TCI servers configured onto the same TCP port.
/// CAT binds its raw listener as a hosted service, which starts before Kestrel
/// binds the TCI endpoint, so a shared port made Kestrel fail and took the
/// whole engine down on every start. The operator could not reach Settings to
/// undo it. TCI therefore yields at startup and both panels warn while the
/// pending configuration collides.
/// </summary>
internal static class StationControlPortConflict
{
    /// <summary>
    /// The CAT settings <see cref="CatServer"/> will bind with: the Cat
    /// configuration section, then the persisted runtime override, then the
    /// receive-only switch, exactly as each host configures CatOptions.
    /// </summary>
    internal static CatOptions EffectiveCat(
        IConfiguration catSection,
        CatRuntimeConfig? persisted,
        bool receiveOnly)
    {
        ArgumentNullException.ThrowIfNull(catSection);
        var cat = catSection.Get<CatOptions>() ?? new CatOptions();
        if (persisted is not null)
        {
            cat.Enabled = persisted.Enabled;
            cat.BindAddress = persisted.BindAddress;
            cat.Port = persisted.Port;
        }
        if (receiveOnly)
            cat.Enabled = false;
        return cat;
    }

    internal static bool Collides(
        bool tciEnabled, string tciBindAddress, int tciPort,
        bool catEnabled, string catBindAddress, int catPort)
    {
        if (!tciEnabled || !catEnabled || tciPort != catPort)
            return false;
        var tci = TciSockets(tciBindAddress);
        var cat = CatSockets(catBindAddress);
        return tci.Any(t => cat.Any(c => Overlap(t, c)));
    }

    /// <summary>
    /// Leaves the TCI listener inactive when CAT will claim its port, so the
    /// engine still starts and Settings > TCI explains why TCI is off. A CAT
    /// address that is not on this computer never binds, so TCI keeps the port.
    /// </summary>
    internal static TciListenerBinding YieldTciToCat(
        TciListenerBinding tci,
        CatOptions cat,
        Func<IReadOnlyCollection<IPAddress>>? getLocalAddresses = null)
    {
        ArgumentNullException.ThrowIfNull(tci);
        ArgumentNullException.ThrowIfNull(cat);
        if (!tci.IsActive
            || !Collides(true, tci.BindAddress, tci.Port, cat.Enabled, cat.BindAddress, cat.Port)
            || !CatCanBind(cat.BindAddress, getLocalAddresses))
            return tci;

        return tci with
        {
            IsActive = false,
            Address = null,
            Error = $"Port {tci.Port} is also set for CAT. TCI listener is inactive; "
                + "choose a different port in Settings > TCI or Settings > CAT.",
            YieldedToCat = true,
        };
    }

    internal static string PendingCollisionMessage(string otherServer, int port) =>
        $"{otherServer} is also set to port {port}. Choose a different port before restarting, "
        + "or TCI will stay off.";

    internal static string CollisionClearedMessage(int port) =>
        $"TCI and CAT no longer share port {port}. Restart Zeus to apply.";

    // Kestrel: ListenAnyIP and Listen(IPv6Any) open dual-mode sockets, and
    // ListenLocalhost binds both loopbacks (TciHostingExtensions).
    private static IPAddress[] TciSockets(string? bindAddress)
    {
        var trimmed = Normalize(bindAddress);
        if (IsLocalhost(trimmed))
            return [IPAddress.Loopback, IPAddress.IPv6Loopback];
        if (!PortBindDiagnostics.TryResolveBindAddress(trimmed, out var ip) || ip is null)
            return [];
        return IsWildcard(ip) ? [IPAddress.Any, IPAddress.IPv6Any] : [ip];
    }

    // CatServer: a TcpListener on the parsed address; "localhost" is IPv4
    // loopback only. An IPv6 wildcard is treated as covering IPv4 too, the
    // cautious reading where the OS decides dual-mode.
    private static IPAddress[] CatSockets(string? bindAddress)
    {
        var trimmed = Normalize(bindAddress);
        if (!PortBindDiagnostics.TryResolveBindAddress(trimmed, out var ip) || ip is null)
            return [];
        return ip.Equals(IPAddress.IPv6Any) ? [IPAddress.Any, IPAddress.IPv6Any] : [ip];
    }

    private static bool Overlap(IPAddress a, IPAddress b) =>
        a.AddressFamily == b.AddressFamily
        && (IsWildcard(a) || IsWildcard(b) || a.GetAddressBytes().SequenceEqual(b.GetAddressBytes()));

    private static bool CatCanBind(
        string? bindAddress,
        Func<IReadOnlyCollection<IPAddress>>? getLocalAddresses)
    {
        var trimmed = Normalize(bindAddress);
        if (!PortBindDiagnostics.TryResolveBindAddress(trimmed, out var ip) || ip is null)
            return false;
        if (IsWildcard(ip) || IPAddress.IsLoopback(ip))
            return true;
        try
        {
            return TciHostingExtensions.IsLocalOrLoopback(
                ip, (getLocalAddresses ?? TciHostingExtensions.GetLocalAddresses)());
        }
        catch (Exception)
        {
            // Unknown adapters: assume CAT binds, so TCI yields and the
            // engine still starts.
            return true;
        }
    }

    private static string Normalize(string? bindAddress) =>
        string.IsNullOrWhiteSpace(bindAddress) ? "" : bindAddress.Trim();

    private static bool IsWildcard(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    private static bool IsLocalhost(string bindAddress) =>
        string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase);
}
