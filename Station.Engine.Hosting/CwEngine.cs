// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// Host-side CW keyer. WDSP's CWX module is not exported by the libwdsp
// binaries we ship today (re-verified 2026-08-23: `nm -D libwdsp.so | grep -i
// CWX` is empty on linux-x64, osx-arm64, and osx-x64), so we generate the IQ here
// instead and push it straight into the protocol-1 TX ring. The shape we
// produce — a single tone at the CW pitch with raised-cosine rise/fall —
// is what piHPSDR's `transmitter_send_cw` ends up emitting on the wire
// anyway, so this is a wire-compatible substitute, not a fallback.
//
// PR 1 of zeus-4np epic: validates the end-to-end keying path (encoder →
// engine → TxIqRing → EP2 → RF). UI, macros, TCI keyer, and sidetone
// monitor land in subsequent PRs.

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Queues text-to-CW jobs and drives them out as host-generated IQ. One
/// instance per process — registered as a singleton hosted service. The
/// engine claims MOX as <see cref="MoxSource.Cwx"/> for the life of each
/// message and releases it on completion; <see cref="AbortAsync"/> is the
/// hard cut for operator override, <see cref="StopAsync(CancellationToken)"/>
/// is the graceful shutdown path used by host shutdown.
///
/// Audio shape: a 600 Hz tone (sign-flipped for CWL) at 48 kHz, envelope
/// shaped with a 5 ms raised-cosine on each edge to keep the on-air signal
/// inside the CW passband. Amplitude is full-scale (operator drives PA
/// power via the normal drive % slider, not here).
/// </summary>
public sealed class CwEngine : BackgroundService
{
    // P1 TX sample rate: the EP2 packer drains TxIqRing at 48 kHz (see
    // Zeus.Protocol1.TxIqRing class doc). P2 does NOT read the ring — its
    // 1029-port DUC stream runs at the TXA output rate (192 kHz on the G2)
    // and is fed via DspPipelineService.ForwardTxIqToP2, the same seam
    // TxTuneDriver and TxAudioIngest use. The live rate is resolved per job
    // from the DSP engine (ResolveTxOutputRateHz); this constant is the P1
    // value and the fallback when no engine is loaded.
    public const int SampleRateHz = 48_000;
    // Raised-cosine ramp on each key edge. 5 ms ≈ ±100 Hz of skirt energy at
    // the CW pitch — well under the WDSP RX CW bandpass (250 Hz wide). Below
    // 2 ms produces audible clicks on the air; above 10 ms starts to round
    // off short dits at 30+ WPM.
    private const int RampMs = 5;
    // Speed bounds. 5 WPM = 240 ms dit (very slow CW); 50 WPM = 24 ms dit
    // (faster than most hand keys can copy). Clamping at ingest keeps the
    // ramp/dit math sane and prevents divide-by-tiny artefacts.
    private const int WpmMin = 5;
    private const int WpmMax = 50;
    private const int WpmDefault = 20;

    // Chunk length used by the playback loop: 10 ms of IQ at the active TX
    // rate (480 samples at 48 kHz, 1920 at 192 kHz) — small enough that the
    // P1 ring stays well under its 340 ms drop-oldest threshold, large enough
    // that the loop only wakes 100 times per second of TX.
    private const int ChunkMs = 10;
    // How far production runs ahead of real time. Covers coarse OS timer
    // granularity (~15.6 ms on Windows) so the radio FIFO never starves
    // between wakeups. The P2 sender queue is unbounded and paces itself to
    // the DAC, and the P1 ring holds 340 ms, so the lead is latency, not loss.
    private const int LeadMs = 20;

    private readonly TxService _tx;
    private readonly RadioService _radio;
    private readonly TxIqRing _ring;
    private readonly DspPipelineService? _pipeline;
    private readonly StreamingHub _hub;
    private readonly CwSettingsStore _settings;
    private readonly CwSidetoneSource? _sidetone;
    private readonly ILogger<CwEngine> _log;
    private readonly Channel<CwJob> _jobs = Channel.CreateUnbounded<CwJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    // .NET's default unbounded channel does NOT implement Reader.Count
    // (CanCount=false), so calling _jobs.Reader.Count throws
    // NotSupportedException. We need the depth for status reporting and
    // idle gating, so track it ourselves via Interlocked. Incremented at
    // the writer (SendAsync, Abort no-op), decremented after a successful
    // worker read.
    private int _pendingJobs;

    // Aborts the currently-playing job. Recreated on each new job so abort
    // doesn't poison the next send.
    private CancellationTokenSource? _currentAbort;
    // True only while a raw-key job (TCI keyer:1) is playing. Read under
    // _abortLock so RawKeyAsync(false) can decide whether the in-flight
    // cancel applies — keyer:0 must not truncate an unrelated text send.
    private bool _currentIsRawKey;
    private string? _currentRemoteTxLeaseId;
    // Identity of the job the worker has dequeued. Cleared with _currentAbort.
    // Status after that clear uses the job's own id, not this field.
    private string? _currentJobId;
    private readonly object _abortLock = new();
    // Ids cancelled before or after they land. Bounded so a page that closes
    // and retries cannot grow this without limit. A global abort tombstones
    // the ids it drained so a late retry of the same id does not key.
    private const int CancelledJobCapacity = 64;
    private readonly Queue<string> _cancelledOrder = new();
    private readonly HashSet<string> _cancelledIds = new(StringComparer.Ordinal);
    // Every Abort bumps this. A send that names a different value arrived
    // after HALT and must not be queued. Clients that omit it are unchanged.
    private int _abortSeq;
    // Set when MOX falls under a job this engine did not abort. The worker's
    // final Idle carries the reason. Abort() moves _abortSeq and does not.
    private int _moxDropCancel;
    // Element key-downs (and raw-key closures) that passed the abort fence
    // and started an envelope. MOX alone is not a key-down. Tests assert a
    // job created before HALT stays at zero.
    private int _keyAttempts;
    private int _moxRiseAttempts;
    // True only after an element key-down has passed the abort fence.
    // Abort clears it under _abortLock so an in-flight envelope goes silent.
    private volatile bool _elementKeyOpen;
    private int _jobsFinished;
    // Monotonic order of status decisions. A publish whose sequence is no
    // longer the latest is dropped, so Aborting cannot follow a final Idle.
    private long _statusSeq;
    private long _emittedStatusSeq;
    private int _terminalIdleEmitted;
    private readonly object _statusEmitLock = new();

    /// <summary>Abort counter. Zero until the first abort. Included on every status.</summary>
    public int AbortSeq => Volatile.Read(ref _abortSeq);

    /// <summary>
    /// Test seam. The worker calls this after a job is dequeued and its
    /// cancellation is registered, before the abort-seq check and any
    /// key-down. Tests block here, abort, then release. Null in production.
    /// </summary>
    internal Action? BeforeKeyForTest;

    /// <summary>
    /// Test seam between the final abort check and <c>TrySetMox(true)</c>.
    /// Tests abort here. Null in production.
    /// </summary>
    internal Action? BeforeMoxForTest;

