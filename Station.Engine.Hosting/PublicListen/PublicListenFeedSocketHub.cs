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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;

namespace Zeus.Server.PublicListen;

/// <summary>Guest receiver capacity as the engine sees it (feed socket status and REST).</summary>
public sealed record PublicListenGuestCapsDto(bool Supported, int Capacity, int MaxSlots);

/// <summary>
/// Virtual receiver capacity and the operator RX1 span as the engine sees it
/// (feed socket status and REST). <see cref="MinHz"/>/<see cref="MaxHz"/> are 0
/// while there is no span.
/// </summary>
public sealed record PublicListenVrxCapsDto(bool Supported, int Capacity, int MaxSlots, long MinHz, long MaxHz, string[]? NrModes = null);

/// <summary>Body of <c>GET /api/station/public-listen/status</c>.</summary>
public sealed record PublicListenEngineStatusDto(
    bool WidebandCapable,
    PublicBandConditions BandConditions,
    PublicListenGuestCapsDto Guest,
    int FeedSockets,
    int Demand,
    PublicListenVrxCapsDto? Vrx = null);

/// <summary>
/// Engine end of the Zeus Link listener feed (docs/designs/public-listening.md,
/// "Engine ↔ host seam"): the loopback <c>/ws?feed=listener</c> socket the
/// product host uses in place of an in-process <see cref="IPublicListenFeedSink"/>,
/// plus the state behind the <c>/api/station/public-listen/*</c> routes.
///
/// A feed socket is NOT an operator client. It never enters
/// <c>StreamingHub._clients</c> (this class holds no hub reference at all), so
/// it can neither see operator frames nor affect <c>LastClientDisconnected</c>.
/// On it:
/// <list type="bullet">
///   <item>binary engine → host: only serialized 0x41/0x42/0x43 feed frames,
///   through a bounded drop-oldest queue so a stalled host never blocks the DSP;</item>
///   <item>text engine → host: small JSON control messages —
///   <c>{t:"status"}</c> (wideband capability and guest capacity, on connect and
///   on change), <c>{t:"guest-leases", event:"reclaimed"|"failed", leases}</c>
///   (the guest pool's eviction events, pushed at once),
///   <c>{t:"guest-signal", signals}</c> (guest S-meter readings, 2 Hz while
///   leases exist), <c>{t:"vrx-ended", receivers:[{slot, generation, reason}]}</c>
///   (virtual receivers the engine stopped, pushed at once) and
///   <c>{t:"vrx-signal", signals:[{slot, generation, dbm, snrDb}]}</c> (2 Hz
///   while virtual receivers exist). Pushing them here avoids a new wire MsgType;</item>
///   <item>host → engine: ONLY <c>[0x28][flags]</c> (<see cref="MsgType.ListenerFeedDemand"/>).
///   Everything else is ignored; an oversized message closes the socket.</item>
/// </list>
/// Each socket owns its own demand; the feed gets the union, and a socket's
/// demand is cleared when it disconnects. At most <see cref="MaxSockets"/>
/// sockets are served at once.
/// </summary>
public sealed class PublicListenFeedSocketHub
{
    public const int MaxSockets = 2;
    internal const int MaxInboundMessageBytes = 64;
    internal const int FrameQueueCapacity = 256;
    internal const int ControlQueueCapacity = 64;
    internal static readonly TimeSpan StatusTick = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IPublicListenFeed _feed;
    private readonly IGuestReceiverPool? _pool;
    private readonly IVirtualReceiverPool? _vrx;
    private readonly Func<PublicBandConditions> _bandConditions;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<long, ListenerFeedDemandFlags> _socketDemand = [];
    private IReadOnlyList<GuestLeaseRequest> _leases = [];
    private IReadOnlyList<VrxRequest> _vrxReceivers = [];
    private long _nextId;
    private long _ignoredInbound;

