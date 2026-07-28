// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Station.AudioRing;
using Zeus.Contracts;

namespace Zeus.Server;

public delegate void ProductPluginTxAudioSink(ReadOnlySpan<float> block48k);
public delegate bool ProductPluginMonitorAudioSink(ReadOnlySpan<float> block48k);

/// <summary>
/// Engine-owned Wave-P2 audio/keying boundary. Every shared-memory resource is
/// lease-scoped and inert until its liveness request is attached. Capture is a
/// nonblocking copy; injection and keying additionally require an in-memory
/// operator arm that is destroyed with the lease.
/// </summary>
public sealed class ProductPluginAudioPort : IDisposable
{
    internal static readonly TimeSpan PendingLeaseLifetime = TimeSpan.FromSeconds(5);
    private static readonly long BlockTicks =
        Stopwatch.Frequency * AudioRingProtocol.BlockPeriodMilliseconds / 1000L;
    private static readonly TimeSpan LeasePumpPeriod = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan LeaseHeartbeatPeriod = TimeSpan.FromMilliseconds(100);
    // How many consecutive delivery boundaries a keyed TX session may miss
    // before the port treats the producer as dead and revokes it. One missed
    // block is almost always scheduler jitter on a loaded machine, not a dead
    // producer (the one-block prefetch slack provably burns down a few
    // seconds into every transmission in the field: 2026-07-23 FT8 TX cut at
    // ~8 s, twice, then key-409s forever). While tolerated, the pump emits a
    // silence block per missed boundary so the keyed transmission keeps RF
    // continuity and the sequencer's one stage still airs in full. 12 blocks
    // = 240 ms absorbs real-world stalls yet still revokes a truly dead
    // producer within a quarter second.
    internal const int MaxToleratedUnderflowBlocks = 12;
    private static readonly float[] UnderflowSilenceBlock =
        new float[AudioRingProtocol.NominalSamplesPerBlock];

    private readonly object _gate = new();
    private readonly ILogger<ProductPluginAudioPort> _log;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly CaptureSession?[] _rxCapture = new CaptureSession?[WireContract.MaxReceivers];
    private CaptureSession? _txMicCapture;
    private InjectionSession? _localMonitorInjection;
    private InjectionSession? _txInjection;
    private InjectionSession? _keyedSession;
    private ProductPluginTxAudioSink? _txSink;
    private ProductPluginMonitorAudioSink? _monitorSink;
    private Action<bool>? _txActiveSink;
    private TxService? _subscribedTx;
    private long _droppedCaptureBlocks;
    private long _droppedInjectionBlocks;
    private long _invalidInjectionBlocks;
    private bool _disposed;

    public ProductPluginAudioPort(ILogger<ProductPluginAudioPort> log)
    {
        _log = log;
    }

