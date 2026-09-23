// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server.Diagnostics;

/// <summary>
/// One key-up or release recorded by <see cref="TransmitHistory"/>. Release
/// timings are milliseconds measured from the moment the release started, so
/// a report shows directly where a slow unkey spent its time (issue #2374).
/// </summary>
internal sealed record TransmitHistoryEntry(
    DateTimeOffset Utc,
    string Event,
    string Intent,
    string? Source,
    string Mode,
    string? Reason = null,
    int? TailConfiguredMs = null,
    double? TailMs = null,
    double? WireDropMs = null,
    double? AfterWireMs = null,
    double? TotalMs = null,
    double? KeyedMs = null,
    bool? FaultLatched = null,
    bool? Failed = null,
    string? Steps = null);

/// <summary>
/// Small ring of the most recent transmit key-ups and releases. The 4000-line
/// diagnostic log ring rolls over in a few minutes of receive chatter, so a
/// report filed after the fact loses the transmit it is about; this ring only
/// grows on a key-up or release and keeps the last <see cref="Capacity"/>
/// events for the Submit-an-Issue report.
/// </summary>
internal sealed class TransmitHistory
{
    internal const int Capacity = 32;

    private readonly object _sync = new();
    private readonly TransmitHistoryEntry[] _ring = new TransmitHistoryEntry[Capacity];
    private int _next;
    private int _count;

    internal void Record(TransmitHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Recorded events, oldest first.</summary>
    internal IReadOnlyList<TransmitHistoryEntry> Snapshot()
    {
        lock (_sync)
        {
            var result = new TransmitHistoryEntry[_count];
            int start = ((_next - _count) % Capacity + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
                result[i] = _ring[(start + i) % Capacity];
            return result;
        }
    }
}
