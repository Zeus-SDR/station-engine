// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Collections.Concurrent;
using System.IO.Ports;
using System.Runtime.ExceptionServices;
using System.Text;
using Zeus.Server.Tci; // reuse TciRateLimiter (DRY — generic key/interval coalescer)

namespace Zeus.Server.Cat;

/// <summary>
/// One live serial CAT connection (a Thetis CAT1–4 port). The serial analogue
/// of <see cref="CatSession"/>: it owns ONLY the serial I/O and the ';' framing;
/// every command goes to a <see cref="CatCommandHandler"/>, so the safety
/// contract (no auto-key, per-source MOX ownership) and the full Tier-1 command
/// surface are shared byte-for-byte with the TCP path — no protocol is
/// duplicated here.
///
/// <para>Deliberately constructible standalone (device path + serial params)
/// so an integration test can drive it over a socat pty pair with no
/// <see cref="CatSerialService"/> / DI in the way.</para>
///
/// <para>Read loop: blocking-free <c>BaseStream.ReadAsync</c> under the caller's
/// cancellation token, exactly as <see cref="CatSession"/> and the shipped
/// <c>G2FrontPanelService</c> do. Because <c>SerialPort.BaseStream.ReadAsync</c>
/// has historically been unreliable about honouring its cancellation token,
/// <see cref="Dispose"/> also closes the port, which force-unblocks any
/// in-flight read — so a settings change or shutdown always tears the loop down
/// promptly.</para>
///
/// <para>Outbound writes are queued and drained by a single writer task, so a
/// stalled serial peer can never block a caller (the MOX/TX transition thread
/// in particular). A <see cref="TimeoutException"/> from the raw write drops
/// that frame and the rest of the queued backlog and keeps the port open —
/// RTS/DTR stay asserted so shared serial PTT is not unkeyed. Any other write
/// exception, and any read fault, ends the loop: <see cref="RunAsync"/> tears
/// the port down and the initiating fault reaches the service so it reconnects.
/// A loop that completes canceled while the caller's token is still open is
/// the same kind of fault: Windows <c>SerialStream</c> maps a driver
/// <c>ERROR_OPERATION_ABORTED</c> to <see cref="OperationCanceledException"/>
/// with no cancellation requested.</para>
/// </summary>
internal sealed class CatSerialPort : ISerialPttPins, IDisposable
{
    // Longest legal Kenwood command is well under this; a token that grows past
    // it without a ';' is a misbehaving client → its buffer is dropped.
    private const int MaxPendingChars = 256;

    // Backlog cap for the outbound writer. A serial peer that has stopped
    // draining (its companion band-decoder program was closed, say) must never
    // grow this queue without bound. Unsolicited AI frames are whole-state
    // snapshots, so past the cap the oldest queued frame is discarded in favour
    // of the newest — the reader always gets current state, never a stale one.
    private const int MaxOutboundQueue = 512;

    // Post-fault drain bound. See RunLoopsAsync — a wedged Close must not
    // swallow the initiating loop fault.
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(2);

    private readonly string _path;
    private readonly int _baud;
    private readonly Parity _parity;
    private readonly int _dataBits;
    private readonly StopBits _stopBits;
    private readonly ILogger _log;

    private readonly TciRateLimiter _rateLimiter;
    private readonly CatCommandHandler _handler;
    private readonly CatWireLogger _wireLog;

    // Outbound queue drained by a single writer (SendLoopAsync). Send() only
    // enqueues, so a stalled serial peer can never block a caller — critically
    // the MOX/TX transition thread that fans out the IF state push.
    private readonly ConcurrentQueue<string> _outbound = new();
    private readonly SemaphoreSlim _outboundSignal = new(0);
    // The raw byte write. Real ports write to _port; tests inject a stand-in so
    // the queueing behaviour is provable with no serial I/O.
    private readonly Action<string> _rawWrite;

    private SerialPort? _port;
    private bool _rtsAsserted;
    private bool _dtrAsserted;
    private string? _lineControlError;
    private int _disposed;
    private int _activity;

