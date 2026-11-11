// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Zeus CSV and CHIRP CSV in and out of the unified memory library. A preview
/// never writes; a commit recomputes the preview against the library as it is
/// now and applies every selected row in one transaction, or nothing. A cell
/// is only ever read as text, a number or a flag: no formula, path or URL in
/// a file is followed.
/// </summary>
public sealed class StationMemoryImportExport
{
    internal const string StatusAdd = "add";
    internal const string StatusUpdate = "update";
    internal const string StatusUnchanged = "unchanged";
    internal const string StatusDuplicate = "duplicate";
    internal const string StatusError = "error";

    internal const string ActionImport = "import";
    internal const string ActionUpdate = "update";
    internal const string ActionAddAsNew = "addAsNew";
    internal const string ActionSkip = "skip";
    internal const string ActionNone = "none";

    internal const string MatchedById = "id";
    internal const string MatchedByLink = "externalLink";
    internal const string MatchedByIdentity = "identity";
    internal const string MatchedByRow = "row";

    private const string UnusableIdWarning = "The id in this row cannot be used; a new id is assigned.";

    private const string HashPrefix = "zeus-memory-import-v1\n";
    private const int BackupTokenLength = 64;
    private static readonly TimeSpan BackupLifetime = TimeSpan.FromMinutes(10);

    private readonly StationMemoryStore _store;
    private readonly StationMemoryRecallService? _recall;
    private readonly TimeProvider _clock;
    private readonly object _backupSync = new();
    private Backup? _backup;

    public StationMemoryImportExport(
        StationMemoryStore store,
        StationMemoryRecallService? recall = null,
        TimeProvider? clock = null)
    {
        _store = store;
        _recall = recall;
        _clock = clock ?? TimeProvider.System;
    }

    public StationMemoryImportPreviewResponse Preview(StationMemoryImportPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (format, options, content) = Accept(request.Format, request.Options, request.Content);
        var analysis = Analyze(format, options, content, _recall?.CapabilityProbe());

        var rows = analysis.Rows;
        return new StationMemoryImportPreviewResponse(
            ComputePreviewHash(format, options, content),
            format,
            options,
            new StationMemoryImportSummary(
                Rows: rows.Count,
                Additions: rows.Count(row => row.Status == StatusAdd),
                Updates: rows.Count(row => row.Status == StatusUpdate),
                Unchanged: rows.Count(row => row.Status == StatusUnchanged),
                Duplicates: rows.Count(row => row.Status == StatusDuplicate),
                Errors: rows.Count(row => row.Status == StatusError),
                Warnings: analysis.FileWarnings.Count + rows.Sum(row => row.Warnings.Count),
                Removals: analysis.DefaultRemovals.Count,
                RemovedFavorites: analysis.DefaultRemovals.Count(memory => memory.Legacy?.FavoriteSlot is not null),
                RemovedBandStack: analysis.DefaultRemovals.Count(memory => memory.Legacy?.BandStackId is not null)),
            rows.Select(row => row.ToContract()).ToArray(),
            analysis.FileWarnings,
            BackupRequired: options.Mode == StationMemoryImportModes.ReplaceLibrary);
    }

    public StationMemoryImportCommitResponse Commit(StationMemoryImportCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (format, options, content) = Accept(request.Format, request.Options, request.Content);
        if (!string.Equals(ComputePreviewHash(format, options, content), request.PreviewHash, StringComparison.Ordinal))
            throw Mismatch("The file or its options changed since the preview. Preview the file again.");

        // What the station can recall is shown in a preview and decides nothing.
        var analysis = Analyze(format, options, content, probe: null);
        var actions = ResolveActions(analysis.Rows, request.Decisions);
        bool replaceLibrary = options.Mode == StationMemoryImportModes.ReplaceLibrary;
        if (replaceLibrary && !BackupCovers(request, analysis.Version))
            throw new StationMemoryConflictException(
                StationMemoryErrorCodes.BackupRequired,
                "Replacing the library needs a confirmed backup of the library as it is now.");

        var result = _store.ApplyImport(BuildPlan(analysis, actions, options));
        if (replaceLibrary)
        {
            lock (_backupSync) _backup = null;
        }
        return result;
    }

    /// <summary>The library, or one group of it, as Zeus CSV. With a backup
    /// token the full export is also remembered as the backup a
    /// replace-library commit must name.</summary>
    public StationMemoryExport Export(string? group, bool groupSpecified, string? backupToken = null)
    {
        if (backupToken is not null)
        {
            if (!IsBackupToken(backupToken))
                throw Invalid("backupToken", "backupToken must be 64 lowercase hexadecimal characters");
            if (groupSpecified)
                throw Invalid("backupToken", "backupToken cannot be combined with group");
        }

        var (library, version) = Snapshot();
        IReadOnlyList<StationMemoryDto> memories = library;
        if (groupSpecified)
        {
            var name = StationMemoryValidator.NormalizeGroup(group);
            memories = library.Where(memory => string.Equals(memory.Group, name, StringComparison.Ordinal)).ToArray();
            if (memories.Count == 0)
                throw new StationMemoryNotFoundException("group", name ?? string.Empty);
        }

        var now = _clock.GetUtcNow();
        var csv = Encoding.UTF8.GetBytes(ZeusCsvFormat.Write(memories));
        if (backupToken is not null)
        {
            lock (_backupSync) _backup = new Backup(backupToken, version, now + BackupLifetime);
        }
        return new StationMemoryExport(
            csv,
            string.Create(CultureInfo.InvariantCulture, $"zeus-memories-{now.UtcDateTime:yyyyMMdd-HHmmss}.csv"),
            memories.Count);
    }

