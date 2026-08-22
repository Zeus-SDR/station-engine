// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Zeus.Protocol1.Discovery;

public sealed class RadioDiscoveryService : IRadioDiscovery
{
    private const int HpsdrPort = 1024;
    private const int DiscoveryPacketLength = 63;
    private const int ReceiveBufferSize = 2048;
    internal const int SendAttempts = 3;
    internal static readonly TimeSpan SendGap = TimeSpan.FromMilliseconds(50);

    private readonly ILogger<RadioDiscoveryService> _log;

    public RadioDiscoveryService(ILogger<RadioDiscoveryService> log)
    {
        _log = log;
    }

    public async Task<IReadOnlyList<DiscoveredRadio>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var socketPlans = GetSocketPlans();

        _log.LogDebug("discovery.start interfaces={Count}", socketPlans.Count);
        foreach (var plan in socketPlans)
        {
            _log.LogDebug("discovery.interface local={Local} broadcast={Broadcast}", plan.BindAddress, plan.TargetAddress);
        }

        var sockets = CreateDiscoverySockets(socketPlans);
        if (sockets.Count == 0)
            return [];

        try
        {
            var packet = BuildDiscoveryPacket();
            await SendProbesAsync(sockets, packet, ct).ConfigureAwait(false);

            var byMac = new Dictionary<PhysicalAddress, DiscoveredRadio>();
            var receiveTasks = sockets
                .Select(s => ReceiveRepliesAsync(s, timeout, ct))
                .ToArray();
            var replies = await Task.WhenAll(receiveTasks).ConfigureAwait(false);
            foreach (var batch in replies)
            {
                foreach (var radio in batch)
                {
                    byMac[radio.Mac] = radio;
                }
            }

            ct.ThrowIfCancellationRequested();

            return byMac.Values.OrderBy(IpSortKey).ToList();
        }
        finally
        {
            foreach (var socket in sockets)
                socket.Socket.Dispose();
        }
    }

    private async Task SendProbesAsync(
        IReadOnlyList<DiscoverySocket> sockets,
        ReadOnlyMemory<byte> packet,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < SendAttempts; attempt++)
        {
            foreach (var socket in sockets)
            {
                var endpoint = new IPEndPoint(socket.Plan.TargetAddress, HpsdrPort);
                try
                {
                    await socket.Socket.SendToAsync(packet, SocketFlags.None, endpoint, ct).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    _log.LogDebug(
                        ex,
                        "discovery.send.skip local={Local} broadcast={Broadcast}",
                        socket.Plan.BindAddress,
                        socket.Plan.TargetAddress);
                }
            }

            if (attempt < SendAttempts - 1)
            {
                try
                {
                    await Task.Delay(SendGap, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task<IReadOnlyList<DiscoveredRadio>> ReceiveRepliesAsync(
        DiscoverySocket socket,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var radios = new List<DiscoveredRadio>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var receiveBuffer = new byte[ReceiveBufferSize];
        var any = new IPEndPoint(IPAddress.Any, 0);

        while (!timeoutCts.IsCancellationRequested)
        {
            SocketReceiveFromResult res;
            try
            {
                res = await socket.Socket.ReceiveFromAsync(
                    receiveBuffer,
                    SocketFlags.None,
                    any,
                    timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows WSAECONNRESET (10054): stray ICMP port-unreachable.
                // Keep collecting replies until the timeout fires.
                continue;
            }
            catch (SocketException ex)
            {
                _log.LogWarning(ex, "discovery.socket.error local={Local}", socket.Plan.BindAddress);
                break;
            }

            var fromIp = ((IPEndPoint)res.RemoteEndPoint).Address;
            var slice = new ReadOnlySpan<byte>(receiveBuffer, 0, res.ReceivedBytes);

            if (!ReplyParser.TryParse(slice, fromIp, out var radio))
            {
                _log.LogDebug(
                    "discovery.reply.invalid from={Ip} len={Len}",
                    fromIp,
                    res.ReceivedBytes);
                continue;
            }

            radios.Add(radio);
            _log.LogInformation(
                "discovery.reply from={Ip} board={Board} mac={Mac} fw={Firmware}",
                radio.Ip,
                radio.Board,
                radio.Mac,
                radio.FirmwareString);
        }

        return radios;
    }

    private IReadOnlyList<DiscoverySocketPlan> GetSocketPlans()
    {
        var interfaces = new List<DiscoveryInterfaceDescriptor>();

        try
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var iface in networkInterfaces)
            {
                // Skip inactive, loopback, and tunnel interfaces
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                if (NetworkAddressSelection.IsTunnelInterface(iface.Name, iface.NetworkInterfaceType))
                    continue;
                var ipProps = iface.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    // Only IPv4 addresses
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    // Calculate broadcast address from IP and netmask
                    var ip = unicast.Address;
                    var mask = unicast.IPv4Mask;

                    // Skip if no valid netmask
                    if (mask == null || mask.Equals(IPAddress.Any))
                        continue;

                    interfaces.Add(new DiscoveryInterfaceDescriptor(
                        iface.Name,
                        iface.NetworkInterfaceType,
                        ip,
                        mask));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "discovery.enumerate.error");
        }

        var plans = BuildSocketPlan(interfaces);
        if (plans.Count == 1 && plans[0].BindAddress.Equals(IPAddress.Any))
        {
            _log.LogWarning("discovery.enumerate.empty, using global broadcast");
        }

        return plans;
    }

    internal static IReadOnlyList<DiscoverySocketPlan> BuildSocketPlan(
        IEnumerable<DiscoveryInterfaceDescriptor> interfaces)
    {
        var plans = new List<DiscoverySocketPlan>();
        foreach (var iface in interfaces)
        {
            if (NetworkAddressSelection.IsTunnelInterface(iface.Name, iface.Type)) continue;
            if (iface.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (iface.Mask.AddressFamily != AddressFamily.InterNetwork) continue;
            if (iface.Mask.Equals(IPAddress.Any)) continue;
            plans.Add(new DiscoverySocketPlan(iface.Address, BroadcastFor(iface.Address, iface.Mask)));
        }

        plans.Add(new DiscoverySocketPlan(IPAddress.Any, IPAddress.Broadcast));

        return plans;
    }

    private IReadOnlyList<DiscoverySocket> CreateDiscoverySockets(IReadOnlyList<DiscoverySocketPlan> plans)
    {
        var sockets = new List<DiscoverySocket>();
        foreach (var plan in plans)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                EnableBroadcast = true,
            };
            DisableUdpConnReset(socket);
            try
            {
                socket.Bind(new IPEndPoint(plan.BindAddress, 0));
                sockets.Add(new DiscoverySocket(socket, plan));
            }
            catch (SocketException ex)
            {
                _log.LogDebug(
                    ex,
                    "discovery.bind.skip local={Local} broadcast={Broadcast}",
                    plan.BindAddress,
                    plan.TargetAddress);
                socket.Dispose();
            }
        }

        if (sockets.Count == 0 && plans.Any(p => !p.BindAddress.Equals(IPAddress.Any)))
        {
            var fallback = new DiscoverySocketPlan(IPAddress.Any, IPAddress.Broadcast);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                EnableBroadcast = true,
            };
            DisableUdpConnReset(socket);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                sockets.Add(new DiscoverySocket(socket, fallback));
                _log.LogWarning("discovery.bind.all_failed, using global broadcast");
            }
            catch (SocketException ex)
            {
                _log.LogWarning(ex, "discovery.bind.fallback_failed");
                socket.Dispose();
            }
        }

        return sockets;
    }

    private static IPAddress BroadcastFor(IPAddress address, IPAddress mask)
    {
        var ipBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var bcastBytes = new byte[4];

        for (var i = 0; i < 4; i++)
            bcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);

        return new IPAddress(bcastBytes);
    }

    internal static byte[] BuildDiscoveryPacket()
    {
        var buf = new byte[DiscoveryPacketLength];
        buf[0] = 0xEF;
        buf[1] = 0xFE;
        buf[2] = 0x02;
        return buf;
    }

    private static uint IpSortKey(DiscoveredRadio r)
    {
        var bytes = r.Ip.GetAddressBytes();
        if (bytes.Length != 4) return uint.MaxValue;
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    // Private copy — Protocol1 is a separate assembly from Protocol2.
    // Windows surfaces ICMP port-unreachable as SocketError 10054 on the next recv;
    // this ioctl disables that behaviour at the OS level.
    private const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

    private static void DisableUdpConnReset(Socket s)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); }
        catch (SocketException) { /* best effort */ }
    }

    internal readonly record struct DiscoveryInterfaceDescriptor(
        string Name,
        NetworkInterfaceType Type,
        IPAddress Address,
        IPAddress Mask);

    internal readonly record struct DiscoverySocketPlan(IPAddress BindAddress, IPAddress TargetAddress);

    private sealed record DiscoverySocket(Socket Socket, DiscoverySocketPlan Plan);
}
