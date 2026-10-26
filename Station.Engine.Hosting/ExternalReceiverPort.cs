// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Dsp.Wdsp;

namespace Zeus.Server;

/// <summary>Receiver context a product-operated radio reports with its audio.</summary>
public readonly record struct ExternalReceiverContext(
    int Receiver,
    long VfoHz,
    RxMode Mode,
    bool Transmitting,
    bool ProcessedAudio = false);

/// <summary>
/// Lets one local product process feed finished receive audio plus a small
/// receiver context into the engine's receive-audio consumers while the engine
/// has no radio of its own (docs/designs/external-receiver-audio-port.md).
/// </summary>
/// <remarks>
/// The port is inert whenever the engine owns a radio. It accepts a source only
/// while <see cref="RadioService.IsConnected"/> is false and the radio status is
/// <see cref="ConnectionStatus.Disconnected"/>, and it detaches the source on
/// any transition out of that state. It never writes radio state or a store.
/// Audio reaches consumers only through
/// <see cref="DspPipelineService.PublishExternalReceiverAudio"/>.
/// </remarks>
public sealed class ExternalReceiverPort : IDisposable
{
    public const int SampleRateHz = 48_000;
    public const int MaxSamplesPerBlock = 4_800;
    internal const int MaxContextBytes = 4_096;
    internal const int MaxBinaryBytes = MaxSamplesPerBlock * sizeof(float);
    internal const string ReasonRadioConnected = "radio-connected";
    internal const string ReasonBusy = "busy";
    internal const string ReasonShutdown = "shutdown";
    internal const string ReasonProtocol = "protocol";
    internal const string ReasonClosed = "closed";
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);
    private const long StatsIntervalMs = 60_000;

    // Lock order: _publishGate, then _gate. Neither is held while calling a
    // ContextChanged handler or any RadioService member.
    private readonly object _publishGate = new();
    private readonly object _gate = new();
    private readonly RadioService _radio;
    private readonly ILogger<ExternalReceiverPort> _log;
    private Source? _source;
    private ContextBox? _context;
    private int _radioStatus;
    private bool _disposed;
    private NrConfig _nr;
    private readonly Nr3ModelStore? _models;
    internal Func<ExternalReceiverNoiseReduction> CreateNoiseReduction { get; set; }

    /// <summary>Test seam: runs after a source is reserved, before the race recheck.</summary>
    internal Action? AttachReservedForTest { get; set; }

    public ExternalReceiverPort(RadioService radio, ILogger<ExternalReceiverPort> log,
        WdspWisdomInitializer? wisdom = null, Nr3ModelStore? models = null)
    {
        _radio = radio;
        _log = log;
        _nr = radio.Snapshot().Nr ?? new NrConfig();
        _models = models;
        if (models is not null) models.Changed += OnModelChanged;
        CreateNoiseReduction = () => new ExternalReceiverNoiseReduction(wisdom, models, log);
        Volatile.Write(ref _radioStatus, (int)radio.Snapshot().Status);
        _radio.StateChanged += OnRadioStateChanged;
    }

    /// <summary>Raised after the context is set, changed, or cleared by a detach.</summary>
    public event Action? ContextChanged;

    /// <summary>True while a source is attached and its context has been received.</summary>
    public bool IsActive => Volatile.Read(ref _context) is not null;

    public bool TryGetContext(int receiver, out ExternalReceiverContext context)
    {
        if (Volatile.Read(ref _context) is { } box && box.Value.Receiver == receiver)
        {
            context = box.Value;
            return true;
        }
        context = default;
        return false;
    }

    internal static bool IsEngineRadioIdle(bool isConnected, ConnectionStatus status) =>
        !isConnected && status == ConnectionStatus.Disconnected;

    private bool EngineRadioIdleNow() =>
        IsEngineRadioIdle(_radio.IsConnected, (ConnectionStatus)Volatile.Read(ref _radioStatus));

    /// <summary>Non-reserving admission check; <see cref="TryAttach"/> decides.</summary>
    internal bool CanAttach(out string? refusal)
    {
        Volatile.Write(ref _radioStatus, (int)_radio.Snapshot().Status);
        if (!EngineRadioIdleNow())
        {
            refusal = ReasonRadioConnected;
            return false;
        }
        lock (_gate)
        {
            if (_disposed)
            {
                refusal = ReasonShutdown;
                return false;
            }
            if (_source is not null)
            {
                refusal = ReasonBusy;
                return false;
            }
        }
        refusal = null;
        return true;
    }

    internal bool TryAttach(DspPipelineService dsp, out Source? source, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(dsp);
        source = null;
        if (!CanAttach(out refusal)) return false;
        lock (_gate)
        {
            if (_disposed)
            {
                refusal = ReasonShutdown;
                return false;
            }
            if (_source is not null)
            {
                refusal = ReasonBusy;
                return false;
            }
            source = new Source(this, dsp);
            _source = source;
        }
        AttachReservedForTest?.Invoke();
        // A connect that raced the admission check above still detaches here.
        if (!EngineRadioIdleNow())
        {
            Detach(source, ReasonRadioConnected);
            source = null;
            refusal = ReasonRadioConnected;
            return false;
        }
        _log.LogInformation("external_receiver.attached");
        return true;
    }

    /// <summary>Runs one WebSocket source for the lifetime of the request.</summary>
    internal async Task RunAsync(HttpContext context, DspPipelineService dsp)
    {
        if (!CanAttach(out var refusal))
        {
            await RefuseAsync(context, refusal!).ConfigureAwait(false);
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!TryAttach(dsp, out var source, out refusal))
        {
            await RefuseAsync(context, refusal!).ConfigureAwait(false);
            return;
        }

        var lifetime = context.RequestServices.GetService<IHostApplicationLifetime>();
        using var stopping = lifetime?.ApplicationStopping.Register(
            static state => ((Source)state!).Detach(ReasonShutdown), source);
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await source!.RunAsync(socket, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            Detach(source!, ReasonClosed);
        }
    }

    private async Task RefuseAsync(HttpContext context, string refusal)
    {
        _log.LogInformation("external_receiver.refused reason={Reason}", refusal);
        context.Response.StatusCode = refusal == ReasonShutdown
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = refusal }).ConfigureAwait(false);
    }

    /// <summary>Applies one context message. False is a protocol error.</summary>
    internal bool ApplyContext(Source source, ReadOnlySpan<byte> utf8Json)
    {
        if (!TryParseContext(utf8Json, out var parsed, out var error))
        {
            _log.LogInformation("external_receiver.protocol_error {Error}", error);
            return false;
        }
        if (!EngineRadioIdleNow())
        {
            Detach(source, ReasonRadioConnected);
            return true;
        }
        bool changed;
        lock (_publishGate)
        lock (_gate)
        {
            if (_source != source) return true;
            var current = _context;
            changed = current is null || current.Value != parsed;
            if (changed) Volatile.Write(ref _context, new ContextBox(parsed));
            source.ConfigureProcessing(parsed, _nr);
        }
        if (changed) RaiseContextChanged();
        return true;
    }

    /// <summary>
    /// Publishes one decoded audio block on the calling (receive) thread.
    /// Returns false when the source has no context yet (a protocol error).
    /// </summary>
    internal bool PublishAudio(Source source, DspPipelineService dsp, ReadOnlySpan<float> samples)
    {
        bool detach = false;
        lock (_publishGate)
        {
            if (Volatile.Read(ref _source) != source) return true;
            var box = Volatile.Read(ref _context);
            if (box is null) return false;
            if (!EngineRadioIdleNow())
            {
                detach = true;
            }
            else
            {
                if (box.Value.Transmitting) source.SuppressedBlocks++;
                else source.PublishedBlocks++;
                dsp.PublishExternalReceiverAudio(box.Value.Receiver, samples);
                if (!box.Value.Transmitting && box.Value.ProcessedAudio) source.FeedProcessing(samples);
            }
        }
        if (detach) Detach(source, ReasonRadioConnected);
        return true;
    }

    internal void Detach(Source source, string reason)
    {
        bool hadContext;
        lock (_publishGate)
        {
            lock (_gate)
            {
                if (_source != source) return;
                _source = null;
                hadContext = Volatile.Read(ref _context) is not null;
                Volatile.Write(ref _context, null);
                source.StopProcessing();
            }
        }
        _log.LogInformation(
            "external_receiver.detached reason={Reason} blocks={Blocks} suppressedTx={Suppressed}",
            reason, source.PublishedBlocks, source.SuppressedBlocks);
        source.SignalDetached(reason);
        if (hadContext) RaiseContextChanged();
    }

    private void DetachCurrent(string reason)
    {
        Source? current;
        lock (_gate) current = _source;
        if (current is not null) Detach(current, reason);
    }

    private void OnRadioStateChanged(StateDto state)
    {
        Volatile.Write(ref _radioStatus, (int)state.Status);
        if (!EngineRadioIdleNow()) DetachCurrent(ReasonRadioConnected);
        else
        {
            lock (_publishGate)
            {
                _nr = state.Nr ?? new NrConfig();
                if (_source is { } source && _context is { } box)
                    source.ConfigureProcessing(box.Value, _nr);
            }
        }
    }

    private void OnModelChanged(string? path)
    {
        lock (_publishGate)
        {
            Source? source;
            lock (_gate) source = _source;
            if (source is not null) source.ResetProcessingModel();
            else Zeus.Dsp.Wdsp.WdspAudioNoiseReduction.InvalidateModelCache();
        }
    }

    private void RaiseContextChanged()
    {
        var handlers = ContextChanged;
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "external_receiver context subscriber failed");
            }
        }
    }

    internal static bool TryParseContext(
        ReadOnlySpan<byte> utf8Json,
        out ExternalReceiverContext context,
        out string? error)
    {
        context = default;
        if (utf8Json.Length > MaxContextBytes)
        {
            error = "context message too large";
            return false;
        }
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "context must be a JSON object";
                return false;
            }
            if (!root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "context")
            {
                error = "type must be \"context\"";
                return false;
            }
            if (!root.TryGetProperty("receiver", out var receiverElement)
                || receiverElement.ValueKind != JsonValueKind.Number
                || !receiverElement.TryGetInt32(out int receiver)
                || receiver != 0)
            {
                error = "receiver must be 0";
                return false;
            }
            if (!root.TryGetProperty("vfoHz", out var vfoElement)
                || vfoElement.ValueKind != JsonValueKind.Number
                || !vfoElement.TryGetInt64(out long vfoHz)
                || vfoHz <= 0)
            {
                error = "vfoHz must be a positive integer";
                return false;
            }
            if (!root.TryGetProperty("mode", out var modeElement)
                || modeElement.ValueKind != JsonValueKind.String
                || !TryParseMode(modeElement.GetString(), out var mode))
            {
                error = "mode is not a known receiver mode";
                return false;
            }
            if (!root.TryGetProperty("transmitting", out var txElement)
                || txElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = "transmitting must be a boolean";
                return false;
            }
            bool processed = false;
            if (root.TryGetProperty("processedAudio", out var processedElement))
            {
                if (processedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "processedAudio must be a boolean";
                    return false;
                }
                processed = processedElement.GetBoolean();
            }
            context = new ExternalReceiverContext(receiver, vfoHz, mode, txElement.GetBoolean(), processed);
            error = null;
            return true;
        }
        catch (JsonException)
        {
            error = "context is not valid JSON";
            return false;
        }
    }

    // Names only: Enum.TryParse would also accept numeric strings.
    internal static bool TryParseMode(string? name, out RxMode mode)
    {
        foreach (RxMode candidate in Enum.GetValues<RxMode>())
        {
            if (string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                mode = candidate;
                return true;
            }
        }
        mode = default;
        return false;
    }

    /// <summary>
    /// Decodes one binary audio message: little-endian float32 mono, 1 to
    /// <see cref="MaxSamplesPerBlock"/> samples. Non-finite samples become 0
    /// and every sample is clamped to ±1.
    /// </summary>
    internal static bool TryDecodeAudio(ReadOnlySpan<byte> payload, Span<float> destination, out int count)
    {
        count = 0;
        if (payload.IsEmpty
            || payload.Length > MaxBinaryBytes
            || payload.Length % sizeof(float) != 0
            || destination.Length < payload.Length / sizeof(float))
            return false;
        count = payload.Length / sizeof(float);
        for (int i = 0; i < count; i++)
        {
            float sample = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(i * sizeof(float), sizeof(float)));
            destination[i] = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
        }
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _radio.StateChanged -= OnRadioStateChanged;
        if (_models is not null) _models.Changed -= OnModelChanged;
        DetachCurrent(ReasonShutdown);
    }

    private sealed class ContextBox(ExternalReceiverContext value)
    {
        public ExternalReceiverContext Value { get; } = value;
    }

    /// <summary>One attached source: its socket loop and its detach signal.</summary>
    internal sealed class Source
    {
        private readonly ExternalReceiverPort _port;
        private readonly DspPipelineService _dsp;
        private readonly TaskCompletionSource<string> _detached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _statsDueMs = Environment.TickCount64 + StatsIntervalMs;
        private long _statsPublished;
        private long _statsSuppressed;
        private readonly ExternalReceiverNoiseReduction _processing;
        private volatile bool _optedIn;
        private int _processingRevision;
        private bool _announced;
        private (bool Active, NrMode Mode, int? LatencyMs) _lastProcessing;

        internal void ConfigureProcessing(ExternalReceiverContext context, NrConfig nr)
        {
            _optedIn = context.ProcessedAudio;
            _processing.Configure(context.ProcessedAudio && !context.Transmitting, nr,
                context.Transmitting ? "transmitting" : !ExternalReceiverNoiseReduction.NeedsProcessing(nr) ? "off" : "opt-out",
                context.Mode);
            Interlocked.Increment(ref _processingRevision);
        }

        internal void FeedProcessing(ReadOnlySpan<float> samples) => _processing.Feed(samples);
        internal void ResetProcessingModel()
        {
            _processing.ResetModel();
            Interlocked.Increment(ref _processingRevision);
        }
        internal void StopProcessing()
        {
            _processing.Dispose();
            Interlocked.Increment(ref _processingRevision);
        }
        internal (bool Active, NrMode Mode, int? LatencyMs, int Samples) ReadProcessing(Span<float> output)
            => _processing.Read(output);

        internal Source(ExternalReceiverPort port, DspPipelineService dsp)
        {
            _port = port;
            _dsp = dsp;
            _processing = port.CreateNoiseReduction();
        }

        // Written under the port's publish gate only.
        internal long PublishedBlocks;
        internal long SuppressedBlocks;

        internal Task<string> Detached => _detached.Task;

        internal void SignalDetached(string reason) => _detached.TrySetResult(reason);

        internal void Detach(string reason) => _port.Detach(this, reason);

        /// <summary>Test seam: one text (context) message, false on a protocol error.</summary>
        internal bool OnText(ReadOnlySpan<byte> utf8Json)
        {
            if (_port.ApplyContext(this, utf8Json)) return true;
            Detach(ReasonProtocol);
            return false;
        }

        /// <summary>Test seam: one binary (audio) message, false on a protocol error.</summary>
        internal bool OnBinary(ReadOnlySpan<byte> payload, Span<float> scratch)
        {
            if (!TryDecodeAudio(payload, scratch, out int count)
                || !_port.PublishAudio(this, _dsp, scratch[..count]))
            {
                Detach(ReasonProtocol);
                return false;
            }
            return true;
        }

        internal async Task RunAsync(WebSocket socket, CancellationToken aborted)
        {
            var buffer = new byte[MaxBinaryBytes + 1];
            var samples = new float[MaxSamplesPerBlock];
            using var sendStop = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            var sending = SendProcessedAsync(socket, sendStop.Token);
            Task<ValueWebSocketReceiveResult>? pending = null;
            int filled = 0;
            try
            {
                while (!Detached.IsCompleted)
                {
                    pending ??= socket.ReceiveAsync(buffer.AsMemory(filled), aborted).AsTask();
                    var finished = await Task.WhenAny(pending, Detached).ConfigureAwait(false);
                    if (finished != pending) break;
                    var result = await pending.ConfigureAwait(false);
                    pending = null;
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Detach(ReasonClosed);
                        break;
                    }
                    filled += result.Count;
                    if (!result.EndOfMessage)
                    {
                        if (filled >= buffer.Length)
                        {
                            Detach(ReasonProtocol);
                            break;
                        }
                        continue;
                    }
                    var message = buffer.AsSpan(0, filled);
                    filled = 0;
                    bool ok = result.MessageType == WebSocketMessageType.Text
                        ? OnText(message)
                        : OnBinary(message, samples);
                    if (!ok) break;
                    LogStatsIfDue();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                Detach(ReasonClosed);
            }

            finally
            {
                await sendStop.CancelAsync().ConfigureAwait(false);
                try { await sending.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (_announced && _lastProcessing.Active && socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                try { await SendProcessingAsync(socket, false, null, null, timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            }
            if (Detached.IsCompletedSuccessfully)
                await CloseAsync(socket, Detached.Result, pending).ConfigureAwait(false);
        }

        private async Task SendProcessedAsync(WebSocket socket, CancellationToken stop)
        {
            var samples = new float[MaxSamplesPerBlock];
            var read = new float[MaxSamplesPerBlock];
            var bytes = new byte[MaxBinaryBytes];
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            Task? pending = null;
            int held = 0, heldRevision = 0;
            // Every read goes through here: audio held under an older processing
            // revision (a context, mode or setting change) is dropped, and so is
            // a read that races a change.
            bool Drain(out (bool Active, NrMode Mode, int? LatencyMs, int Samples) result, out int revision)
            {
                revision = Volatile.Read(ref _processingRevision);
                if (held > 0 && revision != heldRevision) held = 0;
                result = ReadProcessing(read);
                if (revision != Volatile.Read(ref _processingRevision)) { held = 0; return false; }
                held = result.Active ? HoldReturn(samples, held, read.AsSpan(0, result.Samples)) : 0;
                heldRevision = revision;
                return true;
            }
            try
            {
                while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                {
                    // Hold, never discard, what arrives while one send owns the
                    // byte buffer: a hole in the return is a click and a deficit
                    // the playback side can only fill with silence (#3215).
                    // Never cancel a slow send: that aborts the duplex socket
                    // and would also interrupt the independent raw decoder feed.
                    if (pending is { IsCompleted: false })
                    {
                        Drain(out _, out _);
                        continue;
                    }
                    if (pending is not null)
                    {
                        await pending.ConfigureAwait(false);
                        pending = null;
                    }
                    if (!_optedIn)
                    {
                        held = 0;
                        if (!_announced) continue;
                        if (!_lastProcessing.Active) { _announced = false; continue; }
                    }
                    if (!Drain(out var result, out int revision)) continue;
                    var state = (result.Active && _optedIn, result.Mode, result.LatencyMs);
                    if (!_announced || state != _lastProcessing)
                    {
                        var announcement = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            type = "processing", active = state.Item1, nr = result.Mode.ToString(), latencyMs = result.LatencyMs,
                        });
                        var send = BeginReturn(socket, announcement, WebSocketMessageType.Text, revision, stop);
                        if (send is null) continue;
                        pending = send.Value.AsTask();
                        _announced = true;
                        _lastProcessing = state;
                        // Audio resumes on a later tick, after the notice completes.
                        continue;
                    }
                    if (!state.Item1 || Detached.IsCompleted) { held = 0; continue; }
                    if (held == 0) continue;
                    for (int i = 0; i < held; i++)
                        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), samples[i]);
                    var binary = BeginReturn(socket, bytes.AsMemory(0, held * sizeof(float)),
                        WebSocketMessageType.Binary, revision, stop);
                    held = 0;
                    if (binary is { } sending) pending = sending.AsTask();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException
                or ObjectDisposedException or InvalidOperationException)
            {
                // A failed return path disables this sender, never the source.
                // Receive/shutdown owns detachment and cancellation of the socket.
                if (!stop.IsCancellationRequested)
                {
                    StopProcessing();
                    _port._log.LogWarning(ex, "external_receiver.nr.return-unavailable");
                }
            }
            finally
            {
                if (pending is not null)
                {
                    try { await pending.ConfigureAwait(false); }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException
                        or ObjectDisposedException or InvalidOperationException) { }
                }
            }
        }

        /// <summary>
        /// Appends fresh return audio to the held block, keeping the newest
        /// <see cref="MaxSamplesPerBlock"/> samples when it would overflow.
        /// </summary>
        internal static int HoldReturn(float[] held, int count, ReadOnlySpan<float> fresh)
        {
            if (fresh.Length >= held.Length)
            {
                fresh[^held.Length..].CopyTo(held);
                return held.Length;
            }
            int keep = Math.Min(count, held.Length - fresh.Length);
            if (keep < count) held.AsSpan(count - keep, keep).CopyTo(held);
            fresh.CopyTo(held.AsSpan(keep));
            return keep + fresh.Length;
        }

        // Serialize send initiation with context changes and detach, without
        // holding the publication gate across a socket wait.
        private ValueTask? BeginReturn(WebSocket socket, ReadOnlyMemory<byte> payload,
            WebSocketMessageType type, int revision, CancellationToken stop)
        {
            lock (_port._publishGate)
            {
                if (_port._source != this || revision != Volatile.Read(ref _processingRevision)) return null;
                if (type == WebSocketMessageType.Binary
                    && (_port._context is not { Value: { ProcessedAudio: true, Transmitting: false } }
                        || !_lastProcessing.Active)) return null;
                return socket.SendAsync(payload, type, true, stop);
            }
        }

        private static Task SendProcessingAsync(WebSocket socket, bool active, string? nr, int? latencyMs,
            CancellationToken stop) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(
                new { type = "processing", active, nr, latencyMs }).AsMemory(),
                WebSocketMessageType.Text, true, stop).AsTask();

        private static async Task CloseAsync(
            WebSocket socket,
            string reason,
            Task<ValueWebSocketReceiveResult>? pending)
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            if (reason == ReasonClosed)
            {
                // The peer started the close: complete its handshake.
                if (socket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        await socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException
                                                   or ObjectDisposedException or InvalidOperationException)
                    {
                        socket.Abort();
                    }
                }
                return;
            }
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                return;
            try
            {
                var message = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { type = "detached", reason }));
                await socket.SendAsync(message, WebSocketMessageType.Text, true, timeout.Token)
                    .ConfigureAwait(false);
                await socket.CloseOutputAsync(
                    reason == ReasonProtocol
                        ? WebSocketCloseStatus.PolicyViolation
                        : WebSocketCloseStatus.NormalClosure,
                    reason,
                    timeout.Token).ConfigureAwait(false);
                // Wait for the peer's close frame so nothing outlives this
                // request; data still in flight is discarded.
                var drain = new byte[1024];
                while (true)
                {
                    pending ??= socket.ReceiveAsync(drain.AsMemory(), timeout.Token).AsTask();
                    var result = await pending.WaitAsync(timeout.Token).ConfigureAwait(false);
                    pending = null;
                    if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException
                                           or ObjectDisposedException or InvalidOperationException)
            {
                socket.Abort();
            }
        }

        private void LogStatsIfDue()
        {
            long now = Environment.TickCount64;
            if (now < _statsDueMs) return;
            _statsDueMs = now + StatsIntervalMs;
            long published = Interlocked.Read(ref PublishedBlocks);
            long suppressed = Interlocked.Read(ref SuppressedBlocks);
            _port._log.LogInformation(
                "external_receiver.stats blocks={Blocks} suppressedTx={Suppressed}",
                published - _statsPublished,
                suppressed - _statsSuppressed);
            _statsPublished = published;
            _statsSuppressed = suppressed;
        }
    }
}
