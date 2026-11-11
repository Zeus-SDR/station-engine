// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Text.Json.Serialization;

namespace Zeus.Contracts;

/// <summary>Limits and constants of the unified memory model (schema version 1).</summary>
public static class StationMemorySchema
{
    public const int Version = 1;
    public const int MaxIdLength = 64;
    public const int MaxNameLength = 80;
    public const int MaxGroupLength = 80;
    public const int MaxNotesLength = 500;
    public const int MaxSourceModeLength = 16;
    public const int MaxAntennaLength = 32;
    public const int MaxOwnerLength = 80;
    public const int MaxRadioSerialLength = 64;
    public const long MaxFrequencyHz = 9_007_199_254_740_991;   // JavaScript-safe integer
    public const int MaxTuneStepHz = 1_000_000_000;
    public const long MaxStoredOffsetHz = 1_000_000;            // rit / xit, plus or minus
    public const int MinCatChannel = 0;
    public const int MaxCatChannel = 99;
    public const int MinShortcutSlot = 1;
    public const int MaxShortcutSlot = 10;
    public const int ListDefaultLimit = 500;
    public const int ListMaxLimit = 2_000;
    public const int ImportMaxBytes = 2 * 1024 * 1024;
    public const int ImportMaxRows = 10_000;
    public const string FavoritesGroup = "Favorites";
    public const string BandStackGroupPrefix = "Band Stacks/";
    public const string CatGroup = "CAT";
    public const string LegacyFavoriteIdPrefix = "legacy_favorite_";
    public const string LegacyBandStackIdPrefix = "legacy_band_stack_";
}

public static class StationMemorySources
{
    public const string User = "user";
    public const string Favorite = "favorite";
    public const string BandStack = "bandStack";
    public const string ZeusCsv = "zeusCsv";
    public const string Chirp = "chirp";
    public const string FlexRadio = "flexRadio";
    public static readonly IReadOnlyList<string> All = [User, Favorite, BandStack, ZeusCsv, Chirp, FlexRadio];
}

/// <summary>Stable <c>code</c> values of <see cref="StationMemoryError"/>.</summary>
public static class StationMemoryErrorCodes
{
    public const string Validation = "validation";                       // 400
    public const string ImportInvalid = "import_invalid";                // 400
    public const string NotFound = "not_found";                          // 404
    public const string RevisionConflict = "revision_conflict";          // 409
    public const string DuplicateAssignment = "duplicate_assignment";    // 409
    public const string StationLocked = "station_locked";                // 409
    public const string TransmitActive = "transmit_active";              // 409
    public const string CapabilityRefused = "capability_refused";        // 409
    public const string RecallInProgress = "recall_in_progress";         // 409
    public const string RecallFailed = "recall_failed";                  // 409
    public const string PreviewMismatch = "preview_mismatch";            // 409
    public const string BackupRequired = "backup_required";              // 409
    public const string StationUnavailable = "station_unavailable";      // 503
    // Flex only
    public const string StaleSession = "stale_session";                  // 409
    public const string RadioLinkStale = "radio_link_stale";             // 409
    public const string RadioLinkDiverged = "radio_link_diverged";       // 409
    public const string RadioWriteIncomplete = "radio_write_incomplete"; // 409
    public const string RadioSnapshotUnavailable = "radio_snapshot_unavailable"; // 503
}

// ---- nested objects (spec 6.3 to 6.5) --------------------------------------

public sealed record StationMemorySplit(bool Enabled, long? TxFrequencyHz = null, string? TxMode = null);

/// <summary>Shape shared by <c>rit</c> and <c>xit</c>.</summary>
public sealed record StationMemoryOffset(bool Enabled, long OffsetHz);

public sealed record StationMemoryDigital(
    int? RttyMarkHz = null,
    int? RttyShiftHz = null,
    int? DiglOffsetHz = null,
    int? DiguOffsetHz = null);

public sealed record StationMemorySquelch(bool Enabled, int Level);

public sealed record StationMemoryLegacy(int? FavoriteSlot = null, int? BandStackId = null, string? Band = null);

