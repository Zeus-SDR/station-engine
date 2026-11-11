// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Collections.Concurrent;
using System.Globalization;
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// The unified memory library in the engine preferences database: one row
/// per memory in <c>station_memories</c> and one row per external radio link
/// in <c>station_memory_links</c>. The favorite and band-stack stores are
/// adapters over this store, so every surface reads and writes the same rows.
/// </summary>
public sealed class StationMemoryStore : IDisposable
{
    internal const string CollectionName = "station_memories";
    internal const string LinksCollectionName = "station_memory_links";
    internal const string MigrationCollectionName = "station_memory_migration";
    internal const string MigrationMarkerId = "station-memory-v1";

    // One row beside the migration marker, written when a favorite or a
    // band-stack memory is removed. It keeps the highest band-stack id that
    // was ever removed and the last revision of each cleared favorite slot,
    // so a new entry never takes the id of a deleted one and a slot saved
    // again never returns at a revision a client may still hold.
    internal const string RetiredLegacyMarkerId = MigrationMarkerId + "-retired";
    private const string RetiredBandStackIdField = "band_stack_id";
    private const string RetiredFavoriteRevisionsField = "favorite_revisions";

    private readonly StationMemoryGate.Entry _gate;
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<StationMemoryDocument> _memories;
    private readonly ILiteCollection<StationMemoryLinkDocument> _links;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;

    public StationMemoryStore(ILogger<StationMemoryStore> log, string? dbPathOverride = null)
        : this(log, dbPathOverride, clock: null)
    {
    }

    // Adapters and tests: any logger, optional clock. Several instances on one
    // path are legal and consistent: there is no instance cache, and every
    // read goes to LiteDB under the gate shared by that database file.
    internal StationMemoryStore(ILogger log, string? dbPathOverride, TimeProvider? clock)
    {
        _log = log;
        _clock = clock ?? TimeProvider.System;
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        DatabasePath = dbPath;
        _gate = StationMemoryGate.For(dbPath);
        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        try
        {
            _db = _dbLease.Database;
            lock (_gate.Sync)
            {
                _memories = _db.GetCollection<StationMemoryDocument>(CollectionName);
                _memories.EnsureIndex(memory => memory.Group);
                _memories.EnsureIndex(memory => memory.Name);
                _memories.EnsureIndex(memory => memory.FrequencyHz);
                _memories.EnsureIndex(memory => memory.UpdatedUtcMs);
                _memories.EnsureIndex(memory => memory.LastRecalledUtcMs);
                _memories.EnsureIndex(memory => memory.CatChannel);
                _memories.EnsureIndex(memory => memory.ShortcutSlot);
                _memories.EnsureIndex(memory => memory.FavoriteSlot);
                _memories.EnsureIndex(memory => memory.BandStackId);
                _links = _db.GetCollection<StationMemoryLinkDocument>(LinksCollectionName);
                _links.EnsureIndex(link => link.Key, unique: true);
                _links.EnsureIndex(link => link.MemoryId);

                if (StationMemoryMigration.Run(_db, _log))
                    _gate.Version++;
            }
        }
        catch
        {
            _dbLease.Dispose();
            throw;
        }

        _log.LogInformation("StationMemoryStore initialized at {Path}", dbPath);
    }

    internal string DatabasePath { get; }

    /// <summary>Incremented by every committed write on this database path
    /// (in memory, per process).</summary>
    public long LibraryVersion
    {
        get { lock (_gate.Sync) return _gate.Version; }
    }

    /// <summary>Called after each removed, updated or inserted row of
    /// <see cref="ApplyImport"/> with the count of rows written so far.</summary>
    internal Action<int>? ImportWriteHookForTests { get; set; }

    // ---- reads ----

    public StationMemoryPage List(StationMemoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new List<StationMemoryFieldError>();
        if (query.Offset < 0)
            errors.Add(new("offset", "offset must not be negative"));
        if (query.Limit is < 1 or > StationMemorySchema.ListMaxLimit)
            errors.Add(new("limit", "limit must be from 1 to 2000"));
        if (query.Source is not null && !StationMemorySources.All.Contains(query.Source))
            errors.Add(new("source", "source is not valid"));
        if (errors.Count > 0)
            throw new StationMemoryValidationException(errors);

        var group = StationMemoryValidator.NormalizeGroup(query.Group);
        var text = string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim();

        lock (_gate.Sync)
        {
            var all = _memories.FindAll().ToList();
            var matches = Ordered(all.Where(memory =>
                    (!query.GroupSpecified || string.Equals(memory.Group, group, StringComparison.Ordinal))
                    && (query.Source is null || string.Equals(memory.Source, query.Source, StringComparison.Ordinal))
                    && (query.ShortcutSlot is null || memory.ShortcutSlot == query.ShortcutSlot)
                    && (query.CatChannel is null || memory.CatChannel == query.CatChannel)
                    && (text is null || Matches(memory, text))))
                .ToList();
            var page = matches.Skip(query.Offset).Take(query.Limit).ToList();
            return new StationMemoryPage(ToDtos(page), matches.Count, all.Count, GroupCounts(all));
        }
    }

