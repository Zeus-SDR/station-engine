// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Diagnostics;
using Station.AudioRing;

namespace Zeus.Server;

/// <summary>
/// Standalone-engine Wave A adapter. It owns a private ring only while a
/// product lease is attached and otherwise preserves the null-port path.
/// </summary>
public sealed class ProductAudioRingPort : IProductTxAudioPort, IDisposable
{
    // RX per-block reply window: three quarters of the block's own duration,
    // clamped to [3 ms, 15 ms] (the ring itself rejects waits over one 20 ms
    // block period). The product chain budgets itself at half the block, so a
    // healthy product answers inside this window on EVERY block and the audio
    // the operator hears stays 100% processed. A short hardcoded deadline
    // splices processed and passthrough blocks every time the round trip
    // straddles it — that splice is exactly the crackle an out-of-process
    // plugin with real per-block latency (ML noise reduction) produced here.
    internal static readonly TimeSpan MinResponseDeadline = TimeSpan.FromMilliseconds(3);
    internal static readonly TimeSpan MaxResponseDeadline = TimeSpan.FromMilliseconds(15);
    // RX sustained full-window misses take the leg out of processing for a
    // cooldown: pure passthrough with zero ring work (the audio pump never
    // spins behind a dead or overloaded product, and no splice can reach the
    // operator), then the first block past the cooldown probes again. Same
    // fail-soft philosophy as the product-side auto-bypass, one level up.
    internal const int LegOpenMissThreshold = 8;
    internal static readonly TimeSpan LegOpenCooldown = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan PendingLeaseLifetime = TimeSpan.FromSeconds(5);

    internal static TimeSpan ResponseDeadlineFor(int frames)
    {
        if (frames <= 0) return MinResponseDeadline;
        var block = TimeSpan.FromSeconds(frames / (double)AudioRingProtocol.SampleRate);
        var budget = TimeSpan.FromTicks(block.Ticks * 3 / 4);
        if (budget < MinResponseDeadline) return MinResponseDeadline;
        return budget > MaxResponseDeadline ? MaxResponseDeadline : budget;
    }

    private readonly object _gate = new();
    private readonly ILogger<ProductAudioRingPort> _log;
    private readonly IWindowsFirewallService? _windowsFirewall;
    private readonly float[] _txProcessed = new float[AudioRingProtocol.MaxSamplesPerBlock];
    private readonly float[] _rxProcessed = new float[AudioRingProtocol.MaxSamplesPerBlock];
    private Session? _session;
    private long _attemptedBlocks;
    private long _processedBlocks;
    private long _bypassedBlocks;
    private int _audioBusy;
    private int _rxAudioBusy;
    private long _rxAttemptedBlocks;
    private long _rxProcessedBlocks;
    private long _rxBypassedBlocks;
    private long _txConsecutiveMisses;
    private long _rxConsecutiveMisses;
    private long _txOpenUntilTimestamp;
    private long _rxOpenUntilTimestamp;
    // TX: target responses not ready at the nonblocking poll. RX: blocks that
    // engaged the ring and did not get a matching reply inside the response
    // deadline. Distinct from zero-work bypass paths in both cases.
    private long _txMissedDeadlineBlocks;
    private long _rxMissedDeadlineBlocks;
    // TX counts input-ring-full events; RX counts closed->open leg transitions.
    // Kept as one stable diagnostic field for station API compatibility.
    private long _txLegOpenedCount;
    private long _rxLegOpenedCount;
    private int _rxLastRenderKind;
    private float _rxLastRenderedSample;
    private bool _disposed;

    public ProductAudioRingPort(ILogger<ProductAudioRingPort> log)
    {
        _log = log;
    }

    public ProductAudioRingPort(
        ILogger<ProductAudioRingPort> log,
        IWindowsFirewallService windowsFirewall)
    {
        _log = log;
        _windowsFirewall = windowsFirewall;
    }

    public bool Active => Volatile.Read(ref _session)?.IsLeased == true;

