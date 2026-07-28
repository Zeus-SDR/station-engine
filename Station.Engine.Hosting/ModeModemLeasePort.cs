// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Station.AudioRing;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Engine-owned, modem-neutral mode-modem lease. One out-of-process modem at a
/// time may replace RX demod audio and TX mic audio for a leased engine mode
/// (v0 serves only FreeDV) over two private bidirectional audio rings. The
/// engine stays authoritative for mode selection, keying, and the final
/// physical unkey; the leased modem never keys the radio.
/// </summary>
/// <remarks>
/// Safety invariants (design: docs/designs/product-freedv-v0.md):
/// - attach alone never makes the modem available; availability requires an
///   activated hold stream plus the product's <c>ready</c> event;
/// - RX underflow produces silence, never raw modem audio; TX underflow
///   silence-pads (desktop parity) and never passes live mic to RF through the
///   modem slot;
/// - malformed, discontinuous, or non-finite product output is counted, and a
///   small run of consecutive invalid blocks revokes the lease;
/// - lease loss while FreeDV is engaged drops the transmit key when the modem
///   owned the TX audio path and coerces FreeDV receivers to the effective SSB
///   mode exactly like the desktop modem bridge;
/// - <see cref="SyncMode"/>, <see cref="ProcessRx"/>, and
///   <see cref="ProcessTx"/> run on DSP threads: no locks, no blocking, no
///   logging, and no escaping exceptions on those paths.
/// Ring ordering: <see cref="AudioRingOwner.TryPublish"/> discards pending
/// product output (capture-path semantics), so every hot-path cycle reads
/// product output BEFORE publishing the next input block; a block lost to that
/// window surfaces as a counted underflow plus a sequence resync, never as
/// garbage audio.
/// </remarks>
public sealed class ModeModemLeasePort : IAudioModemPort, IDisposable
{
    internal const int SupportedContractVersion = 0;
    internal const string FreeDvLeaseMode = "freedv";
    internal static readonly TimeSpan PendingLeaseLifetime = TimeSpan.FromSeconds(5);
    // Mutable only as a test seam: CI runners (notably Windows) can stall the
    // rig's lifecycle round-trip past the production bound, so lease tests
    // widen it. Production code never writes this; 250 ms is the operator-felt
    // unkey bound and stays the default.
    internal static TimeSpan TailPendingWait = TimeSpan.FromMilliseconds(250);

    private static readonly byte FreeDvModeByte = (byte)RxMode.FreeDv;
    private static readonly TimeSpan LeasePumpPeriod = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan LeaseHeartbeatPeriod = TimeSpan.FromMilliseconds(100);
    private const int InvalidOutputBlockRevokeThreshold = 3;
    private const int LifecycleQueueCapacity = 64;
    private const int LifecycleLogCapacity = 32;
    private const int OutputCarrySamples = AudioRingProtocol.MaxSamplesPerBlock * 2;

    private const string EventReady = "ready";
    private const string EventTailPending = "tail-pending";
    private const string EventTailComplete = "tail-complete";
    private const string EventFault = "fault";
    private const string EventHealth = "health";

    private static readonly HashSet<string> AllowedNativeModes = new(StringComparer.Ordinal)
    {
        "700C", "700D", "700E", "1600", "800XA",
    };

    private readonly object _gate = new();
    private readonly ILogger<ModeModemLeasePort> _log;
    private readonly RadioService _radio;
    private readonly Func<TxService?>? _txServiceProvider;
    private readonly ConcurrentQueue<string> _lifecycleLog = new();
    private Session? _session;
    private long _droppedRxInputBlocks;
    private long _droppedTxInputBlocks;
    private long _rxUnderflowBlocks;
    private long _txUnderflowBlocks;
    private long _invalidOutputBlocks;
    private long _droppedLifecycleRecords;
    private bool _disposed;

    public ModeModemLeasePort(
        RadioService radio,
        ILogger<ModeModemLeasePort> log,
        Func<TxService?>? txServiceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(log);
        _radio = radio;
        _log = log;
        // Lazy TX-service lookup: TxService depends on DspPipelineService,
        // which consumes IAudioModemPort, so a constructor dependency would
        // cycle. Resolved only on the teardown path when a key must drop.
        _txServiceProvider = txServiceProvider;
        // Initial registration: unavailable until a lease activates and the
        // product reports ready. RadioService polls this provider on mode
        // entry; eager receiver coercion happens on every teardown below.
        _radio.SetModemAvailability(() => Available);
    }