    /// <summary>Lowercase hexadecimal SHA-256 over the import options and the
    /// content exactly as sent. The library state is not part of it.</summary>
    public static string ComputePreviewHash(string format, StationMemoryImportOptions options, string content)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(content);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(
            HashPrefix
            + format + "\n"
            + options.Mode + "\n"
            + (options.RestoreRevisions ? "1" : "0") + "\n"
            + (options.TargetGroup ?? string.Empty).Trim() + "\n"));
        hash.AppendData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // ---- request ----

    private static (string Format, StationMemoryImportOptions Options, string Content) Accept(
        string? format,
        StationMemoryImportOptions? requested,
        string? content)
    {
        var options = requested ?? new StationMemoryImportOptions();
        var errors = new List<StationMemoryFieldError>();
        if (format is not (StationMemoryImportFormats.ZeusCsv or StationMemoryImportFormats.ChirpCsv))
            errors.Add(new("format", "format must be zeus-csv or chirp-csv"));
        if (content is null)
            errors.Add(new("content", "content is required"));
        if (options.Mode is not (StationMemoryImportModes.Merge
            or StationMemoryImportModes.MergeIntoGroup
            or StationMemoryImportModes.ReplaceGroup
            or StationMemoryImportModes.ReplaceLibrary))
            errors.Add(new("options.mode", "options.mode must be merge, mergeIntoGroup, replaceGroup or replaceLibrary"));

        var targetGroup = StationMemoryValidator.NormalizeGroup(options.TargetGroup);
        if (targetGroup is null
            && options.Mode is StationMemoryImportModes.MergeIntoGroup or StationMemoryImportModes.ReplaceGroup)
            errors.Add(new("options.targetGroup", "options.targetGroup is required for mergeIntoGroup and replaceGroup"));
        if (targetGroup is not null
            && (targetGroup.Length > StationMemorySchema.MaxGroupLength || StationMemoryValidator.HasControl(targetGroup)))
            errors.Add(new("options.targetGroup", "group must be at most 80 characters with no control characters"));
        if (options.RestoreRevisions
            && (format != StationMemoryImportFormats.ZeusCsv || options.Mode != StationMemoryImportModes.ReplaceLibrary))
            errors.Add(new("options.restoreRevisions", "options.restoreRevisions needs zeus-csv and replaceLibrary"));
        if (errors.Count > 0)
            throw new StationMemoryValidationException(errors);

        return (format!, options with { TargetGroup = targetGroup }, content!);
    }

    private static StationMemoryValidationException Invalid(string field, string message) =>
        new([new StationMemoryFieldError(field, message)]);

    private static StationMemoryConflictException Mismatch(string message, IReadOnlyList<int>? rows = null) =>
        new(
            StationMemoryErrorCodes.PreviewMismatch,
            message,
            rows is null ? null : new StationMemoryErrorDetails(Rows: rows));

    // ---- backup handshake ----

    private static bool IsBackupToken(string token) =>
        token.Length == BackupTokenLength && token.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private bool BackupCovers(StationMemoryImportCommitRequest request, long libraryVersion)
    {
        lock (_backupSync)
        {
            return request.ConfirmReplaceLibrary
                && _backup is { } backup
                && string.Equals(backup.Token, request.BackupToken, StringComparison.Ordinal)
                && _clock.GetUtcNow() < backup.ExpiresUtc
                && backup.LibraryVersion == libraryVersion;
        }
    }

    // A write between the two version reads would leave rows and version
    // describing different libraries, so the pair is read again.
    private (IReadOnlyList<StationMemoryDto> Library, long Version) Snapshot()
    {
        while (true)
        {
            long version = _store.LibraryVersion;
            var library = _store.GetAll();
            if (_store.LibraryVersion == version) return (library, version);
        }
    }

    // ---- analysis (shared by preview and commit) ----

    private Analysis Analyze(
        string format,
        StationMemoryImportOptions options,
        string content,
        Func<StationMemoryDto, StationMemoryCapabilityReport>? probe)
    {
        var table = Rfc4180.Read(content);
        var fileWarnings = new List<string>();
        bool replaceLibrary = options.Mode == StationMemoryImportModes.ReplaceLibrary;
        bool replaceGroup = options.Mode == StationMemoryImportModes.ReplaceGroup;
        bool intoGroup = replaceGroup || options.Mode == StationMemoryImportModes.MergeIntoGroup;
        var rows = format == StationMemoryImportFormats.ZeusCsv
            ? ZeusCsvFormat.Read(table, fileWarnings, options.RestoreRevisions)
            : ChirpCsvFormat.Read(table, fileWarnings);

        var (library, version) = Snapshot();
        IReadOnlyList<StationMemoryDto> replaced = replaceLibrary
            ? library
            : replaceGroup
                ? library.Where(memory => string.Equals(memory.Group, options.TargetGroup, StringComparison.Ordinal)).ToArray()
                : [];
        if (replaceGroup && replaced.Count == 0)
            throw new StationMemoryNotFoundException("group", options.TargetGroup ?? string.Empty);

        var ledger = new Ledger(library, replaced);
        foreach (var row in rows)
        {
            if (row.Memory is not { } memory) continue;

            if (intoGroup)
                memory = memory with { Group = options.TargetGroup };
            if (!replaceLibrary && memory.Legacy is not null)
            {
                memory = memory with { Legacy = null };
                row.Warnings.Add("Legacy shortcut mappings are restored only when the library is replaced.");
            }
            memory = Check(row, ledger.WithUsableId(row, memory, replaceLibrary));

            // In a library replace nothing of the library is left to match.
            var match = replaceLibrary ? null : ledger.Match(row, memory);
            row.Existing = match;
            if (row.Errors.Count == 0)
                ledger.CheckMapping(row, memory, match);
            if (row.Errors.Count == 0)
            {
                memory = ledger.DropHeldAssignments(row, memory, match);
                // A library replace restores each row that carries its own id as
                // the memory it was: two memories may share a name, a frequency
                // and a mode, and identity is always the id.
                ledger.NoteIdentity(row, memory, matched: match is not null || (replaceLibrary && memory.Id.Length > 0));
            }

            row.Memory = memory;
            // Only a row that can be stored is held against the station: the
            // values of a refused row are outside what a recall is judged on.
            if (row.Errors.Count == 0)
                row.UnsupportedFields = probe?.Invoke(memory).UnsupportedFields ?? [];
            (row.Status, row.DefaultDecision) = row switch
            {
                { Errors.Count: > 0 } => (StatusError, ActionNone),
                { InPlace: true } => (StatusUpdate, ActionUpdate),
                { MatchedBy: MatchedById or MatchedByLink } => SameContent(memory, match!)
                    ? (StatusUnchanged, ActionSkip)
                    : (StatusUpdate, ActionSkip),
                { MatchedBy: MatchedByIdentity or MatchedByRow } => (StatusDuplicate, ActionSkip),
                _ => (StatusAdd, ActionImport),
            };
        }

        var kept = rows
            .Where(row => row.InPlace && row.DefaultDecision == ActionUpdate)
            .Select(row => row.Existing!.Id)
            .ToHashSet(StringComparer.Ordinal);
        return new Analysis(
            rows,
            fileWarnings,
            library,
            version,
            replaced,
            replaced.Where(memory => !kept.Contains(memory.Id)).ToArray());
    }

    /// <summary>What the rows of one file are held against: the memories
    /// that stay in the library, and what earlier rows of the file took.</summary>
    private sealed class Ledger
    {
        private readonly Dictionary<string, StationMemoryDto> _byId;
        private readonly HashSet<string> _replaced;
        // Memories a replace removes hold nothing a row could collide with,
        // so the four indexes below leave them out.
        private readonly Dictionary<string, StationMemoryDto> _linkHolders = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _channelHolders = [];
        private readonly Dictionary<int, string> _slotHolders = [];
        private readonly Dictionary<Identity, StationMemoryDto> _identities = [];

        private readonly Dictionary<string, int> _seenIds = new(StringComparer.Ordinal);
        private readonly Dictionary<Identity, int> _seenIdentities = [];
        private readonly Dictionary<int, int> _seenFavoriteSlots = [];
        private readonly Dictionary<int, int> _seenBandStackIds = [];
        private readonly HashSet<int> _claimedChannels = [];
        private readonly HashSet<int> _claimedSlots = [];
        private readonly HashSet<string> _claimedLinks = new(StringComparer.Ordinal);

        public Ledger(IReadOnlyList<StationMemoryDto> library, IReadOnlyList<StationMemoryDto> replaced)
        {
            _byId = library.ToDictionary(memory => memory.Id, StringComparer.Ordinal);
            _replaced = replaced.Select(memory => memory.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var memory in library)
            {
                if (_replaced.Contains(memory.Id)) continue;
                foreach (var link in memory.ExternalLinks)
                    _linkHolders[StationMemoryValidator.LinkKey(link.RadioSerial, link.MemoryIndex)] = memory;
                if (memory.CatChannel is { } channel) _channelHolders[channel] = memory.Id;
                if (memory.ShortcutSlot is { } slot) _slotHolders[slot] = memory.Id;
                // Of several memories with one identity the lowest id is the match.
                var identity = Identity.Of(memory);
                if (!_identities.TryGetValue(identity, out var held) || string.CompareOrdinal(memory.Id, held.Id) < 0)
                    _identities[identity] = memory;
            }
        }

        /// <summary>The row with the id it may keep, or with none (one is
        /// minted at the commit) and a warning. A repeated id is an error.</summary>
        public StationMemoryDto WithUsableId(MemoryImportRow row, StationMemoryDto memory, bool replaceLibrary)
        {
            string id = memory.Id;
            bool usable = id.Length == 0 || StationMemoryValidator.IsValidId(id);
            // A legacy id is the one an adapter mints for a favorite slot or a
            // band-stack entry. A row may hold it only with that very mapping,
            // or as the memory it already is in this library.
            if (usable && IsLegacyId(id))
                usable = replaceLibrary ? NamesOwnMapping(id, memory.Legacy) : _byId.ContainsKey(id);
            if (!usable)
            {
                row.Warnings.Add(UnusableIdWarning);
                return memory with { Id = string.Empty };
            }

            if (id.Length > 0 && !_seenIds.TryAdd(id, row.Number))
                row.Errors.Add($"The id in this row is already used by row {Number(_seenIds[id])}.");
            return memory;
        }

        /// <summary>The library memory the row is: by id, then by a radio
        /// link, then by identity. First match wins.</summary>
        public StationMemoryDto? Match(MemoryImportRow row, StationMemoryDto memory)
        {
            if (memory.Id.Length > 0 && _byId.TryGetValue(memory.Id, out var sameId))
            {
                row.MatchedBy = MatchedById;
                row.InPlace = _replaced.Contains(sameId.Id);
                return sameId;
            }
            foreach (var link in memory.ExternalLinks)
            {
                if (_linkHolders.TryGetValue(StationMemoryValidator.LinkKey(link.RadioSerial, link.MemoryIndex), out var linked))
                {
                    row.MatchedBy = MatchedByLink;
                    return linked;
                }
            }
            if (_identities.TryGetValue(Identity.Of(memory), out var alike))
            {
                row.MatchedBy = MatchedByIdentity;
                return alike;
            }
            return null;
        }

        /// <summary>The row a decision would produce keeps the legacy mapping
        /// of the memory it lands on, so it must stay presentable through
        /// the favorite and band-stack routes; and no two rows of a file may
        /// claim one favorite slot or band-stack id.</summary>
        public void CheckMapping(MemoryImportRow row, StationMemoryDto memory, StationMemoryDto? match)
        {
            var legacy = match is null ? memory.Legacy : match.Legacy;
            foreach (var error in StationMemoryValidator.Validate(
                StationMemoryValidator.ToWrite(memory),
                favoriteMapped: legacy?.FavoriteSlot is not null,
                bandStackMapped: legacy?.BandStackId is not null))
                row.Errors.Add(error.Message);
            if (memory.Legacy?.FavoriteSlot is { } slot && !_seenFavoriteSlots.TryAdd(slot, row.Number))
                row.Errors.Add($"Favorite slot {Number(slot)} is already used by row {Number(_seenFavoriteSlots[slot])}.");
            if (memory.Legacy?.BandStackId is { } bandStackId && !_seenBandStackIds.TryAdd(bandStackId, row.Number))
                row.Errors.Add($"Band stack id {Number(bandStackId)} is already used by row {Number(_seenBandStackIds[bandStackId])}.");
        }

        /// <summary>A CAT channel, shortcut slot or radio link that another
        /// memory or an earlier row already holds leaves the row, with a
        /// warning each.</summary>
        public StationMemoryDto DropHeldAssignments(MemoryImportRow row, StationMemoryDto memory, StationMemoryDto? match)
        {
            if (memory.CatChannel is { } channel && !Claim(channel, _channelHolders, _claimedChannels, match))
            {
                memory = memory with { CatChannel = null };
                row.Warnings.Add($"CAT channel {Number(channel)} is already assigned; the assignment is not imported.");
            }
            if (memory.ShortcutSlot is { } slot && !Claim(slot, _slotHolders, _claimedSlots, match))
            {
                memory = memory with { ShortcutSlot = null };
                row.Warnings.Add($"Shortcut slot {Number(slot)} is already assigned; the assignment is not imported.");
            }

            var links = new List<StationMemoryExternalLink>(memory.ExternalLinks.Count);
            foreach (var link in memory.ExternalLinks)
            {
                string key = StationMemoryValidator.LinkKey(link.RadioSerial, link.MemoryIndex);
                bool heldElsewhere = _linkHolders.TryGetValue(key, out var holder)
                    && !string.Equals(holder.Id, match?.Id, StringComparison.Ordinal);
                if (heldElsewhere || !_claimedLinks.Add(key))
                    row.Warnings.Add(
                        $"Radio memory {Number(link.MemoryIndex)} of {link.RadioSerial.Trim()} is already linked; the link is not imported.");
                else
                    links.Add(link);
            }
            return links.Count == memory.ExternalLinks.Count ? memory : memory with { ExternalLinks = links };
        }

        /// <summary>Remembers the identity of a row without errors. A row
        /// that is no library memory and repeats such an earlier row of its
        /// own file is matched to that row.</summary>
        public void NoteIdentity(MemoryImportRow row, StationMemoryDto memory, bool matched)
        {
            var identity = Identity.Of(memory);
            if (_seenIdentities.TryAdd(identity, row.Number) || matched) return;
            row.MatchedBy = MatchedByRow;
            row.MatchedRow = _seenIdentities[identity];
        }

        // An assignment is free when no memory that stays holds it, other
        // than the one this row lands on, and no earlier row took it.
        private static bool Claim(int value, Dictionary<int, string> holders, HashSet<int> claimed, StationMemoryDto? match) =>
            !(holders.TryGetValue(value, out var holder) && !string.Equals(holder, match?.Id, StringComparison.Ordinal))
            && claimed.Add(value);

        private static bool IsLegacyId(string id) =>
            id.StartsWith(StationMemorySchema.LegacyFavoriteIdPrefix, StringComparison.Ordinal)
            || id.StartsWith(StationMemorySchema.LegacyBandStackIdPrefix, StringComparison.Ordinal);

        private static bool NamesOwnMapping(string id, StationMemoryLegacy? legacy) =>
            (legacy?.FavoriteSlot is { } slot
                && string.Equals(id, StationMemoryMigration.FavoriteMemoryId(slot), StringComparison.Ordinal))
            || (legacy?.BandStackId is { } bandStackId
                && string.Equals(id, StationMemoryMigration.BandStackMemoryId(bandStackId), StringComparison.Ordinal));
    }

    // The storage rules of a row: text normalised, every violated rule one
    // error, and fm brought to its canonical form with a warning per value
    // that moved.
    private static StationMemoryDto Check(MemoryImportRow row, StationMemoryDto memory)
    {
        var write = StationMemoryValidator.Normalize(StationMemoryValidator.ToWrite(memory));
        var errors = StationMemoryValidator.Validate(
            write,
            favoriteMapped: memory.Legacy?.FavoriteSlot is not null,
            bandStackMapped: memory.Legacy?.BandStackId is not null);
        if (errors.Count == 0)
        {
            var canonical = StationMemoryValidator.Canonicalize(write);
            if (write.Fm is { } before && canonical.Fm is { } after)
            {
                foreach (var (field, was, now) in FmChanges(before, after))
                    row.Warnings.Add($"{field} adjusted from {was} to {now}.");
            }
            write = canonical;
        }
        row.Errors.AddRange(errors
            .Where(error => !(row.ModeRefused && error.Field == "mode"))
            .Select(error => error.Message));

        memory = Apply(memory, write);
        row.Errors.AddRange(StationMemoryValidator
            .ValidateRow(memory with
            {
                // Placeholders stand for what the commit mints.
                Id = memory.Id.Length == 0 ? "pending" : memory.Id,
                Revision = row.Revision ?? 1,
                CreatedUtcMs = row.CreatedUtcMs ?? 0,
                UpdatedUtcMs = row.UpdatedUtcMs ?? 0,
                LastRecalledUtcMs = row.LastRecalledUtcMs,
            })
            .Select(error => error.Message));
        return memory;
    }

    private static IEnumerable<(string Field, string Was, string Now)> FmChanges(FmMemory before, FmMemory after)
    {
        if (before.RepeaterShift != after.RepeaterShift)
            yield return ("fm.repeaterShift", before.RepeaterShift.ToString(), after.RepeaterShift.ToString());
        if (before.RepeaterReverse != after.RepeaterReverse)
            yield return ("fm.repeaterReverse", Text(before.RepeaterReverse), Text(after.RepeaterReverse));
        if (before.RepeaterOffsetHz != after.RepeaterOffsetHz)
            yield return ("fm.repeaterOffsetHz", Number(before.RepeaterOffsetHz), Number(after.RepeaterOffsetHz));
        if (before.DeviationHz != after.DeviationHz)
            yield return ("fm.deviationHz", Number(before.DeviationHz), Number(after.DeviationHz));
        if (before.CtcssEnabled != after.CtcssEnabled)
            yield return ("fm.ctcssEnabled", Text(before.CtcssEnabled), Text(after.CtcssEnabled));
        if (!before.CtcssToneHz.Equals(after.CtcssToneHz))
            yield return ("fm.ctcssToneHz", Tone(before.CtcssToneHz), Tone(after.CtcssToneHz));
        if (!Nullable.Equals(before.RxCtcssToneHz, after.RxCtcssToneHz))
            yield return ("fm.rxCtcssToneHz", Tone(before.RxCtcssToneHz), Tone(after.RxCtcssToneHz));
        if (before.DcsTxEnabled != after.DcsTxEnabled)
            yield return ("fm.dcsTxEnabled", Text(before.DcsTxEnabled), Text(after.DcsTxEnabled));
        if (before.DcsCode != after.DcsCode)
            yield return ("fm.dcsCode", Number(before.DcsCode), Number(after.DcsCode));
        if (before.RxDcsCode != after.RxDcsCode)
            yield return ("fm.rxDcsCode", Number(before.RxDcsCode), Number(after.RxDcsCode));
        if (before.DcsInverted != after.DcsInverted)
            yield return ("fm.dcsInverted", Text(before.DcsInverted), Text(after.DcsInverted));
        if (before.RxToneSquelch != after.RxToneSquelch)
            yield return ("fm.rxToneSquelch", before.RxToneSquelch.ToString(), after.RxToneSquelch.ToString());
    }

    // Content is every client-settable field plus the link set.
    private static bool SameContent(StationMemoryDto candidate, StationMemoryDto existing) =>
        StationMemoryValidator.ToWrite(candidate) == StationMemoryValidator.ToWrite(existing)
        && LinkSet(candidate).SequenceEqual(LinkSet(existing));

    private static IEnumerable<(string Key, string? Owner)> LinkSet(StationMemoryDto memory) =>
        memory.ExternalLinks
            .Select(link => (
                Key: StationMemoryValidator.LinkKey(link.RadioSerial, link.MemoryIndex),
                Owner: string.IsNullOrWhiteSpace(link.Owner) ? null : link.Owner.Trim()))
            .OrderBy(link => link.Key, StringComparer.Ordinal);

    private static StationMemoryDto Apply(StationMemoryDto memory, StationMemoryWrite write) => memory with
    {
        Name = write.Name,
        Group = write.Group,
        Notes = write.Notes,
        FrequencyHz = write.FrequencyHz,
        Mode = write.Mode,
        FilterLowHz = write.FilterLowHz,
        FilterHighHz = write.FilterHighHz,
        TuneStepHz = write.TuneStepHz,
        ScanSkip = write.ScanSkip,
        SourceMode = write.SourceMode,
        Split = write.Split,
        Rit = write.Rit,
        Xit = write.Xit,
        Digital = write.Digital,
        RxAntenna = write.RxAntenna,
        TxAntenna = write.TxAntenna,
        Squelch = write.Squelch,
        Fm = write.Fm,
        CatChannel = write.CatChannel,
        ShortcutSlot = write.ShortcutSlot,
    };

    // ---- commit ----

    private static Dictionary<int, string> ResolveActions(
        IReadOnlyList<MemoryImportRow> rows,
        IReadOnlyList<StationMemoryImportDecision>? decisions)
    {
        var byNumber = rows.ToDictionary(row => row.Number);
        var explicitActions = new Dictionary<int, string>();
        var mismatched = new SortedSet<int>();
        foreach (var decision in decisions ?? [])
        {
            if (decision is null
                || !byNumber.TryGetValue(decision.Row, out var row)
                || !explicitActions.TryAdd(decision.Row, decision.Action)
                || !Allowed(row).Contains(decision.Action, StringComparer.Ordinal))
                mismatched.Add(decision?.Row ?? 0);
        }
        if (mismatched.Count > 0)
            throw Mismatch("A decision does not fit its row as the library is now. Preview the file again.", mismatched.ToArray());

        var actions = new Dictionary<int, string>(rows.Count);
        var undecided = new List<int>();
        var updated = new Dictionary<string, int>(StringComparer.Ordinal);
        var twice = new SortedSet<int>();
        foreach (var row in rows)
        {
            string action = explicitActions.GetValueOrDefault(row.Number, row.DefaultDecision);
            if (action == ActionNone) undecided.Add(row.Number);
            if (action == ActionUpdate && !updated.TryAdd(row.Existing!.Id, row.Number))
            {
                twice.Add(updated[row.Existing.Id]);
                twice.Add(row.Number);
            }
            actions[row.Number] = action;
        }
        if (undecided.Count > 0)
            throw new StationMemoryImportInvalidException(
                "Rows with errors must be corrected or skipped explicitly.", undecided);
        if (twice.Count > 0)
            throw new StationMemoryImportInvalidException(
                "Two rows cannot update the same memory.", twice.ToArray());
        return actions;
    }

    private static string[] Allowed(MemoryImportRow row) => row.Status switch
    {
        StatusAdd => [ActionImport, ActionSkip],
        StatusUpdate => [ActionUpdate, ActionAddAsNew, ActionSkip],
        StatusUnchanged => [ActionSkip, ActionAddAsNew],
        StatusDuplicate when row.MatchedBy == MatchedByRow => [ActionSkip, ActionAddAsNew],
        StatusDuplicate => [ActionSkip, ActionAddAsNew, ActionUpdate],
        _ => [ActionSkip],
    };

    private StationMemoryImportPlan BuildPlan(
        Analysis analysis,
        Dictionary<int, string> actions,
        StationMemoryImportOptions options)
    {
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var inserts = new List<StationMemoryDto>();
        var updates = new List<StationMemoryDto>();
        int skipped = 0;
        // The ids of the memories that stay.
        var taken = analysis.Library.Select(memory => memory.Id).ToHashSet(StringComparer.Ordinal);
        taken.ExceptWith(analysis.Replaced.Select(memory => memory.Id));

        foreach (var row in analysis.Rows)
        {
            string action = actions[row.Number];
            // Only a row with a candidate can be given anything but a skip.
            if (action == ActionSkip || row.Memory is not { } memory)
            {
                skipped++;
                continue;
            }

            switch (action)
            {
                case ActionUpdate:
                    var existing = row.Existing!;
                    kept.Add(existing.Id);
                    updates.Add(memory with
                    {
                        Id = existing.Id,
                        Source = existing.Source,
                        CreatedUtcMs = existing.CreatedUtcMs,
                        UpdatedUtcMs = now,
                        LastRecalledUtcMs = existing.LastRecalledUtcMs,
                        Revision = existing.Revision + 1,
                        Legacy = existing.Legacy,
                    });
                    break;
                case ActionImport when options.RestoreRevisions:
                    inserts.Add(memory with
                    {
                        Id = FreeId(memory.Id, taken),
                        CreatedUtcMs = row.CreatedUtcMs ?? now,
                        UpdatedUtcMs = row.UpdatedUtcMs ?? now,
                        LastRecalledUtcMs = row.LastRecalledUtcMs,
                        Revision = row.Revision ?? 1,
                    });
                    break;
                case ActionImport:
                    inserts.Add(memory with
                    {
                        Id = FreeId(memory.Id, taken),
                        CreatedUtcMs = row.CreatedUtcMs ?? now,
                        UpdatedUtcMs = now,
                        LastRecalledUtcMs = null,
                        Revision = 1,
                    });
                    break;
                case ActionAddAsNew:
                    inserts.Add(memory with
                    {
                        Id = FreeId(string.Empty, taken),
                        CreatedUtcMs = row.CreatedUtcMs ?? now,
                        UpdatedUtcMs = now,
                        LastRecalledUtcMs = null,
                        Revision = 1,
                        CatChannel = null,
                        ShortcutSlot = null,
                        Legacy = null,
                        ExternalLinks = [],
                    });
                    break;
            }
        }

        return new StationMemoryImportPlan(
            analysis.Replaced.Where(memory => !kept.Contains(memory.Id)).Select(memory => memory.Id).ToArray(),
            inserts,
            updates,
            skipped,
            analysis.Version);
    }

    // The file's id when it has one that is still free, else a new one.
    private static string FreeId(string id, HashSet<string> taken)
    {
        if (id.Length > 0 && taken.Add(id)) return id;
        string minted;
        do minted = Guid.NewGuid().ToString("N");
        while (!taken.Add(minted));
        return minted;
    }

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Number(long? value) => value is { } number ? Number(number) : "empty";

    internal static string Text(bool value) => value ? "true" : "false";

    internal static string Tone(double? hz) =>
        hz is { } tone ? tone.ToString("0.0###", CultureInfo.InvariantCulture) : "empty";

    private sealed record Backup(string Token, long LibraryVersion, DateTimeOffset ExpiresUtc);

    /// <summary>Case-insensitive (group, name, frequency, mode); a null group
    /// equals an empty one and a null mode only another null mode.</summary>
    private readonly record struct Identity(string Group, string Name, long FrequencyHz, string? Mode)
    {
        public static Identity Of(StationMemoryDto memory) => new(
            (memory.Group ?? string.Empty).ToUpperInvariant(),
            memory.Name.ToUpperInvariant(),
            memory.FrequencyHz,
            memory.Mode?.ToUpperInvariant());
    }

    private sealed record Analysis(
        IReadOnlyList<MemoryImportRow> Rows,
        IReadOnlyList<string> FileWarnings,
        IReadOnlyList<StationMemoryDto> Library,
        long Version,
        IReadOnlyList<StationMemoryDto> Replaced,          // what a replace mode removes unless a row keeps it
        IReadOnlyList<StationMemoryDto> DefaultRemovals);  // under the default decisions
}

