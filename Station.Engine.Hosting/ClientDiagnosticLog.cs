// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Text;
using System.Text.Json;

namespace Zeus.Server;

// Frontend client-error ingest, shared by every host that serves the SPA.
//
// The desktop webview has no console an operator can open, and in the Zeus
// Link topology the standalone engine is the SPA's API origin — so a frontend
// crash is invisible unless the failing line lands in the host's own log
// stream. The SPA's clientErrorBeacon reports uncaught errors over two
// transports: the /ws diagnostic frame (MsgType.ClientDiagnosticLog, tried
// first) and POST /api/diagnostics/client-log (sendBeacon / keepalive-fetch
// fallback). Both funnel through the sanitize + rate-limit primitives below.
//
// These types moved here from Zeus.Server.Hosting so the standalone engine
// can ingest beacons itself instead of dropping them (the field defect where
// uncaught webview errors vanished in attach mode). The product host keeps
// using the same primitives — one sanitizer, one budget shape, two hosts.

/// <summary>POST /api/diagnostics/client-log body (also the JSON payload of
/// the websocket diagnostic frame, which additionally carries a realm).</summary>
internal sealed record ClientLogRequest(string? Where, string? Message, string? Stack);

internal sealed record SanitizedClientLog(string Where, string Message, string Stack);

// Public (with the limiter below) because the websocket sinks and the HTTP
// endpoints share one rate window per host.
public sealed record ClientLogRateDecision(bool Accepted, int DroppedCount);

internal static class ClientLogIngress
{
    internal const int MaxBodyBytes = 16 * 1024;
    private const int MaxWhereChars = 256;
    private const int MaxMessageChars = 2_000;
    private const int MaxStackChars = 4_000;

    private static readonly JsonSerializerOptions FrameJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    internal static SanitizedClientLog Sanitize(ClientLogRequest request) =>
        new(
            SanitizeField(request.Where ?? "(unknown)", MaxWhereChars),
            SanitizeField(request.Message ?? "(no message)", MaxMessageChars),
            SanitizeField(request.Stack ?? "", MaxStackChars));

    /// <summary>Realm label ("main" / "workspace:…" / "audio-suite:tx") carried
    /// only by the websocket diagnostic frame (MsgType.ClientDiagnosticLog) —
    /// same newline-strip + cap treatment as the other client-supplied fields.</summary>
    internal static string SanitizeRealm(string? realm) =>
        SanitizeField(realm ?? "", MaxRealmChars);

    private const int MaxRealmChars = 64;

    internal static bool IsOversized(ClientLogRequest request, long? contentLength)
    {
        if (contentLength is > MaxBodyBytes)
            return true;

        // Content-Length is normally present for the browser beacon. Retain a
        // conservative decoded-payload check for chunked or test clients.
        var decodedBytes = 128
            + Encoding.UTF8.GetByteCount(request.Where ?? "")
            + Encoding.UTF8.GetByteCount(request.Message ?? "")
            + Encoding.UTF8.GetByteCount(request.Stack ?? "");
        return decodedBytes > MaxBodyBytes;
    }

    /// <summary>
    /// Shared policy for websocket diagnostic frame 0x23: parse, size-check,
    /// rate-limit, sanitize, and log with the same Warning "webview.error"
    /// shape as the HTTP endpoint. Callers pass their own logger so each
    /// host's log category is preserved.
    /// </summary>
    internal static void HandleDiagnosticFrame(
        ReadOnlyMemory<byte> payload,
        ClientLogRateLimiter rateLimiter,
        ILogger log)
    {
        ClientDiagnosticLogPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ClientDiagnosticLogPayload>(
                payload.Span, FrameJsonOptions);
        }
        catch (JsonException)
        {
            log.LogDebug("ws.client-log malformed json len={Len}", payload.Length);
            return;
        }
        if (parsed is null) return;

        var request = new ClientLogRequest(parsed.Where, parsed.Message, parsed.Stack);
        if (IsOversized(request, payload.Length))
        {
            log.LogDebug("ws.client-log oversize len={Len}", payload.Length);
            return;
        }