    /// <summary>
    /// Test seam at each element key-down, and at the raw-key closure,
    /// before the abort fence decides whether the envelope may start.
    /// Tests abort here between elements. Null in production.
    /// </summary>
    internal Action? BeforeElementForTest;

    /// <summary>
    /// Test seam after <c>TrySetMox(true)</c> returns and the abort recheck
    /// has passed, before the post-MOX receiver check. The TX transition
    /// lock is not held. Null in production.
    /// </summary>
    internal Action? AfterMoxForTest;

    /// <summary>Explicit receiver is outside 0..MaxReceivers-1 and is not the Kiwi index.</summary>
    internal const string ReceiverOutOfRangeReason = "receiver-out-of-range";

    /// <summary>Kiwi index, or a named non-hardware slice. Not a local DDC.</summary>
    internal const string ReceiverRemoteReason = "receiver-remote";

    /// <summary>Index is in range but that slice is not enabled.</summary>
    internal const string ReceiverUnavailableReason = "receiver-unavailable";

    /// <summary>Accept-time: the slice exists but it is not the TX receiver.</summary>
    internal const string ReceiverNotSelectedReason = "receiver-not-selected";

    /// <summary>Bound slice is not CWU or CWL.</summary>
    internal const string ReceiverModeReason = "receiver-mode";

    /// <summary>Queued send: TX no longer points at the bound receiver.</summary>
    internal const string ReceiverMovedReason = "receiver-moved";

    /// <summary>
    /// Test seam after Abort has cancelled the in-flight job and before it
    /// publishes status. The worker can publish its final Idle here. Null
    /// in production.
    /// </summary>
    internal Action? BeforeAbortStatusForTest;

    /// <summary>Tests: element key-downs and raw-key closures that started an envelope.</summary>
    internal int KeyAttemptsForTest => Volatile.Read(ref _keyAttempts);

    /// <summary>Tests: times TrySetMox(true) was called. An abort in the pre-MOX seam stays at zero.</summary>
    internal int MoxRiseAttemptsForTest => Volatile.Read(ref _moxRiseAttempts);

    /// <summary>Tests: the worker has emitted the terminal Idle.</summary>
    internal bool TerminalIdleEmittedForTest => Volatile.Read(ref _terminalIdleEmitted) != 0;

    /// <summary>Tests: jobs the worker has finished, keyed or dropped.</summary>
    internal int JobsFinishedForTest => Volatile.Read(ref _jobsFinished);

    /// <summary>Raised on every state transition. Subscribers must not block;
    /// fired from the playback worker thread.</summary>
    public event Action<CwEngineStatus>? Status;

    public CwEngine(TxService tx, RadioService radio, TxIqRing ring, StreamingHub hub, CwSettingsStore settings, ILogger<CwEngine> log, CwSidetoneSource? sidetone = null, DspPipelineService? pipeline = null)
    {
        _tx = tx;
        _radio = radio;
        _ring = ring;
        _pipeline = pipeline;
        _hub = hub;
        _settings = settings;
        _sidetone = sidetone;
        _log = log;
        // Drop the current send if the operator overrides MOX from the UI
        // (or a trip happens). Owner==null on the falling edge means MOX
        // was just released; if it wasn't us doing the release, cancel.
        _tx.TxActiveChanged += OnTxActiveChanged;
        _tx.RemoteTxLeaseRevoked += OnRemoteTxLeaseRevoked;
        // Bridge the in-process Status event onto the wire so connected
        // clients (the React macro pad) see state edges without polling.
        Status += BroadcastStatus;
    }

    /// <summary>
    /// Enqueue <paramref name="text"/> for transmission at <paramref name="wpm"/>.
    /// When <paramref name="wpm"/> is null the engine reads the operator's
    /// persisted default from <see cref="CwSettingsStore"/>; if the store
    /// hasn't been initialised yet (test seam) it falls back to
    /// <see cref="WpmDefault"/>. Returns immediately; keying happens on
    /// the worker thread. <paramref name="receiver"/> null keeps the legacy
    /// global-TX send. An explicit index is carried on the job and is not
    /// used to select the TX receiver.
    /// </summary>
    public ValueTask SendAsync(
        string text,
        int? wpm,
        CancellationToken ct,
        string? remoteTxLeaseId = null,
        int? expectedAbortSeq = null,
        int? receiver = null,
        string? jobId = null)
    {
        TryEnqueueSend(text, wpm, ct, remoteTxLeaseId, expectedAbortSeq, receiver, jobId);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Accept one text send. Null means the job was queued. <c>halted</c> is
    /// an abort-seq mismatch and is still reported as status, not as a
    /// receiver refusal. Any <c>receiver-*</c> value was not queued.
    /// </summary>
    internal string? TryEnqueueSend(
        string text,
        int? wpm,
        CancellationToken ct,
        string? remoteTxLeaseId = null,
        int? expectedAbortSeq = null,
        int? receiver = null,
        string? jobId = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ct.ThrowIfCancellationRequested();
        int requested = wpm ?? _settings?.Get().Wpm ?? WpmDefault;
        int effective = Math.Clamp(requested, WpmMin, WpmMax);
        // Empty keeps the legacy send. Anything else must be a canonical id.
        string? identity = string.IsNullOrEmpty(jobId) ? null : jobId;
        // Check and enqueue under the same lock Abort uses to bump the
        // counter and drain the queue. A send that passed the check and
        // then lost the race would key after both aborts. HALT wins over a
        // bad receiver: the client already asked not to queue.
        string? refusal = null;
        int depth;
        bool quietRefusal = false;
        lock (_abortLock)
        {
            if (identity is not null && !CwJobIds.IsValid(identity))
                refusal = InvalidJobIdReason;
            else if (identity is not null && _cancelledIds.Contains(identity))
                refusal = CancelledJobReason;
            else if (expectedAbortSeq is int expected && expected != _abortSeq)
                refusal = "halted";
            else if (receiver is int bound)
                refusal = ClassifyAccept(_radio.Snapshot(), bound);
            if (refusal is null && _jobs.Writer.TryWrite(
                new CwJob(text, effective, false, null, remoteTxLeaseId, _abortSeq, receiver, identity)))
            {
                Interlocked.Increment(ref _pendingJobs);
            }
            depth = Volatile.Read(ref _pendingJobs);
            // A late retry of a cancelled id must not replace a foreign
            // Sending snapshot with Idle. The owner already released it.
            quietRefusal = refusal is (InvalidJobIdReason or CancelledJobReason)
                && (_currentAbort is not null || depth > 0);
        }
        if (refusal is not null)
        {
            if (refusal == "halted")
            {
                _log.LogInformation(
                    "cw.send.refused expectedAbortSeq={Expected} abortSeq={Seq} text={Text}",
                    expectedAbortSeq, AbortSeq, Truncate(text));
            }
            else
            {
                _log.LogInformation(
                    "cw.send.refused receiver={Receiver} reason={Reason} text={Text}",
                    receiver, refusal, Truncate(text));
            }
            if (!quietRefusal)
            {
                PublishStatus(
                    CwEngineState.Idle, text, effective, depth, refusal,
                    CwJobIds.IsValid(identity) ? identity : null);
            }
        }
        return refusal;
    }

    /// <summary>
    /// Manual key-down / key-up entry point used by the TCI <c>keyer:rx,bool</c>
    /// command. Key-down enqueues a raw-key job that emits steady carrier
    /// (with raised-cosine attack/release) until <paramref name="durationMs"/>
    /// expires or a matching key-up arrives. Key-up cancels the in-flight
    /// raw-key job only — a parallel <see cref="SendAsync"/> in progress is
    /// preserved so a stray <c>keyer:0</c> from a contest logger doesn't
    /// truncate a macro mid-message.
    /// </summary>
    public ValueTask RawKeyAsync(bool keyDown, int? durationMs, CancellationToken ct)
    {
        if (keyDown)
        {
            ct.ThrowIfCancellationRequested();
            lock (_abortLock)
            {
                if (_jobs.Writer.TryWrite(
                    new CwJob(string.Empty, 0, RawKeyDown: true, DurationMs: durationMs, RemoteTxLeaseId: null, CreatedAbortSeq: _abortSeq)))
                {
                    Interlocked.Increment(ref _pendingJobs);
                }
            }
            return ValueTask.CompletedTask;
        }

        // Selective cancel — see _currentIsRawKey field doc.
        lock (_abortLock)
        {
            if (_currentIsRawKey && _currentAbort is { } cts)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { /* race with worker */ }
            }
        }
        return ValueTask.CompletedTask;
    }