    public PublicListenFeedSocketHub(
        IPublicListenFeed feed,
        IGuestReceiverPool? pool = null,
        Func<PublicBandConditions>? bandConditions = null,
        ILogger<PublicListenFeedSocketHub>? log = null,
        IVirtualReceiverPool? vrx = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _pool = pool;
        _vrx = vrx;
        _bandConditions = bandConditions ?? (() => PublicBandConditions.Empty);
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public int ActiveSockets { get { lock (_gate) return _socketDemand.Count; } }

    internal long IgnoredInboundMessages => Interlocked.Read(ref _ignoredInbound);

    // ---- REST-backed state -------------------------------------------------

    public PublicListenGuestCapsDto GuestCaps() =>
        _pool is null
            ? new PublicListenGuestCapsDto(false, 0, PublicStreamId.MaxGuestSlots)
            : new PublicListenGuestCapsDto(_pool.Supported, _pool.Capacity, _pool.MaxSlots);

    public PublicListenEngineStatusDto Status()
    {
        PublicBandConditions bands;
        try { bands = _bandConditions() ?? PublicBandConditions.Empty; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "public-listen feed: band conditions unavailable");
            bands = PublicBandConditions.Empty;
        }
        return new PublicListenEngineStatusDto(
            _feed.WidebandCapable, bands, GuestCaps(), ActiveSockets, (int)_feed.Demand, VrxCaps());
    }

    public PublicListenVrxCapsDto VrxCaps()
    {
        if (_vrx is null) return new PublicListenVrxCapsDto(false, 0, PublicStreamId.MaxVrxSlots, 0, 0);
        var span = _vrx.Span;
        return new PublicListenVrxCapsDto(_vrx.Supported, _vrx.Capacity, _vrx.MaxSlots, span.MinHz, span.MaxHz, _vrx.NrModes.Select(mode => mode.ToString()).ToArray());
    }

    public void SetVrxPoolSize(int size) => _vrx?.SetPoolSize(size);

    public void SetVrxReceivers(IReadOnlyList<VrxRequest> receivers)
    {
        ArgumentNullException.ThrowIfNull(receivers);
        lock (_gate)
        {
            _vrxReceivers = receivers.ToArray();
            _vrx?.SetReceivers(receivers);
        }
    }

    /// <summary>Host settings the engine needs: spectrum shape and the band-survey switch.</summary>
    public void Configure(int? spectrumFps, int? spectrumBins, bool? bandSurvey)
    {
        if (spectrumFps is not null || spectrumBins is not null)
        {
            _feed.Configure(
                spectrumFps ?? PublicListenFeed.DefaultSpectrumFps,
                spectrumBins ?? PublicListenFeed.DefaultSpectrumBins);
        }
        if (bandSurvey is { } survey) _feed.SetBandSurvey(survey);
    }

    public void SetPoolSize(int size) => _pool?.SetPoolSize(size);

    public void SetLeases(IReadOnlyList<GuestLeaseRequest> leases)
    {
        ArgumentNullException.ThrowIfNull(leases);
        lock (_gate)
        {
            _leases = leases.ToArray();
            _pool?.SetLeases(leases);
        }
    }

    public bool TryGetSignalDbm(int slot, long generation, out double dbm)
    {
        dbm = double.NaN;
        return _pool is not null && _pool.TryGetSignalDbm(slot, generation, out dbm);
    }

    // ---- per-socket demand -------------------------------------------------

    internal bool TryOpen(out long id)
    {
        lock (_gate)
        {
            if (_socketDemand.Count >= MaxSockets)
            {
                id = 0;
                return false;
            }
            id = ++_nextId;
            _socketDemand[id] = ListenerFeedDemandFlags.None;
            return true;
        }
    }

    internal void SetSocketDemand(long id, ListenerFeedDemandFlags demand)
    {
        lock (_gate)
        {
            if (!_socketDemand.ContainsKey(id)) return;
            _socketDemand[id] = demand;
            PushDemandLocked();
        }
    }

    internal void CloseSocket(long id)
    {
        lock (_gate)
        {
            if (!_socketDemand.Remove(id)) return;
            PushDemandLocked();
            if (_socketDemand.Count == 0)
            {
                // Only release receiver state this socket seam owns. A desktop
                // host may use the same pools directly without any feed sockets.
                if (_leases.Count > 0)
                {
                    _leases = [];
                    _pool?.SetLeases([]);
                }
                if (_vrxReceivers.Count > 0)
                {
                    _vrxReceivers = [];
                    _vrx?.SetReceivers([]);
                }
            }
        }
    }

    private void PushDemandLocked()
    {
        var union = ListenerFeedDemandFlags.None;
        foreach (var demand in _socketDemand.Values) union |= demand;
        _feed.SetDemand(union);
    }