    public CatSerialPort(
        string path, int baud, Parity parity, int dataBits, StopBits stopBits,
        RadioService radio, TxService tx, CatOptions options,
        Func<double> latestRxDbm, ILogger log)
        : this(path, baud, parity, dataBits, stopBits, radio, tx, options, latestRxDbm, log, rawWrite: null)
    {
    }

    /// <summary>Test-only: construct without a real serial device, injecting the
    /// raw byte writer. Lets a unit test prove that <see cref="Send"/> enqueues
    /// and returns promptly even when the writer stalls, that a write timeout
    /// drops the backlog without tearing the port down, and that queued frames
    /// drain in order — with no serial I/O in CI.</summary>
    internal CatSerialPort(
        RadioService radio, TxService tx, CatOptions options,
        Func<double> latestRxDbm, Action<string> rawWrite, ILogger log)
        : this("test:injected", 0, Parity.None, 8, StopBits.One, radio, tx, options, latestRxDbm, log, rawWrite)
    {
    }

    private CatSerialPort(
        string path, int baud, Parity parity, int dataBits, StopBits stopBits,
        RadioService radio, TxService tx, CatOptions options,
        Func<double> latestRxDbm, ILogger log, Action<string>? rawWrite)
    {
        _path = path;
        _baud = baud;
        _parity = parity;
        _dataBits = dataBits;
        _stopBits = stopBits;
        _log = log;
        _wireLog = new CatWireLogger(log, $"serial:{path}", options.WireLogAtInformation);
        _rawWrite = rawWrite ?? WriteLineToPort;
        _rateLimiter = new TciRateLimiter(options.RateLimitMs, Send);
        _handler = new CatCommandHandler(radio, tx, options, latestRxDbm, Send);
    }

    /// <summary>True once the connected client issued AI1/AI2 — gates the
    /// unsolicited state-change pushes <see cref="CatSerialService"/> fans out.</summary>
    public bool AutoInfoEnabled => _handler.AutoInfoEnabled;

    /// <summary>Pre-enable AI1 for this port (per-port "Auto Report" setting).
    /// Called by <see cref="CatSerialService"/> right after <see cref="Open"/>,
    /// so devices that never send <c>AI1;</c> still receive unsolicited state
    /// pushes.</summary>
    public void EnableAutoInfo() => _handler.EnableAutoInfo();

    /// <summary>Count of commands dispatched on this port (a cheap "is something
    /// talking to me" signal for the status panel).</summary>
    public int Activity => Volatile.Read(ref _activity);

    public bool IsOpen => _port?.IsOpen ?? false;

    /// <summary>Open the serial device. Throws on failure (port busy, missing,
    /// permission) — the caller logs and schedules a reconnect.</summary>
    public void Open()
    {
        var port = new SerialPort(_path, _baud, _parity, _dataBits, _stopBits)
        {
            Handshake = Handshake.None,
            // Finite timeouts: a stuck line must never wedge the read/write path.
            ReadTimeout = 500,
            WriteTimeout = 500,
            Encoding = Encoding.ASCII,
        };
        port.Open();
        // Assert RTS/DTR after open (Thetis's "soft rock ptt" hack — some
        // level-shifter interfaces need a line high). Best-effort: a virtual
        // pty that doesn't model line control must not fail the open.
        var lineFailures = new List<string>(2);
        try { port.RtsEnable = true; _rtsAsserted = true; }
        catch (Exception ex) { lineFailures.Add($"RTS: {ex.Message}"); }
        try { port.DtrEnable = true; _dtrAsserted = true; }
        catch (Exception ex) { lineFailures.Add($"DTR: {ex.Message}"); }
        _lineControlError = lineFailures.Count == 0
            ? null
            : $"Unable to assert serial PTT output line(s): {string.Join("; ", lineFailures)}";
        _port = port;
    }

    public bool CtsHolding => (_port ?? throw new IOException("CAT serial port is closed")).CtsHolding;
    public bool DsrHolding => (_port ?? throw new IOException("CAT serial port is closed")).DsrHolding;
    public bool RtsAsserted => _rtsAsserted;
    public bool DtrAsserted => _dtrAsserted;
    public string? LineControlError => _lineControlError;