    private void BroadcastStatus(CwEngineStatus s)
    {
        try { _hub.Broadcast(CwEngineStatusFrame.FromStatus(s)); }
        catch (Exception ex) { _log.LogWarning(ex, "cw.status hub broadcast failed"); }
    }

    /// <summary>Hard cancel. Drains the queue and signals the in-flight
    /// playback to drop the rest of the symbols. MOX falls on the next
    /// playback tick.</summary>
    public int Abort(string reason = "operator abort")
    {
        // Drain under the same lock as SendAsync's accept check. The counter
        // moves first, so a send that arrives while we drain sees the new value.
        // Cancel and force key-up under that same lock. TrySetMox waits on
        // the TX transition lock, so this lock cannot be held across it; the
        // element key-down reads the counter before any envelope starts.
        int seq;
        long statusSeq;
        CwEngineStatus status;
        lock (_abortLock)
        {
            seq = Interlocked.Increment(ref _abortSeq);
            while (_jobs.Reader.TryRead(out var dropped))
            {
                Interlocked.Decrement(ref _pendingJobs);
                RememberCancelled(dropped.JobId);
            }
            RememberCancelled(_currentJobId);
            _elementKeyOpen = false;
            _sidetone?.Up();
            var cts = _currentAbort;
            try { cts?.Cancel(); }
            catch (ObjectDisposedException) { /* race with worker disposal */ }
            // Claim the status while the in-flight job is still visible.
            // A worker that has already published its final Idle cleared
            // _currentAbort under this lock, so this claim is Idle too.
            var state = cts is not null ? CwEngineState.Aborting : CwEngineState.Idle;
            statusSeq = ++_statusSeq;
            status = MakeStatus(
                state, string.Empty, 0, Volatile.Read(ref _pendingJobs), reason);
        }
        _log.LogInformation("cw.abort reason={Reason} abortSeq={Seq}", reason, seq);
        // The worker can finish and publish Idle before this status is emitted.
        BeforeAbortStatusForTest?.Invoke();
        // Drop this claim when a newer status (the worker's final Idle) has
        // already been emitted. Last writer by status sequence wins.
        EmitStatus(statusSeq, status);
        return seq;
    }

    /// <summary>Tests only: snapshot queue depth without taking work.</summary>
    internal int PendingJobCount => Volatile.Read(ref _pendingJobs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _jobs.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) { return; }