public sealed record StationMemoryExport(byte[] Utf8Csv, string FileName, int Count);

/// <summary>The file as a whole cannot be imported, or a commit left a row
/// with errors undecided. Answered as 400 <c>import_invalid</c>.</summary>
public sealed class StationMemoryImportInvalidException(string message, IReadOnlyList<int>? rows = null)
    : Exception(message)
{
    public IReadOnlyList<int>? Rows { get; } = rows;
}

/// <summary>One data row of an import file on its way to a preview row.</summary>
internal sealed class MemoryImportRow(int number)
{
    public int Number { get; } = number;             // 1-based; the header is row 0
    public StationMemoryDto? Memory { get; set; }    // null: no candidate could be built
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    // Server-owned cells as the file has them; the candidate carries them
    // only when revisions are restored.
    public long? CreatedUtcMs { get; set; }
    public long? UpdatedUtcMs { get; set; }
    public long? LastRecalledUtcMs { get; set; }
    public int? Revision { get; set; }
    // The format reader already said why the row has no mode.
    public bool ModeRefused { get; set; }

    public string Status { get; set; } = StationMemoryImportExport.StatusError;
    public string DefaultDecision { get; set; } = StationMemoryImportExport.ActionNone;
    public StationMemoryDto? Existing { get; set; }
    public string? MatchedBy { get; set; }
    public int? MatchedRow { get; set; }
    public bool InPlace { get; set; }                // replaces a member of the replaced group
    public IReadOnlyList<string> UnsupportedFields { get; set; } = [];

