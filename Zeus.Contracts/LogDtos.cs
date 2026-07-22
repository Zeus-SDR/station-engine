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
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Contracts;

public sealed record LogEntry(
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
    IReadOnlyList<string>? Tags = null,
    string? QslSent = null,
    string? QslRcvd = null,
    DateTime? QslSentDate = null,
    DateTime? QslRcvdDate = null,
    DateTime? LotwQslSentUtc = null,
    DateTime? LotwQslRcvdUtc = null,
    DateTime? QrzQslRcvdUtc = null,
    string? Rig = null,
    string? Antenna = null,
    double? TxPowerW = null);

public sealed record CreateLogEntryRequest(
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
    DateTime? QsoDateTimeUtc = null);

/// <summary>
/// Partial QSO update. Null leaves a field unchanged; empty strings and empty
/// lists explicitly clear text/list fields. Nullable value fields use explicit
/// clear flags where a null update value otherwise means "leave unchanged".
/// </summary>
public sealed record UpdateLogEntryRequest(
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

public sealed record LogEntriesResponse(
    IEnumerable<LogEntry> Entries,
    int TotalCount);

public sealed record AdifImportResponse(
    int TotalRecords,
    int ImportedCount,
    int DuplicateCount,
    int SkippedCount,
    IEnumerable<AdifImportError> Errors);

public sealed record AdifImportError(
    int RecordNumber,
    string Message);

public sealed record QrzPublishRequest(
    IEnumerable<string> LogEntryIds);

public sealed record QrzPublishResponse(
    int TotalCount,
    int SuccessCount,
    int FailedCount,
    IEnumerable<QrzPublishResult> Results);

public sealed record QrzPublishResult(
    string LogEntryId,
    bool Success,
    string? QrzLogId,
    string? Message);

/// <summary>
/// Delete one or more logbook entries by id. Mirrors <see cref="QrzPublishRequest"/>
/// — the Logbook panel selects rows and acts on the whole selection at once.
/// </summary>
public sealed record LogDeleteRequest(
    IEnumerable<string> LogEntryIds);

public sealed record LogDeleteResponse(
    int DeletedCount);

/// <summary>
/// Write the logbook (or a selected subset) to an ADIF file in a directory on
/// the machine running the backend. <see cref="Directory"/> is optional — null
/// or blank writes to the operator's Downloads folder. Used by the Logbook
/// panel's Export button so the operator gets a concrete saved path back
/// instead of a silent browser download.
/// </summary>
public sealed record AdifExportToFileRequest(
    string? Directory = null,
    IEnumerable<string>? LogEntryIds = null);

public sealed record AdifExportToFileResponse(
    string Path,
    int Count,
    long Bytes);

public sealed record WorkedCallsignSummary(
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
    IReadOnlyList<WorkedCallsignRecentQso> RecentQsos);

public sealed record WorkedCallsignRecentQso(
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
