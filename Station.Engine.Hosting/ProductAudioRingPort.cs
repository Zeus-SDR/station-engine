// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Station.AudioRing;

namespace Zeus.Server;

/// <summary>
/// Standalone-engine Wave A adapter. It owns a private ring only while a
/// product lease is attached and otherwise preserves the null-port path.
/// </summary>
public sealed class ProductAudioRingPort : IProductTxAudioPort, IDisposable
{
    internal static readonly TimeSpan ResponseDeadline = TimeSpan.FromMilliseconds(4);
    internal static readonly TimeSpan PendingLeaseLifetime = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly ILogger<ProductAudioRingPort> _log;
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
    private bool _disposed;

    public ProductAudioRingPort(ILogger<ProductAudioRingPort> log)
    {
        _log = log;
    }

    public bool Active => Volatile.Read(ref _session)?.IsLeased == true;

    public ProductAudioRingSnapshot Snapshot => new(
        Volatile.Read(ref _session)?.IsLeased == true,
        Interlocked.Read(ref _attemptedBlocks),
        Interlocked.Read(ref _processedBlocks),
        Interlocked.Read(ref _bypassedBlocks),
        Interlocked.Read(ref _rxAttemptedBlocks),
        Interlocked.Read(ref _rxProcessedBlocks),
        Interlocked.Read(ref _rxBypassedBlocks));

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
            || request.Version.Length > 128)
        {
            error = "name and version are required and limited to 128 characters";
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                error = "a product audio host is already attached or negotiating";
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
                owner,
                rxOwner);
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
            return true;
        }
    }

    public async Task HoldLeaseAsync(string leaseId, HttpContext context)
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            if (session is null
                || !CryptographicEquals(session.LeaseId, leaseId)
                || !session.TryLease())
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            session.PendingTimer?.Dispose();
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

    public void ProcessTx(Span<float> block48k)
        => ProcessBlock(
            block48k,
            ref _audioBusy,
            _txProcessed,
            static session => session.Owner,
            ref _attemptedBlocks,
            ref _processedBlocks,
            ref _bypassedBlocks);

    public void ProcessRx(Span<float> block48k)
        => ProcessBlock(
            block48k,
            ref _rxAudioBusy,
            _rxProcessed,
            static session => session.RxOwner,
            ref _rxAttemptedBlocks,
            ref _rxProcessedBlocks,
            ref _rxBypassedBlocks);

    private void ProcessBlock(
        Span<float> block48k,
        ref int busy,
        float[] processed,
        Func<Session, AudioRingOwner> ownerFor,
        ref long attemptedBlocks,
        ref long processedBlocks,
        ref long bypassedBlocks)
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
            if (ownerFor(session).TryRoundTrip(
                    block48k,
                    processed,
                    ResponseDeadline,
                    out _))
            {
                processed.AsSpan(0, block48k.Length).CopyTo(block48k);
                Interlocked.Increment(ref processedBlocks);
                return;
            }
        }
        catch (Exception)
        {
            // The original block is copied only after a full matching reply,
            // so every transport failure remains clean passthrough.
        }
        finally
        {
            Volatile.Write(ref busy, 0);
            session.Release();
        }

        Interlocked.Increment(ref bypassedBlocks);
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
        AudioRingOwner owner,
        AudioRingOwner rxOwner)
    {
        private int _leased;
        private int _references = 1;
        private int _disposeRequested;

        public string LeaseId { get; } = leaseId;
        public string Name { get; } = name;
        public string Version { get; } = version;
        public AudioRingOwner Owner { get; } = owner;
        public AudioRingOwner RxOwner { get; } = rxOwner;
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

public readonly record struct ProductAudioRingSnapshot(
    bool Attached,
    long AttemptedBlocks,
    long ProcessedBlocks,
    long BypassedBlocks,
    long RxAttemptedBlocks = 0,
    long RxProcessedBlocks = 0,
    long RxBypassedBlocks = 0);