    /// <summary>Parse one host → engine message: only a 2-byte 0x28 demand frame is accepted.</summary>
    internal static bool TryParseDemand(ReadOnlySpan<byte> message, out ListenerFeedDemandFlags demand)
    {
        demand = ListenerFeedDemandFlags.None;
        if (message.Length != 2 || message[0] != (byte)MsgType.ListenerFeedDemand) return false;
        demand = (ListenerFeedDemandFlags)message[1]
            & (ListenerFeedDemandFlags.OperatorView | ListenerFeedDemandFlags.ListenAlong | ListenerFeedDemandFlags.Wideband);
        return true;
    }

    // ---- socket session ------------------------------------------------------

    /// <summary>Serve one accepted feed socket until either side closes.</summary>
    public async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (!TryOpen(out var id))
        {
            _log.LogWarning("public-listen feed: refused a feed socket ({Max} already open)", MaxSockets);
            try
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation, "listener feed busy", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            return;
        }

        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(FrameQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var control = Channel.CreateBounded<string>(new BoundedChannelOptions(ControlQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var statusDirty = 1;
        void OnReclaimed(IReadOnlyList<GuestLeaseRequest> leases) =>
            control.Writer.TryWrite(LeaseEventJson("reclaimed", leases));
        void OnFailed(IReadOnlyList<GuestLeaseRequest> leases) =>
            control.Writer.TryWrite(LeaseEventJson("failed", leases));
        void OnCapacity() => Volatile.Write(ref statusDirty, 1);
        void OnVrxEnded(IReadOnlyList<VrxEnded> ended) =>
            control.Writer.TryWrite(VrxEndedJson(ended));

        IDisposable? registration = null;
        if (_pool is not null)
        {
            _pool.LeasesReclaimed += OnReclaimed;
            _pool.LeasesFailed += OnFailed;
            _pool.CapacityChanged += OnCapacity;
        }
        if (_vrx is not null)
        {
            _vrx.ReceiversEnded += OnVrxEnded;
            _vrx.CapacityChanged += OnCapacity;
        }
        _log.LogInformation("public-listen feed: host feed socket {Id} connected", id);
        try
        {
            registration = _feed.AttachSink(new ChannelSink(frames.Writer));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var receive = ReceiveLoopAsync(socket, id, linked.Token);
            var send = SendLoopAsync(socket, frames.Reader, control.Reader, () => Interlocked.Exchange(ref statusDirty, 0) == 1, linked.Token);
            await Task.WhenAny(receive, send).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(receive, send).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        }
        finally
        {
            registration?.Dispose();
            if (_pool is not null)
            {
                _pool.LeasesReclaimed -= OnReclaimed;
                _pool.LeasesFailed -= OnFailed;
                _pool.CapacityChanged -= OnCapacity;
            }
            if (_vrx is not null)
            {
                _vrx.ReceiversEnded -= OnVrxEnded;
                _vrx.CapacityChanged -= OnCapacity;
            }
            frames.Writer.TryComplete();
            control.Writer.TryComplete();
            // The host is gone: nothing it asked for may keep running.
            CloseSocket(id);
            _log.LogInformation("public-listen feed: host feed socket {Id} closed", id);
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, long id, CancellationToken ct)
    {
        var buffer = new byte[MaxInboundMessageBytes];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            int count = 0;
            WebSocketReceiveResult result;
            do
            {
                if (count >= buffer.Length)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig, "feed control message too large", ct).ConfigureAwait(false);
                    return;
                }
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), ct)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
                    return;
                }
                count += result.Count;
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary
                && TryParseDemand(buffer.AsSpan(0, count), out var demand))
            {
                SetSocketDemand(id, demand);
                continue;
            }
            if (Interlocked.Increment(ref _ignoredInbound) == 1)
                _log.LogDebug("public-listen feed: ignored a non-demand message on the feed socket");
        }
    }

    private async Task SendLoopAsync(
        WebSocket socket,
        ChannelReader<byte[]> frames,
        ChannelReader<string> control,
        Func<bool> takeStatusDirty,
        CancellationToken ct)
    {
        string? lastStatus = null;
        var nextTick = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var now = DateTimeOffset.UtcNow;
            bool dirty = takeStatusDirty();
            if (dirty || now >= nextTick)
            {
                var status = StatusJson();
                if (!string.Equals(status, lastStatus, StringComparison.Ordinal))
                {
                    await SendTextAsync(socket, status, ct).ConfigureAwait(false);
                    lastStatus = status;
                }
                if (now >= nextTick)
                {
                    if (SignalJson() is { } signal) await SendTextAsync(socket, signal, ct).ConfigureAwait(false);
                    if (VrxSignalJson() is { } vrxSignal) await SendTextAsync(socket, vrxSignal, ct).ConfigureAwait(false);
                    nextTick = now + StatusTick;
                }
            }

            while (control.TryRead(out var message))
                await SendTextAsync(socket, message, ct).ConfigureAwait(false);

            if (frames.TryRead(out var frame))
            {
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                continue;
            }

            var wait = nextTick - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            var frameReady = frames.WaitToReadAsync(ct).AsTask();
            var controlReady = control.WaitToReadAsync(ct).AsTask();
            var completed = await Task.WhenAny(frameReady, controlReady, Task.Delay(wait, ct)).ConfigureAwait(false);
            // A completed channel (false) means the session is being torn down.
            if (completed == frameReady && !await frameReady.ConfigureAwait(false)) return;
            if (completed == controlReady && !await controlReady.ConfigureAwait(false)) return;
        }
    }

    private static Task SendTextAsync(WebSocket socket, string message, CancellationToken ct) =>
        socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct);

    internal string StatusJson() =>
        JsonSerializer.Serialize(new
        {
            t = "status",
            widebandCapable = _feed.WidebandCapable,
            guest = GuestCaps(),
            vrx = VrxCaps(),
        }, JsonOptions);

    internal string? VrxSignalJson()
    {
        IReadOnlyList<VrxRequest> receivers;
        lock (_gate) receivers = _vrxReceivers;
        if (_vrx is null || receivers.Count == 0) return null;
        var signals = new List<object>(receivers.Count);
        foreach (var receiver in receivers)
        {
            if (_vrx.TryGetMeter(receiver.Slot, receiver.Generation, out var dbm, out var snr) && double.IsFinite(dbm))
            {
                signals.Add(new
                {
                    slot = receiver.Slot,
                    generation = receiver.Generation,
                    dbm = Math.Round(dbm, 1),
                    snrDb = double.IsFinite(snr) ? Math.Round(snr, 1) : (double?)null,
                });
            }
        }
        // A complete snapshot, including no readings during transmit hold.
        // The product must drop its previous meter instead of retaining it.
        return JsonSerializer.Serialize(new { t = "vrx-signal", signals }, JsonOptions);
    }

    internal static string VrxEndedJson(IReadOnlyList<VrxEnded> ended) =>
        JsonSerializer.Serialize(new
        {
            t = "vrx-ended",
            receivers = ended.Select(e => new { slot = e.Slot, generation = e.Generation, reason = e.Reason }).ToArray(),
        }, JsonOptions);

    internal string? SignalJson()
    {
        IReadOnlyList<GuestLeaseRequest> leases;
        lock (_gate) leases = _leases;
        if (_pool is null || leases.Count == 0) return null;
        var signals = new List<object>(leases.Count);
        foreach (var lease in leases)
        {
            if (_pool.TryGetSignalDbm(lease.Slot, lease.Generation, out var dbm) && double.IsFinite(dbm))
                signals.Add(new { slot = lease.Slot, generation = lease.Generation, dbm = Math.Round(dbm, 1) });
        }
        return signals.Count == 0
            ? null
            : JsonSerializer.Serialize(new { t = "guest-signal", signals }, JsonOptions);
    }

    internal static string LeaseEventJson(string kind, IReadOnlyList<GuestLeaseRequest> leases) =>
        JsonSerializer.Serialize(new
        {
            t = "guest-leases",
            @event = kind,
            leases = leases.Select(l => new { slot = l.Slot, generation = l.Generation }).ToArray(),
        }, JsonOptions);

    /// <summary>Copies each shared feed frame into the socket's drop-oldest queue; never blocks.</summary>
    private sealed class ChannelSink(ChannelWriter<byte[]> writer) : IPublicListenFeedSink
    {
        public void OnFeedFrame(ReadOnlyMemory<byte> frame)
        {
            if (frame.IsEmpty) return;
            byte type = frame.Span[0];
            if (type is not ((byte)MsgType.PublicSpectrum or (byte)MsgType.PublicStationStatus or (byte)MsgType.PublicAudioPcm))
                return;
            writer.TryWrite(frame.ToArray());
        }
    }
}
