// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Globalization;
using System.Runtime.InteropServices;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Synchronous, direct-to-sink lifecycle markers for the standalone engine.
/// The framework logging pipeline only starts flowing once the host is built,
/// so an engine that dies during Build (bad args, prefs-migration failure, DI
/// validation) — or a launcher that silently ran an OLD cached engine — leaves
/// an empty logs dir and no way to tell what happened. These markers bypass
/// the pipeline: the banner lands the instant the process starts (proving WHICH
/// build ran, with its commit), and the fatal marker captures any unhandled
/// build/run failure with its stack. Every write is best-effort and never
/// throws into the caller.
/// </summary>
public static class EngineStartupLog
{
    /// <summary>Append the startup banner. Never throws; the banner must never block launch.</summary>
    public static void WriteBanner(IDiagnosticLogFileSink sink, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(sink);
        try
        {
            sink.Append(BuildBannerLine(args));
        }
        catch
        {
            // Best effort — a banner failure must never block launch.
        }
    }

    /// <summary>
    /// The scrubbed banner line: engine version+commit, pid, OS/arch, the
    /// resolved data dir and log path (proving WHERE the engine believes its
    /// log lives), and the raw argument vector.
    /// </summary>
    public static string BuildBannerLine(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var line = $"{timestamp} INFO StationEngine station-engine start " +
                   $"version={StationProtocolEndpoints.EngineVersion} " +
                   $"pid={Environment.ProcessId} " +
                   $"os=\"{RuntimeInformation.OSDescription}\" " +
                   $"arch={RuntimeInformation.ProcessArchitecture} " +
                   $"dataDir={PrefsDbPath.DataDir} " +
                   $"logPath={PrefsDbPath.AppLogPath()} " +
                   $"args=\"{string.Join(' ', args)}\"";
        return Redaction.Scrub(line);
    }

    /// <summary>
    /// Append a fatal crash marker (full exception, including stack) for an
    /// unhandled build/run failure, and echo it to stderr for launches that DO
    /// have a console (dev runs; the launcher's spawned child has none). Never
    /// throws.
    /// </summary>
    public static void WriteFatal(IDiagnosticLogFileSink sink, string phase, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            sink.Append(Redaction.Scrub(
                $"{timestamp} CRIT StationEngine station-engine fatal phase={phase} {exception}"));
        }
        catch
        {
            // Best effort — the process is already on its way out.
        }

        try
        {
            Console.Error.WriteLine($"StationEngine fatal ({phase}): {exception}");
        }
        catch
        {
            // stderr itself may be unavailable.
        }
    }
}
