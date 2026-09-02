// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Plugins.Contracts.Extensions;

/// <summary>
/// Optional extension for a backend plugin that owns the Zeus QSO logbook
/// store and ADIF import/export implementation. The host keeps the stable
/// /api/log/* wire contract and calls through this seam.
/// </summary>
public interface ILogbookPlugin
{
    Task<LogbookEntrySnapshot> CreateAsync(LogbookNewEntry entry, CancellationToken ct = default);
    Task<LogbookPage> GetEntriesAsync(int skip, int take, CancellationToken ct = default);
    Task<IReadOnlyList<LogbookEntrySnapshot>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct = default);
    Task<LogbookWorkedSummary?> GetWorkedSummaryAsync(string callsign, int recentTake, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDigitalWorkedCallsignsAsync(CancellationToken ct = default);
    Task<bool> UpdateQrzUploadStatusAsync(string id, string qrzLogId, CancellationToken ct = default);
    Task<int> DeleteAsync(IEnumerable<string> ids, CancellationToken ct = default);
    Task<string> ExportAdifAsync(IEnumerable<string>? ids = null, CancellationToken ct = default);
    Task<LogbookExportFileResult> ExportAdifToFileAsync(string? directory = null, IEnumerable<string>? ids = null, CancellationToken ct = default);
    Task<LogbookImportResult> ImportAdifAsync(string adifText, CancellationToken ct = default);
}

public interface ILogbookPluginV2 : ILogbookPlugin
{
    Task<LogbookEntrySnapshot?> UpdateAsync(string id, LogbookEntryUpdate update, CancellationToken ct = default);
    Task<int> UpdateQslStatusAsync(IReadOnlyList<LogbookQslStatusUpdate> updates, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default);
}

/// <summary>
/// Logbook SDK 1.5 coordinate-capable provider. Implementing this marker means
/// the provider persists and returns <see cref="LogbookNewEntry.Lat"/> and
/// <see cref="LogbookNewEntry.Lon"/> as an exact pair.
/// </summary>
public interface ILogbookPluginV3 : ILogbookPluginV2;
