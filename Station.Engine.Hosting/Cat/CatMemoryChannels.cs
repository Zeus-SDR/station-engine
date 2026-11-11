// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Cat;

/// <summary>
/// The Kenwood memory channels (MC / MR / MW) over the unified memory
/// library: channel nn is the memory whose <c>catChannel</c> is nn. A recall
/// goes through <see cref="StationMemoryRecallService"/>, so CAT is held to
/// the same lock, key and capability guards as every other surface and never
/// keys anything. Reads and writes need no connected radio.
///
/// A write is all or nothing: a record whose values the library would not
/// store exactly as written is refused, so a read after a write always
/// returns the record that was written.
/// </summary>
public sealed class CatMemoryChannels
{
    private const string Origin = "cat";
    private const string OperationRecall = "catRecall";
    private const string OperationWrite = "catWrite";

    private readonly StationMemoryStore _store;
    private readonly StationMemoryRecallService _recall;
    private readonly MemoryOperationHistory? _history;
    private readonly ILogger _log;

    public CatMemoryChannels(
        StationMemoryStore store,
        StationMemoryRecallService recall,
        MemoryOperationHistory? history = null,
        ILogger<CatMemoryChannels>? log = null)
    {
        _store = store;
        _recall = recall;
        _history = history;
        _log = log ?? NullLogger<CatMemoryChannels>.Instance;
    }

    /// <summary>Recall the memory assigned to <paramref name="channel"/>.
    /// False when no memory has the channel or the recall was refused or
    /// failed; the recall service has already put the radio back.</summary>
    public bool TryRecall(int channel)
    {
        try
        {
            var memory = _store.GetByCatChannel(channel);
            if (memory is null)
            {
                Journal(OperationRecall, null, null, MemoryOperationHistory.ResultRefused, StationMemoryErrorCodes.NotFound);
                return false;
            }

            // The whole memory or nothing: CAT has no way to show the operator
            // which fields would be left out. The service records the outcome.
            _recall.Recall(memory.Id, new StationMemoryRecallRequest(memory.Revision, AllowPartial: false), Origin);
            return true;
        }
        catch (StationMemoryRecallRefusedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cat.memory.recall failed channel={Channel}", channel);
            return false;
        }
    }

    /// <summary>The 20-byte record of <paramref name="channel"/> (selector 0 =
    /// receive, 1 = transmit), or null when the channel is unassigned or the
    /// memory cannot be expressed as a record.</summary>
    public string? ReadRecord(int selector, int channel)
    {
        StationMemoryDto? memory;
        try
        {
            memory = _store.GetByCatChannel(channel);
        }
        catch (Exception ex)
        {
            // A query that went unanswered would leave the client waiting.
            _log.LogWarning(ex, "cat.memory.read failed channel={Channel}", channel);
            return null;
        }
        if (memory is null || !StationMemoryValidator.TryParseMode(memory.Mode, out var mode)) return null;

        long hz = memory.FrequencyHz;
        var recordMode = mode;
        if (selector != 0)
        {
            // Split off reads as frequency zero, which written back keeps it off.
            hz = memory.Split is { Enabled: true, TxFrequencyHz: > 0 } split ? split.TxFrequencyHz.Value : 0;
            if (memory.Split?.TxMode is { } txMode && !StationMemoryValidator.TryParseMode(txMode, out recordMode))
                return null;
        }
        if (hz > CatProtocol.MaxMemoryFrequencyHz) return null;

        int tone = 0;
        bool ctcss = mode == RxMode.FM && memory.Fm is { CtcssEnabled: true };
        if (ctcss)
        {
            tone = CatProtocol.CtcssIndex(memory.Fm!.CtcssToneHz);
            if (tone == 0) return null;
        }

        return CatProtocol.FormatMemoryRecord(new CatMemoryRecord(
            selector, channel, hz, CatProtocol.ModeDigit(recordMode)[0], memory.ScanSkip, ctcss, tone));
    }

    /// <summary>Write <paramref name="record"/> to its channel, creating the
    /// memory "CAT nn" when the channel is unassigned. False when the record
    /// is refused; nothing is written then.</summary>
    public bool WriteRecord(CatMemoryRecord record)
    {
        StationMemoryDto? existing = null;
        try
        {
            existing = _store.GetByCatChannel(record.Channel);
            var write = existing is null ? NewMemory(record) : Edit(existing, record);
            if (write is null)
            {
                Journal(OperationWrite, existing?.Id, existing?.Revision,
                    MemoryOperationHistory.ResultRefused, StationMemoryErrorCodes.Validation);
                return false;
            }

            // A record read and written straight back is not an edit.
            if (existing is not null && write == StationMemoryValidator.ToWrite(existing)) return true;

            var stored = existing is null
                ? _store.Create(write)
                : _store.Update(existing.Id, existing.Revision, write);
            Journal(OperationWrite, stored.Id, stored.Revision, MemoryOperationHistory.ResultOk, code: null);
            return true;
        }
        catch (Exception ex)
        {
            // Silence is how a Kenwood set is acknowledged, so no failure may
            // leave here as one.
            string? code = StationMemoryRecallService.ErrorCode(ex);
            if (code is null) _log.LogWarning(ex, "cat.memory.write failed channel={Channel}", record.Channel);
            Journal(OperationWrite, existing?.Id, existing?.Revision,
                code is null ? MemoryOperationHistory.ResultFailed : MemoryOperationHistory.ResultRefused, code);
            return false;
        }
    }