    public ProductPluginAudioSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new ProductPluginAudioSnapshot(
                    CaptureLeases: _sessions.Values.Count(s => s is CaptureSession && s.IsLeased),
                    InjectionLeases: _sessions.Values.Count(s => s is InjectionSession && s.IsLeased),
                    Armed: _sessions.Values.OfType<InjectionSession>().Any(s => s.Armed),
                    Keyed: _keyedSession is not null,
                    DroppedCaptureBlocks: Interlocked.Read(ref _droppedCaptureBlocks),
                    DroppedInjectionBlocks: Interlocked.Read(ref _droppedInjectionBlocks),
                    InvalidInjectionBlocks: Interlocked.Read(ref _invalidInjectionBlocks));
            }
        }
    }

    public void ConfigureTxSink(ProductPluginTxAudioSink? sink) =>
        Interlocked.Exchange(ref _txSink, sink);

    public void ConfigureLocalMonitorSink(ProductPluginMonitorAudioSink? sink) =>
        Interlocked.Exchange(ref _monitorSink, sink);

    public void ConfigureTxActiveSink(Action<bool>? sink) =>
        Interlocked.Exchange(ref _txActiveSink, sink);

    public bool TryCreateCaptureAttachment(
        ProductPluginCaptureAttachRequest request,
        out ProductPluginAudioAttachResponse? response,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        response = null;
        if (!ValidateIdentity(request.Name, request.Version, out error)) return false;

        ProductPluginCaptureSource source;
        int receiver;
        if (string.Equals(request.Source, "rx-audio", StringComparison.Ordinal))
        {
            if (request.Receiver is not int requestedReceiver
                || requestedReceiver < 0
                || requestedReceiver >= WireContract.MaxReceivers)
            {
                error = $"rx-audio receiver must be between 0 and {WireContract.MaxReceivers - 1}";
                return false;
            }
            source = ProductPluginCaptureSource.RxAudio;
            receiver = requestedReceiver;
        }
        else if (string.Equals(request.Source, "tx-mic", StringComparison.Ordinal)
                 && request.Receiver is null)
        {
            source = ProductPluginCaptureSource.TxMic;
            receiver = -1;
        }
        else
        {
            error = "capture source must be rx-audio with a receiver or tx-mic without one";
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((source == ProductPluginCaptureSource.RxAudio && _rxCapture[receiver] is not null)
                || (source == ProductPluginCaptureSource.TxMic && _txMicCapture is not null))
            {
                error = "the requested capture source is already attached or negotiating";
                return false;
            }

            var session = new CaptureSession(
                Guid.NewGuid().ToString("N"), request.Name, request.Version,
                AudioRingOwner.Create(), source, receiver);
            AddPendingLocked(session);
            if (source == ProductPluginCaptureSource.RxAudio)
                Volatile.Write(ref _rxCapture[receiver], session);
            else
                Volatile.Write(ref _txMicCapture, session);
            response = new ProductPluginAudioAttachResponse(session.LeaseId, session.Owner.Endpoint);
            error = null;
            return true;
        }
    }

    public bool TryCreateInjectionAttachment(
        ProductPluginInjectionAttachRequest request,
        out ProductPluginAudioAttachResponse? response,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        response = null;
        if (!ValidateIdentity(request.Name, request.Version, out error)) return false;

        ProductPluginInjectionDestination destination;
        if (string.Equals(request.Destination, "local-monitor", StringComparison.Ordinal))
            destination = ProductPluginInjectionDestination.LocalMonitor;
        else if (string.Equals(request.Destination, "tx", StringComparison.Ordinal))
            destination = ProductPluginInjectionDestination.Tx;
        else
        {
            error = "injection destination must be local-monitor or tx";
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((destination == ProductPluginInjectionDestination.LocalMonitor
                    && _localMonitorInjection is not null)
                || (destination == ProductPluginInjectionDestination.Tx && _txInjection is not null))
            {
                error = "the requested injection destination is already attached or negotiating";
                return false;
            }

            var session = new InjectionSession(
                Guid.NewGuid().ToString("N"), request.Name, request.Version,
                AudioRingOwner.Create(), destination);
            AddPendingLocked(session);
            if (destination == ProductPluginInjectionDestination.LocalMonitor)
                Volatile.Write(ref _localMonitorInjection, session);
            else
                Volatile.Write(ref _txInjection, session);
            response = new ProductPluginAudioAttachResponse(session.LeaseId, session.Owner.Endpoint);
            error = null;
            return true;
        }
    }

    public async Task HoldLeaseAsync(string leaseId, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryActivateLease(leaseId, out var session))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        _log.LogInformation(
            "product-plugin audio attached kind={Kind} name={ProductName} session={SessionId}",
            session.Kind, session.Name, session.Owner.Endpoint.SessionId);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-store";
        var nextHeartbeat = Stopwatch.GetTimestamp();
        try
        {
            await WithLeaseWriteDeadlineAsync(
                context.RequestAborted,
                token => context.Response.StartAsync(token)).ConfigureAwait(false);
            while (!context.RequestAborted.IsCancellationRequested)
            {
                if (session is InjectionSession injection)
                    PumpInjection(injection, force: false);

                var now = Stopwatch.GetTimestamp();
                if (now >= nextHeartbeat)
                {
                    await WithLeaseWriteDeadlineAsync(
                        context.RequestAborted,
                        async token =>
                        {
                            await context.Response.WriteAsync(
                                "{\"attached\":true}\n", token).ConfigureAwait(false);
                            await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    nextHeartbeat = now + (long)(LeaseHeartbeatPeriod.TotalSeconds * Stopwatch.Frequency);
                }
                await Task.Delay(LeasePumpPeriod, context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            Detach(session, "station-protocol lease disconnected");
        }
    }

    private static async Task WithLeaseWriteDeadlineAsync(
        CancellationToken requestAborted,
        Func<CancellationToken, Task> write)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(AudioRingProtocol.BlockPeriodMilliseconds));
        await write(deadline.Token).ConfigureAwait(false);
    }

    public bool TrySetArm(
        ProductPluginArmRequest request,
        TxService tx,
        out ProductPluginAudioState response,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tx);
        TxService? release = null;
        lock (_gate)
        {
            if (!TryGetLeasedInjectionLocked(request.LeaseId, out var session))
            {
                response = CurrentStateLocked();
                error = "injection lease was not found";
                return false;
            }
            if (!ValidatePluginId(request.PluginId, out error))
            {
                response = CurrentStateLocked();
                return false;
            }

            if (request.Armed)
            {
                if (session.Armed && !string.Equals(session.PluginId, request.PluginId, StringComparison.Ordinal))
                {
                    response = CurrentStateLocked();
                    error = "the injection lease is armed by another plugin";
                    return false;
                }
                session.PluginId = request.PluginId;
                session.Armed = true;
                if (session.Destination == ProductPluginInjectionDestination.LocalMonitor)
                    session.NextDeliveryTicks = Stopwatch.GetTimestamp();
            }
            else
            {
                if (session.Armed
                    && !string.Equals(session.PluginId, request.PluginId, StringComparison.Ordinal))
                {
                    response = CurrentStateLocked();
                    error = "only the plugin that armed the lease may disarm it";
                    return false;
                }
                release = ClearKeyAndArmLocked(session);
            }
            response = CurrentStateLocked();
            error = null;
        }
        if (release is not null)
        {
            NotifyTxActive(false);
            ReleaseProductKey(release);
        }
        return true;
    }

    public bool TryRequestKey(
        ProductPluginKeyRequest request,
        TxService tx,
        out ProductPluginAudioState response,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tx);
        lock (_gate)
        {
            if (!TryGetLeasedInjectionLocked(request.LeaseId, out var session)
                || session.Destination != ProductPluginInjectionDestination.Tx)
            {
                response = CurrentStateLocked();
                error = "a live tx injection lease is required";
                return false;
            }
            if (!session.Armed
                || !string.Equals(session.PluginId, request.PluginId, StringComparison.Ordinal))
            {
                response = CurrentStateLocked();
                error = "the requesting plugin is not armed for this session";
                return false;
            }
            if (_keyedSession is not null && !ReferenceEquals(_keyedSession, session))
            {
                response = CurrentStateLocked();
                error = "another product-plugin lease holds the key";
                return false;
            }
            if (tx.IsMoxOn || tx.IsTunOn)
            {
                response = CurrentStateLocked();
                error = "a higher-precedence transmit source already holds the key";
                return false;
            }

            SubscribeToTxLocked(tx);
            _keyedSession = session;
            session.Keyed = true;
            session.ExpectedSequence = 0;
            session.HasPendingBlock = false;
            session.NextDeliveryTicks = Stopwatch.GetTimestamp() + BlockTicks;
            if (!tx.TrySetMox(true, MoxSource.ProductPlugin, out error)
                || tx.MoxOwner != MoxSource.ProductPlugin)
            {
                _keyedSession = null;
                session.Keyed = false;
                response = CurrentStateLocked();
                error ??= "the transmit safety interlock refused the product key";
                return false;
            }
            NotifyTxActive(true);
            response = CurrentStateLocked();
            error = null;
            return true;
        }
    }

    public bool TryReleaseKey(
        ProductPluginKeyRequest request,
        TxService tx,
        out ProductPluginAudioState response,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tx);
        TxService? release;
        lock (_gate)
        {
            if (!TryGetLeasedInjectionLocked(request.LeaseId, out var session)
                || !ReferenceEquals(_keyedSession, session)
                || !string.Equals(session.PluginId, request.PluginId, StringComparison.Ordinal))
            {
                response = CurrentStateLocked();
                error = "only the keyed plugin and lease may release the key";
                return false;
            }
            release = ClearKeyLocked(session, disarm: false);
            response = CurrentStateLocked();
            error = null;
        }
        if (release is not null)
        {
            NotifyTxActive(false);
            ReleaseProductKey(release);
        }
        return true;
    }

    public void PublishRxAudio(int receiver, int sampleRate, ReadOnlySpan<float> samples)
    {
        if (sampleRate != AudioRingProtocol.SampleRate
            || receiver < 0
            || receiver >= _rxCapture.Length)
            return;
        PublishCapture(Volatile.Read(ref _rxCapture[receiver]), samples);
    }

    public void PublishTxMic(int sampleRate, ReadOnlySpan<float> samples)
    {
        if (sampleRate != AudioRingProtocol.SampleRate) return;
        PublishCapture(Volatile.Read(ref _txMicCapture), samples);
    }

    internal bool TryActivateLeaseForTest(string leaseId) => TryActivateLease(leaseId, out _);

    internal void PumpInjectionForTest(string leaseId)
    {
        InjectionSession? session;
        lock (_gate)
            session = _sessions.TryGetValue(leaseId, out var candidate)
                ? candidate as InjectionSession
                : null;
        if (session is not null) PumpInjection(session, force: true);
    }

    internal void ExpireInjectionForTest(string leaseId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(leaseId, out var candidate)
                && candidate is InjectionSession session)
            {
                session.HasPendingBlock = false;
                session.NextDeliveryTicks = Stopwatch.GetTimestamp() - 1;
            }
        }
    }

    internal void DetachForTest(string leaseId, string reason)
    {
        Session? session;
        lock (_gate) _sessions.TryGetValue(leaseId, out session);
        if (session is not null) Detach(session, reason);
    }

    public void Dispose()
    {
        Session[] sessions;
        TxService? release = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            if (_keyedSession is not null) release = ClearKeyAndArmLocked(_keyedSession);
            Array.Clear(_rxCapture);
            _txMicCapture = null;
            _localMonitorInjection = null;
            _txInjection = null;
            UnsubscribeFromTxLocked();
        }
        if (release is not null)
        {
            NotifyTxActive(false);
            ReleaseProductKey(release);
        }
        foreach (var session in sessions) session.RequestDispose();
    }

    private void PublishCapture(CaptureSession? session, ReadOnlySpan<float> samples)
    {
        if (session?.IsLeased != true || samples.IsEmpty || !session.TryAddRef()) return;
        if (Interlocked.CompareExchange(ref session.PublishBusy, 1, 0) != 0)
        {
            Interlocked.Increment(ref _droppedCaptureBlocks);
            session.Release();
            return;
        }
        try
        {
            while (!samples.IsEmpty)
            {
                var take = Math.Min(
                    AudioRingProtocol.NominalSamplesPerBlock - session.PendingCount,
                    samples.Length);
                samples[..take].CopyTo(session.Pending.AsSpan(session.PendingCount));
                session.PendingCount += take;
                samples = samples[take..];
                if (session.PendingCount != AudioRingProtocol.NominalSamplesPerBlock) continue;
                if (!session.Owner.TryPublish(
                        session.Pending.AsSpan(0, AudioRingProtocol.NominalSamplesPerBlock)))
                    Interlocked.Increment(ref _droppedCaptureBlocks);
                session.PendingCount = 0;
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _droppedCaptureBlocks);
            session.PendingCount = 0;
        }
        finally
        {
            Volatile.Write(ref session.PublishBusy, 0);
            session.Release();
        }
    }

    private void PumpInjection(InjectionSession session, bool force)
    {
        if (!session.TryAddRef()) return;
        try
        {
            bool armed;
            bool keyed;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(session.LeaseId, out var current)
                    || !ReferenceEquals(current, session)
                    || !session.IsLeased)
                    return;
                armed = session.Armed;
                keyed = ReferenceEquals(_keyedSession, session) && session.Keyed;
            }

            // Frames written while the destination is not eligible are
            // discarded, never queued across an arm/key boundary.
            if (!armed
                || (session.Destination == ProductPluginInjectionDestination.Tx && !keyed))
            {
                if (session.Owner.TryReadOutput(session.Scratch, out _, out _))
                    Interlocked.Increment(ref _droppedInjectionBlocks);
                session.HasPendingBlock = false;
                return;
            }

            // Prefetch one block ahead of its delivery boundary. The bundle
            // therefore has the whole preceding 20 ms interval to produce the
            // next frame; ordinary scheduler jitter cannot create a false
            // underflow, while a genuinely missing block still dekeys at the
            // next audio boundary with no held-last sample.
            if (!session.HasPendingBlock
                && session.Owner.TryReadOutput(
                    session.Scratch, out var sequence, out var count))
            {
                if (count != AudioRingProtocol.NominalSamplesPerBlock
                    || (session.ExpectedSequence != 0 && sequence != session.ExpectedSequence + 1)
                    || !Sanitize(session.Scratch.AsSpan(0, count)))
                {
                    Interlocked.Increment(ref _invalidInjectionBlocks);
                    RevokeSession(session, "malformed or discontinuous injection");
                    return;
                }
                session.ExpectedSequence = sequence;
                session.PendingCount = count;
                session.HasPendingBlock = true;
            }

            var now = Stopwatch.GetTimestamp();
            var delivery = Volatile.Read(ref session.NextDeliveryTicks);
            if (!force && now < delivery) return;
            if (!session.HasPendingBlock)
            {
                if (!keyed) return;
                var underflows = session.ConsecutiveUnderflows + 1;
                session.ConsecutiveUnderflows = underflows;
                if (underflows > MaxToleratedUnderflowBlocks)
                {
                    RevokeSession(session, "tx injection underflow");
                    return;
                }
                // Tolerated miss: the producer stalled past its one-block
                // slack but is likely alive. Re-emit the last real block on
                // schedule (zeros only before the first real block exists):
                // a keyed GFSK source then holds constant envelope instead of
                // taking an amplitude crash mid-over, and the delivery grid
                // advances so a recovering producer lands back on cadence.
                var underflowSink = Volatile.Read(ref _txSink);
                if (underflowSink is null)
                {
                    RevokeSession(session, "tx injection sink unavailable");
                    return;
                }
                if (underflows == 1)
                    _log.LogWarning(
                        "product-plugin injection underflow tolerated session={SessionId} " +
                        "(holding the last block; revoking after {Cap} consecutive misses)",
                        session.Owner.Endpoint.SessionId, MaxToleratedUnderflowBlocks);
                underflowSink(session.LastDeliveredBlock ?? UnderflowSilenceBlock);
                var recoveredDelivery = delivery > 0 ? delivery + BlockTicks : now + BlockTicks;
                if (recoveredDelivery <= now) recoveredDelivery = now + BlockTicks;
                Volatile.Write(ref session.NextDeliveryTicks, recoveredDelivery);
                return;
            }
            session.ConsecutiveUnderflows = 0;

            lock (_gate)
            {
                var stillLeased = _sessions.TryGetValue(session.LeaseId, out var current)
                    && ReferenceEquals(current, session)
                    && session.IsLeased;
                var stillEligible = session.Armed
                    && (session.Destination == ProductPluginInjectionDestination.LocalMonitor
                        || (ReferenceEquals(_keyedSession, session) && session.Keyed));
                if (!stillLeased || !stillEligible)
                {
                    session.HasPendingBlock = false;
                    Interlocked.Increment(ref _droppedInjectionBlocks);
                    return;
                }
            }

            var pendingCount = session.PendingCount;
            session.HasPendingBlock = false;
            var nextDelivery = delivery > 0 ? delivery + BlockTicks : now + BlockTicks;
            if (nextDelivery <= now) nextDelivery = now + BlockTicks;
            Volatile.Write(ref session.NextDeliveryTicks, nextDelivery);

            if (session.Destination == ProductPluginInjectionDestination.Tx)
            {
                var sink = Volatile.Read(ref _txSink);
                if (sink is null)
                {
                    Interlocked.Increment(ref _droppedInjectionBlocks);
                    RevokeSession(session, "tx injection sink unavailable");
                    return;
                }
                sink(session.Scratch.AsSpan(0, pendingCount));
                // Keep the last real block for tolerated-miss replay. The
                // buffer is allocated once per session (never per block) so
                // the delivery path stays allocation-free.
                session.LastDeliveredBlock ??= new float[pendingCount];
                session.Scratch.AsSpan(0, pendingCount).CopyTo(session.LastDeliveredBlock);
            }
            else
            {
                var sink = Volatile.Read(ref _monitorSink);
                if (sink is null || !sink(session.Scratch.AsSpan(0, pendingCount)))
                    Interlocked.Increment(ref _droppedInjectionBlocks);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _invalidInjectionBlocks);
            RevokeSession(session, "injection transport failure");
        }
        finally
        {
            session.Release();
        }
    }

    private void RevokeSession(InjectionSession session, string reason)
    {
        TxService? release;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.LeaseId, out var current)
                || !ReferenceEquals(current, session)) return;
            release = ClearKeyAndArmLocked(session);
        }
        if (release is not null)
        {
            NotifyTxActive(false);
            ReleaseProductKey(release);
        }
        _log.LogWarning("product-plugin injection revoked session={SessionId} reason={Reason}",
            session.Owner.Endpoint.SessionId, reason);
    }

    private void Detach(Session session, string reason)
    {
        TxService? release = null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.LeaseId, out var current)
                || !ReferenceEquals(current, session)) return;
            _sessions.Remove(session.LeaseId);
            RemoveSlotLocked(session);
            if (session is InjectionSession injection)
                release = ClearKeyAndArmLocked(injection);
        }
        if (release is not null)
        {
            NotifyTxActive(false);
            ReleaseProductKey(release);
        }
        session.RequestDispose();
        _log.LogInformation(
            "product-plugin audio detached kind={Kind} name={ProductName} session={SessionId} reason={Reason}",
            session.Kind, session.Name, session.Owner.Endpoint.SessionId, reason);
    }

    private bool TryActivateLease(string leaseId, out Session session)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(leaseId, out session!)
                || !CryptographicEquals(session.LeaseId, leaseId)
                || !session.TryLease())
                return false;
            session.PendingTimer?.Dispose();
            session.PendingTimer = null;
            return true;
        }
    }

    private void AddPendingLocked(Session session)
    {
        session.PendingTimer = new Timer(
            static state =>
            {
                var pending = (PendingExpiration)state!;
                pending.Port.ExpirePending(pending.Session);
            },
            new PendingExpiration(this, session),
            PendingLeaseLifetime,
            Timeout.InfiniteTimeSpan);
        _sessions.Add(session.LeaseId, session);
    }

    private void ExpirePending(Session session)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.LeaseId, out var current)
                || !ReferenceEquals(current, session)
                || session.IsLeased) return;
            _sessions.Remove(session.LeaseId);
            RemoveSlotLocked(session);
        }
        session.RequestDispose();
        _log.LogInformation("product-plugin negotiation expired session={SessionId}",
            session.Owner.Endpoint.SessionId);
    }

    private void RemoveSlotLocked(Session session)
    {
        if (session is CaptureSession capture)
        {
            if (capture.Source == ProductPluginCaptureSource.RxAudio
                && ReferenceEquals(_rxCapture[capture.Receiver], capture))
                Volatile.Write(ref _rxCapture[capture.Receiver], null);
            else if (capture.Source == ProductPluginCaptureSource.TxMic
                     && ReferenceEquals(_txMicCapture, capture))
                Volatile.Write(ref _txMicCapture, null);
        }
        else if (session is InjectionSession injection)
        {
            if (injection.Destination == ProductPluginInjectionDestination.LocalMonitor
                && ReferenceEquals(_localMonitorInjection, injection))
                Volatile.Write(ref _localMonitorInjection, null);
            else if (injection.Destination == ProductPluginInjectionDestination.Tx
                     && ReferenceEquals(_txInjection, injection))
                Volatile.Write(ref _txInjection, null);
        }
    }

    private bool TryGetLeasedInjectionLocked(string leaseId, out InjectionSession session)
    {
        session = null!;
        return _sessions.TryGetValue(leaseId, out var candidate)
            && candidate is InjectionSession injection
            && injection.IsLeased
            && CryptographicEquals(injection.LeaseId, leaseId)
            && (session = injection) is not null;
    }

    private void SubscribeToTxLocked(TxService tx)
    {
        if (ReferenceEquals(_subscribedTx, tx)) return;
        if (_subscribedTx is not null)
            throw new InvalidOperationException("product-plugin key port cannot span multiple TX services");
        _subscribedTx = tx;
        tx.TransmitRequested += OnHigherPrecedenceTransmitRequested;
        tx.TxActiveChanged += OnTxActiveChanged;
    }

    private void UnsubscribeFromTxLocked()
    {
        if (_subscribedTx is null) return;
        _subscribedTx.TransmitRequested -= OnHigherPrecedenceTransmitRequested;
        _subscribedTx.TxActiveChanged -= OnTxActiveChanged;
        _subscribedTx = null;
    }

    private void OnHigherPrecedenceTransmitRequested(MoxSource source)
    {
        if (source == MoxSource.ProductPlugin) return;
        TxService? release;
        lock (_gate)
        {
            if (_keyedSession is null) return;
            release = ClearKeyLocked(_keyedSession, disarm: false);
        }
        NotifyTxActive(false);
        if (release is not null) ReleaseProductKey(release);
    }

    private void OnTxActiveChanged(bool active)
    {
        if (active) return;
        lock (_gate)
        {
            if (_keyedSession is null) return;
            ClearKeyLocked(_keyedSession, disarm: false);
        }
        NotifyTxActive(false);
    }

    private TxService? ClearKeyAndArmLocked(InjectionSession session)
    {
        session.Armed = false;
        session.PluginId = null;
        return ClearKeyLocked(session, disarm: true);
    }

    private TxService? ClearKeyLocked(InjectionSession session, bool disarm)
    {
        if (disarm)
        {
            session.Armed = false;
            session.PluginId = null;
        }
        session.ExpectedSequence = 0;
        session.PendingCount = 0;
        session.HasPendingBlock = false;
        session.NextDeliveryTicks = 0;
        if (!ReferenceEquals(_keyedSession, session))
        {
            session.Keyed = false;
            return null;
        }
        _keyedSession = null;
        session.Keyed = false;
        return _subscribedTx;
    }

    private static void ReleaseProductKey(TxService tx)
    {
        tx.TryReleaseMoxImmediately(MoxSource.ProductPlugin, out _);
    }

    private void NotifyTxActive(bool active)
    {
        try
        {
            Volatile.Read(ref _txActiveSink)?.Invoke(active);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "product-plugin TX audio-state subscriber failed active={Active}", active);
        }
    }

    private ProductPluginAudioState CurrentStateLocked() => new(
        Armed: _sessions.Values.OfType<InjectionSession>().Any(s => s.Armed),
        Keyed: _keyedSession is not null);

    private static bool ValidateIdentity(string name, string version, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128
            || string.IsNullOrWhiteSpace(version) || version.Length > 128)
        {
            error = "name and version are required and limited to 128 characters";
            return false;
        }
        error = null;
        return true;
    }

    private static bool ValidatePluginId(string pluginId, out string? error)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 128)
        {
            error = "pluginId is required and limited to 128 characters";
            return false;
        }
        error = null;
        return true;
    }

    private static bool Sanitize(Span<float> samples)
    {
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            if (!float.IsFinite(sample)) return false;
            samples[index] = Math.Clamp(sample, -1f, 1f);
        }
        return true;
    }

    private static bool CryptographicEquals(string expected, string supplied)
    {
        if (expected.Length != supplied.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
    }

    private abstract class Session(
        string leaseId,
        string name,
        string version,
        AudioRingOwner owner)
    {
        private int _leased;
        private int _references = 1;
        private int _disposeRequested;

        public string LeaseId { get; } = leaseId;
        public string Name { get; } = name;
        public string Version { get; } = version;
        public AudioRingOwner Owner { get; } = owner;
        public abstract string Kind { get; }
        public Timer? PendingTimer { get; set; }
        public bool IsLeased => Volatile.Read(ref _leased) != 0;
        public bool TryLease() => Interlocked.CompareExchange(ref _leased, 1, 0) == 0;

        public bool TryAddRef()
        {
            while (Volatile.Read(ref _disposeRequested) == 0)
            {
                var references = Volatile.Read(ref _references);
                if (references == 0) return false;
                if (Interlocked.CompareExchange(ref _references, references + 1, references) != references)
                    continue;
                if (Volatile.Read(ref _disposeRequested) == 0) return true;
                Release();
                return false;
            }
            return false;
        }

        public void RequestDispose()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
            PendingTimer?.Dispose();
            if (Interlocked.Decrement(ref _references) == 0) Owner.Dispose();
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
                ThreadPool.UnsafeQueueUserWorkItem(static session => session.Owner.Dispose(), this, false);
        }
    }

    private sealed class CaptureSession(
        string leaseId,
        string name,
        string version,
        AudioRingOwner owner,
        ProductPluginCaptureSource source,
        int receiver) : Session(leaseId, name, version, owner)
    {
        public override string Kind => Source == ProductPluginCaptureSource.RxAudio
            ? $"rx-audio:{Receiver}" : "tx-mic";
        public ProductPluginCaptureSource Source { get; } = source;
        public int Receiver { get; } = receiver;
        public float[] Pending { get; } = new float[AudioRingProtocol.NominalSamplesPerBlock];
        public int PendingCount;
        public int PublishBusy;
    }

    private sealed class InjectionSession(
        string leaseId,
        string name,
        string version,
        AudioRingOwner owner,
        ProductPluginInjectionDestination destination) : Session(leaseId, name, version, owner)
    {
        public override string Kind => Destination == ProductPluginInjectionDestination.Tx
            ? "tx" : "local-monitor";
        public ProductPluginInjectionDestination Destination { get; } = destination;
        public float[] Scratch { get; } = new float[AudioRingProtocol.MaxSamplesPerBlock];
        public string? PluginId;
        public bool Armed;
        public bool Keyed;
        public long ExpectedSequence;
        public int PendingCount;
        public bool HasPendingBlock;
        public long NextDeliveryTicks;
        public int ConsecutiveUnderflows;
        public float[]? LastDeliveredBlock;
    }

    private sealed record PendingExpiration(ProductPluginAudioPort Port, Session Session);
}

internal enum ProductPluginCaptureSource
{
    RxAudio,
    TxMic,
}

internal enum ProductPluginInjectionDestination
{
    LocalMonitor,
    Tx,
}

public sealed record ProductPluginCaptureAttachRequest(
    string Name,
    string Version,
    string Source,
    int? Receiver);

public sealed record ProductPluginInjectionAttachRequest(
    string Name,
    string Version,
    string Destination);

public sealed record ProductPluginAudioAttachResponse(string LeaseId, AudioRingEndpoint Ring);

public sealed record ProductPluginArmRequest(string LeaseId, string PluginId, bool Armed);

public sealed record ProductPluginKeyRequest(string LeaseId, string PluginId);

public readonly record struct ProductPluginAudioState(bool Armed, bool Keyed);

public readonly record struct ProductPluginAudioSnapshot(
    int CaptureLeases,
    int InjectionLeases,
    bool Armed,
    bool Keyed,
    long DroppedCaptureBlocks,
    long DroppedInjectionBlocks,
    long InvalidInjectionBlocks);