    public ProductAudioRingSnapshot Snapshot => new(
        Volatile.Read(ref _session)?.IsLeased == true,
        Interlocked.Read(ref _attemptedBlocks),
        Interlocked.Read(ref _processedBlocks),
        Interlocked.Read(ref _bypassedBlocks),
        Interlocked.Read(ref _rxAttemptedBlocks),
        Interlocked.Read(ref _rxProcessedBlocks),
        Interlocked.Read(ref _rxBypassedBlocks),
        Interlocked.Read(ref _txMissedDeadlineBlocks),
        Interlocked.Read(ref _rxMissedDeadlineBlocks),
        Interlocked.Read(ref _txLegOpenedCount),
        Interlocked.Read(ref _rxLegOpenedCount));

    public bool TryCreateAttachment(
        ProductAudioAttachRequest request,
        out ProductAudioAttachResponse? response,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        response = null;
        error = null;
        if (string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Length > 128
            || string.IsNullOrWhiteSpace(request.Version)
            || request.Version.Length > 128
            || request.HttpPort is <= 0 or > 65_535)
        {
            error = "name and version are required and limited to 128 characters; httpPort must be 1..65535 when supplied";
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                error = "a product audio host is already attached or negotiating";
                _log.LogWarning(
                    "product-audio attach rejected requestedName={RequestedName} "
                    + "existingSession={SessionId} existingLeased={Leased} existingHttpPort={HttpPort}",
                    request.Name,
                    _session.Owner.Endpoint.SessionId,
                    _session.IsLeased,
                    _session.HttpPort);
                return false;
            }

            var owner = AudioRingOwner.Create();
            AudioRingOwner? rxOwner = null;
            try
            {
                rxOwner = AudioRingOwner.Create();
            }
            catch
            {
                owner.Dispose();
                throw;
            }
            var session = new Session(
                Guid.NewGuid().ToString("N"),
                request.Name,
                request.Version,
                request.HttpPort,
                owner,
                rxOwner);
            // Leg-health state is session-scoped: a fresh product must never
            // inherit miss counts or an open cooldown from a detached one.
            Interlocked.Exchange(ref _txConsecutiveMisses, 0);
            Interlocked.Exchange(ref _rxConsecutiveMisses, 0);
            Interlocked.Exchange(ref _txOpenUntilTimestamp, 0);
            Interlocked.Exchange(ref _rxOpenUntilTimestamp, 0);
            Volatile.Write(ref _rxLastRenderKind, 0);
            Volatile.Write(ref _rxLastRenderedSample, 0f);
            session.PendingTimer = new Timer(
                static state =>
                {
                    var expiration = (PendingExpiration)state!;
                    expiration.Port.ExpirePending(expiration.Session);
                },
                new PendingExpiration(this, session),
                PendingLeaseLifetime,
                Timeout.InfiniteTimeSpan);
            Volatile.Write(ref _session, session);
            response = new ProductAudioAttachResponse(
                session.LeaseId,
                owner.Endpoint,
                rxOwner.Endpoint);
            _log.LogInformation(
                "product-audio attach negotiating name={ProductName} version={ProductVersion} "
                + "httpPort={HttpPort} session={SessionId} pendingLeaseSeconds={PendingSeconds}",
                request.Name,
                request.Version,
                request.HttpPort,
                owner.Endpoint.SessionId,
                (int)PendingLeaseLifetime.TotalSeconds);
            return true;
        }
    }

    public bool TryGetProductEndpoint(out int port)
    {
        lock (_gate)
        {
            if (_session?.HttpPort is int advertisedPort)
            {
                port = advertisedPort;
                return true;
            }
        }
        port = 0;
        return false;
    }

    /// <summary>
    /// Returns the product HTTP endpoint only after the bearer-token-protected
    /// lease has been claimed and while that lease remains connected. Pending
    /// attachment negotiations are intentionally not sufficient to authorize
    /// privileged browser access such as the native microphone bridge.
    /// </summary>
    public bool TryGetActiveProductEndpoint(out int port)
    {
        lock (_gate)
        {
            if (_session is { IsLeased: true, HttpPort: int advertisedPort })
            {
                port = advertisedPort;
                return true;
            }
        }
        port = 0;
        return false;
    }

    public async Task HoldLeaseAsync(string leaseId, HttpContext context)
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            // TryLease has a side effect (atomically claims the session) so it
            // must run exactly once, and only once the earlier checks confirm
            // there IS a matching pending session to claim.
            string? rejectReason = session switch
            {
                null => "no attachment is currently pending",
                _ when !CryptographicEquals(session.LeaseId, leaseId) => "leaseId does not match the pending attachment",
                _ when !session.TryLease() => "the pending attachment was already leased",
                _ => null,
            };
            if (rejectReason is not null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                _log.LogWarning(
                    "product-audio lease rejected reason=\"{Reason}\" pendingSession={PendingSessionId}",
                    rejectReason,
                    session?.Owner.Endpoint.SessionId ?? "none");
                return;
            }

            // rejectReason is null only when the switch fell through the
            // final arm, which requires session to be non-null.
            session!.PendingTimer?.Dispose();
            session.PendingTimer = null;
        }

        _log.LogInformation(
            "product-audio attached name={ProductName} version={ProductVersion} session={SessionId}",
            session.Name,
            session.Version,
            session.Owner.Endpoint.SessionId);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);

        // A claimed local product lease is the first lifecycle point that proves
        // Zeus Link has attached to this engine. Apply from this process so the
        // program-scoped inbound rule names StationEngine.exe, which owns the
        // radio UDP sockets, rather than the launcher. Run it in the background
        // so an elevation prompt cannot hold up the product's audio attachment.
        if (_windowsFirewall is not null)
            _ = Task.Run(() => ApplyFirewallRuleAsync(CancellationToken.None));

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                await context.Response.WriteAsync(
                    "{\"attached\":true}\n",
                    context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(100), context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
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

    private async Task ApplyFirewallRuleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _windowsFirewall!.ApplyAllowRuleAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "product-audio firewall rule apply failed");
        }
    }

    /// <summary>
    /// Realtime TX seam. The callback never waits for Product: it drains a
    /// completed prior response, publishes the current block, and emits either
    /// the complete target response or the exactly aligned retained dry block.
    /// Product's reported internal delay is included in that alignment, so a
    /// late VST response cannot splice N-1 wet with current N dry audio.
    /// </summary>
    public void ProcessTx(Span<float> block48k)
    {
        var session = Volatile.Read(ref _session);
        if (session?.IsLeased != true
            || block48k.IsEmpty
            || block48k.Length > AudioRingProtocol.MaxSamplesPerBlock
            || !session.TryAddRef())
        {
            return;
        }

        Interlocked.Increment(ref _attemptedBlocks);
        if (Interlocked.CompareExchange(ref _audioBusy, 1, 0) != 0)
        {
            Interlocked.Increment(ref _bypassedBlocks);
            session.Release();
            return;
        }

        try
        {
            while (session.Owner.TryReadOutput(
                       _txProcessed,
                       out var completedSequence,
                       out var completedCount,
                       out var processingDelayBlocks))
            {
                if (completedCount <= 0 || completedCount > _txProcessed.Length)
                    continue;
                session.TxPipeline.RememberProcessed(
                    completedSequence,
                    _txProcessed.AsSpan(0, completedCount),
                    processingDelayBlocks);
            }

            var published = session.Owner.TryPublish(block48k, out var submittedSequence);
            session.TxPipeline.RememberDry(submittedSequence, block48k);
            var status = session.TxPipeline.Render(submittedSequence, block48k, block48k);
            if (status == ProductAudioTransportStatus.Processed)
            {
                Interlocked.Increment(ref _processedBlocks);
                Interlocked.Exchange(ref _txConsecutiveMisses, 0);
                return;
            }

            Interlocked.Increment(ref _bypassedBlocks);
            if (status == ProductAudioTransportStatus.Priming)
                return;

            Interlocked.Increment(ref _txMissedDeadlineBlocks);
            Interlocked.Increment(ref _txConsecutiveMisses);
            if (!published)
                Interlocked.Increment(ref _txLegOpenedCount);
        }
        finally
        {
            Volatile.Write(ref _audioBusy, 0);
            session.Release();
        }
    }

    public void ProcessRx(Span<float> block48k)
        => ProcessBlock(
            block48k,
            ref _rxAudioBusy,
            _rxProcessed,
            static session => session.RxOwner,
            ref _rxAttemptedBlocks,
            ref _rxProcessedBlocks,
            ref _rxBypassedBlocks,
            ref _rxConsecutiveMisses,
            ref _rxOpenUntilTimestamp,
            ref _rxMissedDeadlineBlocks,
            ref _rxLegOpenedCount,
            "rx");

    private void ProcessBlock(
        Span<float> block48k,
        ref int busy,
        float[] processed,
        Func<Session, AudioRingOwner> ownerFor,
        ref long attemptedBlocks,
        ref long processedBlocks,
        ref long bypassedBlocks,
        ref long consecutiveMisses,
        ref long openUntilTimestamp,
        ref long missedDeadlineBlocks,
        ref long legOpenedCount,
        string leg)
    {
        var session = Volatile.Read(ref _session);
        if (session?.IsLeased != true
            || block48k.IsEmpty
            || block48k.Length > AudioRingProtocol.MaxSamplesPerBlock
            || !session.TryAddRef())
        {
            return;
        }

        Interlocked.Increment(ref attemptedBlocks);
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            Interlocked.Increment(ref bypassedBlocks);
            session.Release();
            return;
        }

        try
        {
            // Leg open after sustained misses: pure passthrough with no ring
            // work at all — the audio pump never spins behind a dead or
            // overloaded product, and no processed/raw splice can reach the
            // operator. The first block past the cooldown probes again below.
            var openUntil = Interlocked.Read(ref openUntilTimestamp);
            if (openUntil != 0 && Stopwatch.GetTimestamp() < openUntil)
            {
                ApplyRxDryTransition(block48k);
                Interlocked.Increment(ref bypassedBlocks);
                return;
            }

            if (ownerFor(session).TryRoundTrip(
                    block48k,
                    processed,
                    ResponseDeadlineFor(block48k.Length),
                    out _))
            {
                ApplyRxProcessedTransition(
                    block48k,
                    processed.AsSpan(0, block48k.Length));
                Interlocked.Increment(ref processedBlocks);
                Interlocked.Exchange(ref consecutiveMisses, 0);
                if (openUntil != 0
                    && Interlocked.Exchange(ref openUntilTimestamp, 0) != 0)
                {
                    _log.LogInformation("product-audio {Leg} leg resumed after cooldown", leg);
                }
                return;
            }

            // Missed the reply window. A failed probe past cooldown silently
            // extends the open state; otherwise count toward opening the leg.
            // Counted separately from the zero-work bypasses above: this is the
            // only bypass path that waited on the product.
            Interlocked.Increment(ref missedDeadlineBlocks);
            var cooldownTicks = (long)(LegOpenCooldown.TotalSeconds * Stopwatch.Frequency);
            if (openUntil != 0)
            {
                Interlocked.Exchange(
                    ref openUntilTimestamp,
                    Stopwatch.GetTimestamp() + cooldownTicks);
            }
            else if (Interlocked.Increment(ref consecutiveMisses) >= LegOpenMissThreshold
                     && Interlocked.CompareExchange(
                         ref openUntilTimestamp,
                         Stopwatch.GetTimestamp() + cooldownTicks,
                         0) == 0)
            {
                Interlocked.Increment(ref legOpenedCount);
                _log.LogWarning(
                    "product-audio {Leg} leg paused after {Misses} consecutive missed deadlines; passing audio through unprocessed for {CooldownMs} ms",
                    leg, LegOpenMissThreshold, (int)LegOpenCooldown.TotalMilliseconds);
            }
            ApplyRxDryTransition(block48k);
        }
        catch (Exception)
        {
            // The original block is copied only after a full matching reply,
            // so every transport failure remains clean passthrough.
            ApplyRxDryTransition(block48k);
        }
        finally
        {
            Volatile.Write(ref busy, 0);
            session.Release();
        }

        Interlocked.Increment(ref bypassedBlocks);
    }

    private void ApplyRxProcessedTransition(Span<float> dry, ReadOnlySpan<float> processed)
    {
        var priorKind = Volatile.Read(ref _rxLastRenderKind);
        var count = priorKind == 2
            ? Math.Min(ProductAudioTransportPipeline.TransitionSamples, dry.Length)
            : 0;
        if (count > 1)
        {
            var denominator = count - 1f;
            for (var index = 0; index < count; index++)
            {
                var mix = index / denominator;
                dry[index] += (processed[index] - dry[index]) * mix;
            }
            processed[count..].CopyTo(dry[count..]);
        }
        else
        {
            processed.CopyTo(dry);
        }
        Volatile.Write(ref _rxLastRenderedSample, dry[^1]);
        Volatile.Write(ref _rxLastRenderKind, 1);
    }

    private void ApplyRxDryTransition(Span<float> dry)
    {
        if (dry.IsEmpty) return;
        if (Volatile.Read(ref _rxLastRenderKind) == 1)
        {
            var count = Math.Min(ProductAudioTransportPipeline.TransitionSamples, dry.Length);
            if (count > 1)
            {
                var start = Volatile.Read(ref _rxLastRenderedSample);
                var denominator = count - 1f;
                for (var index = 0; index < count; index++)
                {
                    var mix = index / denominator;
                    dry[index] = start + (dry[index] - start) * mix;
                }
            }
        }
        Volatile.Write(ref _rxLastRenderedSample, dry[^1]);
        Volatile.Write(ref _rxLastRenderKind, 2);
    }

    public void Dispose()
    {
        Session? session;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = Interlocked.Exchange(ref _session, null);
        }

        session?.RequestDispose();
    }

    private void ExpirePending(Session session)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session) || session.IsLeased)
                return;
            Volatile.Write(ref _session, null);
        }

        session.RequestDispose();
        _log.LogInformation(
            "product-audio negotiation expired session={SessionId}",
            session.Owner.Endpoint.SessionId);
    }

    private void Detach(Session session, string reason)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
                return;
            Volatile.Write(ref _session, null);
        }

        session.RequestDispose();
        _log.LogInformation(
            "product-audio detached name={ProductName} session={SessionId} reason={Reason}",
            session.Name,
            session.Owner.Endpoint.SessionId,
            reason);
    }

    private static bool CryptographicEquals(string expected, string supplied)
    {
        if (expected.Length != supplied.Length)
            return false;
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            expectedBytes,
            suppliedBytes);
    }

    private sealed class Session(
        string leaseId,
        string name,
        string version,
        int? httpPort,
        AudioRingOwner owner,
        AudioRingOwner rxOwner)
    {
        private int _leased;
        private int _references = 1;
        private int _disposeRequested;

        public string LeaseId { get; } = leaseId;
        public string Name { get; } = name;
        public string Version { get; } = version;
        public int? HttpPort { get; } = httpPort;
        public AudioRingOwner Owner { get; } = owner;
        public AudioRingOwner RxOwner { get; } = rxOwner;
        public ProductAudioTransportPipeline TxPipeline { get; } = new();
        public Timer? PendingTimer { get; set; }
        public bool IsLeased => Volatile.Read(ref _leased) != 0;

        public bool TryLease() => Interlocked.CompareExchange(ref _leased, 1, 0) == 0;

        public bool TryAddRef()
        {
            while (Volatile.Read(ref _disposeRequested) == 0)
            {
                var references = Volatile.Read(ref _references);
                if (references == 0)
                    return false;
                if (Interlocked.CompareExchange(ref _references, references + 1, references) != references)
                    continue;
                if (Volatile.Read(ref _disposeRequested) == 0)
                    return true;
                Release();
                return false;
            }

            return false;
        }

        public void RequestDispose()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
                return;
            PendingTimer?.Dispose();
            if (Interlocked.Decrement(ref _references) == 0)
            {
                Owner.Dispose();
                RxOwner.Dispose();
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static state =>
                    {
                        state.Owner.Dispose();
                        state.RxOwner.Dispose();
                    },
                    this,
                    preferLocal: false);
            }
        }
    }

    private sealed record PendingExpiration(ProductAudioRingPort Port, Session Session);
}

