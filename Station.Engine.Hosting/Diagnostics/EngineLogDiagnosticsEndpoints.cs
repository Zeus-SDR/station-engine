// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server.Diagnostics;

/// <summary>
/// GET /api/diagnostics/engine-log — loopback-readable snapshot of the engine's
/// diagnostic-log pipeline: which build is running, where the rolling
/// zeus-app.log SHOULD live, whether the on-disk sink is healthy (and if not,
/// why), plus the tail of the in-memory ring so support can pull recent engine
/// logs over HTTP even when the file itself cannot be written on the
/// operator's machine. Safe on the engine's loopback-only surface: paths and
/// usernames are redacted before they leave the process.
/// </summary>
public static class EngineLogDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapEngineLogDiagnosticsEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(
            "/api/diagnostics/engine-log",
            (RequestDelegate)(context =>
        {
            var buffer = context.RequestServices.GetService<DiagnosticLogBuffer>();
            var sink = context.RequestServices.GetService<IDiagnosticLogFileSink>();
            var maxLines = ResolveMaxLines(
                context.Request.Query["lines"].ToString());
            return context.Response.WriteAsJsonAsync(
                EngineLogDiagnosticsSnapshot.Capture(
                    buffer,
                    sink,
                    maxLines));
        }));
        return endpoints;
    }

    internal static int ResolveMaxLines(string? value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var requested)
        && requested > 0
            ? Math.Min(requested, DiagnosticLogBuffer.Capacity)
            : DiagnosticLogBuffer.ReportTailLines;
}

internal sealed record EngineLogDiagnosticsSnapshot(
    string EngineVersion,
    int Pid,
    string DataDir,
    string LogPath,
    bool LogExists,
    long LogBytes,
    bool? SinkDegraded,
    string? SinkLastError,
    IReadOnlyList<string> RecentLines)
{
    public static EngineLogDiagnosticsSnapshot Capture(
        DiagnosticLogBuffer? buffer,
        IDiagnosticLogFileSink? sink,
        int maxLines = DiagnosticLogBuffer.ReportTailLines)
    {
        var logPath = PrefsDbPath.AppLogPath();
        var exists = false;
        long bytes = 0;
        try
        {
            var info = new FileInfo(logPath);
            exists = info.Exists;
            if (exists) bytes = info.Length;
        }
        catch
        {
            // Path probing is best-effort; a bad path reports as "no file".
        }

        var status = sink?.Status;
        return new EngineLogDiagnosticsSnapshot(
            EngineVersion: StationProtocolEndpoints.EngineVersion,
            Pid: Environment.ProcessId,
            DataDir: Redaction.Scrub(PrefsDbPath.DataDir),
            LogPath: Redaction.Scrub(logPath),
            LogExists: exists,
            LogBytes: bytes,
            SinkDegraded: status?.Degraded,
            SinkLastError: status?.LastError,
            RecentLines: buffer?.Snapshot(maxLines) ?? []);
    }
}