    /// <summary>Every memory, by name (ordinal, ignoring case), then id.</summary>
    public IReadOnlyList<StationMemoryDto> GetAll()
    {
        lock (_gate.Sync)
            return ToDtos(Ordered(_memories.FindAll()).ToList());
    }

    public StationMemoryDto? Get(string id)
    {
        lock (_gate.Sync)
            return Find(id) is { } memory ? ToDto(memory) : null;
    }

    public StationMemoryDto? GetByCatChannel(int channel) =>
        GetByColumn(nameof(StationMemoryDocument.CatChannel), channel);

    public StationMemoryDto? GetByShortcutSlot(int slot) =>
        GetByColumn(nameof(StationMemoryDocument.ShortcutSlot), slot);

    /// <summary>Ungrouped first, then by group name.</summary>
    public IReadOnlyList<StationMemoryGroupCount> Groups()
    {
        lock (_gate.Sync)
            return GroupCounts(_memories.FindAll());
    }

    public int Count()
    {
        lock (_gate.Sync)
            return _memories.Count();
    }

    // ---- writes ----

    public StationMemoryDto Create(StationMemoryWrite write, string source = StationMemorySources.User)
    {
        var clean = Prepare(write, favoriteMapped: false, bandStackMapped: false);
        if (!StationMemorySources.All.Contains(source))
            throw new StationMemoryValidationException([new("source", "source is not valid")]);

        lock (_gate.Sync)
        {
            EnsureAssignmentsFree(clean.CatChannel, clean.ShortcutSlot, exceptId: null);
            var memory = NewDocument(Guid.NewGuid().ToString("N"), source, NowMs());
            Apply(memory, clean);
            return Write(() =>
            {
                _memories.Insert(memory);
                return ToDto(memory);
            });
        }
    }

    public StationMemoryDto Update(string id, int expectedRevision, StationMemoryWrite write)
    {
        lock (_gate.Sync)
        {
            var memory = Require(id, expectedRevision);
            var clean = Prepare(
                write,
                favoriteMapped: memory.FavoriteSlot is not null,
                bandStackMapped: memory.BandStackId is not null);
            EnsureAssignmentsFree(clean.CatChannel, clean.ShortcutSlot, memory.Id);
            Apply(memory, clean);
            Touch(memory);
            return Write(() =>
            {
                _memories.Update(memory);
                return ToDto(memory);
            });
        }
    }

    public void Delete(string id, int expectedRevision)
    {
        lock (_gate.Sync)
        {
            var memory = Require(id, expectedRevision);
            Write(() => Remove(memory.Id));
        }
    }

    /// <summary>Sets lastRecalledUtcMs only. Revision and updatedUtcMs do not
    /// change: a recall is not an edit.</summary>
    public StationMemoryDto MarkRecalled(string id, long utcMs)
    {
        lock (_gate.Sync)
        {
            var memory = Require(id);
            memory.LastRecalledUtcMs = utcMs;
            return Write(() =>
            {
                _memories.Update(memory);
                return ToDto(memory);
            });
        }
    }

    public StationMemoryGroupOperationResponse RenameGroup(string? fromGroup, string? toGroup)
    {
        var from = StationMemoryValidator.NormalizeGroup(fromGroup);
        var to = TargetGroup(toGroup);

        lock (_gate.Sync)
        {
            var members = _memories.FindAll()
                .Where(memory => string.Equals(memory.Group, from, StringComparison.Ordinal))
                .ToList();
            if (members.Count == 0)
                throw new StationMemoryNotFoundException("group", from ?? string.Empty);
            if (string.Equals(from, to, StringComparison.Ordinal))
                return new StationMemoryGroupOperationResponse("renameGroup", 0, [], [], GroupCounts(_memories.FindAll()));

            return Write(() => Regroup("renameGroup", members, to));
        }
    }

    public StationMemoryGroupOperationResponse MoveToGroup(
        IReadOnlyList<StationMemoryRevisionRef> memories,
        string? toGroup)
    {
        var to = TargetGroup(toGroup);

        lock (_gate.Sync)
        {
            var members = RequireAll(memories);
            return Write(() => Regroup("moveToGroup", members, to));
        }
    }

    public StationMemoryGroupOperationResponse DeleteMany(IReadOnlyList<StationMemoryRevisionRef> memories)
    {
        lock (_gate.Sync)
        {
            var members = RequireAll(memories);
            return Write(() =>
            {
                foreach (var memory in members)
                    Remove(memory.Id);
                return new StationMemoryGroupOperationResponse(
                    "delete",
                    members.Count,
                    [],
                    members.Select(memory => memory.Id).ToArray(),
                    GroupCounts(_memories.FindAll()));
            });
        }
    }

