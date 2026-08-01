// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server.Diagnostics;

/// <summary>
/// A durable sink for already-formatted, already-redacted log lines. It retains
/// the full Information-and-above trace, including framework lines filtered
/// from the in-memory report ring, so crash forensics can be tailed by the
/// out-of-process support sidecar. Implementations must be thread-safe and
/// best-effort: a logging sink must never throw into the logging pipeline or
/// block the app on an I/O hiccup.
/// </summary>
public interface IDiagnosticLogFileSink
{
    /// <summary>Append one formatted+redacted line. Cheap; safe from any thread; never throws.</summary>
    void Append(string line);

    /// <summary>
    /// Health snapshot for support diagnostics: the configured path, whether
    /// the sink is currently dropping lines, and the most recent redacted
    /// failure text. Reading it is an in-memory snapshot only — never throws,
    /// never touches the filesystem.
    /// </summary>
    DiagnosticLogSinkStatus Status { get; }
}