    public StationMemoryImportRow ToContract() => new(
        Number,
        Status,
        DefaultDecision,
        Memory,
        Existing?.Id,
        MatchedBy,
        MatchedRow,
        Warnings.ToArray(),
        Errors.ToArray(),
        UnsupportedFields);
}

/// <summary>RFC 4180 reading and writing, and the two file limits.</summary>
internal static class Rfc4180
{
    internal sealed record Table(IReadOnlyList<string> Header, IReadOnlyList<string[]> Rows)
    {
        private readonly Dictionary<string, int> _columns = Header
            .Select((name, index) => (name, index))
            .ToDictionary(column => column.name, column => column.index, StringComparer.Ordinal);

        public bool Has(string column) => _columns.ContainsKey(column);

        /// <summary>The cell under a column; a missing column or a missing
        /// trailing cell reads as empty.</summary>
        public string Cell(string[] row, string column) =>
            _columns.TryGetValue(column, out int index) && index < row.Length ? row[index] : string.Empty;
    }

    /// <summary>Header and data rows. File-level problems throw
    /// <see cref="StationMemoryImportInvalidException"/>.</summary>
    public static Table Read(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > StationMemorySchema.ImportMaxBytes)
            throw new StationMemoryImportInvalidException("The file is larger than 2 MiB.");
        var records = Records(content);
        if (records.Count == 0)
            throw new StationMemoryImportInvalidException("The file has no header row.");
        if (records.Count - 1 > StationMemorySchema.ImportMaxRows)
            throw new StationMemoryImportInvalidException("The file has more than 10000 rows.");

