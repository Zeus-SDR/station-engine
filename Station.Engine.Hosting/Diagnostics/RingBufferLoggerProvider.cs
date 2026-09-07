// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using System.Diagnostics;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// An <see cref="ILoggerProvider"/> that mirrors signal-bearing formatted log
/// lines into a singleton <see cref="DiagnosticLogBuffer"/> after running them
/// through <see cref="Redaction.Scrub"/>. Framework infrastructure chatter below
/// Warning is excluded from the ring. Attaching this to the host's logging
/// pipeline means the "Report a problem" button can snapshot the recent log
/// retroactively.
///
/// When an <see cref="IDiagnosticLogFileSink"/> is supplied, the SAME redacted
/// line is also mirrored to disk. Formatting, redaction, subscribers and disk I/O
/// run on one background worker, never on an audio producer. Pending entries can
/// be lost in a crash; the startup/fatal banner retains its direct-write path.
///
/// Trace/Debug are intentionally dropped to keep the (capacity-bounded) ring
/// signal-dense. Scopes are no-ops. The underlying buffer is thread-safe, so the
/// provider uses non-waiting admission to a bounded queue. Overflow drops log
/// entries, counts the loss, and reports it when the worker makes progress.
/// </summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    // Match full logger category names only. Short names such as "Diagnostics"
    // are also used by Zeus code and are not sufficient to identify framework
    // infrastructure.
    private static readonly string[] InfrastructureCategoryPrefixes =
    [
        "Microsoft.",
        "System.Net.Http.",
        "System.Net.Security.",
        "Microsoft.Extensions.Http.",
    ];

    private readonly DiagnosticLogBuffer _buffer;
    private readonly IDiagnosticLogFileSink? _fileSink;

    internal const int QueueCapacity = 1024;
    private readonly Channel<Action> _pending = Channel.CreateBounded<Action>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false,
    });
    private readonly Thread _worker;
    private long _droppedEntries;
    private long _failedEntries;
    private long _unreportedDrops;
    private long _unreportedFailures;
    private long _lastLossReport;
    private int _disposed;

    public long DroppedEntries => Interlocked.Read(ref _droppedEntries);
    public long FailedEntries => Interlocked.Read(ref _failedEntries);

    public RingBufferLoggerProvider(DiagnosticLogBuffer buffer, IDiagnosticLogFileSink? fileSink = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
        _fileSink = fileSink;
        _worker = new Thread(Drain) { IsBackground = true, Name = "diagnostic-log-writer" };
        _worker.Start();
    }

    public ILogger CreateLogger(string categoryName) =>
        new RingBufferLogger(
            this,
            categoryName ?? string.Empty,
            ShortCategory(categoryName ?? string.Empty));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _pending.Writer.TryComplete();
        // The provider is disposed before its DI-owned sink. Drain accepted
        // entries and join the worker so no I/O can race sink disposal.
        if (Thread.CurrentThread != _worker) _worker.Join();
    }

    private void Enqueue(Action write)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_pending.Writer.TryWrite(write)) return;
        Interlocked.Increment(ref _droppedEntries);
        Interlocked.Increment(ref _unreportedDrops);
    }

    private void Drain()
    {
        while (_pending.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            int batch = 0;
            while (batch++ < 64 && _pending.Reader.TryRead(out var write))
            {
                try { write(); }
                catch { CountFailure(); }
            }
            ReportLosses(force: false);
        }
        ReportLosses(force: true);
    }

    private void CountFailure()
    {
        Interlocked.Increment(ref _failedEntries);
        Interlocked.Increment(ref _unreportedFailures);
    }

    private void ReportLosses(bool force)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && now - _lastLossReport < Stopwatch.Frequency) return;
        long dropped = Interlocked.Exchange(ref _unreportedDrops, 0);
        long failed = Interlocked.Exchange(ref _unreportedFailures, 0);
        if (dropped == 0 && failed == 0) return;
        _lastLossReport = now;
        try
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            var line = $"{timestamp} WARN Diagnostics log.queue dropped={dropped} failed={failed} totalDropped={DroppedEntries} totalFailed={FailedEntries}";
            _buffer.Add(line);
            _fileSink?.Append(line);
        }
        catch { CountFailure(); }
    }

    /// <summary>Last dotted segment of a logger category (e.g. "Zeus.Server.RadioService" → "RadioService").</summary>
    private static string ShortCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return category ?? string.Empty;
        int dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    internal static bool ShouldIncludeInRing(
        string fullCategory,
        LogLevel logLevel)
    {
        if (logLevel >= LogLevel.Warning) return true;
        return !InfrastructureCategoryPrefixes.Any(prefix =>
            fullCategory.StartsWith(prefix, StringComparison.Ordinal));
    }

    private sealed class RingBufferLogger : ILogger
    {
        private static readonly IDisposable NoopScope = new NoopDisposable();

        private readonly RingBufferLoggerProvider _owner;
        private readonly string _fullCategory;
        private readonly string _shortCategory;

        public RingBufferLogger(
            RingBufferLoggerProvider owner,
            string fullCategory,
            string shortCategory)
        {
            _owner = owner;
            _fullCategory = fullCategory;
            _shortCategory = shortCategory;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope;

        // Information and above only — keep the ring dense.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);

            DateTime timestamp = DateTime.Now;
            _owner.Enqueue(() => Write(logLevel, state, exception, formatter, timestamp));
        }

        private void Write<TState>(LogLevel logLevel, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter, DateTime timestamp)
        {
            string message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;

            // "{HH:mm:ss.fff} {level} {category-short} {message}" — culture-invariant timestamp.
            string ts = timestamp.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            string line = Flatten(exception is null
                ? $"{ts} {Level(logLevel)} {_shortCategory} {message}"
                : $"{ts} {Level(logLevel)} {_shortCategory} {message} {exception}");

            // Redact once, fan out to both the in-memory ring and (when present)
            // the on-disk sink. Framework Information chatter is excluded only
            // from the report ring; the file keeps the complete forensic trace.
            string redacted = Redaction.Scrub(line);
            if (ShouldIncludeInRing(_fullCategory, logLevel))
                _owner._buffer.Add(redacted);
            _owner._fileSink?.Append(redacted);
        }

        private static string Flatten(string value) =>
            value.Replace("\r\n", " | ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ');

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT",
            _ => level.ToString().ToUpperInvariant(),
        };

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
