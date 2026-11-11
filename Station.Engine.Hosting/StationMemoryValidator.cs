// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// The one serializer configuration of the memory library: the JSON string
/// columns of <see cref="StationMemoryDocument"/> equal the wire JSON. The
/// same options read the PascalCase, numeric-enum <c>FmJson</c> the favorite
/// store wrote before the unified library existed.
/// </summary>
public static class StationMemoryJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    internal static string? Write<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, Options);

    internal static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Storage rules of a unified memory. The three phases always run in the
/// order <see cref="Normalize"/>, <see cref="Validate"/>,
/// <see cref="Canonicalize"/>, so a range rule sees the value the client
/// sent and a refused value is never altered into an accepted one.
/// </summary>
public static class StationMemoryValidator
{
    public const int MaxFavoriteSlot = StationFavoriteStore.SlotCount;
    public const int MaxLegacyBandLength = 64;
    public const int MinRttyMarkHz = 100;
    public const int MaxRttyMarkHz = 10_000;
    public const int MinRttyShiftHz = 1;
    public const int MaxRttyShiftHz = 5_000;
    public const int MaxDigitalOffsetHz = 12_000;
    public const int MaxSquelchLevel = 100;
    public const double MinToneHz = 60.0;
    public const double MaxToneHz = 260.0;

    public static readonly IReadOnlyList<string> ModeNames = Enum.GetNames<RxMode>();