    /// <summary>Run the port until cancelled or it faults: a read loop that
    /// frames on ';' and dispatches commands, and a writer that drains the
    /// outbound queue. Whichever loop faults first ends the other, and that
    /// fault propagates so <see cref="CatSerialService"/> sets the slot error
    /// and reconnects (including a loop that ends canceled with no cancel
    /// requested — see the class remarks). External cancellation stays quiet. A write timeout
    /// (peer not draining) is tolerated: the timed-out frame and queued
    /// backlog are dropped and the writer keeps running, so RTS/DTR stay
    /// asserted for shared serial PTT.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var port = _port ?? throw new InvalidOperationException("Open() must precede RunAsync()");
        // SerialPort.BaseStream.ReadAsync has historically ignored its
        // cancellation token, which would wedge a settings-change/shutdown
        // teardown (the read never returns, so the caller's finally that would
        // dispose the port never runs). Closing the port force-unblocks the
        // in-flight read regardless. This callback is the load-bearing teardown
        // path; do not remove it.
        //
        // Offload the Close to the thread pool: CancellationTokenSource.Cancel()
        // runs registrations SYNCHRONOUSLY on the canceller's thread (the HTTP
        // PUT thread, or the host-shutdown thread), and SerialPort.Close() can
        // block on a surprise-removed USB adapter. Queuing it keeps the canceller
        // responsive while still unblocking the read promptly.
        await using var reg = ct.Register(() =>
            ThreadPool.QueueUserWorkItem(_ => { try { port.Close(); } catch { /* already torn down */ } }));