        var header = records[0].Select(name => name.Trim()).ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in header)
        {
            if (!names.Add(name))
                throw new StationMemoryImportInvalidException($"Column '{name}' appears more than once.");
        }
        records.RemoveAt(0);
        return new Table(header, records);
    }

    public static string Write(IEnumerable<IEnumerable<string>> records)
    {
        var text = new StringBuilder();
        foreach (var record in records)
        {
            bool first = true;
            foreach (var cell in record)
            {
                if (!first) text.Append(',');
                first = false;
                if (NeedsQuotes(cell))
                    text.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                else
                    text.Append(cell);
            }
            text.Append('\n');
        }
        return text.ToString();
    }

    private static bool NeedsQuotes(string cell) =>
        cell.Length > 0
        && (cell[0] == ' ' || cell[^1] == ' ' || cell.AsSpan().IndexOfAny(",\"\r\n") >= 0);

    // Records end at CRLF or LF outside quotes. A quoted cell may hold
    // commas, CR, LF and doubled quotes. Completely empty lines are skipped.
    private static List<string[]> Records(string content)
    {
        var records = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        bool anything = false;   // the current line holds a character
        int index = content.Length > 0 && content[0] == '\uFEFF' ? 1 : 0;

        void EndRecord()
        {
            if (anything)
            {
                cells.Add(cell.ToString());
                records.Add(cells.ToArray());
            }
            cells.Clear();
            cell.Clear();
            anything = false;
        }

        for (; index < content.Length; index++)
        {
            char c = content[index];
            if (quoted)
            {
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (index + 1 < content.Length && content[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else
                {
                    quoted = false;
                }
                continue;
            }

            switch (c)
            {
                case '"' when cell.Length == 0:
                    quoted = true;
                    anything = true;
                    break;
                case ',':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    anything = true;
                    break;
                case '\r' when index + 1 < content.Length && content[index + 1] == '\n':
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    cell.Append(c);
                    anything = true;
                    break;
            }
        }
        if (quoted)
            throw new StationMemoryImportInvalidException("A quoted cell is not closed.");
        EndRecord();
        return records;
    }
}

/// <summary>The Zeus CSV backup format: one column per leaf of a memory.</summary>
internal static class ZeusCsvFormat
{
    private sealed record Column(string Name, bool Text, Func<StationMemoryDto, string?> Read, bool Optional = false);

    private static readonly Column[] Columns =
    [
        Whole("schemaVersion", memory => memory.SchemaVersion),
        Text("id", memory => memory.Id, optional: true),
        Text("name", memory => memory.Name),
        Text("group", memory => memory.Group),
        Text("notes", memory => memory.Notes),
        Text("source", memory => memory.Source),
        Whole("frequencyHz", memory => memory.FrequencyHz),
        Text("mode", memory => memory.Mode),
        Whole("filterLowHz", memory => memory.FilterLowHz),
        Whole("filterHighHz", memory => memory.FilterHighHz),
        Whole("tuneStepHz", memory => memory.TuneStepHz),
        Flag("scanSkip", memory => memory.ScanSkip),
        Flag("split.enabled", memory => memory.Split?.Enabled),
        Whole("split.txFrequencyHz", memory => memory.Split?.TxFrequencyHz),
        Text("split.txMode", memory => memory.Split?.TxMode),
        Flag("rit.enabled", memory => memory.Rit?.Enabled),
        Whole("rit.offsetHz", memory => memory.Rit?.OffsetHz),
        Flag("xit.enabled", memory => memory.Xit?.Enabled),
        Whole("xit.offsetHz", memory => memory.Xit?.OffsetHz),
        Text("rxAntenna", memory => memory.RxAntenna),
        Text("txAntenna", memory => memory.TxAntenna),
        Flag("squelch.enabled", memory => memory.Squelch?.Enabled),
        Whole("squelch.level", memory => memory.Squelch?.Level),
        Whole("digital.rttyMarkHz", memory => memory.Digital?.RttyMarkHz),
        Whole("digital.rttyShiftHz", memory => memory.Digital?.RttyShiftHz),
        Whole("digital.diglOffsetHz", memory => memory.Digital?.DiglOffsetHz),
        Whole("digital.diguOffsetHz", memory => memory.Digital?.DiguOffsetHz),
        Text("fm.repeaterShift", memory => memory.Fm?.RepeaterShift.ToString()),
        Flag("fm.repeaterReverse", memory => memory.Fm?.RepeaterReverse),
        Whole("fm.repeaterOffsetHz", memory => memory.Fm?.RepeaterOffsetHz),
        Whole("fm.deviationHz", memory => memory.Fm?.DeviationHz),
        Flag("fm.ctcssEnabled", memory => memory.Fm?.CtcssEnabled),
        Tone("fm.ctcssToneHz", memory => memory.Fm?.CtcssToneHz),
        Tone("fm.rxCtcssToneHz", memory => memory.Fm?.RxCtcssToneHz),
        Flag("fm.dcsTxEnabled", memory => memory.Fm?.DcsTxEnabled),
        Whole("fm.dcsCode", memory => memory.Fm?.DcsCode),
        Whole("fm.rxDcsCode", memory => memory.Fm?.RxDcsCode),
        Flag("fm.dcsInverted", memory => memory.Fm?.DcsInverted),
        Text("fm.rxToneSquelch", memory => memory.Fm?.RxToneSquelch.ToString()),
        Whole("catChannel", memory => memory.CatChannel),
        Whole("shortcutSlot", memory => memory.ShortcutSlot),
        Whole("legacy.favoriteSlot", memory => memory.Legacy?.FavoriteSlot, optional: true),
        Whole("legacy.bandStackId", memory => memory.Legacy?.BandStackId, optional: true),
        Text("legacy.band", memory => memory.Legacy?.Band, optional: true),
        new("externalLinksJson", false, Links, Optional: true),
        Whole("createdUtcMs", memory => memory.CreatedUtcMs, optional: true),
        Whole("updatedUtcMs", memory => memory.UpdatedUtcMs, optional: true),
        Whole("lastRecalledUtcMs", memory => memory.LastRecalledUtcMs, optional: true),
        Whole("revision", memory => memory.Revision, optional: true),
        Text("sourceMode", memory => memory.SourceMode),
    ];

    private static readonly HashSet<string> ColumnNames =
        Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlyList<string> Header { get; } = Columns.Select(column => column.Name).ToArray();

    /// <summary>Every column a file must have. The others are the ones a
    /// hand-authored file may leave out.</summary>
    internal static IReadOnlyList<string> RequiredHeader { get; } =
        Columns.Where(column => !column.Optional).Select(column => column.Name).ToArray();

    /// <summary>Header and one row per memory, LF line ends, a final LF, no
    /// byte order mark.</summary>
    public static string Write(IEnumerable<StationMemoryDto> memories) =>
        Rfc4180.Write(memories
            .Select(memory => Columns.Select(column =>
            {
                string cell = column.Read(memory) ?? string.Empty;
                return column.Text ? Protect(cell) : cell;
            }))
            .Prepend(Header));

    public static List<MemoryImportRow> Read(Rfc4180.Table table, List<string> fileWarnings, bool restoreRevisions)
    {
        foreach (var name in RequiredHeader)
        {
            if (!table.Has(name))
                throw new StationMemoryImportInvalidException($"Column '{name}' is missing.");
        }
        foreach (var name in table.Header)
        {
            if (!ColumnNames.Contains(name))
                fileWarnings.Add($"Column '{name}' is not part of the Zeus format and is ignored.");
        }

        var rows = new List<MemoryImportRow>(table.Rows.Count);
        for (int index = 0; index < table.Rows.Count; index++)
        {
            var row = new MemoryImportRow(index + 1);
            rows.Add(row);
            var cells = table.Rows[index];
            if (cells.Length > table.Header.Count)
            {
                row.Errors.Add("The row has more cells than the header.");
                continue;
            }
            ReadRow(new Cells(table, cells, row), row, restoreRevisions);
        }
        return rows;
    }

    private static void ReadRow(Cells cells, MemoryImportRow row, bool restoreRevisions)
    {
        string name = cells.Text("name") ?? string.Empty;
        long? frequencyHz = cells.Int64("frequencyHz");
        if (name.Length == 0) row.Errors.Add("The row has no name.");
        if (frequencyHz is null && !cells.Has("frequencyHz")) row.Errors.Add("The row has no frequency.");

        string schemaVersion = cells.Typed("schemaVersion");
        if (schemaVersion.Length > 0 && schemaVersion != "1")
            row.Errors.Add("schemaVersion must be 1.");
        string? source = cells.Word("source") is { } named && StationMemorySources.All.Contains(named)
            ? named
            : null;

        StationMemorySplit? split = null;
        if (cells.Any("split.enabled", "split.txFrequencyHz", "split.txMode"))
            split = new StationMemorySplit(
                cells.RequiredFlag("split.enabled"),
                cells.Int64("split.txFrequencyHz"),
                cells.Word("split.txMode"));

        StationMemoryDigital? digital = null;
        if (cells.Any("digital.rttyMarkHz", "digital.rttyShiftHz", "digital.diglOffsetHz", "digital.diguOffsetHz"))
            digital = new StationMemoryDigital(
                cells.Int32("digital.rttyMarkHz"),
                cells.Int32("digital.rttyShiftHz"),
                cells.Int32("digital.diglOffsetHz"),
                cells.Int32("digital.diguOffsetHz"));

        StationMemorySquelch? squelch = null;
        if (cells.Any("squelch.enabled", "squelch.level"))
            squelch = new StationMemorySquelch(cells.RequiredFlag("squelch.enabled"), cells.RequiredInt32("squelch.level"));

        FmMemory? fm = null;
        if (cells.Any(
            "fm.repeaterShift", "fm.repeaterReverse", "fm.repeaterOffsetHz", "fm.deviationHz", "fm.ctcssEnabled",
            "fm.ctcssToneHz", "fm.rxCtcssToneHz", "fm.dcsTxEnabled", "fm.dcsCode", "fm.rxDcsCode", "fm.dcsInverted",
            "fm.rxToneSquelch"))
            fm = new FmMemory(
                cells.Named<FmRepeaterShift>("fm.repeaterShift") ?? FmRepeaterShift.Simplex,
                cells.Flag("fm.repeaterReverse") ?? false,
                cells.Int64("fm.repeaterOffsetHz") ?? 0,
                cells.Int32("fm.deviationHz") ?? FmConfig.DefaultDeviationHz,
                cells.Flag("fm.ctcssEnabled") ?? false,
                cells.Decimal("fm.ctcssToneHz") ?? FmConfig.Default.CtcssToneHz,
                cells.Decimal("fm.rxCtcssToneHz"),
                cells.Flag("fm.dcsTxEnabled") ?? false,
                cells.Int32("fm.dcsCode") ?? FmConfig.Default.DcsCode,
                cells.Int32("fm.rxDcsCode"),
                cells.Flag("fm.dcsInverted") ?? false,
                cells.Named<FmRxToneSquelch>("fm.rxToneSquelch") ?? FmRxToneSquelch.Off);

        StationMemoryLegacy? legacy = null;
        if (cells.Any("legacy.favoriteSlot", "legacy.bandStackId") || cells.Text("legacy.band") is not null)
            legacy = new StationMemoryLegacy(
                cells.Int32("legacy.favoriteSlot"),
                cells.Int32("legacy.bandStackId"),
                cells.Text("legacy.band"));

        row.CreatedUtcMs = cells.Stamp("createdUtcMs", restoreRevisions);
        row.UpdatedUtcMs = cells.Stamp("updatedUtcMs", restoreRevisions);
        row.LastRecalledUtcMs = cells.Stamp("lastRecalledUtcMs", restoreRevisions);
        row.Revision = (int?)cells.Stamp("revision", restoreRevisions, int.MinValue, int.MaxValue);

        var memory = new StationMemoryDto(
            StationMemorySchema.Version,
            cells.Word("id") ?? string.Empty,
            name,
            cells.Text("group"),
            cells.Text("notes"),
            source ?? StationMemorySources.ZeusCsv,
            restoreRevisions ? row.CreatedUtcMs ?? 0 : 0,
            restoreRevisions ? row.UpdatedUtcMs ?? 0 : 0,
            restoreRevisions ? row.LastRecalledUtcMs : null,
            restoreRevisions ? row.Revision ?? 0 : 0,
            frequencyHz ?? 0,
            cells.Word("mode"),
            cells.Int32("filterLowHz"),
            cells.Int32("filterHighHz"),
            cells.Int32("tuneStepHz"),
            cells.Flag("scanSkip") ?? false,
            cells.Text("sourceMode"),
            split,
            Offset(cells, "rit"),
            Offset(cells, "xit"),
            digital,
            cells.Text("rxAntenna"),
            cells.Text("txAntenna"),
            squelch,
            fm,
            cells.Int32("catChannel"),
            cells.Int32("shortcutSlot"),
            legacy,
            ReadLinks(cells.Typed("externalLinksJson"), row));

        // Without a name or a frequency there is nothing to show as a candidate.
        if (name.Length > 0 && frequencyHz is not null)
            row.Memory = memory;
    }

    private static StationMemoryOffset? Offset(Cells cells, string name) =>
        cells.Any(name + ".enabled", name + ".offsetHz")
            ? new StationMemoryOffset(cells.RequiredFlag(name + ".enabled"), cells.RequiredInt64(name + ".offsetHz"))
            : null;

    private static string? Links(StationMemoryDto memory) =>
        memory.ExternalLinks.Count == 0
            ? null
            : JsonSerializer.Serialize(memory.ExternalLinks, StationMemoryJson.Options);

    private static IReadOnlyList<StationMemoryExternalLink> ReadLinks(string json, MemoryImportRow row)
    {
        if (json.Length == 0) return [];
        try
        {
            if (JsonSerializer.Deserialize<List<StationMemoryExternalLink>>(json, StationMemoryJson.Options) is { } links
                && links.All(link => link is not null))
                return links;
        }
        catch (JsonException)
        {
        }
        row.Errors.Add("externalLinksJson is not valid.");
        return [];
    }

    // A text cell that a spreadsheet would read as a formula gets one
    // apostrophe in front; a leading apostrophe gets one too, so reading is
    // the exact inverse.
    private static string Protect(string text) =>
        text.Length > 0 && IsFormulaLead(text[0]) ? "'" + text : text;

    private static string Unprotect(string text) =>
        text.Length > 1 && text[0] == '\'' && IsFormulaLead(text[1]) ? text[1..] : text;

    private static bool IsFormulaLead(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r' or '\'';

    private static Column Text(string name, Func<StationMemoryDto, string?> read, bool optional = false) =>
        new(name, true, read, optional);

    private static Column Whole(string name, Func<StationMemoryDto, long?> read, bool optional = false) =>
        new(name, false, memory => read(memory)?.ToString(CultureInfo.InvariantCulture), optional);

    private static Column Flag(string name, Func<StationMemoryDto, bool?> read) =>
        new(name, false, memory => read(memory) is { } flag ? StationMemoryImportExport.Text(flag) : null);

    private static Column Tone(string name, Func<StationMemoryDto, double?> read) =>
        new(name, false, memory => read(memory) is { } hz ? StationMemoryImportExport.Tone(hz) : null);

    /// <summary>The cells of one row by column name. A cell that does not
    /// parse is one row error and reads as empty.</summary>
    private sealed class Cells(Rfc4180.Table table, string[] cells, MemoryImportRow row)
    {
        /// <summary>A text cell as written; empty is null.</summary>
        public string? Text(string column) =>
            table.Cell(cells, column) is { Length: > 0 } text ? Unprotect(text) : null;

        /// <summary>A text cell that holds one word (an id, a mode).</summary>
        public string? Word(string column) =>
            Text(column)?.Trim() is { Length: > 0 } word ? word : null;

        public string Typed(string column) => table.Cell(cells, column).Trim();

        public bool Has(string column) => Typed(column).Length > 0;

        public bool Any(params string[] columns) => columns.Any(Has);

        public long? Int64(string column)
        {
            string text = Typed(column);
            if (text.Length == 0) return null;
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                return value;
            row.Errors.Add($"{column} must be a whole number.");
            return null;
        }

        /// <summary>A cell the library owns (a time, the revision). Its exact
        /// value matters only to a restore of revisions. Elsewhere a cell
        /// that no longer parses is ignored with a warning: a spreadsheet
        /// rewrites a 13-digit time as 1.76254E+12 on save.</summary>
        public long? Stamp(string column, bool exact, long min = long.MinValue, long max = long.MaxValue)
        {
            string text = Typed(column);
            if (text.Length == 0) return null;
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
                && value >= min && value <= max)
                return value;
            if (exact)
                row.Errors.Add($"{column} must be a whole number.");
            else
                row.Warnings.Add($"{column} is not a whole number and is ignored.");
            return null;
        }

        public int? Int32(string column)
        {
            string text = Typed(column);
            if (text.Length == 0) return null;
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
                return value;
            row.Errors.Add($"{column} must be a whole number.");
            return null;
        }

        public double? Decimal(string column)
        {
            string text = Typed(column);
            if (text.Length == 0) return null;
            // The parser also reads "NaN" and "Infinity", which no JSON
            // answer can carry.
            if (double.TryParse(
                text,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double value)
                && double.IsFinite(value))
                return value;
            row.Errors.Add($"{column} must be a number.");
            return null;
        }

        public bool? Flag(string column)
        {
            string text = Typed(column);
            if (text.Length == 0) return null;
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            row.Errors.Add($"{column} must be true or false.");
            return null;
        }

        public TEnum? Named<TEnum>(string column) where TEnum : struct, Enum
        {
            string text = Typed(column);
            if (text.Length == 0) return null;
            if (System.Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal))
                return System.Enum.Parse<TEnum>(text);
            row.Errors.Add($"{column} is not valid.");
            return null;
        }

        public bool RequiredFlag(string column) => Flag(column) ?? Missing(column, false);

        public int RequiredInt32(string column) => Int32(column) ?? Missing(column, 0);

        public long RequiredInt64(string column) => Int64(column) ?? Missing(column, 0L);

        // An unparsable cell already has its own error.
        private T Missing<T>(string column, T fallback)
        {
            if (!Has(column))
                row.Errors.Add($"{column} is required when its object has a value.");
            return fallback;
        }
    }
}