internal enum ProductAudioTransportStatus
{
    Priming,
    Processed,
    DryFallback,
}

/// <summary>
/// Fixed one-block response runway for Station -&gt; Product TX audio. Responses
/// and retained dry blocks are keyed by ring sequence. Every callback advances
/// the render target exactly once; a late response is never retried after its
/// aligned dry block has already been emitted.
/// </summary>
internal sealed class ProductAudioTransportPipeline
{
    internal const int SlotCount = AudioRingProtocol.SlotCount;
    internal const int TransitionSamples = 64;
    internal const int InitialRenderDelayBlocks = 1;
    private readonly float[][] _dry = CreateBuffers();
    private readonly float[][] _processed = CreateBuffers();
    private readonly long[] _drySequences = new long[SlotCount];
    private readonly long[] _processedSequences = new long[SlotCount];
    private readonly int[] _dryCounts = new int[SlotCount];
    private readonly int[] _processedCounts = new int[SlotCount];
    private readonly int[] _processedDelays = new int[SlotCount];
    private long _firstSequence;
    private long _lastRenderedResponse;
    private int _lastProcessingDelay;
    private bool _hasRenderedTarget;
    private ProductAudioTransportStatus _lastTargetStatus;
    private float _lastRenderedSample;

    internal int RenderDelayBlocks => InitialRenderDelayBlocks;
    internal long LastRenderedResponse => _lastRenderedResponse;