    public ModeModemLeaseSnapshot Snapshot
    {
        get
        {
            var session = Volatile.Read(ref _session);
            return new ModeModemLeaseSnapshot(
                Attached: session is not null,
                Leased: session?.IsLeased == true,
                Ready: session is not null && Volatile.Read(ref session.ProductReady) != 0,
                Engaged: session is not null && Volatile.Read(ref session.ModeEngaged) != 0,
                RxEpoch: session is null ? 0 : Volatile.Read(ref session.RxEpoch),
                OverId: session is null ? 0 : Volatile.Read(ref session.TxOverId),
                PendingTxSamples: PendingTxSamples,
                DroppedRxInputBlocks: Interlocked.Read(ref _droppedRxInputBlocks),
                DroppedTxInputBlocks: Interlocked.Read(ref _droppedTxInputBlocks),
                RxUnderflowBlocks: Interlocked.Read(ref _rxUnderflowBlocks),
                TxUnderflowBlocks: Interlocked.Read(ref _txUnderflowBlocks),
                InvalidOutputBlocks: Interlocked.Read(ref _invalidOutputBlocks),
                DroppedLifecycleRecords: Interlocked.Read(ref _droppedLifecycleRecords));
        }
    }

    public bool TryCreateAttachment(
        ModeModemAttachRequest request,
        out ModeModemAttachResponse? response,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        response = null;
        if (request.ContractVersion != SupportedContractVersion)
        {
            error = $"contractVersion must be {SupportedContractVersion}";
            return false;
        }
        if (!string.Equals(request.Mode, FreeDvLeaseMode, StringComparison.Ordinal))
        {
            error = $"mode must be \"{FreeDvLeaseMode}\"";
            return false;
        }
        if (!ValidateIdentity(request.Name, request.Version, out error)) return false;
        if (request.NativeModes is null || request.NativeModes.Length == 0)
        {
            error = "nativeModes must list at least one native mode";
            return false;
        }
        foreach (var nativeMode in request.NativeModes)
        {
            if (nativeMode is null || !AllowedNativeModes.Contains(nativeMode))
            {
                error = $"native mode \"{nativeMode}\" is not allowed";
                return false;
            }
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                error = "a mode-modem lease is already attached or negotiating";
                return false;
            }

            AudioRingOwner? rxOwner = null;
            Session session;
            try
            {
                rxOwner = AudioRingOwner.Create();
                session = new Session(
                    Guid.NewGuid().ToString("N"),
                    request.Name,
                    request.Version,
                    rxOwner,
                    AudioRingOwner.Create());
            }
            catch
            {
                rxOwner?.Dispose();
                throw;
            }
            AddPendingLocked(session);
            Volatile.Write(ref _session, session);
            response = new ModeModemAttachResponse(
                session.LeaseId, session.RxOwner.Endpoint, session.TxOwner.Endpoint);
            error = null;
            return true;
        }
    }

    /// <summary>
    /// Activates the pending lease and holds it for the life of the streaming
    /// request: NDJSON heartbeats plus the engine-to-product lifecycle channel
    /// (<c>enter-mode</c>, <c>flush-rx</c>, <c>begin-over</c>,
    /// <c>finish-over</c>, <c>abort-over</c>, <c>leave-mode</c>). Stream loss
    /// tears the whole lease down.
    /// </summary>
    public async Task HoldLeaseAsync(string leaseId, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryActivateLease(leaseId, out var session))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        _log.LogInformation(
            "mode-modem lease attached name={ProductName} version={ProductVersion} session={SessionId}",
            session.Name, session.Version, session.RxOwner.Endpoint.SessionId);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-store";
        using var holdCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        Interlocked.Exchange(ref session.HoldCancellation, holdCancellation);
        var nextHeartbeat = Stopwatch.GetTimestamp();
        try
        {
            await WithLeaseWriteDeadlineAsync(
                holdCancellation.Token,
                token => context.Response.StartAsync(token)).ConfigureAwait(false);
            while (!holdCancellation.IsCancellationRequested
                   && Volatile.Read(ref session.TeardownStarted) == 0)
            {
                while (session.Lifecycle.TryDequeue(out var record))
                {
                    await WithLeaseWriteDeadlineAsync(
                        holdCancellation.Token,
                        async token =>
                        {
                            await context.Response.WriteAsync(record, token).ConfigureAwait(false);
                            await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                }

                var now = Stopwatch.GetTimestamp();
                if (now >= nextHeartbeat)
                {
                    await WithLeaseWriteDeadlineAsync(
                        holdCancellation.Token,
                        async token =>
                        {
                            await context.Response.WriteAsync(
                                "{\"attached\":true}\n", token).ConfigureAwait(false);
                            await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    nextHeartbeat = now + (long)(LeaseHeartbeatPeriod.TotalSeconds * Stopwatch.Frequency);
                }
                await Task.Delay(LeasePumpPeriod, holdCancellation.Token).ConfigureAwait(false);
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
            Interlocked.CompareExchange(ref session.HoldCancellation, null, holdCancellation);
            Teardown(session, "station-protocol lease disconnected");
        }
    }

    /// <summary>
    /// Product-to-engine lifecycle results and health. The lease ID is
    /// validated in fixed time; events for a dead or foreign lease are
    /// refused. <c>fault</c> is a product-initiated revoke and runs the same
    /// teardown as a dropped hold stream.
    /// </summary>
    public bool PostEvent(ModeModemEventRequest request, out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = Volatile.Read(ref _session);
        if (!IsUsable(session) || string.IsNullOrEmpty(request.LeaseId)
            || !CryptographicEquals(session.LeaseId, request.LeaseId))
        {
            error = "mode-modem lease was not found";
            return false;
        }

        switch (request.Event)
        {
            case EventReady:
                Volatile.Write(ref session.ProductReady, 1);
                _log.LogInformation(
                    "mode-modem lease ready session={SessionId}", session.RxOwner.Endpoint.SessionId);
                error = null;
                return true;

            case EventTailPending:
                if (request.OverId is not long pendingOver
                    || pendingOver != Volatile.Read(ref session.TxOverId))
                {
                    error = "tail-pending does not match the current over";
                    return false;
                }
                if (Volatile.Read(ref session.TailDrainActive) == 0)
                {
                    error = "finish-over was not issued for the current over";
                    return false;
                }
                if (request.PendingSamples is not int pendingSamples || pendingSamples < 0)
                {
                    error = "tail-pending requires a non-negative pendingSamples";
                    return false;
                }
                Volatile.Write(ref session.TailPendingSamples, pendingSamples);
                Volatile.Write(ref session.PendingTxSamples, pendingSamples);
                try { session.TailWaitHandle.Set(); }
                catch (ObjectDisposedException) { }
                error = null;
                return true;

            case EventTailComplete:
                if (request.OverId is not long completeOver
                    || completeOver != Volatile.Read(ref session.TxOverId))
                {
                    error = "tail-complete does not match the current over";
                    return false;
                }
                Volatile.Write(ref session.TailComplete, 1);
                // Defensive: a product with an empty tail may skip tail-pending;
                // releasing the unkey wait here keeps it inside the bound.
                try { session.TailWaitHandle.Set(); }
                catch (ObjectDisposedException) { }
                error = null;
                return true;

            case EventFault:
                _log.LogWarning(
                    "mode-modem lease product fault session={SessionId} message={Message}",
                    session.RxOwner.Endpoint.SessionId, request.Message ?? "(none)");
                error = null;
                Teardown(session, "product reported fault");
                return true;

            case EventHealth:
                _log.LogDebug(
                    "mode-modem lease health session={SessionId} rxQueue={RxQueue} txQueue={TxQueue} rxLatencyMs={RxLatency} txLatencyMs={TxLatency}",
                    session.RxOwner.Endpoint.SessionId,
                    request.RxQueueDepth, request.TxQueueDepth,
                    request.RxLatencyMs, request.TxLatencyMs);
                error = null;
                return true;

            default:
                error = "unknown mode-modem event";
                return false;
        }
    }

    // ------------------------------------------------------------------
    // IAudioModemPort. SyncMode/ProcessRx/ProcessTx are DSP-thread paths:
    // volatile reads, no locks, no allocation on the steady-state path, no
    // logging, and no escaping exceptions.
    // ------------------------------------------------------------------

    public bool Available
    {
        get
        {
            var session = Volatile.Read(ref _session);
            return IsUsable(session) && Volatile.Read(ref session.ProductReady) != 0;
        }
    }

    public bool Active => Available && IsEngaged(Volatile.Read(ref _session));

    public int PendingTxSamples
    {
        get
        {
            var session = Volatile.Read(ref _session);
            return session is null ? 0 : Math.Max(0, Volatile.Read(ref session.PendingTxSamples));
        }
    }

    public void SyncMode(byte rxModeByte)
    {
        var session = Volatile.Read(ref _session);
        if (!IsUsable(session)) return;
        if (rxModeByte == FreeDvModeByte)
        {
            if (Interlocked.CompareExchange(ref session.ModeEngaged, 1, 0) != 0) return;
            var epoch = Interlocked.Increment(ref session.RxEpoch);
            Volatile.Write(ref session.RxResetRequested, 1);
            EnqueueLifecycle(session, $"{{\"type\":\"enter-mode\",\"rxEpoch\":{epoch}}}\n");
        }
        else
        {
            if (Interlocked.CompareExchange(ref session.ModeEngaged, 0, 1) != 1) return;
            EnqueueLifecycle(
                session,
                $"{{\"type\":\"leave-mode\",\"rxEpoch\":{Volatile.Read(ref session.RxEpoch)}}}\n");
        }
    }

    public void ProcessRx(Span<float> block48k)
    {
        var session = Volatile.Read(ref _session);
        if (!IsUsable(session) || !IsEngaged(session)
            || Volatile.Read(ref session.ProductReady) == 0)
        {
            // A dead or not-ready modem must never leak raw modem audio into
            // the RX path; the caller only invokes us while Active, so this is
            // the mid-block teardown race.
            block48k.Clear();
            return;
        }
        if (!session.TryAddRef())
        {
            block48k.Clear();
            return;
        }
        try
        {
            if (Interlocked.CompareExchange(ref session.RxBusy, 1, 0) != 0)
            {
                block48k.Clear();
                Interlocked.Increment(ref _rxUnderflowBlocks);
                return;
            }
            try
            {
                ApplyRxResetIfRequested(session);
                // Read product output BEFORE publishing: TryPublish discards
                // pending output, so publishing first would eat decoded audio.
                RefillOutput(session, session.RxOwner, session.RxScratch, session.RxOutputCarry,
                    ref session.RxOutputCarryCount, ref session.RxExpectedSequence,
                    ref session.RxConsecutiveInvalid, block48k.Length);
                PublishInput(session.RxOwner, session.RxInputPending,
                    ref session.RxInputPendingCount, block48k, ref _droppedRxInputBlocks);
                ServeOutput(session.RxOutputCarry, ref session.RxOutputCarryCount,
                    ref session.RxExpectedSequence, block48k, ref _rxUnderflowBlocks);
            }
            finally
            {
                Volatile.Write(ref session.RxBusy, 0);
            }
        }
        catch (Exception)
        {
            block48k.Clear();
            Interlocked.Increment(ref _rxUnderflowBlocks);
        }
        finally
        {
            session.Release();
        }
    }

    public void ProcessTx(Span<float> block48k)
    {
        var session = Volatile.Read(ref _session);
        if (!IsUsable(session) || Volatile.Read(ref session.ProductReady) == 0)
        {
            // Never pass live mic through the modem slot on a dead lease.
            block48k.Clear();
            return;
        }
        if (!session.TryAddRef())
        {
            block48k.Clear();
            return;
        }
        try
        {
            if (Interlocked.CompareExchange(ref session.TxBusy, 1, 0) != 0)
            {
                block48k.Clear();
                Interlocked.Increment(ref _txUnderflowBlocks);
                return;
            }
            try
            {
                if (Volatile.Read(ref session.TailDrainActive) != 0)
                {
                    DrainTxTail(session, block48k);
                }
                else
                {
                    if (!IsEngaged(session))
                    {
                        block48k.Clear();
                        return;
                    }
                    ApplyTxResetIfRequested(session);
                    Interlocked.CompareExchange(ref session.OverOpen, 1, 0);
                    RefillOutput(session, session.TxOwner, session.TxScratch, session.TxOutputCarry,
                        ref session.TxOutputCarryCount, ref session.TxExpectedSequence,
                        ref session.TxConsecutiveInvalid, block48k.Length);
                    PublishInput(session.TxOwner, session.TxInputPending,
                        ref session.TxInputPendingCount, block48k, ref _droppedTxInputBlocks);
                    ServeOutput(session.TxOutputCarry, ref session.TxOutputCarryCount,
                        ref session.TxExpectedSequence, block48k, ref _txUnderflowBlocks);
                }
            }
            finally
            {
                Volatile.Write(ref session.TxBusy, 0);
            }
        }
        catch (Exception)
        {
            block48k.Clear();
            Interlocked.Increment(ref _txUnderflowBlocks);
        }
        finally
        {
            session.Release();
        }
    }

    public void FlushRx()
    {
        var session = Volatile.Read(ref _session);
        if (!IsUsable(session) || !IsEngaged(session)) return;
        Volatile.Write(ref session.RxResetRequested, 1);
        EnqueueLifecycle(
            session,
            $"{{\"type\":\"flush-rx\",\"rxEpoch\":{Volatile.Read(ref session.RxEpoch)}}}\n");
    }

    public void FlushTx()
    {
        var session = Volatile.Read(ref _session);
        if (!IsUsable(session)) return;
        // TX start resets the over: every over gets a fresh monotonic identity
        // (over 0 is implicit from activation until the first FlushTx), and a
        // new begin-over supersedes any unfinished prior over.
        var overId = Interlocked.Increment(ref session.TxOverId);
        Volatile.Write(ref session.OverOpen, 1);
        Volatile.Write(ref session.TailDrainActive, 0);
        Volatile.Write(ref session.TailComplete, 0);
        Volatile.Write(ref session.TailPendingSamples, 0);
        Volatile.Write(ref session.PendingTxSamples, 0);
        Volatile.Write(ref session.TxResetRequested, 1);
        EnqueueLifecycle(session, $"{{\"type\":\"begin-over\",\"overId\":{overId}}}\n");
    }

    /// <summary>
    /// Sends <c>finish-over</c> for the current over, then waits up to
    /// <see cref="TailPendingWait"/> for the product's <c>tail-pending</c>
    /// answer. This runs on the unkey path, not the RX hot path, so the short
    /// bounded wait is acceptable; a lost lease or a slow stream returns 0 and
    /// the caller unkeys without a tail.
    /// </summary>
    public int FinishTx()
    {
        var session = Volatile.Read(ref _session);
        if (!IsUsable(session) || Volatile.Read(ref session.ProductReady) == 0
            || !IsEngaged(session))
            return 0;
        var overId = Volatile.Read(ref session.TxOverId);
        try { session.TailWaitHandle.Reset(); }
        catch (ObjectDisposedException) { return 0; }
        Volatile.Write(ref session.TailPendingSamples, 0);
        Volatile.Write(ref session.TailComplete, 0);
        Volatile.Write(ref session.OverOpen, 0);
        Volatile.Write(ref session.TailDrainActive, 1);
        EnqueueLifecycle(session, $"{{\"type\":\"finish-over\",\"overId\":{overId}}}\n");
        var signaled = false;
        try
        {
            signaled = session.TailWaitHandle.Wait(TailPendingWait);
        }
        catch (ObjectDisposedException)
        {
        }
        var pending = signaled && Volatile.Read(ref session.TeardownStarted) == 0
            ? Math.Max(0, Volatile.Read(ref session.TailPendingSamples))
            : 0;
        Volatile.Write(ref session.PendingTxSamples, pending);
        return pending;
    }

    // ------------------------------------------------------------------
    // Test hooks (mirroring the product-plugin audio port's *ForTest seam).
    // ------------------------------------------------------------------

    internal bool TryActivateLeaseForTest(string leaseId) => TryActivateLease(leaseId, out _);

    internal void DetachForTest(string leaseId, string reason)
    {
        var session = Volatile.Read(ref _session);
        if (session is not null && string.Equals(session.LeaseId, leaseId, StringComparison.Ordinal))
            Teardown(session, reason);
    }

    internal bool TryDequeueLifecycleForTest(out string record)
    {
        var session = Volatile.Read(ref _session);
        if (session is not null && session.Lifecycle.TryDequeue(out var dequeued))
        {
            record = dequeued;
            return true;
        }
        record = string.Empty;
        return false;
    }

    internal bool TryPeekLifecycleForTest()
    {
        var session = Volatile.Read(ref _session);
        return session is not null && session.Lifecycle.TryPeek(out _);
    }

    /// <summary>Recent lifecycle records, including records queued during a
    /// teardown (the per-session queue is unreachable once the slot clears).</summary>
    internal string[] LifecycleLogForTest => _lifecycleLog.ToArray();

    public void Dispose()
    {
        Session? session;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
        }
        try { _radio.SetModemAvailability(null); }
        catch (Exception ex) { _log.LogDebug(ex, "mode-modem availability provider already gone"); }
        if (session is not null) Teardown(session, "mode-modem port disposed");
    }

    // ------------------------------------------------------------------
    // Data plane (all called under the per-direction busy CAS).
    // ------------------------------------------------------------------

    /// <summary>Re-blocks engine audio into exactly 960-sample ring records.
    /// A trailing partial block stays buffered for the next call; nothing is
    /// ever padded or dropped on the input side.</summary>
    private void PublishInput(
        AudioRingOwner owner,
        float[] pending,
        ref int pendingCount,
        ReadOnlySpan<float> samples,
        ref long droppedCounter)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(
                AudioRingProtocol.NominalSamplesPerBlock - pendingCount,
                samples.Length);
            samples[..take].CopyTo(pending.AsSpan(pendingCount));
            pendingCount += take;
            samples = samples[take..];
            if (pendingCount != AudioRingProtocol.NominalSamplesPerBlock) continue;
            if (!owner.TryPublish(pending.AsSpan(0, AudioRingProtocol.NominalSamplesPerBlock)))
                Interlocked.Increment(ref droppedCounter);
            pendingCount = 0;
        }
    }

    /// <summary>Pulls product output blocks into the carry buffer, validating
    /// count, sequence continuity, and finiteness. Invalid blocks are counted;
    /// a run of consecutive invalid blocks revokes the lease.</summary>
    private void RefillOutput(
        Session session,
        AudioRingOwner owner,
        float[] scratch,
        float[] carry,
        ref int carryCount,
        ref long expectedSequence,
        ref int consecutiveInvalid,
        int needed)
    {
        while (carryCount < needed)
        {
            if (!owner.TryReadOutput(scratch, out var sequence, out var count))
                return;
            var expected = expectedSequence;
            if (count != AudioRingProtocol.NominalSamplesPerBlock
                || (expected != 0 && sequence != expected + 1)
                || carryCount + count > carry.Length
                || !Sanitize(scratch.AsSpan(0, count)))
            {
                Interlocked.Increment(ref _invalidOutputBlocks);
                if (Interlocked.Increment(ref consecutiveInvalid) >= InvalidOutputBlockRevokeThreshold)
                    Revoke(session, "malformed or discontinuous modem output");
                return;
            }
            Volatile.Write(ref consecutiveInvalid, 0);
            scratch.AsSpan(0, count).CopyTo(carry.AsSpan(carryCount));
            carryCount += count;
            expectedSequence = sequence;
        }
    }

    /// <summary>Serves destination samples from the carry buffer and
    /// silence-fills any shortfall. A shortfall is a counted underflow and
    /// resynchronizes the expected sequence so the next block is accepted
    /// instead of rejected for a gap. Input audio is never echoed through.</summary>
    private static void ServeOutput(
        float[] carry,
        ref int carryCount,
        ref long expectedSequence,
        Span<float> destination,
        ref long underflowCounter)
    {
        var served = Math.Min(carryCount, destination.Length);
        if (served > 0)
        {
            carry.AsSpan(0, served).CopyTo(destination);
            carry.AsSpan(served, carryCount - served).CopyTo(carry);
            carryCount -= served;
        }
        if (served < destination.Length)
        {
            destination[served..].Clear();
            Interlocked.Increment(ref underflowCounter);
            expectedSequence = 0;
        }
    }

    /// <summary>Tail mode after <see cref="FinishTx"/>: the ring is drained
    /// without publishing live mic, pending bookkeeping tracks ring-sourced
    /// samples, and draining stops once the product's <c>tail-complete</c> for
    /// the current over has arrived and the ring is empty.</summary>
    private void DrainTxTail(Session session, Span<float> block48k)
    {
        // Consume any reset queued by FlushTx before the over finished (the
        // degenerate FlushTx -> FinishTx ordering with no mic block between);
        // a no-op in the normal ordering where the live path already took it.
        ApplyTxResetIfRequested(session);
        if (Volatile.Read(ref session.TailComplete) != 0
            && Volatile.Read(ref session.PendingTxSamples) == 0)
        {
            block48k.Clear();
            return;
        }
        RefillOutput(session, session.TxOwner, session.TxScratch, session.TxOutputCarry,
            ref session.TxOutputCarryCount, ref session.TxExpectedSequence,
            ref session.TxConsecutiveInvalid, block48k.Length);
        var carryBefore = session.TxOutputCarryCount;
        ServeOutput(session.TxOutputCarry, ref session.TxOutputCarryCount,
            ref session.TxExpectedSequence, block48k, ref _txUnderflowBlocks);
        var served = carryBefore - session.TxOutputCarryCount;
        if (served > 0) DecrementPending(session, served);
        if (served == 0 && Volatile.Read(ref session.TailComplete) != 0)
            Volatile.Write(ref session.PendingTxSamples, 0);
    }

    private static void DecrementPending(Session session, int samples)
    {
        while (true)
        {
            var before = Volatile.Read(ref session.PendingTxSamples);
            var next = Math.Max(0, before - samples);
            if (Interlocked.CompareExchange(ref session.PendingTxSamples, next, before) == before)
                return;
        }
    }

    private static void ApplyRxResetIfRequested(Session session)
    {
        if (Interlocked.Exchange(ref session.RxResetRequested, 0) == 0) return;
        session.RxInputPendingCount = 0;
        session.RxOutputCarryCount = 0;
        session.RxExpectedSequence = 0;
    }

    private static void ApplyTxResetIfRequested(Session session)
    {
        if (Interlocked.Exchange(ref session.TxResetRequested, 0) == 0) return;
        session.TxInputPendingCount = 0;
        session.TxOutputCarryCount = 0;
        session.TxExpectedSequence = 0;
    }

    // ------------------------------------------------------------------
    // Lease lifecycle.
    // ------------------------------------------------------------------

    private static bool IsUsable([NotNullWhen(true)] Session? session) =>
        session is not null && session.IsLeased && Volatile.Read(ref session.TeardownStarted) == 0;

    private static bool IsEngaged([NotNullWhen(true)] Session? session) =>
        session is not null && Volatile.Read(ref session.ModeEngaged) != 0;

    private bool TryActivateLease(string leaseId, out Session session)
    {
        lock (_gate)
        {
            session = null!;
            var current = Volatile.Read(ref _session);
            if (current is null
                || !CryptographicEquals(current.LeaseId, leaseId)
                || !current.TryLease())
                return false;
            current.PendingTimer?.Dispose();
            current.PendingTimer = null;
            session = current;
            return true;
        }
    }

    private void AddPendingLocked(Session session)
    {
        session.PendingTimer = new Timer(
            static state =>
            {
                var pending = (PendingExpiration)state!;
                pending.Port.ExpirePending(pending.LeaseSession);
            },
            new PendingExpiration(this, session),
            PendingLeaseLifetime,
            Timeout.InfiniteTimeSpan);
    }

    private void ExpirePending(Session session)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session) || session.IsLeased) return;
            Volatile.Write(ref session.TeardownStarted, 1);
            Volatile.Write(ref _session, null);
        }
        session.RequestDispose();
        _log.LogInformation("mode-modem negotiation expired session={SessionId}",
            session.RxOwner.Endpoint.SessionId);
    }

    /// <summary>DSP-path revoke: marks the lease dead immediately (lock-free,
    /// so the very next block silence-fills) and runs the heavy teardown off
    /// the realtime thread.</summary>
    private void Revoke(Session session, string reason)
    {
        if (Interlocked.CompareExchange(ref session.TeardownStarted, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ => TeardownCore(session, reason), false);
    }

    private void Teardown(Session session, string reason)
    {
        if (Interlocked.CompareExchange(ref session.TeardownStarted, 1, 0) != 0) return;
        TeardownCore(session, reason);
    }

    private void TeardownCore(Session session, string reason)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
                Volatile.Write(ref _session, null);
        }
        // Best-effort abort-over while an over is open; the hold stream may
        // already be dead, and an unsent record is harmless.
        if (Volatile.Read(ref session.OverOpen) != 0)
        {
            var abort = $"{{\"type\":\"abort-over\",\"overId\":{Volatile.Read(ref session.TxOverId)}}}\n";
            session.Lifecycle.Enqueue(abort);
            RecordLifecycleForDiagnostics(abort);
        }
        try { session.HoldCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { session.TailWaitHandle.Set(); }
        catch (ObjectDisposedException) { }
        DropTransmitKeyIfModemEngaged(session);
        CoerceFreeDvReceiversToSsb();
        session.RequestDispose();
        _log.LogWarning(
            "mode-modem lease torn down session={SessionId} reason={Reason}",
            session.RxOwner.Endpoint.SessionId, reason);
    }

    /// <summary>Lease loss while transmitting with the modem engaged drops the
    /// physical key before any mode fallback, so the radio never holds an
    /// unmodulated carrier and never passes live mic through a dead mode. The
    /// held-PTT re-key inhibit latch from the design is deferred (needs
    /// bench); the operator's released key source may re-key normally.</summary>
    private void DropTransmitKeyIfModemEngaged(Session session)
    {
        if (!IsEngaged(session)) return;
        TxService? tx;
        try
        {
            tx = _txServiceProvider?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "mode-modem lease could not resolve the transmit service");
            return;
        }
        if (tx is null) return;
        try
        {
            if (tx.IsMoxOn)
                tx.TrySetMox(false, MoxSource.UI, out _);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "mode-modem lease could not drop the transmit key");
        }
    }

    /// <summary>Availability-loss receiver fallback, exactly mirroring the
    /// desktop modem bridge: each receiver sitting in FreeDV returns to the
    /// effective SSB mode (USB at/above 10 MHz, 60 m USB; the RadioService
    /// mode path owns the LSB-below-10 MHz convention on its own).</summary>
    private void CoerceFreeDvReceiversToSsb()
    {
        try
        {
            var state = _radio.Snapshot();
            if (state.Mode == RxMode.FreeDv)
                _radio.SetMode(RxMode.USB, TxVfo.A);
            if (state.Rx2Enabled && state.Rx2().Mode == RxMode.FreeDv)
                _radio.SetMode(RxMode.USB, TxVfo.B);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "mode-modem lease could not leave FreeDV after teardown");
        }
    }

    private void EnqueueLifecycle(Session session, string record)
    {
        if (Volatile.Read(ref session.TeardownStarted) != 0) return;
        while (session.Lifecycle.Count >= LifecycleQueueCapacity)
        {
            if (!session.Lifecycle.TryDequeue(out _)) break;
            Interlocked.Increment(ref _droppedLifecycleRecords);
        }
        session.Lifecycle.Enqueue(record);
        RecordLifecycleForDiagnostics(record);
    }

    private void RecordLifecycleForDiagnostics(string record)
    {
        while (_lifecycleLog.Count >= LifecycleLogCapacity)
            _lifecycleLog.TryDequeue(out _);
        _lifecycleLog.Enqueue(record);
    }

    private static async Task WithLeaseWriteDeadlineAsync(
        CancellationToken holdCancelled,
        Func<CancellationToken, Task> write)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(holdCancelled);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(AudioRingProtocol.BlockPeriodMilliseconds));
        await write(deadline.Token).ConfigureAwait(false);
    }

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

    private sealed class Session
    {
        private int _leased;
        private int _references = 1;
        private int _disposeRequested;

        public Session(
            string leaseId,
            string name,
            string version,
            AudioRingOwner rxOwner,
            AudioRingOwner txOwner)
        {
            LeaseId = leaseId;
            Name = name;
            Version = version;
            RxOwner = rxOwner;
            TxOwner = txOwner;
        }

        public string LeaseId { get; }
        public string Name { get; }
        public string Version { get; }
        public AudioRingOwner RxOwner { get; }
        public AudioRingOwner TxOwner { get; }
        public Timer? PendingTimer { get; set; }
        public ConcurrentQueue<string> Lifecycle { get; } = new();
        public ManualResetEventSlim TailWaitHandle { get; } = new(false);
        public CancellationTokenSource? HoldCancellation;

        // Lease state (volatile/Interlocked access only).
        public int TeardownStarted;
        public int ProductReady;
        public int ModeEngaged;
        public int OverOpen;
        public int TailDrainActive;
        public int TailComplete;
        public int TailPendingSamples;
        public int PendingTxSamples;
        public int RxResetRequested;
        public int TxResetRequested;
        public long RxEpoch;
        public long TxOverId;

        // Hot-path state, owned by the respective busy CAS below.
        public float[] RxInputPending { get; } = new float[AudioRingProtocol.NominalSamplesPerBlock];
        public int RxInputPendingCount;
        public float[] RxOutputCarry { get; } = new float[OutputCarrySamples];
        public int RxOutputCarryCount;
        public float[] RxScratch { get; } = new float[AudioRingProtocol.MaxSamplesPerBlock];
        public long RxExpectedSequence;
        public int RxConsecutiveInvalid;
        public int RxBusy;
        public float[] TxInputPending { get; } = new float[AudioRingProtocol.NominalSamplesPerBlock];
        public int TxInputPendingCount;
        public float[] TxOutputCarry { get; } = new float[OutputCarrySamples];
        public int TxOutputCarryCount;
        public float[] TxScratch { get; } = new float[AudioRingProtocol.MaxSamplesPerBlock];
        public long TxExpectedSequence;
        public int TxConsecutiveInvalid;
        public int TxBusy;

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
            if (Interlocked.Decrement(ref _references) == 0) DisposeResources();
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
                ThreadPool.UnsafeQueueUserWorkItem(static session => session.DisposeResources(), this, false);
        }

        private void DisposeResources()
        {
            RxOwner.Dispose();
            TxOwner.Dispose();
            TailWaitHandle.Dispose();
        }
    }

    private sealed record PendingExpiration(ModeModemLeasePort Port, Session LeaseSession);
}

