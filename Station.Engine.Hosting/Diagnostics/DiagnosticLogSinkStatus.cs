// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Point-in-time health snapshot of an <see cref="IDiagnosticLogFileSink"/> for
/// support diagnostics. The sink itself stays best-effort-silent toward the
/// logging pipeline, but a sink that can never open its file (ACL-denied logs
/// dir, Controlled Folder Access, a stray FILE named "logs") must leave a
/// readable signal somewhere: an engine that "runs fine yet writes no
/// zeus-app.log" is otherwise undiagnosable from the operator's machine.
/// </summary>
/// <param name="Path">The log file path the sink was configured with.</param>
/// <param name="Degraded">
/// True when the most recent open/write attempt failed and the sink is
/// currently dropping lines; clears on the next successful write.
/// </param>
/// <param name="LastError">
/// Redacted text of the most recent failure (sticky across recovery so a
/// transient failure remains visible); null when no failure has occurred.
/// </param>
public sealed record DiagnosticLogSinkStatus(
    string Path,
    bool Degraded,
    string? LastError);
