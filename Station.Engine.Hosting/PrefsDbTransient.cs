// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server;

// Two separate answers. PrefsDbPath.Probe uses only IsTransientLock, which
// is develop's rule: an IOException (a file lock, and EndOfStreamException,
// which is an IOException) or an inner IOException. Access denial is not a
// lock. A permanently unwritable file — the Windows read-only attribute —
// throws UnauthorizedAccessException, and Probe treats that as corrupt so
// EnsureUsable can move it aside and the engine can start.
// PrefsCollectionShapeGuard uses both predicates. It never moves a file, so
// a lock or an access denial means the row shape cannot be judged and the
// file stays. EnsureUsable owns page-level judgement.
internal static class PrefsDbTransient
{
    internal const int Attempts = 3;
    internal const int BackoffMs = 150;

    internal static bool IsTransientLock(Exception ex) =>
        ex is IOException || ex.InnerException is IOException;

    internal static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex.InnerException is UnauthorizedAccessException;
}