    public StationMemoryDto SetExternalLink(string id, int expectedRevision, StationMemoryExternalLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!StationMemoryValidator.IsValidLink(link))
            throw new StationMemoryValidationException([new("externalLinks", "externalLinks is not valid")]);

        lock (_gate.Sync)
        {
            var memory = Require(id, expectedRevision);
            var key = StationMemoryValidator.LinkKey(link.RadioSerial, link.MemoryIndex);
            var existing = _links.FindOne(candidate => candidate.Key == key);
            if (existing is not null && !string.Equals(existing.MemoryId, memory.Id, StringComparison.Ordinal))
                throw LinkTaken(link, existing.MemoryId);

            Touch(memory);
            return Write(() =>
            {
                if (existing is null)
                {
                    _links.Insert(NewLink(memory.Id, link));
                }
                else
                {
                    existing.Owner = Owner(link);
                    _links.Update(existing);
                }
                _memories.Update(memory);
                return ToDto(memory);
            });
        }
    }

    public StationMemoryDto RemoveExternalLink(string id, int expectedRevision, string radioSerial, int memoryIndex)
    {
        lock (_gate.Sync)
        {
            var memory = Require(id, expectedRevision);
            var key = StationMemoryValidator.LinkKey(radioSerial, memoryIndex);
            var existing = _links.FindOne(candidate => candidate.Key == key);
            if (existing is null || !string.Equals(existing.MemoryId, memory.Id, StringComparison.Ordinal))
                throw new StationMemoryNotFoundException("radio entry", key);

            Touch(memory);
            return Write(() =>
            {
                _links.Delete(existing.Id);
                _memories.Update(memory);
                return ToDto(memory);
            });
        }
    }

    /// <summary>One transaction: every removal, update and insert of the
    /// plan, or nothing. A plan made from an older library is refused.</summary>
    public StationMemoryImportCommitResponse ApplyImport(StationMemoryImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        lock (_gate.Sync)
        {
            if (plan.ExpectedLibraryVersion is { } expected && expected != _gate.Version)
                throw new StationMemoryConflictException(
                    StationMemoryErrorCodes.PreviewMismatch,
                    "The library changed while the import was prepared. Preview the file again.");

            return Write(() =>
            {
                var written = 0;
                var removed = 0;
                foreach (var id in plan.RemoveIds)
                {
                    if (Remove(id)) removed++;
                    ImportWriteHookForTests?.Invoke(++written);
                }

                // An update may hand a radio link to another row of the same
                // plan, so every updated row gives up its links before the
                // first one is written back.
                foreach (var row in plan.Updates)
                    RemoveLinks(row.Id);

                foreach (var row in plan.Updates)
                {
                    if (Find(row.Id) is null)
                        throw new StationMemoryNotFoundException("memory", row.Id ?? string.Empty);
                    _memories.Update(ImportDocument(row));
                    InsertLinks(row);
                    ImportWriteHookForTests?.Invoke(++written);
                }

                foreach (var row in plan.Inserts)
                {
                    if (Find(row.Id) is not null)
                        throw Duplicate("id", $"A memory with the id '{row.Id}' already exists.", row.Id);
                    _memories.Insert(ImportDocument(row));
                    InsertLinks(row);
                    ImportWriteHookForTests?.Invoke(++written);
                }

                foreach (var row in plan.Updates.Concat(plan.Inserts))
                    EnsureImportedAssignmentsFree(row);
                return new StationMemoryImportCommitResponse(
                    plan.Inserts.Count,
                    plan.Updates.Count,
                    plan.Skipped,
                    removed,
                    plan.Inserts.Select(row => row.Id).ToArray(),
                    plan.Updates.Select(row => row.Id).ToArray(),
                    _memories.Count());
            });
        }
    }

    // ---- compatibility adapters ----

    internal StationMemoryDto? GetByFavoriteSlot(int slot) =>
        GetByColumn(nameof(StationMemoryDocument.FavoriteSlot), slot);

    /// <summary>Band-stack memories in the legacy order: band, creation
    /// time, band-stack id.</summary>
    internal IReadOnlyList<StationMemoryDto> GetBandStack(string? band = null)
    {
        lock (_gate.Sync)
        {
            return ToDtos(_memories.FindAll()
                .Where(memory => memory.BandStackId is not null
                    && (band is null || string.Equals(memory.LegacyBand, band, StringComparison.Ordinal)))
                .OrderBy(memory => memory.LegacyBand, StringComparer.Ordinal)
                .ThenBy(memory => memory.CreatedUtcMs)
                .ThenBy(memory => memory.BandStackId)
                .ToList());
        }
    }

    internal StationMemoryDto UpsertFavorite(
        int slot,
        long frequencyHz,
        RxMode mode,
        int filterLowHz,
        int filterHighHz,
        FmMemory? fm)
    {
        // The favorite route never refused an fm value, so it is brought to
        // its canonical form before the rules run instead of being judged.
        var canonicalFm = fm is null ? null : FmMemories.Normalize(fm, frequencyHz);

        lock (_gate.Sync)
        {
            var memory = FindByColumn(nameof(StationMemoryDocument.FavoriteSlot), slot);
            var write = memory is null
                ? new StationMemoryWrite(
                    StationMemoryMigration.FavoriteName(slot),
                    frequencyHz,
                    Group: StationMemorySchema.FavoritesGroup)
                : StationMemoryValidator.ToWrite(ToDto(memory));
            write = write with
            {
                FrequencyHz = frequencyHz,
                Mode = mode.ToString(),
                FilterLowHz = filterLowHz,
                FilterHighHz = filterHighHz,
                Fm = canonicalFm,
            };
            StationMemoryValidator.ThrowIfInvalid(write, favoriteMapped: true);

            if (memory is not null)
            {
                Apply(memory, write);
                Touch(memory);
                return Write(() =>
                {
                    _memories.Update(memory);
                    return ToDto(memory);
                });
            }

            var id = StationMemoryMigration.FavoriteMemoryId(slot);
            if (Find(id) is not null)
                throw Duplicate("id", $"A memory with the id '{id}' already exists.", id);
            memory = NewDocument(id, StationMemorySources.Favorite, NowMs());
            memory.FavoriteSlot = slot;
            // Above the revision the slot had when it was cleared: the id is
            // the same, and a client that still holds the old row must not
            // pass a revision check against this one.
            memory.Revision = RetiredFavoriteRevision(slot) + 1;
            Apply(memory, write);
            return Write(() =>
            {
                _memories.Insert(memory);
                return ToDto(memory);
            });
        }
    }

    internal bool DeleteFavorite(int slot)
    {
        lock (_gate.Sync)
        {
            var memory = FindByColumn(nameof(StationMemoryDocument.FavoriteSlot), slot);
            return memory is not null && Write(() => Remove(memory.Id));
        }
    }

    /// <summary>Sanitises instead of refusing what the band-stack route always
    /// accepted: control characters leave the label, a lone or inverted
    /// filter pair is stored as no filter, the band is kept verbatim.</summary>
    internal StationMemoryDto AddBandStack(
        string band,
        string label,
        long hz,
        RxMode mode,
        int? filterLowHz,
        int? filterHighHz)
    {
        var hasFilter = filterLowHz is not null && StationMemoryValidator.IsFilterPair(filterLowHz, filterHighHz);
        var write = new StationMemoryWrite(
            StationMemoryMigration.BandStackName(label, band),
            hz,
            Group: StationMemoryMigration.BandStackGroup(band),
            Mode: mode.ToString(),
            FilterLowHz: hasFilter ? filterLowHz : null,
            FilterHighHz: hasFilter ? filterHighHz : null);
        StationMemoryValidator.ThrowIfInvalid(write, bandStackMapped: true);

        lock (_gate.Sync)
        {
            // Band-stack ids are minted here, one above the highest in use or
            // ever removed, and never land on an id a memory already holds.
            var bandStackId = Math.Max(
                _memories.FindAll()
                    .Select(memory => memory.BandStackId ?? 0)
                    .DefaultIfEmpty(0)
                    .Max(),
                RetiredBandStackId()) + 1;
            while (Find(StationMemoryMigration.BandStackMemoryId(bandStackId)) is not null)
                bandStackId++;

            var memory = NewDocument(
                StationMemoryMigration.BandStackMemoryId(bandStackId),
                StationMemorySources.BandStack,
                NowMs());
            memory.BandStackId = bandStackId;
            memory.LegacyBand = band;
            Apply(memory, write);
            return Write(() =>
            {
                _memories.Insert(memory);
                return ToDto(memory);
            });
        }
    }

    internal bool DeleteBandStack(string band, int bandStackId)
    {
        lock (_gate.Sync)
        {
            var memory = FindByColumn(nameof(StationMemoryDocument.BandStackId), bandStackId);
            if (memory is null || !string.Equals(memory.LegacyBand, band, StringComparison.Ordinal))
                return false;
            return Write(() => Remove(memory.Id));
        }
    }

    public void Dispose() => _dbLease.Dispose();

    // ---- internals (every caller holds the gate) ----

    private T Write<T>(Func<T> work)
    {
        if (!_db.BeginTrans())
            throw new InvalidOperationException("Could not begin the memory transaction.");
        T result;
        try
        {
            result = work();
            _db.Commit();
        }
        catch
        {
            _db.Rollback();
            throw;
        }
        _gate.Version++;
        return result;
    }

    private static StationMemoryWrite Prepare(StationMemoryWrite write, bool favoriteMapped, bool bandStackMapped)
    {
        var clean = StationMemoryValidator.Normalize(write);
        StationMemoryValidator.ThrowIfInvalid(clean, favoriteMapped, bandStackMapped);
        return StationMemoryValidator.Canonicalize(clean);
    }

    private static string? TargetGroup(string? toGroup)
    {
        var to = StationMemoryValidator.NormalizeGroup(toGroup);
        if (to is not null && (to.Length > StationMemorySchema.MaxGroupLength || StationMemoryValidator.HasControl(to)))
            throw new StationMemoryValidationException(
                [new("toGroup", "group must be at most 80 characters with no control characters")]);
        return to;
    }

    private long NowMs() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private void Touch(StationMemoryDocument memory)
    {
        memory.Revision++;
        memory.UpdatedUtcMs = NowMs();
    }

    private StationMemoryDocument? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : _memories.FindById(id);

    private StationMemoryDocument? FindByColumn(string column, int value) =>
        _memories.FindOne(Query.EQ(column, value));

    private StationMemoryDto? GetByColumn(string column, int value)
    {
        lock (_gate.Sync)
            return FindByColumn(column, value) is { } memory ? ToDto(memory) : null;
    }

    private StationMemoryDocument Require(string id) =>
        Find(id) ?? throw new StationMemoryNotFoundException("memory", id ?? string.Empty);

    private StationMemoryDocument Require(string id, int expectedRevision)
    {
        var memory = Require(id);
        if (memory.Revision != expectedRevision)
            throw new StationMemoryConflictException(
                StationMemoryErrorCodes.RevisionConflict,
                "The memory was changed since it was read.",
                new StationMemoryErrorDetails(CurrentRevision: memory.Revision));
        return memory;
    }

    // Every id is loaded and revision-checked before the first write, so a
    // missing or stale row leaves the whole selection untouched.
    private List<StationMemoryDocument> RequireAll(IReadOnlyList<StationMemoryRevisionRef> memories)
    {
        if (memories is null || memories.Any(reference => reference is null)
            || memories.Select(reference => reference.Id).Distinct(StringComparer.Ordinal).Count() != memories.Count)
            throw new StationMemoryValidationException(
                [new("memories", "memories must name each memory once")]);
        return memories.Select(reference => Require(reference.Id, reference.ExpectedRevision)).ToList();
    }

    private StationMemoryGroupOperationResponse Regroup(
        string operation,
        List<StationMemoryDocument> members,
        string? to)
    {
        foreach (var memory in members)
        {
            memory.Group = to;
            Touch(memory);
            _memories.Update(memory);
        }
        return new StationMemoryGroupOperationResponse(
            operation,
            members.Count,
            ToDtos(Ordered(members).ToList()),
            [],
            GroupCounts(_memories.FindAll()));
    }

    private bool Remove(string id)
    {
        RemoveLinks(id);
        if (Find(id) is { } memory) Retire(memory);
        return _memories.Delete(id);
    }

    // In the transaction of the removal, so the two commit together.
    private void Retire(StationMemoryDocument memory)
    {
        if (memory.BandStackId is null && memory.FavoriteSlot is null) return;
        var markers = _db.GetCollection<BsonDocument>(MigrationCollectionName);
        var retired = markers.FindById(RetiredLegacyMarkerId) ?? new BsonDocument { ["_id"] = RetiredLegacyMarkerId };
        if (memory.BandStackId is { } bandStackId)
            retired[RetiredBandStackIdField] = Math.Max(bandStackId, RetiredInt(retired[RetiredBandStackIdField]));
        if (memory.FavoriteSlot is { } slot)
        {
            var revisions = retired[RetiredFavoriteRevisionsField].IsDocument
                ? retired[RetiredFavoriteRevisionsField].AsDocument
                : new BsonDocument();
            string key = slot.ToString(CultureInfo.InvariantCulture);
            revisions[key] = Math.Max(memory.Revision, RetiredInt(revisions[key]));
            retired[RetiredFavoriteRevisionsField] = revisions;
        }
        markers.Upsert(retired);
    }

    private int RetiredBandStackId() =>
        _db.GetCollection<BsonDocument>(MigrationCollectionName).FindById(RetiredLegacyMarkerId) is { } retired
            ? RetiredInt(retired[RetiredBandStackIdField])
            : 0;

    private int RetiredFavoriteRevision(int slot) =>
        _db.GetCollection<BsonDocument>(MigrationCollectionName).FindById(RetiredLegacyMarkerId) is { } retired
        && retired[RetiredFavoriteRevisionsField].IsDocument
            ? RetiredInt(retired[RetiredFavoriteRevisionsField].AsDocument[slot.ToString(CultureInfo.InvariantCulture)])
            : 0;

    // The row may come from an imported file: anything but a usable number is 0.
    private static int RetiredInt(BsonValue value) =>
        value.IsInt32 && value.AsInt32 is > 0 and < int.MaxValue ? value.AsInt32 : 0;

    private void RemoveLinks(string memoryId) =>
        _links.DeleteMany(link => link.MemoryId == memoryId);

    private void EnsureAssignmentsFree(int? catChannel, int? shortcutSlot, string? exceptId)
    {
        if (catChannel is { } channel
            && FindByColumn(nameof(StationMemoryDocument.CatChannel), channel) is { } channelHolder
            && !string.Equals(channelHolder.Id, exceptId, StringComparison.Ordinal))
            throw Duplicate("catChannel", $"CAT channel {channel} is already assigned to another memory.", channelHolder.Id);
        if (shortcutSlot is { } slot
            && FindByColumn(nameof(StationMemoryDocument.ShortcutSlot), slot) is { } slotHolder
            && !string.Equals(slotHolder.Id, exceptId, StringComparison.Ordinal))
            throw Duplicate("shortcutSlot", $"Shortcut slot {slot} is already assigned to another memory.", slotHolder.Id);
    }

    // An import can move an assignment from one row of the plan to another,
    // so the "unique when present" rules are checked on the finished rows,
    // inside the transaction, once everything is written.
    private void EnsureImportedAssignmentsFree(StationMemoryDto row)
    {
        EnsureSoleHolder(row, nameof(StationMemoryDocument.CatChannel), row.CatChannel, "catChannel", "CAT channel");
        EnsureSoleHolder(row, nameof(StationMemoryDocument.ShortcutSlot), row.ShortcutSlot, "shortcutSlot", "Shortcut slot");
        EnsureSoleHolder(row, nameof(StationMemoryDocument.FavoriteSlot), row.Legacy?.FavoriteSlot, "legacy.favoriteSlot", "Favorite slot");
        EnsureSoleHolder(row, nameof(StationMemoryDocument.BandStackId), row.Legacy?.BandStackId, "legacy.bandStackId", "Band stack id");
    }

    private void EnsureSoleHolder(StationMemoryDto row, string column, int? assignment, string field, string label)
    {
        if (assignment is not { } value) return;
        var other = _memories.Find(Query.EQ(column, value))
            .FirstOrDefault(memory => !string.Equals(memory.Id, row.Id, StringComparison.Ordinal));
        if (other is not null)
            throw Duplicate(field, $"{label} {value} is already assigned to another memory.", other.Id);
    }

    private StationMemoryDocument ImportDocument(StationMemoryDto row)
    {
        var errors = new List<StationMemoryFieldError>();
        var clean = StationMemoryValidator.Normalize(StationMemoryValidator.ToWrite(row));
        errors.AddRange(StationMemoryValidator.Validate(
            clean,
            favoriteMapped: row.Legacy?.FavoriteSlot is not null,
            bandStackMapped: row.Legacy?.BandStackId is not null));
        errors.AddRange(StationMemoryValidator.ValidateRow(row));
        if (errors.Count > 0)
            throw new StationMemoryValidationException(errors);

        var memory = new StationMemoryDocument
        {
            Id = row.Id,
            Source = row.Source,
            FavoriteSlot = row.Legacy?.FavoriteSlot,
            BandStackId = row.Legacy?.BandStackId,
            LegacyBand = row.Legacy?.Band,
            CreatedUtcMs = row.CreatedUtcMs,
            UpdatedUtcMs = row.UpdatedUtcMs,
            LastRecalledUtcMs = row.LastRecalledUtcMs,
            Revision = row.Revision,
        };
        Apply(memory, StationMemoryValidator.Canonicalize(clean));
        return memory;
    }

    // The key is looked up before the insert, so a collision is a
    // duplicate_assignment and never a LiteException from the unique index.
    private void InsertLinks(StationMemoryDto row)
    {
        foreach (var link in row.ExternalLinks ?? [])
        {
            var key = StationMemoryValidator.LinkKey(link.RadioSerial, link.MemoryIndex);
            if (_links.FindOne(candidate => candidate.Key == key) is { } holder)
                throw LinkTaken(link, holder.MemoryId);
            _links.Insert(NewLink(row.Id, link));
        }
    }

    private static StationMemoryLinkDocument NewLink(string memoryId, StationMemoryExternalLink link) => new()
    {
        Key = StationMemoryValidator.LinkKey(link.RadioSerial, link.MemoryIndex),
        MemoryId = memoryId,
        RadioSerial = link.RadioSerial.Trim(),
        MemoryIndex = link.MemoryIndex,
        Owner = Owner(link),
    };

    private static string? Owner(StationMemoryExternalLink link) =>
        string.IsNullOrWhiteSpace(link.Owner) ? null : link.Owner.Trim();

    private static StationMemoryConflictException LinkTaken(StationMemoryExternalLink link, string holderId) =>
        Duplicate(
            "externalLinks",
            $"Radio memory {link.MemoryIndex} of {link.RadioSerial.Trim()} is already linked to another memory.",
            holderId);

    private static StationMemoryConflictException Duplicate(string field, string message, string conflictingMemoryId) =>
        new(
            StationMemoryErrorCodes.DuplicateAssignment,
            message,
            new StationMemoryErrorDetails(Field: field, ConflictingMemoryId: conflictingMemoryId));

    private static StationMemoryDocument NewDocument(string id, string source, long nowMs) => new()
    {
        Id = id,
        Source = source,
        CreatedUtcMs = nowMs,
        UpdatedUtcMs = nowMs,
        Revision = 1,
    };

    private static void Apply(StationMemoryDocument memory, StationMemoryWrite write)
    {
        memory.Name = write.Name;
        memory.Group = write.Group;
        memory.Notes = write.Notes;
        memory.FrequencyHz = write.FrequencyHz;
        memory.Mode = write.Mode;
        memory.FilterLowHz = write.FilterLowHz;
        memory.FilterHighHz = write.FilterHighHz;
        memory.TuneStepHz = write.TuneStepHz;
        memory.ScanSkip = write.ScanSkip;
        memory.SourceMode = write.SourceMode;
        memory.RxAntenna = write.RxAntenna;
        memory.TxAntenna = write.TxAntenna;
        memory.CatChannel = write.CatChannel;
        memory.ShortcutSlot = write.ShortcutSlot;
        memory.SplitJson = StationMemoryJson.Write(write.Split);
        memory.RitJson = StationMemoryJson.Write(write.Rit);
        memory.XitJson = StationMemoryJson.Write(write.Xit);
        memory.DigitalJson = StationMemoryJson.Write(write.Digital);
        memory.SquelchJson = StationMemoryJson.Write(write.Squelch);
        memory.FmJson = StationMemoryJson.Write(write.Fm);
    }

    private static IOrderedEnumerable<StationMemoryDocument> Ordered(IEnumerable<StationMemoryDocument> memories) =>
        memories
            .OrderBy(memory => memory.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(memory => memory.Id, StringComparer.Ordinal);

    private static bool Matches(StationMemoryDocument memory, string text)
    {
        bool In(string? value) => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
        var hz = memory.FrequencyHz;
        return In(memory.Name)
            || In(memory.Notes)
            || In(memory.Group)
            || In(memory.Mode)
            || In(memory.SourceMode)
            || In(hz.ToString(CultureInfo.InvariantCulture))
            || In(string.Create(CultureInfo.InvariantCulture, $"{hz / 1_000_000}.{hz % 1_000_000:D6}"));
    }

    private static IReadOnlyList<StationMemoryGroupCount> GroupCounts(IEnumerable<StationMemoryDocument> memories) =>
        memories
            .GroupBy(memory => memory.Group, StringComparer.Ordinal)
            .Select(group => new StationMemoryGroupCount(group.Key, group.Count()))
            .OrderBy(group => group.Group is null ? 0 : 1)
            .ThenBy(group => group.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Group, StringComparer.Ordinal)
            .ToArray();

    private StationMemoryDto ToDto(StationMemoryDocument memory)
    {
        var memoryId = memory.Id;
        return ToDto(memory, _links.Find(link => link.MemoryId == memoryId));
    }

    private IReadOnlyList<StationMemoryDto> ToDtos(List<StationMemoryDocument> memories)
    {
        if (memories.Count == 0) return [];
        var links = _links.FindAll().ToLookup(link => link.MemoryId, StringComparer.Ordinal);
        return memories.Select(memory => ToDto(memory, links[memory.Id])).ToArray();
    }

    private StationMemoryDto ToDto(StationMemoryDocument memory, IEnumerable<StationMemoryLinkDocument> links) => new(
        memory.SchemaVersion,
        memory.Id,
        memory.Name,
        memory.Group,
        memory.Notes,
        memory.Source,
        memory.CreatedUtcMs,
        memory.UpdatedUtcMs,
        memory.LastRecalledUtcMs,
        memory.Revision,
        memory.FrequencyHz,
        memory.Mode,
        memory.FilterLowHz,
        memory.FilterHighHz,
        memory.TuneStepHz,
        memory.ScanSkip,
        memory.SourceMode,
        ReadColumn<StationMemorySplit>(memory, memory.SplitJson, nameof(memory.SplitJson)),
        ReadColumn<StationMemoryOffset>(memory, memory.RitJson, nameof(memory.RitJson)),
        ReadColumn<StationMemoryOffset>(memory, memory.XitJson, nameof(memory.XitJson)),
        ReadColumn<StationMemoryDigital>(memory, memory.DigitalJson, nameof(memory.DigitalJson)),
        memory.RxAntenna,
        memory.TxAntenna,
        ReadColumn<StationMemorySquelch>(memory, memory.SquelchJson, nameof(memory.SquelchJson)),
        ReadColumn<FmMemory>(memory, memory.FmJson, nameof(memory.FmJson)),
        memory.CatChannel,
        memory.ShortcutSlot,
        memory.FavoriteSlot is null && memory.BandStackId is null && memory.LegacyBand is null
            ? null
            : new StationMemoryLegacy(memory.FavoriteSlot, memory.BandStackId, memory.LegacyBand),
        links
            .OrderBy(link => link.Key, StringComparer.Ordinal)
            .Select(link => new StationMemoryExternalLink(link.RadioSerial, link.MemoryIndex, link.Owner))
            .ToArray());

    private T? ReadColumn<T>(StationMemoryDocument memory, string? json, string column) where T : class
    {
        var value = StationMemoryJson.Read<T>(json);
        if (value is null && !string.IsNullOrWhiteSpace(json))
            _log.LogWarning("Memory {Id} has an unreadable {Column}; it reads as empty", memory.Id, column);
        return value;
    }
}

