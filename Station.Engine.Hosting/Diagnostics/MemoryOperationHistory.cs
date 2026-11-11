// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server.Diagnostics;

/// <summary>
/// One memory operation recorded by <see cref="MemoryOperationHistory"/>.
/// It carries the identity and outcome of the operation only: no name, group,
/// notes, frequency or file content is ever recorded.
/// </summary>
public sealed record MemoryOperationEntry(
    DateTimeOffset Utc,
    string Operation,
    string Origin,
    string? MemoryId,
    int? Revision,
    long? Generation,
    string Result,
    string? Code,
    string? FailedStep,
    bool? RollbackSucceeded,
    int? Count);

/// <summary>
/// Small ring of the most recent memory operations (saves, edits, recalls,
/// group operations and imports) for the Submit-an-Issue report. Like
/// <see cref="TransmitHistory"/> it only grows when an operation happens, so a
/// report filed after the fact still shows the recall it is about.
/// </summary>
public sealed class MemoryOperationHistory
{
    public const int Capacity = 50;

    public const string ResultOk = "ok";
    public const string ResultRefused = "refused";
    public const string ResultFailed = "failed";

    private readonly object _sync = new();
    private readonly MemoryOperationEntry[] _ring = new MemoryOperationEntry[Capacity];
    private int _next;
    private int _count;

    public void Record(MemoryOperationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Recorded operations, oldest first.</summary>
    public IReadOnlyList<MemoryOperationEntry> Snapshot()
    {
        lock (_sync)
        {
            var result = new MemoryOperationEntry[_count];
            int start = ((_next - _count) % Capacity + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
                result[i] = _ring[(start + i) % Capacity];
            return result;
        }
    }
}

/// <summary>The one place a memory operation is logged and recorded, so the
/// log line and the report row always agree.</summary>
public static class MemoryOperationJournal
{
    public static void Write(ILogger log, MemoryOperationHistory? history, MemoryOperationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(entry);
        log.LogInformation(
            "memory.{Operation} id={Id} revision={Revision} origin={Origin} generation={Generation} result={Result} step={Step} rollback={Rollback}",
            entry.Operation,
            entry.MemoryId,
            entry.Revision,
            entry.Origin,
            entry.Generation ?? 0,
            entry.Result,
            entry.FailedStep,
            entry.RollbackSucceeded);
        history?.Record(entry);
    }
}