/// <summary>CHIRP's generic CSV, read only.</summary>
internal static class ChirpCsvFormat
{
    private static readonly string[] Recognised =
    [
        "Location", "Name", "Frequency", "Duplex", "Offset", "Tone", "rToneFreq", "cToneFreq", "DtcsCode",
        "RxDtcsCode", "DtcsPolarity", "CrossMode", "Mode", "TStep", "Comment",
    ];

    private const string FmOnlyWarning = "Repeater shift and tones apply to FM only and are not imported for this row.";
    private const string ReceiveOnlyWarning =
        "CHIRP marks this channel receive only; a Zeus memory cannot inhibit transmit.";
    private const string StepTooLargeWarning =
        "TStep is larger than the 5 kHz Zeus tunes in; the tuning step is not imported.";

    public static List<MemoryImportRow> Read(Rfc4180.Table table, List<string> fileWarnings)
    {
        foreach (var name in (string[])["Name", "Frequency"])
        {
            if (!table.Has(name))
                throw new StationMemoryImportInvalidException($"Column '{name}' is missing.");
        }
        foreach (var name in table.Header)
        {
            if (!Recognised.Contains(name, StringComparer.Ordinal))
                fileWarnings.Add($"Column '{name}' is not imported.");
        }

        var rows = new List<MemoryImportRow>(table.Rows.Count);
        for (int index = 0; index < table.Rows.Count; index++)
        {
            var row = new MemoryImportRow(index + 1);
            rows.Add(row);
            var cells = table.Rows[index];
            if (cells.Length > table.Header.Count)
            {
                row.Errors.Add("The row has more cells than the header.");
                continue;
            }
            ReadRow(column => table.Cell(cells, column).Trim(), row);
        }
        return rows;
    }