public sealed record ModeModemAttachRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("nativeModes")] string[] NativeModes);

/// <summary>
/// Lease identity plus the two private ring descriptors. The RX ring's input
/// carries rx-demod-input (engine to product) and its output carries
/// rx-decoded-output; the TX ring's input carries tx-mic-input and its output
/// carries tx-modem-output. Descriptors never appear in a browser response.
/// </summary>
public sealed record ModeModemAttachResponse(
    [property: JsonPropertyName("leaseId")] string LeaseId,
    [property: JsonPropertyName("rxRing")] AudioRingEndpoint RxRing,
    [property: JsonPropertyName("txRing")] AudioRingEndpoint TxRing);

public sealed record ModeModemEventRequest(
    [property: JsonPropertyName("leaseId")] string LeaseId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("overId")] long? OverId = null,
    [property: JsonPropertyName("pendingSamples")] int? PendingSamples = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("rxQueueDepth")] int? RxQueueDepth = null,
    [property: JsonPropertyName("txQueueDepth")] int? TxQueueDepth = null,
    [property: JsonPropertyName("rxLatencyMs")] double? RxLatencyMs = null,
    [property: JsonPropertyName("txLatencyMs")] double? TxLatencyMs = null);

public readonly record struct ModeModemLeaseSnapshot(
    bool Attached,
    bool Leased,
    bool Ready,
    bool Engaged,
    long RxEpoch,
    long OverId,
    int PendingTxSamples,
    long DroppedRxInputBlocks,
    long DroppedTxInputBlocks,
    long RxUnderflowBlocks,
    long TxUnderflowBlocks,
    long InvalidOutputBlocks,
    long DroppedLifecycleRecords);