public sealed class StationMemoryDocument
{
    [BsonId] public string Id { get; set; } = "";
    public int SchemaVersion { get; set; } = StationMemorySchema.Version;
    public string Name { get; set; } = "";
    public string? Group { get; set; }            // null = Ungrouped, never ""
    public string? Notes { get; set; }
    public string Source { get; set; } = StationMemorySources.User;
    public long FrequencyHz { get; set; }
    public string? Mode { get; set; }
    public int? FilterLowHz { get; set; }
    public int? FilterHighHz { get; set; }
    public int? TuneStepHz { get; set; }
    public bool ScanSkip { get; set; }
    public string? SourceMode { get; set; }
    public string? RxAntenna { get; set; }
    public string? TxAntenna { get; set; }
    public int? CatChannel { get; set; }
    public int? ShortcutSlot { get; set; }
    public int? FavoriteSlot { get; set; }        // legacy.favoriteSlot
    public int? BandStackId { get; set; }         // legacy.bandStackId
    public string? LegacyBand { get; set; }       // legacy.band
    public long CreatedUtcMs { get; set; }
    public long UpdatedUtcMs { get; set; }
    public long? LastRecalledUtcMs { get; set; }
    public int Revision { get; set; }
    // Nested objects as System.Text.Json string columns (the FmJson pattern
    // of the favorite store): no new nested POCO for LiteDB's BsonMapper.
    public string? SplitJson { get; set; }
    public string? RitJson { get; set; }
    public string? XitJson { get; set; }
    public string? DigitalJson { get; set; }
    public string? SquelchJson { get; set; }
    public string? FmJson { get; set; }
}