    private static StationMemoryWrite? NewMemory(CatMemoryRecord record)
    {
        if (record.FrequencyHz <= 0 || WrittenMode(record) is not { } mode) return null;

        var write = new StationMemoryWrite(
            "CAT " + record.Channel.ToString("00", CultureInfo.InvariantCulture),
            record.FrequencyHz,
            Group: StationMemorySchema.CatGroup,
            Mode: mode.ToString(),
            ScanSkip: record.Skip,
            // A transmit record on an empty channel is a split memory that
            // listens on the transmit frequency until MW0 says otherwise.
            Split: record.Selector == 0 ? null : new StationMemorySplit(true, record.FrequencyHz),
            CatChannel: record.Channel);
        return WithTone(write, existingFm: null, record);
    }

    private static StationMemoryWrite? Edit(StationMemoryDto existing, CatMemoryRecord record)
    {
        if (WrittenMode(record) is not { } written) return null;
        var write = StationMemoryValidator.ToWrite(existing) with { ScanSkip = record.Skip };
        // Several Zeus modes share one Kenwood digit. A digit that already
        // describes the stored mode leaves it alone, so SAM, DSB and FreeDV
        // survive a read-then-write.
        bool keepsMode = StationMemoryValidator.TryParseMode(existing.Mode, out var stored)
            && SameDigit(stored, record.ModeDigit);

        if (record.Selector == 0)
        {
            if (record.FrequencyHz <= 0) return null;
            write = write with { FrequencyHz = record.FrequencyHz };
            // Signed filter edges are valid only for the mode they were
            // captured in.
            if (!keepsMode)
                write = write with { Mode = written.ToString(), FilterLowHz = null, FilterHighHz = null };
        }
        else if (record.FrequencyHz > 0)
        {
            string? txMode = existing.Split?.TxMode;
            if (!StationMemoryValidator.TryParseMode(txMode, out var storedTx) || !SameDigit(storedTx, record.ModeDigit))
                txMode = keepsMode ? null : written.ToString();
            write = write with { Split = new StationMemorySplit(true, record.FrequencyHz, txMode) };
        }
        else if (existing.Split is { } split)
        {
            // Zero switches split off and keeps what it was set to.
            write = write with { Split = split with { Enabled = false } };
        }

        return WithTone(write, existing.Fm, record);
    }

    /// <summary>The CTCSS fields of the record. They touch <c>fm</c> only when
    /// the resulting mode is FM: an <c>fm</c> object on a memory of another
    /// mode is inert and is kept as it is, so no mode change drops DCS or tone
    /// data. Null when the tone state of the record cannot be stored.</summary>
    private static StationMemoryWrite? WithTone(StationMemoryWrite write, FmMemory? existingFm, CatMemoryRecord record)
    {
        bool fmMode = write.Mode == nameof(RxMode.FM);
        if (!record.CtcssEnabled)
        {
            if (!fmMode || existingFm is null) return write;
            // A DCS-only memory has no tone to switch off, and reading it back
            // as "no tone" would misrepresent it.
            if (existingFm.DcsTxEnabled) return null;
            return existingFm.CtcssEnabled ? write with { Fm = existingFm with { CtcssEnabled = false } } : write;
        }

        if (!fmMode) return null;
        double tone = CatProtocol.CtcssTones[record.CtcssIndex - 1];
        var fm = (existingFm ?? FmMemories.Capture(FmConfig.Default, write.FrequencyHz)) with
        {
            CtcssEnabled = true,
            CtcssToneHz = tone,
        };
        // The library keeps fm in its canonical form: TX DCS switches the tone
        // off, and a tone snaps to the Zeus tone table, which has no 196.6 Hz.
        var canonical = FmMemories.Normalize(fm, write.FrequencyHz);
        if (!canonical.CtcssEnabled || Math.Abs(canonical.CtcssToneHz - tone) > CatProtocol.CtcssToleranceHz) return null;
        return write with { Fm = fm };
    }

    private static RxMode? WrittenMode(CatMemoryRecord record) => CatProtocol.ParseMode(record.ModeDigit.ToString());

    private static bool SameDigit(RxMode mode, char digit) => CatProtocol.ModeDigit(mode)[0] == digit;

    private void Journal(string operation, string? memoryId, int? revision, string result, string? code) =>
        MemoryOperationJournal.Write(
            _log,
            _history,
            new MemoryOperationEntry(
                DateTimeOffset.UtcNow,
                operation,
                Origin,
                memoryId,
                revision,
                Generation: null,
                result,
                code,
                FailedStep: null,
                RollbackSucceeded: null,
                Count: null));
}