            // Dequeue and register the cancellation under the same lock Abort
            // uses to drain the queue. A job taken before it is registered
            // is in neither place, so HALT misses it and it keys.
            CwJob job;
            CancellationTokenSource jobCts;
            int remaining;
            lock (_abortLock)
            {
                if (!_jobs.Reader.TryRead(out job)) continue;
                remaining = Interlocked.Decrement(ref _pendingJobs);
                jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _currentAbort = jobCts;
                _currentJobId = job.JobId;
                _currentIsRawKey = job.RawKeyDown;
                _currentRemoteTxLeaseId = job.RemoteTxLeaseId;
            }
            // Outside the lock so a test can abort here without deadlocking.
            // The seq check below drops a job created before that abort.
            // A MOX drop from the previous job must not label this one.
            Interlocked.Exchange(ref _moxDropCancel, 0);
            BeforeKeyForTest?.Invoke();
            // A refused key-down already reported Idle carrying the job text
            // (plus the reason); that frame is how clients tell "not keyed"
            // from "finished", so don't overwrite it with a bare Idle.
            // The final Idle keeps the cancel reason. Clients often see only
            // that Idle: the Aborting frame is gone by the time they render.
            bool refused = false;
            string? endReason = null;
            try
            {
                if (SupersededByAbort(job, jobCts.Token))
                    throw new OperationCanceledException(jobCts.Token);
                refused = job.RawKeyDown
                    ? !await PlayRawKeyAsync(job, remaining, jobCts.Token).ConfigureAwait(false)
                    : !await PlayJobAsync(job, remaining, jobCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Operator abort moves the counter. A MOX drop that does not
                // (UI unkey, a trip) still has to say so on the final Idle.
                var droppedByMox = Interlocked.Exchange(ref _moxDropCancel, 0) == 1
                    && job.CreatedAbortSeq == Volatile.Read(ref _abortSeq);
                bool scopedCancel;
                lock (_abortLock)
                    scopedCancel = job.JobId is not null && _cancelledIds.Contains(job.JobId);
                var seqMoved = job.CreatedAbortSeq != Volatile.Read(ref _abortSeq);
                endReason = droppedByMox
                    ? TxDroppedReason
                    : scopedCancel && !seqMoved ? CancelledJobReason : "aborted";
                PublishStatus(
                    CwEngineState.Aborting, job.Text, job.Wpm,
                    Volatile.Read(ref _pendingJobs),
                    endReason,
                    job.JobId);
                TryReleaseMox(endReason, job.RemoteTxLeaseId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "cw.play failed text={Text} wpm={Wpm}", Truncate(job.Text), job.Wpm);
                TryReleaseMox("error", job.RemoteTxLeaseId);
            }
            finally
            {
                lock (_abortLock)
                {
                    if (ReferenceEquals(_currentAbort, jobCts))
                    {
                        _currentAbort = null;
                        _currentJobId = null;
                    }
                    _currentIsRawKey = false;
                    _currentRemoteTxLeaseId = null;
                    _elementKeyOpen = false;
                }
                jobCts.Dispose();
                Interlocked.Increment(ref _jobsFinished);
            }

            // Emit Idle once the queue actually drains. Mid-queue jobs roll
            // into the next iteration without a flicker. Sequenced with
            // Abort's status so a late Aborting cannot replace this Idle.
            if (!refused && Volatile.Read(ref _pendingJobs) == 0)
                PublishStatus(CwEngineState.Idle, string.Empty, 0, 0, endReason, job.JobId);
        }
    }

    private void OnRemoteTxLeaseRevoked(string leaseId)
    {
        lock (_abortLock)
        {
            if (!string.Equals(_currentRemoteTxLeaseId, leaseId, StringComparison.Ordinal)) return;
            try { _currentAbort?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// True when HALT has moved the counter since this job was queued, or
    /// the job's cancellation is already signalled. Checked again immediately
    /// before MOX so a job created before the latest abort never keys.
    /// </summary>
    private bool SupersededByAbort(CwJob job, CancellationToken ct)
    {
        lock (_abortLock)
            return job.CreatedAbortSeq != _abortSeq || ct.IsCancellationRequested;
    }

    /// <summary>
    /// Raise MOX, then drop it at once if HALT landed while <c>TrySetMox</c>
    /// waited on the TX transition lock. That lock and <see cref="_abortLock"/>
    /// cannot be held together: the transition waits inside <c>TrySetMox</c>,
    /// and its falling edge takes <see cref="_abortLock"/>. MOX by itself is
    /// not CW RF. The authoritative fence is <see cref="TryElementKeyDown"/>,
    /// at the moment an element envelope or the raw-key closure starts.
    /// </summary>
    /// <returns>False when MOX was refused and nothing was keyed.</returns>
    private bool TryCommitCarrier(CwJob job, CancellationToken ct, out string? err)
    {
        err = null;
        lock (_abortLock)
        {
            if (CarrierBlockedUnderLock(job, ct))
                throw new OperationCanceledException(ct);
        }
        if (QueuedReceiverRefusal(job) is { } beforeHook)
        {
            err = beforeHook;
            return false;
        }
        // Outside the lock so a test can abort here without deadlocking.
        BeforeMoxForTest?.Invoke();
        lock (_abortLock)
        {
            if (CarrierBlockedUnderLock(job, ct))
                throw new OperationCanceledException(ct);
        }
        // The hook can move TX after the check above. That must not reach
        // TrySetMox, and it is not an abort.
        if (QueuedReceiverRefusal(job) is { } afterHook)
        {
            err = afterHook;
            return false;
        }

        Interlocked.Increment(ref _moxRiseAttempts);
        bool keyed = job.RemoteTxLeaseId is null
            ? _tx.TrySetMox(true, MoxSource.Cwx, out err)
            : _tx.TrySetRemoteMox(true, job.RemoteTxLeaseId, MoxSource.Cwx, out err);

        // Re-check after the rise. An abort that landed while TrySetMox
        // held the transition lock is dropped before any element keys.
        bool aborted;
        lock (_abortLock)
            aborted = CarrierBlockedUnderLock(job, ct);
        if (aborted)
        {
            // Lock is released before the drop. TrySetMox takes TxService's
            // transition lock and can call back into _abortLock.
            if (keyed) DropCarrier(job);
            throw new OperationCanceledException(ct);
        }

        // Transition lock is free here. A receiver move is dropped before
        // host keying or the first element. Do not call TryReleaseMox:
        // host CW keying has not been armed yet.
        AfterMoxForTest?.Invoke();
        if (QueuedReceiverRefusal(job) is { } afterMox)
        {
            if (keyed) DropCarrier(job);
            err = afterMox;
            return false;
        }
        return keyed;
    }

    /// <summary>
    /// Authoritative fence. Called at the moment an element envelope starts,
    /// and at the raw-key closure, with <see cref="_abortLock"/> held for the
    /// seq check. False with a null <paramref name="receiverRefusal"/> means
    /// HALT has landed and the caller must abort. A <c>receiver-*</c> value
    /// means this element must not key and must not be reported as aborted.
    /// </summary>
    private bool TryElementKeyDown(CwJob job, CancellationToken ct, out string? receiverRefusal)
    {
        receiverRefusal = null;
        lock (_abortLock)
        {
            if (CarrierBlockedUnderLock(job, ct))
            {
                _elementKeyOpen = false;
                _sidetone?.Up();
                return false;
            }
            if (job.Receiver is int bound)
            {
                var refusal = ClassifyQueued(_radio.Snapshot(), bound);
                if (refusal is not null)
                {
                    receiverRefusal = refusal;
                    _elementKeyOpen = false;
                    _sidetone?.Up();
                    return false;
                }
            }
            _elementKeyOpen = true;
            return true;
        }
    }

    /// <summary>Caller holds <see cref="_abortLock"/>.</summary>
    private bool CarrierBlockedUnderLock(CwJob job, CancellationToken ct)
        => job.CreatedAbortSeq != _abortSeq || ct.IsCancellationRequested;

    private void DropCarrier(CwJob job)
    {
        if (job.RemoteTxLeaseId is null)
            _tx.TrySetMox(false, MoxSource.Cwx, out _);
        else
            _tx.TrySetRemoteMox(false, job.RemoteTxLeaseId, MoxSource.Cwx, out _);
    }

    /// <returns>False when MOX was refused and nothing was keyed.</returns>
    private async Task<bool> PlayJobAsync(CwJob job, int queueDepth, CancellationToken ct)
    {
        if (SupersededByAbort(job, ct))
            throw new OperationCanceledException(ct);
        // A bound job whose TX receiver moved must return before this retune.
        // AlignLoForCwTx follows the receiver that owns TX now, so a queued
        // RX3 send would otherwise move the operator's new RX1.
        if (RefuseBeforeTransmit(job, queueDepth, job.Text, job.Wpm))
            return false;
        // Before keying, force the hardware LO to the canonical CW offset
        // of the dial — eliminates CTUN drift so the carrier lands on the
        // operator's displayed VFO. No-op when CTUN wasn't in play; when it
        // was, the panadapter view recenters, which matches Thetis CW-TX
        // behaviour (the CTUN convenience is RX-only).
        bool loRealigned = _radio.AlignLoForCwTx();

        var snap = _radio.Snapshot();
        // P1 baseband follows the aligned shared LO. P2 has an independent TX
        // DUC and deliberately leaves the RX DDC parked for display DUP, so its
        // baseband must follow the TX DUC rather than RadioLoHz.
        //
        // Sign note (HL2 IQ convention): the HL2 emits the RF carrier at
        // (LO − baseband_hz), not (LO + baseband_hz) — i.e. the "I − jQ"
        // complex-baseband convention. Verified on a live HL2 2026-05-24
        // (EA5IUE bench test).
        long txHz = RadioFrequencyResolver.TxFrequencyHz(snap);
        int basebandHz = ResolveBasebandHz(snap);

        if (!TryCommitCarrier(job, ct, out var err))
        {
            _log.LogWarning("cw.mox.refused text={Text} reason={Err}", Truncate(job.Text), err);
            PublishStatus(
                CwEngineState.Idle, job.Text, job.Wpm, queueDepth,
                err ?? "MOX refused", job.JobId);
            return false;
        }
        // Host CW now owns the air — disarm the P2 internal keyer so the
        // gateware doesn't self-key against this host-keyed send (#1032).
        _radio.SetHostCwKeying(true);
        PublishStatus(CwEngineState.Sending, job.Text, job.Wpm, queueDepth, jobId: job.JobId);
        var pump = new IqPump(_ring, ForwardToDuc, ResolveTxRateHz(snap), basebandHz);
        _log.LogInformation(
            "cw.send text={Text} wpm={Wpm} mode={Mode} txVfo={TxVfo} txHz={TxHz}Hz lo={Lo}Hz baseband={Bb}Hz rate={Rate}Hz p2Forward={P2} loRealigned={LoRealigned}",
            Truncate(job.Text), job.Wpm, snap.Mode, snap.TxVfo, txHz, snap.RadioLoHz, basebandHz, pump.RateHz, _pipeline is not null, loRealigned);

        try
        {
            foreach (var symbol in MorseEncoder.Encode(job.Text, job.Wpm))
            {
                ct.ThrowIfCancellationRequested();
                // The fence is the envelope, not MOX. Every key-down checks
                // the job's abort seq under _abortLock before the first
                // non-zero sample. A key-up is silence and does not key.
                if (symbol.KeyDown)
                {
                    BeforeElementForTest?.Invoke();
                    if (!TryElementKeyDown(job, ct, out var receiverRefusal))
                    {
                        if (receiverRefusal is not null)
                        {
                            // Host keying is already armed and MOX is up.
                            TryReleaseMox(receiverRefusal, job.RemoteTxLeaseId);
                            PublishStatus(
                                CwEngineState.Idle, job.Text, job.Wpm, queueDepth, receiverRefusal, job.JobId);
                            return false;
                        }
                        throw new OperationCanceledException(ct);
                    }
                    Interlocked.Increment(ref _keyAttempts);
                    _sidetone?.Down();
                }
                else
                {
                    lock (_abortLock) _elementKeyOpen = false;
                    _sidetone?.Up();
                }

                int totalSamples = (int)((long)symbol.DurationMs * pump.RateHz / 1000);
                int rampSamples = Math.Min(pump.RampSamples, totalSamples / 2);

                int written = 0;
                while (written < totalSamples)
                {
                    ct.ThrowIfCancellationRequested();
                    // TryElementKeyDown runs once per key-down. A 5 WPM dash
                    // is many 10 ms chunks, and neither a TX move nor a mode
                    // change cancels this job. Re-check each chunk. A null
                    // receiver returns before any radio read. RefuseBeforeTransmit
                    // publishes Idle and does not drop MOX, so an active mark
                    // has to close its own envelope and release the carrier.
                    // Do not Abort: a later queued job must still be played.
                    if (QueuedReceiverRefusal(job) is { } chunkRefusal)
                    {
                        // Snapshot already ran outside this lock. MOX release
                        // takes the TX transition lock, whose falling edge
                        // re-enters _abortLock, so the envelope closes here
                        // and the drop happens after the lock is released.
                        lock (_abortLock)
                        {
                            _elementKeyOpen = false;
                            _sidetone?.Up();
                        }
                        TryReleaseMox(chunkRefusal, job.RemoteTxLeaseId);
                        PublishStatus(
                            CwEngineState.Idle, job.Text, job.Wpm, queueDepth, chunkRefusal, job.JobId);
                        return false;
                    }
                    int n = Math.Min(pump.ChunkSamples, totalSamples - written);
                    int start = written;
                    // Abort forces key-up under _abortLock. Samples already
                    // inside this chunk go silent; the next chunk sees the cancel.
                    pump.Emit(n, i => symbol.KeyDown && _elementKeyOpen
                        ? RaisedCosineEnvelope(start + i, totalSamples, rampSamples)
                        : 0.0);
                    written += n;
                    await pump.PaceAsync(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // End-of-message and cancellation share this path so a mid-message
            // abort still releases the sidetone — otherwise the mixer would
            // sit "keyed" until the next CW send arrived.
            _sidetone?.Up();
        }

        // Drop MOX after the final symbol, once the production lead has
        // played out, so the radio actually transmits the envelope tail.
        // Not cancellable: every symbol is already produced, so a STOP here
        // must not relabel a complete message as aborted.
        await pump.DrainAsync(CancellationToken.None).ConfigureAwait(false);
        TryReleaseMox("done", job.RemoteTxLeaseId);
        return true;
    }

    /// <summary>
    /// Steady-carrier playback for the TCI <c>keyer:1</c> path. Same envelope
    /// shaper as <see cref="PlayJobAsync"/> (raised-cosine attack, plateau,
    /// raised-cosine release on cancel or duration-end), but no Morse
    /// encoding — just one long key-down symbol that ends on
    /// <paramref name="ct"/> cancellation or after <c>job.DurationMs</c>
    /// elapses, whichever comes first.
    /// </summary>
    private async Task<bool> PlayRawKeyAsync(CwJob job, int queueDepth, CancellationToken ct)
    {
        if (SupersededByAbort(job, ct))
            throw new OperationCanceledException(ct);
        // Raw-key jobs are unbound today. The same guard still runs first so
        // a bound raw job cannot retune a receiver the operator has left.
        if (RefuseBeforeTransmit(job, queueDepth, string.Empty, 0))
            return false;
        // Same LO-align and baseband math as PlayJobAsync — keep them in
        // step so the carrier lands at the operator's dial regardless of
        // the keying source (text macro vs. raw key from logger).
        bool loRealigned = _radio.AlignLoForCwTx();
        var snap = _radio.Snapshot();
        long txHz = RadioFrequencyResolver.TxFrequencyHz(snap);
        int basebandHz = ResolveBasebandHz(snap);

        if (!TryCommitCarrier(job, ct, out var err))
        {
            _log.LogWarning("cw.mox.refused keyer reason={Err}", err);
            PublishStatus(
                CwEngineState.Idle, string.Empty, 0, queueDepth,
                err ?? "MOX refused");
            return false;
        }
        // Host CW (raw keyer/logger) owns the air — disarm the P2 internal
        // keyer for the duration so the gateware doesn't self-key too (#1032).
        _radio.SetHostCwKeying(true);
        PublishStatus(CwEngineState.Sending, "<keyer>", 0, queueDepth);
        var pump = new IqPump(_ring, ForwardToDuc, ResolveTxRateHz(snap), basebandHz);
        _log.LogInformation(
            "cw.keyer.down txVfo={TxVfo} txHz={TxHz}Hz baseband={Bb}Hz rate={Rate}Hz durationMs={Dur} loRealigned={LoR}",
            snap.TxVfo, txHz, basebandHz, pump.RateHz, job.DurationMs?.ToString() ?? "until-release", loRealigned);

        int rampSamples = pump.RampSamples;

        // No upper bound when the operator wants to hold the key indefinitely —
        // ct + keyer:0 are the release path. With a duration, we play exactly
        // that many samples then release with the same fade-out shape.
        int? totalSamples = job.DurationMs.HasValue
            ? (int)((long)job.DurationMs.Value * pump.RateHz / 1000)
            : null;

        int written = 0;
        bool releasedByCancel = false;
        // The raw-key closure is the same fence as an element key-down.
        // HALT that landed after MOX, or while we were between checks,
        // refuses the carrier before the first non-zero sample.
        BeforeElementForTest?.Invoke();
        if (!TryElementKeyDown(job, ct, out var rawRefusal))
        {
            if (rawRefusal is not null)
            {
                TryReleaseMox(rawRefusal, job.RemoteTxLeaseId);
                PublishStatus(CwEngineState.Idle, string.Empty, 0, queueDepth, rawRefusal);
                return false;
            }
            throw new OperationCanceledException(ct);
        }
        Interlocked.Increment(ref _keyAttempts);
        // Sidetone follows the raw key for its whole held duration. Down here,
        // Up in the finally so a cancel / duration-expiry / error all release
        // the monitor tone; the DSP-thread mixer runs its own 5 ms fade so the
        // Up lands in lockstep with the carrier release tail below.
        _sidetone?.Down();
        try
        {
            while (!totalSamples.HasValue || written < totalSamples.Value)
            {
                if (ct.IsCancellationRequested) { releasedByCancel = true; break; }
                int n = totalSamples.HasValue
                    ? Math.Min(pump.ChunkSamples, totalSamples.Value - written)
                    : pump.ChunkSamples;
                int start = written;
                // Attack region only — once past rampSamples we plateau at 1.0
                // until release. Abort clears _elementKeyOpen under the abort
                // lock, so the rest of this chunk is silence.
                pump.Emit(n, i =>
                {
                    if (!_elementKeyOpen) return 0.0;
                    int pos = start + i;
                    return pos < rampSamples
                        ? 0.5 * (1.0 - Math.Cos(Math.PI * pos / rampSamples))
                        : 1.0;
                });
                written += n;
                try { await pump.PaceAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { releasedByCancel = true; break; }
            }
        }
        finally
        {
            _sidetone?.Up();
        }

        // Release tail — raised-cosine fall from 1.0 to 0, only when the
        // key is still closed. HALT already forced key-up; a tail after
        // that would be CW RF. A normal key-up keeps the fade so the edge
        // does not click. Non-cancellable so the fade itself is not cut.
        if (_elementKeyOpen)
        {
            for (int chunkStart = 0; chunkStart < rampSamples; chunkStart += pump.ChunkSamples)
            {
                int n = Math.Min(pump.ChunkSamples, rampSamples - chunkStart);
                int start = chunkStart;
                pump.Emit(n, i => _elementKeyOpen
                    ? 0.5 * (1.0 - Math.Cos(Math.PI * (rampSamples - (start + i)) / rampSamples))
                    : 0.0);
                await pump.PaceAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        await pump.DrainAsync(CancellationToken.None).ConfigureAwait(false);
        TryReleaseMox(releasedByCancel ? "keyer.up" : "keyer.duration", job.RemoteTxLeaseId);
        return true;
    }

    /// <summary>
    /// TX output rate of the active DSP engine: the TXA input block is
    /// always 48 kHz mic audio, so the output rate is 48 kHz scaled by the
    /// output/input block ratio (1:1 on P1, 4:1 → 192 kHz on P2). Falls
    /// back to the P1 rate when no engine is loaded.
    /// </summary>
    internal delegate void TxIqForward(ReadOnlySpan<float> iqInterleaved);

    private TxIqForward? ForwardToDuc => _pipeline is { } p ? p.ForwardTxIqToP2 : null;

    /// <summary>
    /// Rate for this job's IQ. P1 always drains the ring at 48 kHz; P2/P3
    /// follow the loaded TXA profile. A mismatch (e.g. a P2 radio with the
    /// P1-profile engine still loaded) would key 4× off-speed and off-pitch,
    /// so it is logged loudly.
    /// </summary>
    private int ResolveTxRateHz(StateDto snap)
    {
        bool p1 = string.IsNullOrEmpty(snap.ConnectedProtocol)
            || string.Equals(snap.ConnectedProtocol, "P1", StringComparison.OrdinalIgnoreCase);
        if (p1) return SampleRateHz;
        int rate = ResolveTxOutputRateHz(_pipeline?.CurrentEngine);
        if (rate == SampleRateHz)
            _log.LogWarning("cw.rate.mismatch protocol={Protocol} engineRate={Rate}Hz — TXA profile does not match the transport",
                snap.ConnectedProtocol, rate);
        return rate;
    }

    internal static int ResolveTxOutputRateHz(Zeus.Dsp.IDspEngine? engine)
    {
        if (engine is null) return SampleRateHz;
        return ResolveTxOutputRateHz(engine.TxBlockSamples, engine.TxOutputSamples);
    }

    internal static int ResolveTxOutputRateHz(int txBlockSamples, int txOutputSamples)
    {
        if (txBlockSamples <= 0 || txOutputSamples <= 0) return SampleRateHz;
        long rate = (long)SampleRateHz * txOutputSamples / txBlockSamples;
        // Only integer multiples of 48 kHz are real DAC rates; anything else
        // means a half-initialised engine, so stay on the P1 rate.
        return rate is >= SampleRateHz and <= 8 * SampleRateHz && rate % SampleRateHz == 0
            ? (int)rate
            : SampleRateHz;
    }

    /// <summary>
    /// Per-job IQ producer. Renders a phase-continuous tone at the job's
    /// baseband offset, writes each chunk to BOTH transports — the P1 ring
    /// and the P2/P3 DUC forward (each is a no-op when its protocol isn't
    /// active) — and paces production against a monotonic deadline so the
    /// average rate stays locked to the DAC regardless of timer granularity.
    /// Mirrors TxTuneDriver's dual-write.
    /// </summary>
    internal sealed class IqPump
    {
        private readonly TxIqRing _ring;
        private readonly TxIqForward? _forward;
        private readonly float[] _iq;
        private readonly double _phaseStep;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _phase;
        private long _producedSamples;

        public IqPump(TxIqRing ring, TxIqForward? forward, int rateHz, int basebandHz)
        {
            _ring = ring;
            _forward = forward;
            RateHz = rateHz;
            ChunkSamples = rateHz * ChunkMs / 1000;
            RampSamples = rateHz * RampMs / 1000;
            _iq = new float[ChunkSamples * 2];
            _phaseStep = 2.0 * Math.PI * basebandHz / rateHz;
        }

        public int RateHz { get; }
        public int ChunkSamples { get; }
        public int RampSamples { get; }

        public void Emit(int n, Func<int, double> envelope)
        {
            for (int i = 0; i < n; i++)
            {
                double env = envelope(i);
                _iq[2 * i] = (float)(env * Math.Cos(_phase));
                _iq[2 * i + 1] = (float)(env * Math.Sin(_phase));
                _phase += _phaseStep;
                // Keep phase bounded so cos/sin stays numerically clean
                // over multi-minute transmissions.
                if (_phase > 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
            }
            var span = new ReadOnlySpan<float>(_iq, 0, 2 * n);
            _ring.Write(span);
            _forward?.Invoke(span);
            _producedSamples += n;
        }

        /// <summary>Wait until real time is within <see cref="LeadMs"/> of
        /// what has been produced. Returns immediately when behind.</summary>
        public Task PaceAsync(CancellationToken ct)
        {
            double aheadMs = ProducedMs - _clock.Elapsed.TotalMilliseconds - LeadMs;
            int delayMs = (int)aheadMs;
            return delayMs > 0 ? Task.Delay(delayMs, ct) : Task.CompletedTask;
        }

        /// <summary>Wait for the production lead to play out, plus a small
        /// margin for the radio FIFO, before MOX falls.</summary>
        public Task DrainAsync(CancellationToken ct)
        {
            double remainingMs = ProducedMs - _clock.Elapsed.TotalMilliseconds;
            int delayMs = Math.Max(0, (int)remainingMs) + 20;
            return Task.Delay(delayMs, ct);
        }

        private double ProducedMs => _producedSamples * 1000.0 / RateHz;
    }

    private void TryReleaseMox(string reason, string? remoteTxLeaseId = null)
    {
        // Host CW is done keying — re-arm the P2 internal keyer (cleared
        // unconditionally, even if UI/trip took MOX from us, so a paddle works
        // again the moment this host send ends). No-op on P1. (#1032)
        _radio.SetHostCwKeying(false);
        // Only release MOX if we still own it — UI or trip may have already
        // dropped it under us. Idempotent at the TxService layer either way.
        if (_tx.MoxOwner == MoxSource.Cwx)
        {
            if (remoteTxLeaseId is null)
                _tx.TrySetMox(false, MoxSource.Cwx, out _);
            else
                _tx.TrySetRemoteMox(false, remoteTxLeaseId, MoxSource.Cwx, out _);
            _log.LogInformation("cw.mox.released reason={Reason}", reason);
        }
    }

    /// <summary>Shown on the final Idle when MOX falls without an abort.</summary>
    internal const string TxDroppedReason = "TX dropped";

    private void OnTxActiveChanged(bool active)
    {
        if (active) return;
        // MOX just fell. If we have a job in flight and the falling edge
        // wasn't initiated by us (TryReleaseMox above), the operator hit
        // the UI override or a trip fired — cancel the playback so the
        // remaining symbols don't queue up against a closed transmitter.
        CancellationTokenSource? cts;
        lock (_abortLock) cts = _currentAbort;
        if (cts is null) return;
        // MoxOwner is null after the falling edge regardless of who caused
        // it, so we can't disambiguate "we released" from "UI overrode" by
        // owner alone. The flag tells the worker this cancel did not come
        // from Abort(), which is the path that moves the abort counter.
        // A redundant cancel is safe: the linked-token CTS is one-shot.
        Interlocked.Exchange(ref _moxDropCancel, 1);
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Test seam: precompute the IQ stream a raw-key job
    /// (<c>keyer:0,true,durationMs</c>) would emit, including the attack
    /// and release ramps. Same envelope shape as <see cref="PlayRawKeyAsync"/>
    /// — the divergence is only in how the engine drives MOX and the
    /// ring, not in the carrier shape.</summary>
    internal static float[] RenderRawKeyForTest(int durationMs, int basebandHz)
    {
        int totalSamples = durationMs * SampleRateHz / 1000;
        int rampSamples = RampMs * SampleRateHz / 1000;
        // Plateau cannot be negative — if duration < 2× ramp the attack and
        // release overlap. Match PlayRawKeyAsync: attack fills the front,
        // release the tail, plateau (if any) in between.
        int plateauSamples = Math.Max(0, totalSamples - rampSamples);

        double phase = 0.0;
        double phaseStep = 2.0 * Math.PI * basebandHz / SampleRateHz;
        var buf = new float[2 * (totalSamples + rampSamples)];
        int idx = 0;

        for (int pos = 0; pos < plateauSamples; pos++)
        {
            double env = pos < rampSamples
                ? 0.5 * (1.0 - Math.Cos(Math.PI * pos / rampSamples))
                : 1.0;
            buf[idx++] = (float)(env * Math.Cos(phase));
            buf[idx++] = (float)(env * Math.Sin(phase));
            phase += phaseStep;
            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
        }
        for (int into = 0; into < rampSamples; into++)
        {
            double env = 0.5 * (1.0 - Math.Cos(Math.PI * (rampSamples - into) / rampSamples));
            buf[idx++] = (float)(env * Math.Cos(phase));
            buf[idx++] = (float)(env * Math.Sin(phase));
            phase += phaseStep;
            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
        }
        return buf;
    }

    internal static int ResolveBasebandHz(StateDto state)
    {
        long txCarrierHz = RadioService.TxCarrierHz(state);
        long txLoHz = string.Equals(state.ConnectedProtocol, "P2", StringComparison.OrdinalIgnoreCase)
            ? RadioService.TxEffectiveLoHz(state)
            : state.RadioLoHz;
        return checked((int)(txLoHz - txCarrierHz));
    }

    /// <summary>Test seam: precompute the IQ stream for <paramref name="text"/>
    /// at <paramref name="wpm"/>. <paramref name="basebandHz"/> is the live
    /// engine's signed <c>(TX effective LO − TX carrier Hz)</c> — pass -600
    /// for CWU, +600 for CWL, or any signed offset to exercise CTUN.</summary>
    internal static float[] RenderForTest(string text, int wpm, int basebandHz, int rateHz = SampleRateHz)
    {
        double phase = 0.0;
        double phaseStep = 2.0 * Math.PI * basebandHz / rateHz;
        var buf = new System.Collections.Generic.List<float>();
        foreach (var sym in MorseEncoder.Encode(text, wpm))
        {
            int total = (int)((long)sym.DurationMs * rateHz / 1000);
            int ramp = Math.Min((int)((long)RampMs * rateHz / 1000), total / 2);
            for (int i = 0; i < total; i++)
            {
                double env = sym.KeyDown ? RaisedCosineEnvelope(i, total, ramp) : 0.0;
                buf.Add((float)(env * Math.Cos(phase)));
                buf.Add((float)(env * Math.Sin(phase)));
                phase += phaseStep;
                if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
            }
        }
        return buf.ToArray();
    }

    private CwEngineStatus MakeStatus(
        CwEngineState state, string text, int wpm, int depth, string? reason = null, string? jobId = null)
        => new(
            state, text, wpm, depth, reason, Volatile.Read(ref _abortSeq),
            CwJobIds.IsValid(jobId) ? jobId : null);

    /// <summary>
    /// Assign the next status sequence and emit it unless a newer status
    /// has already been claimed. Callers must not hold <see cref="_abortLock"/>.
    /// </summary>
    private void PublishStatus(
        CwEngineState state, string text, int wpm, int depth, string? reason = null, string? jobId = null)
    {
        long seq;
        CwEngineStatus status;
        lock (_abortLock)
        {
            seq = ++_statusSeq;
            status = MakeStatus(state, text, wpm, depth, reason, jobId);
        }
        EmitStatus(seq, status);
    }

    /// <summary>
    /// Emit <paramref name="status"/> when <paramref name="seq"/> is still
    /// the latest claim and nothing newer has been emitted. A slower writer
    /// (Abort's Aborting, claimed before the worker's final Idle) is dropped.
    /// </summary>
    private void EmitStatus(long seq, CwEngineStatus status)
    {
        var emitted = false;
        lock (_statusEmitLock)
        {
            if (seq <= _emittedStatusSeq) return;
            lock (_abortLock)
            {
                if (seq != _statusSeq) return;
            }
            _emittedStatusSeq = seq;
            Notify(status);
            emitted = true;
        }
        if (emitted && status.State == CwEngineState.Idle && status.QueueDepth == 0)
            Volatile.Write(ref _terminalIdleEmitted, 1);
    }

    private void Notify(CwEngineStatus status)
    {
        try { Status?.Invoke(status); }
        catch (Exception ex) { _log.LogWarning(ex, "cw.status subscriber threw"); }
    }

    private static double RaisedCosineEnvelope(int sample, int total, int ramp)
    {
        // Plateau region.
        if (sample >= ramp && sample < total - ramp) return 1.0;
        // Rising edge: 0.5 (1 - cos(π·t/ramp)) — Tukey window's leading half.
        if (sample < ramp)
            return 0.5 * (1.0 - Math.Cos(Math.PI * sample / ramp));
        // Falling edge: same curve mirrored.
        int into = sample - (total - ramp);
        return 0.5 * (1.0 - Math.Cos(Math.PI * (ramp - into) / ramp));
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>
    /// Bound jobs only. A null receiver is the legacy global-TX send and
    /// does not read the radio. True means the caller already published the
    /// refusal and must not retune, key, or raise MOX.
    /// </summary>
    private bool RefuseBeforeTransmit(CwJob job, int queueDepth, string text, int wpm)
    {
        if (QueuedReceiverRefusal(job) is not { } refusal) return false;
        _log.LogInformation(
            "cw.send.refused receiver={Receiver} reason={Reason} text={Text}",
            job.Receiver, refusal, Truncate(text));
        PublishStatus(CwEngineState.Idle, text, wpm, queueDepth, refusal, job.JobId);
        return true;
    }

    /// <summary>Null when the job is unbound or its receiver is still the CW TX target.</summary>
    private string? QueuedReceiverRefusal(CwJob job)
    {
        if (job.Receiver is not int receiver) return null;
        return ClassifyQueued(_radio.Snapshot(), receiver);
    }

    /// <summary>
    /// Accept-time classification. Does not clamp onto RX1. Index 0 is
    /// synthesized from the flat RX1 fields only when the receiver list has
    /// no index-0 entry.
    /// </summary>
    internal static string? ClassifyAccept(StateDto state, int receiver)
    {
        if (receiver == WireContract.KiwiReceiverIndex) return ReceiverRemoteReason;
        if ((uint)receiver >= (uint)WireContract.MaxReceivers) return ReceiverOutOfRangeReason;
        var slice = FindHardwareSlice(state, receiver);
        if (slice is null || !slice.Enabled) return ReceiverUnavailableReason;
        if (slice.Name is not null) return ReceiverRemoteReason;
        if (state.TxReceiverIndex != receiver) return ReceiverNotSelectedReason;
        if (slice.Mode is not (RxMode.CWU or RxMode.CWL)) return ReceiverModeReason;
        return null;
    }

    /// <summary>
    /// Classification after the job is queued. A TX index change is
    /// <see cref="ReceiverMovedReason"/> even when the old slice was also
    /// disabled. A mode change that leaves the index alone stays
    /// <see cref="ReceiverModeReason"/>.
    /// </summary>
    internal static string? ClassifyQueued(StateDto state, int receiver)
    {
        if (receiver == WireContract.KiwiReceiverIndex) return ReceiverRemoteReason;
        if ((uint)receiver >= (uint)WireContract.MaxReceivers) return ReceiverOutOfRangeReason;
        if (state.TxReceiverIndex != receiver) return ReceiverMovedReason;
        var slice = FindHardwareSlice(state, receiver);
        if (slice is null || !slice.Enabled) return ReceiverUnavailableReason;
        if (slice.Name is not null) return ReceiverRemoteReason;
        if (slice.Mode is not (RxMode.CWU or RxMode.CWL)) return ReceiverModeReason;
        return null;
    }

    private static ReceiverDto? FindHardwareSlice(StateDto state, int index)
    {
        var list = state.Receivers;
        if (list is not null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Index == index) return list[i];
            }
        }
        if (index != 0) return null;
        return new ReceiverDto(
            0, true, 0, state.VfoHz, state.Mode,
            state.FilterLowHz, state.FilterHighHz,
            state.FilterPresetName, state.Rx1AfGainDb, state.SampleRate,
            state.Rx1Muted);
    }

    /// <summary>
    /// Cancel only the named jobs. Does not move <see cref="AbortSeq"/> and
    /// does not cancel a raw-key job or any other id. Unknown ids are
    /// remembered so a request that lands after close is refused. Caller
    /// must already have validated every id.
    /// </summary>
    internal int TryCancelJobs(IReadOnlyList<string> jobIds)
    {
        lock (_abortLock)
        {
            var wanted = new HashSet<string>(jobIds, StringComparer.Ordinal);
            foreach (var id in wanted) RememberCancelled(id);
            if (_currentJobId is { } current
                && wanted.Contains(current)
                && _currentAbort is { } cts)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* race with worker disposal */ }
            }
            if (Volatile.Read(ref _pendingJobs) == 0) return _abortSeq;
            var keep = new List<CwJob>();
            while (_jobs.Reader.TryRead(out var queued))
            {
                if (queued.JobId is { } id && wanted.Contains(id))
                    Interlocked.Decrement(ref _pendingJobs);
                else
                    keep.Add(queued);
            }
            foreach (var queued in keep)
            {
                if (!_jobs.Writer.TryWrite(queued))
                    Interlocked.Decrement(ref _pendingJobs);
            }
            return _abortSeq;
        }
    }

    /// <summary>Caller holds <see cref="_abortLock"/>.</summary>
    private void RememberCancelled(string? jobId)
    {
        if (!CwJobIds.IsValid(jobId)) return;
        if (!_cancelledIds.Add(jobId!)) return;
        _cancelledOrder.Enqueue(jobId!);
        while (_cancelledOrder.Count > CancelledJobCapacity)
            _cancelledIds.Remove(_cancelledOrder.Dequeue());
    }

    internal const string InvalidJobIdReason = "invalid-job-id";
    internal const string CancelledJobReason = "cancelled";

    private readonly record struct CwJob(
        string Text,
        int Wpm,
        bool RawKeyDown,
        int? DurationMs,
        string? RemoteTxLeaseId,
        int CreatedAbortSeq,
        int? Receiver = null,
        string? JobId = null);
}