public sealed class StationMemoryLinkDocument
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public string Key { get; set; } = "";         // lowercase radio serial + ":" + memory index
    public string MemoryId { get; set; } = "";
    public string RadioSerial { get; set; } = ""; // trimmed, original case
    public int MemoryIndex { get; set; }
    public string? Owner { get; set; }
}

/// <summary>
/// One lock per database file per process. The memory store and the favorite
/// and band-stack adapters over it serialize on the same entry even when they
/// are constructed separately.
/// </summary>
internal static class StationMemoryGate
{
    internal sealed class Entry
    {
        public readonly object Sync = new();
        public long Version;
    }

    // Windows and macOS default to case-insensitive filesystems; Linux is
    // case-sensitive. Key the registry to match so two spellings of the same
    // file share one entry where the OS treats them as one file.
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    internal static Entry For(string dbPath) =>
        Entries.GetOrAdd(Path.GetFullPath(dbPath), static _ => new Entry());
}

/// <summary><see cref="StationMemoryQuery.GroupSpecified"/> with a null
/// <see cref="StationMemoryQuery.Group"/> selects the ungrouped memories.</summary>
public sealed record StationMemoryQuery(
    string? Group = null,
    bool GroupSpecified = false,
    string? Query = null,
    string? Source = null,
    int Offset = 0,
    int Limit = StationMemorySchema.ListDefaultLimit,
    int? ShortcutSlot = null,
    int? CatChannel = null);