        await RunLoopsAsync(token => ReadLoopAsync(port, token), SendLoopAsync, () => port.Close(), ct, _log);
    }

    /// <summary>Start <paramref name="read"/> and <paramref name="send"/>, wait
    /// until one finishes, then cancel and <paramref name="closePort"/> so the
    /// other can drain. The drain wait is bounded by <see cref="TeardownTimeout"/>
    /// (or <paramref name="teardownTimeout"/>): a close that does not release the
    /// other loop must not swallow the outcome of the loop that finished first.
    /// Unless <paramref name="ct"/> is cancelled, that outcome is rethrown
    /// (stack preserved) whether or not the drain timed out — a fault, or a
    /// cancellation nobody requested (a Windows driver abort). The other loop's teardown exception
    /// is swallowed. No serial I/O — <see cref="RunAsync"/> supplies the real
    /// loops and the port close.</summary>
    internal static async Task RunLoopsAsync(
        Func<CancellationToken, Task> read,
        Func<CancellationToken, Task> send,
        Action closePort,
        CancellationToken ct,
        ILogger? log = null,
        TimeSpan? teardownTimeout = null)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // SendLoopAsync runs on its caller until the first incomplete await. A
        // frame already queued makes WaitAsync complete synchronously, so
        // SerialPort.Write then runs here — CatSerialService.ExecuteAsync's
        // port-start loop, through RunAsync and RunPortAsync. While producers
        // keep the queue non-empty, send() never returns, WhenAny never starts,
        // a read fault goes unsupervised, and later CAT ports never open. Hop
        // the writer onto the pool before supervising so that write cannot
        // stall the starter. Read stays a direct call: ReadLoopAsync's first
        // await is BaseStream.ReadAsync, which returns a task instead of
        // blocking this thread for bytes.
        var readTask = read(linkedCts.Token);
        var sendTask = Task.Run(() => send(linkedCts.Token));
        // The loop that finishes first is the one whose fault must surface.
        // The other fault is produced by this teardown (cancel + close) and
        // must not replace it. WhenAny does not throw when a task faults.
        Task initiating;
        try
        {
            initiating = await Task.WhenAny(readTask, sendTask);
        }
        finally
        {
            // Whichever loop ended first, cancel the other and let both drain.
            // Cancelling linkedCts alone is not enough to release a read wedged in
            // SerialPort.BaseStream.ReadAsync (it ignores the token — see the class
            // remarks): only closing the port unblocks it. RunAsync's ct.Register
            // covers EXTERNAL cancellation; closePort covers an INTERNAL teardown
            // (a loop fault), which cancels linkedCts but not ct. Without this,
            // Task.WhenAll would await a read that never returns. Offloaded to the
            // pool: SerialPort.Close() can block on a surprise-removed adapter.
            linkedCts.Cancel();
            ThreadPool.QueueUserWorkItem(_ => { try { closePort(); } catch { /* already torn down */ } });
            // Close can also wedge (removed adapter; Close flushes). Bound the
            // drain so a stuck read cannot swallow the initiating fault — the
            // same reason Dispose bounds its own close. WhenAny has already
            // completed the initiating task, so its fault is known even if this
            // wait times out; only the other loop can still be running.
            var pending = Task.WhenAll(readTask, sendTask);
            try
            {
                await pending.WaitAsync(teardownTimeout ?? TeardownTimeout);
            }
            catch (TimeoutException)
            {
                // A loop that itself threw TimeoutException completes the drain.
                // Only an unfinished drain is the wedged-close case.
                if (!pending.IsCompleted)
                    log?.LogWarning("cat.serial.teardown.timeout");
                ObserveAbandoned(pending);
                ObserveAbandoned(readTask);
                ObserveAbandoned(sendTask);
            }
            catch
            {
                /* secondary loop / cancellation */
            }
        }

        // External cancellation stays quiet. Otherwise surface the initiating
        // outcome. The linked token is only cancelled in the finally above or
        // by ct, so a canceled initiating task here is an unrequested abort;
        // awaiting it throws the OperationCanceledException it carries.
        if (!ct.IsCancellationRequested)
        {
            if (initiating.IsFaulted
                && initiating.Exception?.GetBaseException() is Exception fault)
            {
                ExceptionDispatchInfo.Capture(fault).Throw();
            }

            if (initiating.IsCanceled)
                await initiating;
        }
    }

    // A loop left running after the teardown bound, and the WhenAll that joins
    // the pair, can still fault once this method has returned. Touch the
    // exception so it cannot surface as UnobservedTaskException.
    private static void ObserveAbandoned(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task ReadLoopAsync(SerialPort port, CancellationToken ct)
    {
        var stream = port.BaseStream;
        var buf = new byte[2048];
        var acc = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            }
            catch (TimeoutException) { continue; }

            if (n <= 0) { await Task.Delay(20, ct); continue; }

            acc.Append(Encoding.ASCII.GetString(buf, 0, n));
            var (commands, remainder) = CatProtocol.ExtractCommands(acc.ToString());
            acc.Clear();
            // Bound an un-terminated command so a client that never sends ';'
            // can't grow the buffer without limit.
            acc.Append(remainder.Length > MaxPendingChars ? string.Empty : remainder);

            foreach (var token in commands)
            {
                Interlocked.Increment(ref _activity);
                try
                {
                    _wireLog.Rx(token + ";");
                    await _handler.DispatchAsync(token, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _log.LogDebug(ex, "cat.serial dispatch error port={Port} token={Token}", _path, token); }
            }
        }
    }

    /// <summary>Enqueue a terminated CAT response for the writer to send. Returns
    /// immediately — the actual serial write happens on <see cref="SendLoopAsync"/>,
    /// never on the caller's thread. This is load-bearing: <see cref="Send"/> is
    /// invoked from the radio MOX/TX transition thread (the IF-state Auto-Info
    /// push), and a synchronous serial write there would stall the transmit
    /// transition for as long as the peer refuses to drain. Called from the read
    /// loop (command replies), the rate-limiter timer, and the AI fan-out.</summary>
    public void Send(string line)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // Bound the backlog. If the peer has stopped draining, replace the oldest
        // queued frame with this one instead of growing without limit — AI frames
        // are whole-state snapshots, so the reader still gets current state.
        // Signal in BOTH cases: SendLoopAsync drains the whole queue per wake, so
        // permits do not track the queue 1:1 and a surplus permit is a harmless
        // no-op wake — but a MISSING permit strands the frame when the writer has
        // already parked on an empty queue (issue #2402), leaving stale state on
        // the CAT peer after a TX transition.
        if (_outbound.Count >= MaxOutboundQueue)
        {
            _outbound.TryDequeue(out _);
        }
        _outbound.Enqueue(line);
        _outboundSignal.Release();
    }

    // Single-writer drain of the outbound queue.
    // TimeoutException (peer not draining, SerialPort.WriteTimeout): drop this
    // frame and the rest of the queued backlog (stale snapshots/replies) and
    // keep running so the port — and its RTS/DTR pins — stay up.
    // Any other exception is a device fault: log and propagate so RunLoopsAsync
    // tears the port down and the service reconnects. An OCE without a
    // requested cancel (Windows driver abort) is a device fault, not a quiet exit.
    // Debug, not Warning — CatSerialService already logs cat.serial.error at
    // Warning with this same exception.
    private async Task SendLoopAsync(CancellationToken ct)
    {
        var timeoutStreak = 0;
        var droppedDuringStreak = 0;
        while (!ct.IsCancellationRequested)
        {
            await _outboundSignal.WaitAsync(ct);
            // Dispose wakes this wait without cancelling ct. Leave before draining
            // so a torn-down port writes nothing further.
            if (Volatile.Read(ref _disposed) != 0) return;
            while (_outbound.TryDequeue(out var line))
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                ct.ThrowIfCancellationRequested();
                try
                {
                    _rawWrite(line);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    droppedDuringStreak++;
                    while (_outbound.TryDequeue(out _))
                        droppedDuringStreak++;
                    if (timeoutStreak == 0)
                        _log.LogWarning("cat.serial.write.timeout port={Port}", _path);
                    else
                        _log.LogDebug("cat.serial.write.timeout port={Port}", _path);
                    timeoutStreak++;
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "cat.serial.write.fault port={Port}", _path);
                    throw;
                }

                _wireLog.Tx(line);
                if (timeoutStreak > 0)
                {
                    _log.LogInformation(
                        "cat.serial.write.recovered port={Port} dropped={Dropped}",
                        _path, droppedDuringStreak);
                    timeoutStreak = 0;
                    droppedDuringStreak = 0;
                }
            }
        }
    }

    // Default raw writer: synchronous SerialPort.Write, but now only ever runs on
    // the writer task, never on a caller (e.g. the MOX transition) thread.
    private void WriteLineToPort(string line)
    {
        var port = _port;
        if (port is null || !port.IsOpen) return;
        port.Write(line);
    }

    /// <summary>Test-only: run the outbound writer loop against the injected raw
    /// writer, with no serial device.</summary>
    internal Task RunSendLoopForTest(CancellationToken ct) => SendLoopAsync(ct);

    /// <summary>Enqueue a rate-limited (coalesced-by-key) push, e.g. FA during a
    /// VFO spin. Bursts collapse to one send per RateLimitMs.</summary>
    public void SendRateLimited(string key, string line) => _rateLimiter.Enqueue(key, line);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _wireLog.FlushSuppressed(); } catch { }
        _rateLimiter.Dispose();
        // Wake a parked writer. Deliberately do not dispose this semaphore: SemaphoreSlim.Dispose abandons async waiters without completing them, and AvailableWaitHandle is never used.
        _outboundSignal.Release();
        var port = _port;
        _port = null;
        _rtsAsserted = false;
        _dtrAsserted = false;
        if (port is null) return;
        // A surprise-removed USB adapter can wedge SerialPort.Close/Dispose in
        // the native driver. Run both on the pool and wait only a small bounded
        // interval: healthy ports are released before Dispose returns, while a
        // wedged device can never stall the service run-set or host shutdown.
        var closeTask = Task.Run(() =>
        {
            try { if (port.IsOpen) port.Close(); } catch { /* already torn down */ }
            try { port.Dispose(); } catch { /* best-effort native release */ }
        });
        try { closeTask.Wait(TimeSpan.FromMilliseconds(250)); }
        catch { /* shutdown remains best-effort */ }
    }
}