        var rate = rateLimiter.TryAcquire(DateTimeOffset.UtcNow);
        if (!rate.Accepted) return;
        if (rate.DroppedCount > 0)
        {
            log.LogWarning(
                "webview.error rate limit dropped={DroppedCount} in previous window",
                rate.DroppedCount);
        }

        var entry = Sanitize(request);
        log.LogWarning(
            "webview.error where={Where} message={Message} stack={Stack} realm={Realm}",
            entry.Where,
            entry.Message,
            entry.Stack,
            SanitizeRealm(parsed.Realm));
    }

    private sealed record ClientDiagnosticLogPayload(
        string? Where,
        string? Message,
        string? Stack,
        string? Realm);

    private static string SanitizeField(string value, int maxChars)
    {
        var singleLine = value
            .Replace("\r\n", " | ", StringComparison.Ordinal)
            .Replace("\r", " | ", StringComparison.Ordinal)
            .Replace("\n", " | ", StringComparison.Ordinal);
        return singleLine.Length <= maxChars ? singleLine : singleLine[..maxChars];
    }
}

public sealed class ClientLogRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxEntriesPerWindow = 60;
    private readonly object _sync = new();
    private DateTimeOffset? _windowStarted;
    private int _accepted;
    private int _dropped;

    public ClientLogRateDecision TryAcquire(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_windowStarted is null || now - _windowStarted >= Window || now < _windowStarted)
            {
                var previousDropped = _dropped;
                _windowStarted = now;
                _accepted = 1;
                _dropped = 0;
                return new ClientLogRateDecision(true, previousDropped);
            }

            if (_accepted < MaxEntriesPerWindow)
            {
                _accepted++;
                return new ClientLogRateDecision(true, _dropped);
            }

            _dropped++;
            return new ClientLogRateDecision(false, _dropped);
        }
    }
}

/// <summary>
/// Standalone-engine policy for frontend diagnostic frame 0x23. Before this
/// sink existed the engine bound IClientDiagnosticSink to the null sink, so
/// in local attach the beacon's preferred websocket transport delivered the
/// frame and the engine silently discarded it.
/// </summary>
public sealed class EngineClientDiagnosticSink : IClientDiagnosticSink
{
    private readonly ClientLogRateLimiter _rateLimiter;
    private readonly ILogger<EngineClientDiagnosticSink> _log;

    public EngineClientDiagnosticSink(
        ClientLogRateLimiter rateLimiter,
        ILogger<EngineClientDiagnosticSink> log)
    {
        _rateLimiter = rateLimiter;
        _log = log;
    }

    public void Handle(ReadOnlyMemory<byte> payload) =>
        ClientLogIngress.HandleDiagnosticFrame(payload, _rateLimiter, _log);
}

/// <summary>
/// Maps the standalone engine's client-error beacon endpoint. Mirrors the
/// product host's /api/diagnostics/client-log contract (413 on oversize,
/// 204 otherwise, Warning "webview.error" log line) so the SPA beacon works
/// unchanged in local attach.
/// </summary>
public static class ClientDiagnosticLogEndpoints
{
    public static IEndpointRouteBuilder MapClientDiagnosticLogEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/diagnostics/client-log",
            (HttpContext ctx,
                ClientLogRequest req,
                ClientLogRateLimiter rateLimiter,
                ILogger<EngineClientDiagnosticSink> log) =>
            {
                if (ClientLogIngress.IsOversized(req, ctx.Request.ContentLength))
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                var rate = rateLimiter.TryAcquire(DateTimeOffset.UtcNow);
                if (!rate.Accepted)
                    return Results.NoContent();
                if (rate.DroppedCount > 0)
                {
                    log.LogWarning(
                        "webview.error rate limit dropped={DroppedCount} in previous window",
                        rate.DroppedCount);
                }

                var entry = ClientLogIngress.Sanitize(req);
                log.LogWarning("webview.error where={Where} message={Message} stack={Stack}",
                    entry.Where,
                    entry.Message,
                    entry.Stack);
                return Results.NoContent();
            })
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
                ClientLogIngress.MaxBodyBytes));

        return endpoints;
    }
}