    internal void Reset()
    {
        Array.Clear(_drySequences);
        Array.Clear(_processedSequences);
        _firstSequence = 0;
        _lastRenderedResponse = 0;
        _lastProcessingDelay = 0;
        _hasRenderedTarget = false;
        _lastTargetStatus = ProductAudioTransportStatus.Priming;
        _lastRenderedSample = 0f;
    }

    internal void RememberDry(long sequence, ReadOnlySpan<float> block)
    {
        if (_firstSequence == 0) _firstSequence = sequence;
        var index = Index(sequence);
        block.CopyTo(_dry[index]);
        _dryCounts[index] = block.Length;
        Volatile.Write(ref _drySequences[index], sequence);
    }

    internal void RememberProcessed(
        long responseSequence,
        ReadOnlySpan<float> block,
        int processingDelayBlocks)
    {
        var index = Index(responseSequence);
        block.CopyTo(_processed[index]);
        _processedCounts[index] = block.Length;
        _processedDelays[index] = Math.Clamp(processingDelayBlocks, 0, SlotCount - 2);
        Volatile.Write(ref _processedSequences[index], responseSequence);
    }

    internal ProductAudioTransportStatus Render(
        long submittedSequence,
        ReadOnlySpan<float> currentDry,
        Span<float> output)
    {
        if (submittedSequence == _firstSequence)
        {
            currentDry.CopyTo(output);
            RememberRendered(output, ProductAudioTransportStatus.DryFallback);
            return ProductAudioTransportStatus.Priming;
        }

        var targetResponse = submittedSequence - InitialRenderDelayBlocks;
        if (targetResponse < _firstSequence)
        {
            currentDry.CopyTo(output);
            RememberRendered(output, ProductAudioTransportStatus.DryFallback);
            return ProductAudioTransportStatus.Priming;
        }
        _lastRenderedResponse = targetResponse;
        var responseIndex = Index(targetResponse);
        if (Volatile.Read(ref _processedSequences[responseIndex]) == targetResponse
            && _processedCounts[responseIndex] == currentDry.Length)
        {
            _lastProcessingDelay = _processedDelays[responseIndex];
            var alignedSourceSequence = targetResponse - _lastProcessingDelay;
            var alignedDryIndex = Index(alignedSourceSequence);
            var transitionDry = alignedSourceSequence > 0
                && Volatile.Read(ref _drySequences[alignedDryIndex]) == alignedSourceSequence
                && _dryCounts[alignedDryIndex] == currentDry.Length
                ? _dry[alignedDryIndex].AsSpan(0, currentDry.Length)
                : currentDry;
            _processed[responseIndex].AsSpan(0, currentDry.Length).CopyTo(output);
            ApplyTransitionRamp(transitionDry, output, ProductAudioTransportStatus.Processed);
            return ProductAudioTransportStatus.Processed;
        }

        var sourceSequence = targetResponse - _lastProcessingDelay;
        var dryIndex = Index(sourceSequence);
        if (sourceSequence > 0
            && Volatile.Read(ref _drySequences[dryIndex]) == sourceSequence
            && _dryCounts[dryIndex] == currentDry.Length)
        {
            _dry[dryIndex].AsSpan(0, currentDry.Length).CopyTo(output);
        }
        else
        {
            currentDry.CopyTo(output);
        }
        var fallbackDry = sourceSequence > 0
            && Volatile.Read(ref _drySequences[dryIndex]) == sourceSequence
            && _dryCounts[dryIndex] == currentDry.Length
            ? _dry[dryIndex].AsSpan(0, currentDry.Length)
            : currentDry;
        ApplyTransitionRamp(fallbackDry, output, ProductAudioTransportStatus.DryFallback);
        return ProductAudioTransportStatus.DryFallback;
    }

