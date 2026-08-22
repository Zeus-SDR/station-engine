// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Persists the operator's additive S-meter calibration in the shared
/// station-engine database. The single row follows the established per-board
/// dictionary shape used by other radio calibration stores; the 0x0A alias
/// family adds its selected variant to the key.
/// </summary>
public sealed class SMeterCalibrationStore : IDisposable
{
    public const double MinOffsetDb = -50.0;
    public const double MaxOffsetDb = 50.0;
    public const double StepDb = 0.1;

    private const string ProfileId = "default";
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<SMeterCalibrationEntry> _entries;
    private readonly object _sync = new();

    public event Action? Changed;

    public SMeterCalibrationStore(
        ILogger<SMeterCalibrationStore> log,
        string? dbPathOverride = null)
    {
        string dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        string? directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _entries = _dbLease.Database.GetCollection<SMeterCalibrationEntry>(
            "smeter_calibration");
        _entries.EnsureIndex(x => x.ProfileId, unique: true);
        log.LogInformation("SMeterCalibrationStore initialized at {Path}", dbPath);
    }

    public double Get(
        HpsdrBoardKind board,
        OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        lock (_sync)
        {
            var entry = _entries.FindOne(x => x.ProfileId == ProfileId);
            return entry?.OffsetDbByBoard?.TryGetValue(BoardKey(board, variant), out double offset) is true
                ? offset
                : 0.0;
        }
    }

    public double Set(
        HpsdrBoardKind board,
        OrionMkIIVariant variant,
        double offsetDb)
    {
        if (!double.IsFinite(offsetDb))
            throw new ArgumentOutOfRangeException(
                nameof(offsetDb),
                offsetDb,
                "S-meter calibration must be finite.");

        double applied = Math.Round(Math.Round(
            Math.Clamp(offsetDb, MinOffsetDb, MaxOffsetDb) / StepDb,
            MidpointRounding.AwayFromZero) * StepDb, 6);
        if (applied == 0.0) applied = 0.0;

        lock (_sync)
        {
            var entry = _entries.FindOne(x => x.ProfileId == ProfileId);
            if (entry is null)
            {
                entry = new SMeterCalibrationEntry
                {
                    ProfileId = ProfileId,
                    OffsetDbByBoard = new Dictionary<string, double>
                    {
                        [BoardKey(board, variant)] = applied,
                    },
                    UpdatedUtc = DateTime.UtcNow,
                };
                _entries.Insert(entry);
            }
            else
            {
                entry.OffsetDbByBoard ??= new Dictionary<string, double>();
                entry.OffsetDbByBoard[BoardKey(board, variant)] = applied;
                entry.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(entry);
            }
        }

        Changed?.Invoke();
        return applied;
    }

    internal static string BoardKey(
        HpsdrBoardKind board,
        OrionMkIIVariant variant) =>
        board == HpsdrBoardKind.OrionMkII
            ? $"{board}:{variant}"
            : board.ToString();

    public void Dispose() => _dbLease.Dispose();
}

public sealed class SMeterCalibrationEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public Dictionary<string, double>? OffsetDbByBoard { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}