public sealed record StationMemoryPage(
    IReadOnlyList<StationMemoryDto> Memories,
    int Total,
    int LibraryTotal,
    IReadOnlyList<StationMemoryGroupCount> Groups);

/// <summary>A finished import: complete rows with their ids minted and their
/// revisions as they must be stored. Applied as given by
/// <see cref="StationMemoryStore.ApplyImport"/>, and only to the library the
/// plan was made from when <see cref="ExpectedLibraryVersion"/> is set.</summary>
public sealed record StationMemoryImportPlan(
    IReadOnlyList<string> RemoveIds,
    IReadOnlyList<StationMemoryDto> Inserts,
    IReadOnlyList<StationMemoryDto> Updates,
    int Skipped,
    long? ExpectedLibraryVersion = null);

public sealed class StationMemoryValidationException(IReadOnlyList<StationMemoryFieldError> errors)
    : Exception(errors.Count > 0 ? errors[0].Message : "The memory is not valid.")
{
    public IReadOnlyList<StationMemoryFieldError> Errors { get; } = errors;
}

public sealed class StationMemoryNotFoundException(string kind, string key)
    : Exception($"No {kind} '{key}' exists.")
{
    public string Kind { get; } = kind;
    public string Key { get; } = key;
}

public sealed class StationMemoryConflictException(
    string code,
    string message,
    StationMemoryErrorDetails? details = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public StationMemoryErrorDetails? Details { get; } = details;
}