    private static void ReadRow(Func<string, string> cell, MemoryImportRow row)
    {
        string frequency = cell("Frequency");
        if (Megahertz(frequency) is not { } frequencyHz || frequencyHz <= 0)
        {
            row.Errors.Add("Frequency must be a positive number of MHz.");
            return;
        }

        string name = cell("Name");
        if (name.Length == 0)
        {
            name = frequency + " MHz";
            row.Warnings.Add("The row has no name; the frequency is used.");
        }
        else if (name.Length > StationMemorySchema.MaxNameLength)
        {
            name = Shorten(name, StationMemorySchema.MaxNameLength);
            row.Warnings.Add("Name shortened to 80 characters.");
        }

        string? notes = cell("Comment") is { Length: > 0 } comment ? comment : null;
        if (notes is { Length: > StationMemorySchema.MaxNotesLength })
        {
            notes = Shorten(notes, StationMemorySchema.MaxNotesLength);
            row.Warnings.Add("Comment shortened to 500 characters.");
        }

        int deviationHz = FmConfig.DefaultDeviationHz;
        string? mode = null;
        switch (cell("Mode"))
        {
            case "FM": mode = nameof(RxMode.FM); break;
            case "NFM": mode = nameof(RxMode.FM); deviationHz = 2_500; break;
            case "AM": mode = nameof(RxMode.AM); break;
            case "USB": mode = nameof(RxMode.USB); break;
            case "LSB": mode = nameof(RxMode.LSB); break;
            case "CW": mode = nameof(RxMode.CWU); break;
            case "DIG": mode = nameof(RxMode.DIGU); break;
            case "": row.Errors.Add("The row has no mode."); break;
            case var other: row.Errors.Add($"Mode '{other}' cannot be imported."); break;
        }
        row.ModeRefused = mode is null;
        bool isFm = mode == nameof(RxMode.FM);

        int? tuneStepHz = null;
        if (cell("TStep") is { Length: > 0 } step)
        {
            if (Scaled(step, 1_000m) is not { } stepHz || stepHz < int.MinValue || stepHz > int.MaxValue)
                row.Errors.Add("TStep must be a number of kHz.");
            else if (stepHz > ToolbarSettingsStore.MaxStepHz)
                // Stored, such a step would be refused on every recall.
                row.Warnings.Add(StepTooLargeWarning);
            else
                tuneStepHz = (int)stepHz;
        }

        var shift = FmRepeaterShift.Simplex;
        long repeaterOffsetHz = 0;
        StationMemorySplit? split = null;
        string duplex = cell("Duplex");
        bool fmOnly = false;
        if (duplex is "+" or "-")
        {
            if (!isFm)
            {
                fmOnly = true;
            }
            else
            {
                shift = duplex == "+" ? FmRepeaterShift.Plus : FmRepeaterShift.Minus;
                if (cell("Offset") is { Length: > 0 } offset)
                {
                    if (Megahertz(offset) is { } offsetHz)
                        repeaterOffsetHz = offsetHz;
                    else
                        row.Errors.Add("Offset must be a number of MHz.");
                }
            }
        }
        else if (duplex.Equals("split", StringComparison.OrdinalIgnoreCase))
        {
            if (Megahertz(cell("Offset")) is { } txHz and > 0)
                split = new StationMemorySplit(true, txHz);
            else
                row.Errors.Add("Offset must be the positive transmit frequency in MHz when Duplex is split.");
        }
        else if (duplex.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            row.Warnings.Add(ReceiveOnlyWarning);
        }
        else if (duplex.Length > 0 && !duplex.Equals("simplex", StringComparison.OrdinalIgnoreCase))
        {
            row.Errors.Add($"Duplex '{duplex}' cannot be imported.");
        }

        string tone = cell("Tone");
        FmMemory? fm = null;
        if (isFm)
        {
            fm = Tones(cell, row, tone) with
            {
                RepeaterShift = shift,
                RepeaterOffsetHz = repeaterOffsetHz,
                DeviationHz = deviationHz,
            };
        }
        else if (tone.Length > 0)
        {
            fmOnly = true;
        }
        if (fmOnly) row.Warnings.Add(FmOnlyWarning);

        row.Memory = new StationMemoryDto(
            StationMemorySchema.Version,
            Id: string.Empty,
            name,
            Group: null,
            notes,
            StationMemorySources.Chirp,
            CreatedUtcMs: 0,
            UpdatedUtcMs: 0,
            LastRecalledUtcMs: null,
            Revision: 0,
            frequencyHz,
            mode,
            FilterLowHz: null,
            FilterHighHz: null,
            tuneStepHz,
            ScanSkip: false,
            SourceMode: null,
            split,
            Rit: null,
            Xit: null,
            Digital: null,
            RxAntenna: null,
            TxAntenna: null,
            Squelch: null,
            fm,
            CatChannel: null,
            ShortcutSlot: null,
            Legacy: null,
            ExternalLinks: []);
    }