    private void ApplyTransitionRamp(
        ReadOnlySpan<float> currentDry,
        Span<float> output,
        ProductAudioTransportStatus targetStatus)
    {
        var shouldRamp = _hasRenderedTarget && _lastTargetStatus != targetStatus;
        if (shouldRamp)
        {
            var count = Math.Min(TransitionSamples, output.Length);
            if (count > 1)
            {
                var denominator = count - 1f;
                if (targetStatus == ProductAudioTransportStatus.Processed)
                {
                    for (var index = 0; index < count; index++)
                    {
                        var mix = index / denominator;
                        output[index] = currentDry[index]
                            + (output[index] - currentDry[index]) * mix;
                    }
                }
                else
                {
                    var start = _lastRenderedSample;
                    for (var index = 0; index < count; index++)
                    {
                        var mix = index / denominator;
                        output[index] = start + (output[index] - start) * mix;
                    }
                }
            }
        }
        RememberRendered(output, targetStatus);
    }

    private void RememberRendered(
        ReadOnlySpan<float> output,
        ProductAudioTransportStatus status)
    {
        if (!output.IsEmpty) _lastRenderedSample = output[^1];
        _hasRenderedTarget = true;
        _lastTargetStatus = status;
    }

    private static int Index(long sequence) => (int)(sequence & (SlotCount - 1));

    private static float[][] CreateBuffers()
    {
        var buffers = new float[SlotCount][];
        for (var index = 0; index < buffers.Length; index++)
            buffers[index] = new float[AudioRingProtocol.MaxSamplesPerBlock];
        return buffers;
    }
}

public readonly record struct ProductAudioRingSnapshot(
    bool Attached,
    long AttemptedBlocks,
    long ProcessedBlocks,
    long BypassedBlocks,
    long RxAttemptedBlocks = 0,
    long RxProcessedBlocks = 0,
    long RxBypassedBlocks = 0,
    // Blocks that engaged the ring and missed the response deadline. The
    // remainder of the bypass count never touched the ring at all, so
    // `Bypassed - MissedDeadline` is the number of blocks that returned
    // without waiting on the product for anything.
    long MissedDeadlineBlocks = 0,
    long RxMissedDeadlineBlocks = 0,
    // How many times the leg has been taken out of processing after sustained
    // misses. While it is out, audio passes through untouched and the pump does
    // no ring work at all.
    long LegOpenedCount = 0,
    long RxLegOpenedCount = 0);
