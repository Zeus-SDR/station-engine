// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists the Auto-CQ control-operator acknowledgement audit stamp while
/// keeping the per-process session acknowledgement in memory only.
/// Thread-safe; registered as a singleton.
/// </summary>
public sealed class OperatorAckStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<OperatorAckEntry> _state;
    private readonly ILogger<OperatorAckStore> _log;
    private readonly object _sync = new();
    private bool _autoCqAcknowledgedThisSession;

    public OperatorAckStore(ILogger<OperatorAckStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _state = _db.GetCollection<OperatorAckEntry>("operator_ack");

        _log.LogInformation("OperatorAckStore initialized at {Path}", dbPath);
    }

    public DateTime? AutoCqLastAckUtc
    {
        get
        {
            lock (_sync)
            {
                var entry = _state.FindAll().FirstOrDefault();
                return entry?.AutoCqLastAckUtc;
            }
        }
    }

    public bool AutoCqAcknowledgedThisSession
    {
        get
        {
            lock (_sync)
            {
                return _autoCqAcknowledgedThisSession;
            }
        }
    }

    public DateTime RecordAutoCqAck()
    {
        DateTime nowUtc;
        int ackCount;

        lock (_sync)
        {
            var existing = _state.FindAll().FirstOrDefault();
            nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                ackCount = 1;
                _state.Insert(new OperatorAckEntry
                {
                    AutoCqLastAckUtc = nowUtc,
                    AckCount = ackCount,
                });
            }
            else
            {
                ackCount = Math.Max(0, existing.AckCount) + 1;
                existing.AutoCqLastAckUtc = nowUtc;
                existing.AckCount = ackCount;
                _state.Update(existing);
            }

            _autoCqAcknowledgedThisSession = true;
        }

        _log.LogInformation("operator.ack.autocq recorded count={Count}", ackCount);
        return nowUtc;
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class OperatorAckEntry
{
    public int Id { get; set; }
    public DateTime? AutoCqLastAckUtc { get; set; }
    public int AckCount { get; set; }
}