    /// <summary>The route literals under <c>/api/station/memories/</c>; none
    /// of them may be a memory id.</summary>
    public static readonly IReadOnlySet<string> ReservedIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "export",
        "capture",
        "group-operation",
        "import-preview",
        "import-commit",
        "radio-snapshot",
        "radio-import-preview",
        "radio-import-commit",
        "diagnostics",
    };

    /// <summary>Text only: trim strings, turn "" into null, drop an all-null
    /// digital object. Never touches fm and never changes a value the rules
    /// reject. Notes are trimmed like the rest: the database keeps no
    /// leading or trailing white space, so an answer that did would differ
    /// from the next read.</summary>
    public static StationMemoryWrite Normalize(StationMemoryWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return write with
        {
            Name = (write.Name ?? string.Empty).Trim(),
            Group = NormalizeGroup(write.Group),
            Notes = TrimToNull(write.Notes),
            Mode = string.IsNullOrWhiteSpace(write.Mode) ? null : write.Mode,
            SourceMode = TrimToNull(write.SourceMode),
            Split = write.Split is { } split && string.IsNullOrWhiteSpace(split.TxMode)
                ? split with { TxMode = null }
                : write.Split,
            Digital = write.Digital is { RttyMarkHz: null, RttyShiftHz: null, DiglOffsetHz: null, DiguOffsetHz: null }
                ? null
                : write.Digital,
            RxAntenna = TrimToNull(write.RxAntenna),
            TxAntenna = TrimToNull(write.TxAntenna),
        };
    }

    /// <summary>Every violated rule, in rule order. Empty means valid. Call
    /// after <see cref="Normalize"/>.</summary>
    public static IReadOnlyList<StationMemoryFieldError> Validate(
        StationMemoryWrite write,
        bool favoriteMapped = false,
        bool bandStackMapped = false)
    {
        ArgumentNullException.ThrowIfNull(write);
        var errors = new List<StationMemoryFieldError>();
        void Fail(string field, string message) => errors.Add(new StationMemoryFieldError(field, message));

        if (write.Name is not { Length: >= 1 and <= StationMemorySchema.MaxNameLength } || HasControl(write.Name))
            Fail("name", "name must be 1 to 80 characters with no control characters");
        if (write.Group is { } group && (group.Length > StationMemorySchema.MaxGroupLength || HasControl(group)))
            Fail("group", "group must be at most 80 characters with no control characters");
        if (write.Notes is { } notes
            && (notes.Length > StationMemorySchema.MaxNotesLength
                || notes.Any(static c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))))
            Fail("notes", "notes must be at most 500 characters");
        if (!IsSafeFrequency(write.FrequencyHz))
            Fail("frequencyHz", "frequencyHz must be a positive safe integer");
        if (write.Mode is not null && !IsKnownMode(write.Mode))
            Fail("mode", "mode is not a Zeus mode");
        if (write.Mode is null && string.IsNullOrEmpty(write.SourceMode))
            Fail("mode", "mode is required unless sourceMode holds an unmapped imported mode");
        if (write.SourceMode is { } sourceMode
            && (sourceMode.Length > StationMemorySchema.MaxSourceModeLength || HasControl(sourceMode)))
            Fail("sourceMode", "sourceMode must be at most 16 characters");
        if (!IsFilterPair(write.FilterLowHz, write.FilterHighHz))
            Fail("filterLowHz", "filterLowHz must be less than filterHighHz, or both must be empty");
        if (write.TuneStepHz is { } step && step is < 1 or > StationMemorySchema.MaxTuneStepHz)
            Fail("tuneStepHz", "tuneStepHz must be from 1 to 1000000000");

        if (write.Split is { } split)
        {
            if (split.TxFrequencyHz is { } txHz && !IsSafeFrequency(txHz))
                Fail("split.txFrequencyHz", "split.txFrequencyHz must be a positive safe integer");
            if (split.TxMode is not null && !IsKnownMode(split.TxMode))
                Fail("split.txMode", "split.txMode is not a Zeus mode");
        }

        if (write.Rit is { } rit && !IsStoredOffset(rit.OffsetHz))
            Fail("rit.offsetHz", "rit.offsetHz must be within plus or minus 1000000");
        if (write.Xit is { } xit && !IsStoredOffset(xit.OffsetHz))
            Fail("xit.offsetHz", "xit.offsetHz must be within plus or minus 1000000");

        if (write.Digital is { } digital)
        {
            if (digital.RttyMarkHz is < MinRttyMarkHz or > MaxRttyMarkHz)
                Fail("digital.rttyMarkHz", "digital.rttyMarkHz must be from 100 to 10000");
            if (digital.RttyShiftHz is < MinRttyShiftHz or > MaxRttyShiftHz)
                Fail("digital.rttyShiftHz", "digital.rttyShiftHz must be from 1 to 5000");
            if (digital.DiglOffsetHz is < -MaxDigitalOffsetHz or > MaxDigitalOffsetHz)
                Fail("digital.diglOffsetHz", "digital.diglOffsetHz must be within plus or minus 12000");
            if (digital.DiguOffsetHz is < -MaxDigitalOffsetHz or > MaxDigitalOffsetHz)
                Fail("digital.diguOffsetHz", "digital.diguOffsetHz must be within plus or minus 12000");
        }

        if (write.RxAntenna is { } rxAntenna
            && (rxAntenna.Length > StationMemorySchema.MaxAntennaLength || HasControl(rxAntenna)))
            Fail("rxAntenna", "rxAntenna must be at most 32 characters");
        if (write.TxAntenna is { } txAntenna
            && (txAntenna.Length > StationMemorySchema.MaxAntennaLength || HasControl(txAntenna)))
            Fail("txAntenna", "txAntenna must be at most 32 characters");
        if (write.Squelch is { Level: < 0 or > MaxSquelchLevel })
            Fail("squelch.level", "squelch.level must be from 0 to 100");

        if (write.Fm is { } fm)
        {
            if (!IsTone(fm.CtcssToneHz))
                Fail("fm.ctcssToneHz", "fm.ctcssToneHz is not valid");
            if (fm.RxCtcssToneHz is { } rxTone && !IsTone(rxTone))
                Fail("fm.rxCtcssToneHz", "fm.rxCtcssToneHz is not valid");
            if (!FmConfig.DcsCodes.Contains(fm.DcsCode))
                Fail("fm.dcsCode", "fm.dcsCode is not valid");
            if (fm.RxDcsCode is { } rxCode && !FmConfig.DcsCodes.Contains(rxCode))
                Fail("fm.rxDcsCode", "fm.rxDcsCode is not valid");
            if (fm.RepeaterOffsetHz is < 0 or > FmConfig.MaxRepeaterOffsetHz)
                Fail("fm.repeaterOffsetHz", "fm.repeaterOffsetHz must be from 0 to 50000000");
            if (fm.DeviationHz is < FmConfig.MinDeviationHz or > FmConfig.MaxDeviationHz)
                Fail("fm.deviationHz", "fm.deviationHz must be from 1000 to 8000");
            if (!Enum.IsDefined(fm.RepeaterShift))
                Fail("fm.repeaterShift", "fm.repeaterShift is not valid");
            if (!Enum.IsDefined(fm.RxToneSquelch))
                Fail("fm.rxToneSquelch", "fm.rxToneSquelch is not valid");
        }

        if (write.CatChannel is < StationMemorySchema.MinCatChannel or > StationMemorySchema.MaxCatChannel)
            Fail("catChannel", "catChannel must be from 0 to 99");
        if (write.ShortcutSlot is < StationMemorySchema.MinShortcutSlot or > StationMemorySchema.MaxShortcutSlot)
            Fail("shortcutSlot", "shortcutSlot must be from 1 to 10");

        if (favoriteMapped)
        {
            const string message = "a memory assigned to a favorite slot keeps a mode and both filter edges";
            if (write.Mode is null) Fail("mode", message);
            if (write.FilterLowHz is null) Fail("filterLowHz", message);
            if (write.FilterHighHz is null) Fail("filterHighHz", message);
        }
        if (bandStackMapped && write.Mode is null)
            Fail("mode", "a band stack memory keeps a mode");

        return errors;
    }

    /// <summary>Only after <see cref="Validate"/> returned no error: fm in
    /// the canonical form of <see cref="FmMemories.Normalize"/>.</summary>
    public static StationMemoryWrite Canonicalize(StationMemoryWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return write.Fm is { } fm
            ? write with { Fm = FmMemories.Normalize(fm, write.FrequencyHz) }
            : write;
    }

    /// <summary>The server-owned members of a full row: id, source, legacy
    /// mapping, links, timestamps and revision.</summary>
    public static IReadOnlyList<StationMemoryFieldError> ValidateRow(StationMemoryDto row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var errors = new List<StationMemoryFieldError>();
        void Fail(string field) => errors.Add(new StationMemoryFieldError(field, field + " is not valid"));

        if (!IsValidId(row.Id)) Fail("id");
        if (!StationMemorySources.All.Contains(row.Source)) Fail("source");

        if (row.Legacy is { } legacy)
        {
            if (legacy.FavoriteSlot is < 1 or > MaxFavoriteSlot) Fail("legacy.favoriteSlot");
            if (legacy.BandStackId is < 1) Fail("legacy.bandStackId");
            if (legacy.Band is { } band && (band.Length > MaxLegacyBandLength || HasControl(band)))
                Fail("legacy.band");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var links = row.ExternalLinks ?? [];
        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            if (link is null || !IsValidLink(link) || !keys.Add(LinkKey(link.RadioSerial, link.MemoryIndex)))
                Fail($"externalLinks[{index.ToString(CultureInfo.InvariantCulture)}]");
        }

        if (row.SchemaVersion != StationMemorySchema.Version) Fail("schemaVersion");
        if (row.Revision < 1) Fail("revision");
        if (row.CreatedUtcMs < 0) Fail("createdUtcMs");
        if (row.UpdatedUtcMs < 0) Fail("updatedUtcMs");
        if (row.LastRecalledUtcMs is < 0) Fail("lastRecalledUtcMs");

        return errors;
    }

    public static void ThrowIfInvalid(
        StationMemoryWrite write,
        bool favoriteMapped = false,
        bool bandStackMapped = false)
    {
        var errors = Validate(write, favoriteMapped, bandStackMapped);
        if (errors.Count > 0)
            throw new StationMemoryValidationException(errors);
    }

    public static bool IsValidId(string? id)
    {
        if (id is not { Length: >= 1 and <= StationMemorySchema.MaxIdLength } || ReservedIds.Contains(id))
            return false;
        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                return false;
        }
        return true;
    }

    public static bool IsKnownMode(string? mode) =>
        mode is not null && ModeNames.Contains(mode, StringComparer.Ordinal);

    /// <summary>An exact <see cref="RxMode"/> member name. Numeric text,
    /// which <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/> accepts,
    /// is not a mode.</summary>
    internal static bool TryParseMode(string? mode, out RxMode parsed)
    {
        parsed = default;
        return IsKnownMode(mode) && Enum.TryParse(mode, out parsed);
    }

    /// <summary>Null and empty both mean Ungrouped, stored as null.</summary>
    public static string? NormalizeGroup(string? group) => TrimToNull(group);

    public static string LinkKey(string radioSerial, int memoryIndex) =>
        (radioSerial ?? string.Empty).Trim().ToLowerInvariant()
        + ":"
        + memoryIndex.ToString(CultureInfo.InvariantCulture);

    /// <summary>The client-settable members of a stored memory.</summary>
    public static StationMemoryWrite ToWrite(StationMemoryDto memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return new StationMemoryWrite(
            memory.Name,
            memory.FrequencyHz,
            memory.Group,
            memory.Notes,
            memory.Mode,
            memory.FilterLowHz,
            memory.FilterHighHz,
            memory.TuneStepHz,
            memory.ScanSkip,
            memory.SourceMode,
            memory.Split,
            memory.Rit,
            memory.Xit,
            memory.Digital,
            memory.RxAntenna,
            memory.TxAntenna,
            memory.Squelch,
            memory.Fm,
            memory.CatChannel,
            memory.ShortcutSlot);
    }

    internal static bool IsSafeFrequency(long hz) => hz is >= 1 and <= StationMemorySchema.MaxFrequencyHz;

    /// <summary>Both edges empty, or both set with low below high.</summary>
    internal static bool IsFilterPair(int? lowHz, int? highHz) =>
        (lowHz is null && highHz is null) || (lowHz is { } low && highHz is { } high && low < high);

    internal static bool IsValidLink(StationMemoryExternalLink link)
    {
        var serial = link.RadioSerial?.Trim();
        return serial is { Length: >= 1 and <= StationMemorySchema.MaxRadioSerialLength }
            && !HasControl(serial)
            && link.MemoryIndex >= 0
            && (link.Owner is null
                || (link.Owner.Length <= StationMemorySchema.MaxOwnerLength && !HasControl(link.Owner)));
    }

    internal static bool HasControl(string value) => value.Any(char.IsControl);

    private static bool IsStoredOffset(long offsetHz) =>
        offsetHz is >= -StationMemorySchema.MaxStoredOffsetHz and <= StationMemorySchema.MaxStoredOffsetHz;

    private static bool IsTone(double hz) => double.IsFinite(hz) && hz is >= MinToneHz and <= MaxToneHz;

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