public sealed record StationMemoryExternalLink(string RadioSerial, int MemoryIndex, string? Owner = null);

// ---- the memory ------------------------------------------------------------

public sealed record StationMemoryDto(
    int SchemaVersion,
    string Id,
    string Name,
    string? Group,
    string? Notes,
    string Source,
    long CreatedUtcMs,
    long UpdatedUtcMs,
    long? LastRecalledUtcMs,
    int Revision,
    long FrequencyHz,
    string? Mode,
    int? FilterLowHz,
    int? FilterHighHz,
    int? TuneStepHz,
    bool ScanSkip,
    string? SourceMode,
    StationMemorySplit? Split,
    StationMemoryOffset? Rit,
    StationMemoryOffset? Xit,
    StationMemoryDigital? Digital,
    string? RxAntenna,
    string? TxAntenna,
    StationMemorySquelch? Squelch,
    FmMemory? Fm,
    int? CatChannel,
    int? ShortcutSlot,
    StationMemoryLegacy? Legacy,
    IReadOnlyList<StationMemoryExternalLink> ExternalLinks);

/// <summary>The fields a client may set. Identity, source, timestamps, revision,
/// legacy mapping and external links are owned by the server.</summary>
public sealed record StationMemoryWrite(
    string Name,
    long FrequencyHz,
    string? Group = null,
    string? Notes = null,
    string? Mode = null,
    int? FilterLowHz = null,
    int? FilterHighHz = null,
    int? TuneStepHz = null,
    bool ScanSkip = false,
    string? SourceMode = null,
    StationMemorySplit? Split = null,
    StationMemoryOffset? Rit = null,
    StationMemoryOffset? Xit = null,
    StationMemoryDigital? Digital = null,
    string? RxAntenna = null,
    string? TxAntenna = null,
    StationMemorySquelch? Squelch = null,
    FmMemory? Fm = null,
    int? CatChannel = null,
    int? ShortcutSlot = null);

// ---- capabilities (spec 7) -------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter<MemoryCapabilityState>))]
public enum MemoryCapabilityState
{
    [JsonStringEnumMemberName("supported")] Supported,
    [JsonStringEnumMemberName("unsupported")] Unsupported,
    [JsonStringEnumMemberName("unknown")] Unknown,
}

public sealed record StationMemoryFrequencySpan(long MinHz, long MaxHz);

/// <summary>Native range plus the enabled transverter (or radio-reported) spans.</summary>
public sealed record StationMemoryFrequencyRange(
    MemoryCapabilityState State,
    long MinHz,
    long MaxHz,
    IReadOnlyList<StationMemoryFrequencySpan> Extra);

public sealed record StationMemoryCapabilities(
    bool Available,
    string? ReasonUnavailable,
    MemoryCapabilityState NativeApply,
    MemoryCapabilityState RadioRead,
    MemoryCapabilityState RadioWrite,
    StationMemoryFrequencyRange FrequencyRange,
    IReadOnlyList<string> SupportedModes,
    MemoryCapabilityState Filter,
    MemoryCapabilityState Split,
    MemoryCapabilityState Rit,
    MemoryCapabilityState Xit,
    MemoryCapabilityState AntennaSelection,
    MemoryCapabilityState FmRepeater,
    MemoryCapabilityState FmDcs,
    MemoryCapabilityState FmDeviation,
    MemoryCapabilityState DigitalOffsets,
    MemoryCapabilityState Squelch,
    MemoryCapabilityState TuneStep,
    MemoryCapabilityState ReceiverSelection);

public sealed record StationMemoryCapabilityReport(
    bool CanRecall,
    IReadOnlyList<string> SupportedFields,
    IReadOnlyList<string> UnsupportedFields,
    IReadOnlyList<string> Reasons,
    bool CanApplyPartial);

// ---- errors (spec 9.3) -----------------------------------------------------

public sealed record StationMemoryFieldError(string Field, string Message);

