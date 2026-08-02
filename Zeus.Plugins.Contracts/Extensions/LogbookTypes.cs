// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Plugins.Contracts.Extensions;

public sealed record LogbookNewEntry(
    string Callsign,
    string? Name,
    double FrequencyMhz,
    string Band,
    string Mode,
    string RstSent,
    string RstRcvd,
    string? Grid = null,
    string? Country = null,
    int? Dxcc = null,
    int? CqZone = null,
    int? ItuZone = null,
    string? State = null,
    string? Comment = null,
    DateTime? QsoDateTimeUtc = null,
    Dictionary<string, string>? AdifFields = null);

public sealed record LogbookEntrySnapshot(
    string Id,
    DateTime QsoDateTimeUtc,
    string Callsign,
    string? Name,
    double? FrequencyMhz,
    string Band,
    string Mode,
    string RstSent,
    string RstRcvd,
    string? Grid,
    string? Country,
    int? Dxcc,
    int? CqZone,
    int? ItuZone,
    string? State,
    string? Comment,
    DateTime CreatedUtc,
    string? QrzLogId = null,
    DateTime? QrzUploadedUtc = null,
    Dictionary<string, string>? AdifFields = null)
{
    public IReadOnlyList<string>? Tags { get; init; }
    public string? QslSent { get; init; }
    public string? QslRcvd { get; init; }
    public DateTime? QslSentDate { get; init; }
    public DateTime? QslRcvdDate { get; init; }
    public DateTime? LotwQslSentUtc { get; init; }
    public DateTime? LotwQslRcvdUtc { get; init; }
    public DateTime? QrzQslRcvdUtc { get; init; }
    public string? Rig { get; init; }
    public string? Antenna { get; init; }
    public double? TxPowerW { get; init; }
}

/// <summary>
/// Partial QSO update. Null leaves a field unchanged; empty strings and empty
/// lists explicitly clear text/list fields. Nullable value fields use explicit
/// clear flags where a null update value otherwise means "leave unchanged".
/// </summary>
public sealed record LogbookEntryUpdate(
    string? Name = null,
    string? Grid = null,
    string? Country = null,
    string? State = null,
    string? Comment = null,
    IReadOnlyList<string>? Tags = null,
    string? QslSent = null,
    string? QslRcvd = null,
    DateTime? QslSentDate = null,
    DateTime? QslRcvdDate = null,
    string? Rig = null,
    string? Antenna = null,
    double? TxPowerW = null,
    string? RstSent = null,
    string? RstRcvd = null,
    string? Mode = null,
    string? Band = null,
    double? FrequencyMhz = null,
    DateTime? QsoDateTimeUtc = null,
    bool ClearQslSentDate = false,
    bool ClearQslRcvdDate = false,
    bool ClearTxPowerW = false,
    bool ClearFrequencyMhz = false);

public sealed record LogbookQslStatusUpdate(
    string Id,
    DateTime? LotwQslRcvdUtc,
    DateTime? LotwQslSentUtc,
    DateTime? QrzQslRcvdUtc,
    string? QslRcvd,
    DateTime? QslRcvdDate);

public sealed record LogbookPage(
    IReadOnlyList<LogbookEntrySnapshot> Entries,
    int TotalCount);

public sealed record LogbookImportResult(
    int TotalRecords,
    int ImportedCount,
    int DuplicateCount,
    int SkippedCount,
    IReadOnlyList<LogbookImportError> Errors);

public sealed record LogbookImportError(
    int RecordNumber,
    string Message);

public sealed record LogbookExportFileResult(
    string Path,
    int Count,
    long Bytes);

public sealed record LogbookWorkedSummary(
    string Callsign,
    bool WorkedBefore,
    int TotalCount,
    DateTime? LastWorkedUtc,
    string? LastBand,
    string? LastMode,
    double? LastFrequencyMhz,
    string? LastRstSent,
    string? LastRstRcvd,
    string? LastName,
    string? LastGrid,
    string? LastCountry,
    string? LastState,
    string? LastComment,
    IReadOnlyList<string> Bands,
    IReadOnlyList<string> Modes,
    IReadOnlyList<LogbookWorkedRecentQso> RecentQsos);

public sealed record LogbookWorkedRecentQso(
    DateTime QsoDateTimeUtc,
    string? Band,
    string? Mode,
    double? FrequencyMhz,
    string? RstSent,
    string? RstRcvd,
    string? Name,
    string? Grid,
    string? Country,
    string? State,
    string? Comment,
    string? QrzLogId);