    // What the row transmits and what opens its squelch. Everything the tone
    // mode does not name is off.
    //
    // CHIRP writes all four tone columns on every row and leaves the ones the
    // tone mode does not use at their defaults (88.5 and 023). TSQL uses
    // cToneFreq in both directions and DTCS uses DtcsCode in both; rToneFreq
    // is the transmit tone of Tone and of the cross modes, and RxDtcsCode is
    // read only in a cross mode.
    private static FmMemory Tones(Func<string, string> cell, MemoryImportRow row, string tone)
    {
        bool txTone = false, txDcs = false, rxTone = false, rxDcs = false;
        switch (tone)
        {
            case "": break;
            case "Tone": txTone = true; break;
            case "TSQL": txTone = rxTone = true; break;
            case "DTCS": txDcs = rxDcs = true; break;
            case "Cross":
                switch (cell("CrossMode"))
                {
                    case "Tone->Tone": txTone = rxTone = true; break;
                    case "Tone->DTCS": txTone = rxDcs = true; break;
                    case "DTCS->Tone": txDcs = rxTone = true; break;
                    case "DTCS->DTCS": txDcs = rxDcs = true; break;
                    case "->Tone": rxTone = true; break;
                    case "->DTCS": rxDcs = true; break;
                    case "DTCS->": txDcs = true; break;
                    case "Tone->": txTone = true; break;
                    case "->": break;
                    case var other: row.Errors.Add($"CrossMode '{other}' cannot be imported."); break;
                }
                break;
            default:
                row.Errors.Add($"Tone '{tone}' cannot be imported.");
                break;
        }

        double ctcssToneHz = FmConfig.Default.CtcssToneHz;
        double? rxCtcssToneHz = null;
        string rxToneColumn = cell("cToneFreq").Length > 0 ? "cToneFreq" : "rToneFreq";
        string txToneColumn = tone == "TSQL" ? rxToneColumn : "rToneFreq";
        if (txTone) ctcssToneHz = Hertz(cell, row, txToneColumn) ?? ctcssToneHz;
        // One cell for both directions was read, and judged, as the transmit tone.
        if (rxTone && !(txTone && rxToneColumn == txToneColumn))
        {
            double? heard = Hertz(cell, row, rxToneColumn);
            if (heard is { } hz && !hz.Equals(ctcssToneHz)) rxCtcssToneHz = hz;
        }

        int dcsCode = FmConfig.Default.DcsCode;
        int? rxDcsCode = null;
        bool inverted = false;
        if (txDcs) dcsCode = Code(cell, row, "DtcsCode") ?? dcsCode;
        string rxDcsColumn = tone == "Cross" && cell("RxDtcsCode").Length > 0 ? "RxDtcsCode" : "DtcsCode";
        if (rxDcs && !(txDcs && rxDcsColumn == "DtcsCode"))
        {
            int? heard = Code(cell, row, rxDcsColumn);
            if (heard is { } code && code != dcsCode) rxDcsCode = code;
        }
        if (txDcs || rxDcs)
        {
            switch (cell("DtcsPolarity"))
            {
                case "" or "NN": break;
                case "RR": inverted = true; break;
                case "NR" or "RN": row.Errors.Add("Split DCS polarity cannot be stored."); break;
                case var other: row.Errors.Add($"DtcsPolarity '{other}' cannot be imported."); break;
            }
        }

        return new FmMemory(
            FmRepeaterShift.Simplex,
            RepeaterReverse: false,
            RepeaterOffsetHz: 0,
            FmConfig.DefaultDeviationHz,
            CtcssEnabled: txTone,
            ctcssToneHz,
            rxCtcssToneHz,
            DcsTxEnabled: txDcs,
            dcsCode,
            rxDcsCode,
            inverted,
            rxTone ? FmRxToneSquelch.Ctcss : rxDcs ? FmRxToneSquelch.Dcs : FmRxToneSquelch.Off);
    }

    private static double? Hertz(Func<string, string> cell, MemoryImportRow row, string column)
    {
        if (Number(cell(column)) is { } hz) return (double)hz;
        row.Errors.Add($"{column} must be a tone frequency in Hz.");
        return null;
    }

    private static int? Code(Func<string, string> cell, MemoryImportRow row, string column)
    {
        if (int.TryParse(cell(column), NumberStyles.None, CultureInfo.InvariantCulture, out int code)) return code;
        row.Errors.Add($"{column} must be a DCS code.");
        return null;
    }

    private static long? Megahertz(string text) => Scaled(text, 1_000_000m);

    // Rounded away from zero to a whole number; a value too large for the
    // model stays too large instead of wrapping.
    private static long? Scaled(string text, decimal factor)
    {
        if (Number(text) is not { } number) return null;
        if (Math.Abs(number) > long.MaxValue / factor) return number > 0 ? long.MaxValue : long.MinValue;
        return (long)Math.Round(number * factor, MidpointRounding.AwayFromZero);
    }

    // Invariant culture; a cell with a comma and no period uses the comma
    // as its decimal separator.
    private static decimal? Number(string text)
    {
        if (text.Contains(',') && !text.Contains('.'))
            text = text.Replace(',', '.');
        return decimal.TryParse(
            text,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out decimal number)
            ? number
            : null;
    }

    // Never ends on half of a surrogate pair.
    private static string Shorten(string text, int length) =>
        text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];
}