public sealed record StationMemoryErrorDetails(
    string? Field = null,
    IReadOnlyList<StationMemoryFieldError>? Fields = null,
    int? CurrentRevision = null,
    string? ConflictingMemoryId = null,
    StationMemoryCapabilityReport? Report = null,
    string? FailedStep = null,
    bool? RollbackSucceeded = null,
    IReadOnlyList<string>? AppliedFields = null,
    bool? OperatorRecoveryRequired = null,   // true whenever RollbackSucceeded is false
    string? KeySource = null,
    IReadOnlyList<int>? Rows = null);

/// <summary>Body of every non-2xx answer of the memory routes.</summary>
public sealed record StationMemoryError(string Code, string Error, StationMemoryErrorDetails? Details = null);

// ---- list / read / write ---------------------------------------------------

public sealed record StationMemoryGroupCount(string? Group, int Count);   // Group null = Ungrouped

public sealed record StationMemoryListResponse(
    IReadOnlyList<StationMemoryDto> Memories,
    int Total,          // rows matching the filters
    int LibraryTotal,   // rows in the library
    int Offset,
    int Limit,
    IReadOnlyList<StationMemoryGroupCount> Groups,   // whole library
    StationMemoryCapabilities Capabilities);

/// <summary>Answer of GET {id}, POST, PUT.</summary>
public sealed record StationMemoryResponse(StationMemoryDto Memory, StationMemoryCapabilities Capabilities);

public sealed record StationMemoryCreateRequest(StationMemoryWrite Memory);

public sealed record StationMemoryUpdateRequest(int ExpectedRevision, StationMemoryWrite Memory);

// ---- capture ---------------------------------------------------------------

/// <summary>One coherent reading of a live receiver, in memory terms.</summary>
public sealed record StationMemoryLiveState(
    int ReceiverIndex,
    long FrequencyHz,
    string? Mode,
    int? FilterLowHz,
    int? FilterHighHz,
    int? TuneStepHz,
    StationMemorySplit? Split,
    StationMemoryOffset? Rit,
    StationMemoryOffset? Xit,
    StationMemoryDigital? Digital,
    string? RxAntenna,
    string? TxAntenna,
    StationMemorySquelch? Squelch,
    FmMemory? Fm);

/// <summary>Save=false returns a draft and writes nothing. Save=true with Id=null creates;
/// with Id and ExpectedRevision it overwrites that memory's receiver fields and keeps its
/// name, group, notes and assignments unless they are supplied here.</summary>
public sealed record StationMemoryCaptureRequest(
    int ReceiverIndex = 0,
    bool Save = false,
    string? Id = null,
    int? ExpectedRevision = null,
    string? Name = null,
    string? Group = null,
    string? Notes = null,
    int? CatChannel = null,
    int? ShortcutSlot = null);

public sealed record StationMemoryCaptureResponse(
    StationMemoryWrite Draft,
    StationMemoryDto? Memory,          // set only when Save=true
    StationMemoryLiveState Live,
    StationMemoryCapabilities Capabilities);

// ---- recall ----------------------------------------------------------------

public sealed record StationMemoryRecallPreviewRequest(int? ExpectedRevision = null, int ReceiverIndex = 0);

/// <summary>A guard that would refuse the recall right now (codes of
/// <see cref="StationMemoryErrorCodes"/>).</summary>
public sealed record StationMemoryRecallConflict(string Code, string Error);

public sealed record StationMemoryRecallPreviewResponse(
    StationMemoryDto Memory,
    StationMemoryCapabilityReport Report,
    IReadOnlyList<StationMemoryRecallConflict> Conflicts,
    StationMemoryCapabilities Capabilities,
    bool NativeApply);                 // Flex: this recall would use the radio's own apply

public sealed record StationMemoryRecallRequest(
    int ExpectedRevision,
    bool AllowPartial = false,
    IReadOnlyList<string>? AcceptedUnsupportedFields = null,
    int ReceiverIndex = 0);

public sealed record StationMemoryRecallResponse(
    StationMemoryDto Memory,                     // lastRecalledUtcMs already set
    int RecalledRevision,
    StationMemoryCapabilityReport Report,
    StationMemoryLiveState FinalState,
    IReadOnlyList<string> AppliedFields,         // verified from FinalState
    IReadOnlyList<string> OmittedFields,         // the accepted unsupported fields
    bool NativeApply);

// ---- group operations ------------------------------------------------------

public sealed record StationMemoryRevisionRef(string Id, int ExpectedRevision);

/// <summary>Operation is "renameGroup", "moveToGroup" or "delete". A null or empty group
/// name means Ungrouped.</summary>
public sealed record StationMemoryGroupOperationRequest(
    string Operation,
    string? FromGroup = null,                                   // renameGroup
    string? ToGroup = null,                                     // renameGroup, moveToGroup
    IReadOnlyList<StationMemoryRevisionRef>? Memories = null);  // moveToGroup, delete

public sealed record StationMemoryGroupOperationResponse(
    string Operation,
    int Affected,
    IReadOnlyList<StationMemoryDto> Memories,    // changed rows (empty for delete)
    IReadOnlyList<string> DeletedIds,
    IReadOnlyList<StationMemoryGroupCount> Groups);

// ---- import / export -------------------------------------------------------

public static class StationMemoryImportFormats
{
    public const string ZeusCsv = "zeus-csv";
    public const string ChirpCsv = "chirp-csv";
    /// <summary>Only in the preview of a radio import. The CSV routes refuse it.</summary>
    public const string Radio = "radio";
}

public static class StationMemoryImportModes
{
    public const string Merge = "merge";
    public const string MergeIntoGroup = "mergeIntoGroup";
    public const string ReplaceGroup = "replaceGroup";
    public const string ReplaceLibrary = "replaceLibrary";
}

public sealed record StationMemoryImportOptions(
    string Mode = StationMemoryImportModes.Merge,
    string? TargetGroup = null,          // required for mergeIntoGroup and replaceGroup
    bool RestoreRevisions = false);      // zeus-csv + replaceLibrary only

public sealed record StationMemoryImportPreviewRequest(
    string Format,
    string Content,                      // the raw CSV text
    StationMemoryImportOptions? Options = null);

/// <summary>Status: "add", "update", "unchanged", "duplicate" or "error".
/// DefaultDecision: "import", "skip", "none" (the row needs an explicit decision) or
/// "update", which only a row that replaces a member of the replaced group in place gets.</summary>
public sealed record StationMemoryImportRow(
    int Row,                             // 1-based data row; the header is row 0
    string Status,
    string DefaultDecision,
    StationMemoryDto? Memory,            // parsed candidate, see the placeholder rule below
    string? ExistingId,
    string? MatchedBy,                   // "id", "externalLink", "identity", "row" or null
    int? MatchedRow,                     // set when MatchedBy is "row"
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> UnsupportedFields);   // against the active station; informational

/// <summary>Removals counts every library row the commit would delete; RemovedFavorites and
/// RemovedBandStack are the favorite-mapped and band-stack-mapped rows among them.</summary>
public sealed record StationMemoryImportSummary(
    int Rows, int Additions, int Updates, int Unchanged, int Duplicates, int Errors, int Warnings, int Removals,
    int RemovedFavorites, int RemovedBandStack);

public sealed record StationMemoryImportPreviewResponse(
    string PreviewHash,
    string Format,
    StationMemoryImportOptions Options,
    StationMemoryImportSummary Summary,
    IReadOnlyList<StationMemoryImportRow> Rows,
    IReadOnlyList<string> FileWarnings,
    bool BackupRequired);

/// <summary>Action: "import", "update", "addAsNew" or "skip".</summary>
public sealed record StationMemoryImportDecision(int Row, string Action);

public sealed record StationMemoryImportCommitRequest(
    string Format,
    string Content,
    StationMemoryImportOptions? Options,
    string PreviewHash,
    IReadOnlyList<StationMemoryImportDecision>? Decisions = null,
    string? BackupToken = null,           // replaceLibrary only
    bool ConfirmReplaceLibrary = false);  // replaceLibrary only

public sealed record StationMemoryImportCommitResponse(
    int Added,
    int Updated,
    int Skipped,
    int Removed,
    IReadOnlyList<string> AddedIds,
    IReadOnlyList<string> UpdatedIds,
    int LibraryTotal);
