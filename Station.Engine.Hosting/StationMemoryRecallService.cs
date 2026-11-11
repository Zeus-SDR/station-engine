// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Collections.Concurrent;
using System.Globalization;
using Zeus.Contracts;
using Zeus.Server.Diagnostics;

namespace Zeus.Server;

/// <summary>
/// Captures the live receiver into a memory and recalls a memory onto it.
/// Every surface (API, CAT, controllers) recalls through this one service, so
/// the guards are the same everywhere: a recall is refused while the VFO is
/// locked or anything holds the key, it never keys, unkeys or arms anything,
/// and a recall that fails part-way puts the previous state back.
/// </summary>
public sealed class StationMemoryRecallService
{
    internal const string StepTransition = "transition";
    internal const string StepSnapshot = "snapshot";
    internal const string StepMode = "mode";
    internal const string StepFrequency = "frequency";
    internal const string StepModeReassert = "modeReassert";
    internal const string StepFilter = "filter";
    internal const string StepTuneStep = "tuneStep";
    internal const string StepRit = "rit";
    internal const string StepXit = "xit";
    internal const string StepSplitTxFrequency = "splitTxFrequency";
    internal const string StepSplit = "split";
    internal const string StepFm = "fm";
    internal const string StepSquelch = "squelch";
    internal const string StepFilterReassert = "filterReassert";
    internal const string StepVerify = "verify";
    internal const string StepCommit = "commit";
    internal const string StepSettle = "settle";
    internal const string RollbackPrefix = "rollback:";

    // The engine process serves exactly one station.
    private const string StationKey = "engine";
    private const string OriginApi = "api";
    private const string OriginCat = "cat";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _recallGates = new(StringComparer.Ordinal);
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly StationMemoryStore _store;
    private readonly ILogger<StationMemoryRecallService> _log;
    private readonly ToolbarSettingsStore? _toolbar;
    private readonly ExternalPttService? _externalPtt;
    private readonly IAudioModemPort? _modem;
    private readonly MemoryOperationHistory? _history;
    private readonly TimeProvider _clock;

    public StationMemoryRecallService(
        RadioService radio,
        TxService tx,
        StationMemoryStore store,
        ILogger<StationMemoryRecallService> log,
        ToolbarSettingsStore? toolbar = null,
        ExternalPttService? externalPtt = null,
        IAudioModemPort? modem = null,
        MemoryOperationHistory? history = null,
        TimeProvider? clock = null)
    {
        _radio = radio;
        _tx = tx;
        _store = store;
        _log = log;
        _toolbar = toolbar;
        _externalPtt = externalPtt;
        _modem = modem;
        _history = history;
        _clock = clock ?? TimeProvider.System;
    }

    // How long a rollback waits for a queued digital-mode drive recall. The
    // recall is a few state reads and one write; this only bounds a stall.
    private static readonly TimeSpan DigitalDriveSettleBudget = TimeSpan.FromSeconds(2);

    /// <summary>Test seam: called with the step name before each forward
    /// step and with "rollback:&lt;name&gt;" before each rollback step; the
    /// returned fault is applied to that step.</summary>
    internal Func<string, RecallStepFault>? StepFaultForTests { get; set; }

    /// <summary>True when the external and hardware key sources are watched.
    /// Unit hosts may leave them out; the production registration never does.</summary>
    internal bool HasExternalKeyProbe => _externalPtt is not null;

    public StationMemoryCapabilities Capabilities() =>
        StationMemoryCapabilityResolver.Resolve(_radio, _toolbar, _modem);

    /// <summary>What this station could recall of a memory that is not in
    /// the library, judged against one state snapshot so the rows of an
    /// import preview are all measured alike. Null when no station is
    /// connected.</summary>
    public Func<StationMemoryDto, StationMemoryCapabilityReport>? CapabilityProbe(int receiverIndex = 0)
    {
        if (!_radio.IsConnected) return null;
        var state = _radio.Snapshot();
        return memory => StationMemoryCapabilityResolver.Report(memory, receiverIndex, _radio, state, _toolbar, _modem);
    }

    /// <summary>One coherent reading of a receiver, from a single state
    /// snapshot.</summary>
    public StationMemoryLiveState CaptureLive(int receiverIndex) =>
        Live(_radio.Snapshot(), receiverIndex, _toolbar?.CurrentStepHz)
        ?? throw new StationMemoryValidationException(
            [new("receiverIndex", $"Receiver {receiverIndex + 1} is not enabled.")]);

    public StationMemoryCaptureResponse Capture(StationMemoryCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (!_radio.IsConnected) throw Unavailable();
            if (request.Save && request.Id is not null && request.ExpectedRevision is null)
                throw new StationMemoryValidationException(
                    [new("expectedRevision", "expectedRevision is required to overwrite a memory")]);

            var live = CaptureLive(request.ReceiverIndex);
            var draft = Draft(live, request);
            StationMemoryDto? saved = null;
            if (request.Save && request.Id is null)
            {
                saved = _store.Create(draft);
            }
            else if (request.Save)
            {
                var existing = _store.Get(request.Id!) ?? throw NotFound(request.Id!);
                draft = Overwrite(existing, draft, request);
                saved = _store.Update(existing.Id, request.ExpectedRevision!.Value, draft);
            }

            if (request.Save)
                Journal("capture", OriginApi, saved!.Id, saved.Revision, MemoryOperationHistory.ResultOk);
            return new StationMemoryCaptureResponse(draft, saved, live, Capabilities());
        }
        catch (Exception ex) when (request.Save && ErrorCode(ex) is { } code)
        {
            Journal("capture", OriginApi, request.Id, request.ExpectedRevision, MemoryOperationHistory.ResultRefused, code);
            throw;
        }
    }

    /// <summary>What a recall would do right now, with no radio change. Every
    /// guard that would refuse it is listed instead of thrown.</summary>
    public StationMemoryRecallPreviewResponse Preview(string id, StationMemoryRecallPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var memory = _store.Get(id) ?? throw NotFound(id);
        var state = _radio.Snapshot();
        var report = StationMemoryCapabilityResolver.Report(memory, request.ReceiverIndex, _radio, state, _toolbar, _modem);

        var conflicts = new List<StationMemoryRecallConflict>();
        void Conflict(StationMemoryRecallRefusedException refusal) =>
            conflicts.Add(new StationMemoryRecallConflict(refusal.Code, refusal.Message));

        if (RecallGate().CurrentCount == 0) Conflict(InProgress());
        if (request.ExpectedRevision is { } expected && expected != memory.Revision)
            Conflict(RevisionConflict(memory));
        if (!_radio.IsConnected) Conflict(Unavailable());
        if (state.VfoLocked) Conflict(Locked());
        if (ActiveKeySource() is { } keySource) Conflict(Keyed(keySource));
        if (!report.CanRecall && !report.CanApplyPartial) Conflict(CapabilityRefused(report));

        return new StationMemoryRecallPreviewResponse(memory, report, conflicts, Capabilities(), NativeApply: false);
    }

    /// <summary>Synchronous. Throws
    /// <see cref="StationMemoryRecallRefusedException"/> for every refusal
    /// and failure.</summary>
    public StationMemoryRecallResponse Recall(string id, StationMemoryRecallRequest request, string origin)
    {
        ArgumentNullException.ThrowIfNull(request);
        string operation = origin == OriginCat ? "catRecall" : "recall";
        var gate = RecallGate();
        if (!gate.Wait(0))
        {
            var busy = InProgress();
            Journal(operation, origin, id, request.ExpectedRevision, MemoryOperationHistory.ResultRefused, busy.Code);
            throw busy;
        }

        try
        {
            var response = RecallCore(id, request);
            Journal(operation, origin, id, response.RecalledRevision, MemoryOperationHistory.ResultOk);
            return response;
        }
        catch (StationMemoryRecallRefusedException refusal)
        {
            Journal(
                operation,
                origin,
                id,
                request.ExpectedRevision,
                refusal.Details?.FailedStep is null ? MemoryOperationHistory.ResultRefused : MemoryOperationHistory.ResultFailed,
                refusal.Code,
                refusal.Details?.FailedStep,
                refusal.Details?.RollbackSucceeded);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private StationMemoryRecallResponse RecallCore(string id, StationMemoryRecallRequest request)
    {
        // Pre-checks: no radio command is sent before all of them pass.
        var memory = _store.Get(id) ?? throw NotFound(id);
        if (memory.Revision != request.ExpectedRevision) throw RevisionConflict(memory);
        if (!_radio.IsConnected) throw Unavailable();
        if (_radio.Snapshot().VfoLocked) throw Locked();
        if (ActiveKeySource() is { } keySource) throw Keyed(keySource);

        var report = StationMemoryCapabilityResolver.Report(memory, request.ReceiverIndex, _radio, _toolbar, _modem);
        IReadOnlyList<string> omitted = [];
        if (!report.CanRecall)
        {
            // Partial consent is for exactly the list the operator was shown.
            var accepted = StationMemoryCapabilityResolver.Canonical(request.AcceptedUnsupportedFields);
            if (!request.AllowPartial
                || !report.CanApplyPartial
                || !accepted.SequenceEqual(report.UnsupportedFields, StringComparer.Ordinal))
                throw CapabilityRefused(report);
            omitted = report.UnsupportedFields;
        }
        if (!StationMemoryValidator.TryParseMode(memory.Mode, out var mode)) throw CapabilityRefused(report);

        var run = new Run(memory, request.ReceiverIndex, mode, report.SupportedFields);
        Reading? final = null;
        // No MOX, TUN or two-tone transition Zeus itself starts can begin
        // until this returns.
        if (!_tx.TryRunWithTransmitIdle(() => final = Apply(run), out _))
            throw Keyed("transition");

        // After the transition lock is released: a key request waits on that
        // lock and the store gate can be held by a long import.
        StationMemoryDto stored;
        try
        {
            if (Fault(StepCommit) != RecallStepFault.None)
                throw new InvalidOperationException("Fault injected at the commit step.");
            stored = _store.MarkRecalled(memory.Id, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        }
        catch (Exception ex)
        {
            // The radio keeps the recalled state; only the timestamp is lost.
            _log.LogWarning(ex, "memory.recall could not record the recall time id={Id}", memory.Id);
            throw new StationMemoryRecallRefusedException(
                StatusCodes.Status409Conflict,
                StationMemoryErrorCodes.RecallFailed,
                "The memory was recalled, but the recall time could not be saved.",
                new StationMemoryErrorDetails(
                    FailedStep: StepCommit,
                    AppliedFields: report.SupportedFields));
        }

        return new StationMemoryRecallResponse(
            stored,
            memory.Revision,
            report,
            ToLive(final!.Value, run.Receiver),
            report.SupportedFields,
            omitted,
            NativeApply: false);
    }

    // Runs inside the transmit transition lock. Returns the verified final
    // reading, or throws the refusal that describes what happened.
    private Reading Apply(Run run)
    {
        // Test seam only: the transition lock is held and nothing was checked
        // under it yet.
        Fault(StepTransition);
        // Only the operator dial setters enforce the lock themselves; mode,
        // filter and an extra receiver's dial do not, so the recall must.
        if (_radio.Snapshot().VfoLocked) throw Locked();
        if (ActiveKeySource() is { } keySource) throw Keyed(keySource);

        var memory = run.Memory;
        int receiver = run.Receiver;
        long hz = memory.FrequencyHz;
        var mode = run.Mode;
        try
        {
            Forward(run, StepSnapshot, hasSetter: false, () =>
            {
                run.Before = _radio.Snapshot();
                run.StepBefore = _toolbar?.CurrentStepHz;
            });

            if (Read(receiver).Rx.Mode != mode)
                Forward(run, StepMode, () => SetMode(receiver, mode), Holds(memory, "mode"));

            // The operator dial path restores the destination band's mode,
            // and an SSB/CW change bumps the dial by the CW pitch: a dial that
            // moved together with the mode is put back by the next step.
            Forward(run, StepFrequency, () => SetFrequency(receiver, hz),
                reading => reading.Rx.FrequencyHz == hz || reading.Rx.Mode != mode);
            // The state the band transition produced, before any other field
            // of the memory is applied.
            run.Arrival = _radio.Snapshot();

            if (Read(receiver).Rx.Mode != mode)
            {
                Forward(run, StepModeReassert, () =>
                {
                    SetMode(receiver, mode);
                    if (Read(receiver).Rx.FrequencyHz != hz) SetFrequency(receiver, hz);
                }, Holds(memory, "mode", "frequencyHz"));
                // The filter the memory's mode came back with: what a rollback
                // must leave behind when it leaves that mode again.
                run.Reasserted = Read(receiver).Rx;
            }

            bool filter = run.Has("filterLowHz");
            if (filter)
                Forward(run, StepFilter, () => SetFilter(receiver, memory.FilterLowHz!.Value, memory.FilterHighHz!.Value, null),
                    Holds(memory, "filterLowHz", "filterHighHz"));

            if (run.Has("tuneStepHz"))
                Forward(run, StepTuneStep, () => _toolbar!.Save(stepHz: memory.TuneStepHz), Holds(memory, "tuneStepHz"));

            if (run.Has("rit.enabled"))
                Forward(run, StepRit, () => _radio.SetRit(memory.Rit!.Enabled, memory.Rit.OffsetHz),
                    Holds(memory, "rit.enabled", "rit.offsetHz"));

            if (run.Has("xit.enabled"))
                Forward(run, StepXit, () => _radio.SetXit(memory.Xit!.Enabled, memory.Xit.OffsetHz),
                    Holds(memory, "xit.enabled", "xit.offsetHz"));

            if (run.Has("split.enabled"))
            {
                if (memory.Split!.Enabled)
                {
                    // Never the TX dial an earlier split left behind: with no
                    // usable stored TX frequency the memory transmits on its
                    // own frequency.
                    long txHz = run.Has("split.txFrequencyHz") ? memory.Split.TxFrequencyHz!.Value : hz;
                    Forward(run, StepSplitTxFrequency, () => _radio.SetSplitFrequency(receiver, txHz),
                        reading => reading.Rx.SplitTxHz == txHz);
                }
                // After the TX dial, so it is in place before split turns on.
                Forward(run, StepSplit, () => _radio.SetSplit(receiver, memory.Split.Enabled), Holds(memory, "split.enabled"));
            }

            if (run.Has("fm.repeaterShift"))
                Forward(run, StepFm, () => _radio.RecallFmMemory(memory.Fm!, hz), Holds(memory, "fm.repeaterShift"));

            if (run.Has("squelch.enabled"))
                Forward(run, StepSquelch, () => _radio.SetSquelch(
                        (_radio.Snapshot().Squelch ?? new SquelchConfig()) with
                        {
                            Enabled = memory.Squelch!.Enabled,
                            Level = memory.Squelch.Level,
                        }),
                    Holds(memory, "squelch.enabled", "squelch.level"));

            // An FM deviation change re-derives the FM receive filter.
            if (filter && !Holds(memory, "filterLowHz", "filterHighHz")(Read(receiver)))
                Forward(run, StepFilterReassert, () => SetFilter(receiver, memory.FilterLowHz!.Value, memory.FilterHighHz!.Value, null),
                    Holds(memory, "filterLowHz", "filterHighHz"));

            Reading final = default;
            Forward(run, StepVerify, hasSetter: false, () =>
            {
                // Every applied field once more, from one snapshot.
                final = Read(receiver);
                if (!Holds(memory, [.. run.Plan])(final) || !run.Checks.All(check => check(final)))
                    throw new InvalidOperationException("A recalled value did not hold.");
            });
            if (ActiveKeySource() is { } late) throw KeySeen(run, StepVerify, late);
            return final;
        }
        catch (Exception ex) when (ex is not StationMemoryRecallRefusedException)
        {
            throw Failed(run, ex);
        }
    }

    private void Forward(Run run, string step, Action call, Func<Reading, bool> holds) =>
        Forward(run, step, hasSetter: true, call, holds);

    private void Forward(Run run, string step, bool hasSetter, Action call, Func<Reading, bool>? holds = null)
    {
        run.Current = step;
        var fault = Fault(step);
        // A key that arrives mid-recall stops it at once: every setter from
        // here on would act on a live transmitter. The first step follows the
        // check made on entry, and the last one checks after its own reading.
        if (step is not (StepSnapshot or StepVerify) && ActiveKeySource() is { } keySource)
            throw KeySeen(run, step, keySource);
        if (fault == RecallStepFault.Throw || (fault == RecallStepFault.SkipCall && !hasSetter))
            throw new InvalidOperationException($"Fault injected at the {step} step.");

        run.Ran.Add(step);
        if (fault != RecallStepFault.SkipCall) call();
        if (holds is null) return;
        // The setters signal refusal by leaving state unchanged, so a
        // mismatch on read-back is the failure signal.
        if (!holds(Read(run.Receiver)))
            throw new InvalidOperationException($"The station did not accept the {step} step.");
        run.Checks.Add(holds);
    }

    private RecallStepFault Fault(string step) => StepFaultForTests?.Invoke(step) ?? RecallStepFault.None;

    // ---- failure and rollback ----

    private StationMemoryRecallRefusedException Failed(Run run, Exception cause)
    {
        string step = run.Current;
        _log.LogWarning(cause, "memory.recall step failed id={Id} step={Step}", run.Memory.Id, step);
        if (ActiveKeySource() is { } keySource) return KeySeen(run, step, keySource);

        bool dropped = !_radio.IsConnected;
        bool restored;
        try
        {
            restored = Rollback(run);
        }
        catch (KeySeenDuringRollback seen)
        {
            return KeySeen(run, step, seen.KeySource);
        }

        var details = new StationMemoryErrorDetails(
            FailedStep: step,
            RollbackSucceeded: restored,
            AppliedFields: restored ? [] : AppliedNow(run),
            OperatorRecoveryRequired: !restored);
        string outcome = restored
            ? "The previous state was restored."
            : "The previous state could not be fully restored.";
        return dropped
            ? new StationMemoryRecallRefusedException(
                StatusCodes.Status503ServiceUnavailable,
                StationMemoryErrorCodes.StationUnavailable,
                $"The station disconnected during the recall. {outcome}",
                details)
            : new StationMemoryRecallRefusedException(
                StatusCodes.Status409Conflict,
                StationMemoryErrorCodes.RecallFailed,
                $"The recall failed at the {step} step. {outcome}",
                details);
    }

    // The radio is left between two states and the answer says so: nothing
    // more is sent, not even a rollback.
    private StationMemoryRecallRefusedException KeySeen(Run run, string step, string keySource) =>
        new(
            StatusCodes.Status409Conflict,
            StationMemoryErrorCodes.TransmitActive,
            "The station started transmitting during the recall. The recall stopped and nothing more was sent.",
            new StationMemoryErrorDetails(
                FailedStep: step,
                RollbackSucceeded: false,
                AppliedFields: AppliedNow(run),
                OperatorRecoveryRequired: true,
                KeySource: keySource));

    // The supported paths whose live value equals the memory, from one
    // read-only snapshot.
    private IReadOnlyList<string> AppliedNow(Run run)
    {
        var state = _radio.Snapshot();
        if (!StationMemoryReceiver.TryRead(state, run.Receiver, out var rx)) return [];
        var reading = new Reading(state, rx, _toolbar?.CurrentStepHz);
        return run.Plan.Where(path => Holds(run.Memory, path)(reading)).ToArray();
    }

    // True only when the pre-recall snapshot is back in force.
    private bool Rollback(Run run)
    {
        if (run.Before is not { } before
            || !StationMemoryReceiver.TryRead(before, run.Receiver, out var rxBefore))
            return true;   // the snapshot step never completed: nothing was sent
        int receiver = run.Receiver;

        // Phase A, while the receiver is still on the recalled frequency: RIT,
        // XIT and squelch enable are remembered per band and persisted on
        // every change, and a filter is remembered per mode, so the
        // destination band and the memory's mode get back what the band
        // transition left there before the dial returns.
        if (run.Arrival is { } arrival && StationMemoryReceiver.TryRead(arrival, receiver, out var rxArrival))
        {
            if (run.Ran.Contains(StepSquelch))
                Back(StepSquelch, () => _radio.SetSquelch(arrival.Squelch ?? new SquelchConfig()));
            if (run.Ran.Contains(StepXit))
                Back(StepXit, () => _radio.SetXit(arrival.XitEnabled, arrival.XitHz));
            if (run.Ran.Contains(StepRit))
                Back(StepRit, () => _radio.SetRit(arrival.RitEnabled, arrival.RitHz));
            // Before the mode is left: a mode keeps the filter it is left with.
            var filter = run.Reasserted ?? rxArrival;
            if (run.Ran.Contains(StepFilter) || run.Ran.Contains(StepFm) || run.Ran.Contains(StepFilterReassert))
                Back(StepFilter, () => SetFilter(receiver, filter.FilterLowHz, filter.FilterHighHz, filter.FilterPresetName));
            if (run.Ran.Contains(StepModeReassert))
                Back(StepMode, () => SetMode(receiver, rxArrival.Mode));
        }

        // Phase B: frequency first, so every later setter writes the original
        // band's memory; then once more for anything a later step disturbed.
        Restore(run, before, rxBefore, settle: false);
        Restore(run, before, rxBefore, settle: true);
        SettleDigitalDrive();
        return IsRestored(run, before, rxBefore);
    }

    // A change between a voice and a digital mode moves the drive on a
    // background task, once for the recalled mode and once more for the mode
    // the rollback put back. The verdict compares the drive, so it is read
    // only after the queued recalls have run; otherwise it would depend on
    // thread timing and could report a drive that returns a moment later as
    // not restored. Only waits: the rollback itself never writes a drive.
    private void SettleDigitalDrive()
    {
        try
        {
            _radio.PendingDigitalDriveRecall.Wait(DigitalDriveSettleBudget);
        }
        catch (AggregateException ex)
        {
            _log.LogWarning(ex, "memory.recall rollback could not wait for the drive recall");
        }
    }

    private void Restore(Run run, StateDto before, StationMemoryReceiver rxBefore, bool settle)
    {
        int receiver = run.Receiver;
        RecallStepFault? settleFault = null;
        void Do(string step, Action call)
        {
            if (!settle)
            {
                Back(step, call);
                return;
            }
            settleFault ??= Fault(RollbackPrefix + StepSettle);
            RequireNothingKeyed();
            if (settleFault == RecallStepFault.None) Attempt(StepSettle, call);
        }

        // The display centre belongs to the frequency: the operator dial
        // path keeps or moves it by its own rules, so it is put back after.
        void RestoreCentre()
        {
            if (receiver == 0 && _radio.Snapshot().RadioLoHz != before.RadioLoHz)
                _radio.SetRadioLo(before.RadioLoHz);
        }

        var now = Peek(receiver);
        if (settle ? now?.Rx.FrequencyHz != rxBefore.FrequencyHz : run.Ran.Contains(StepFrequency))
        {
            Do(StepFrequency, () =>
            {
                SetFrequency(receiver, rxBefore.FrequencyHz);
                RestoreCentre();
            });
        }
        else if (settle && receiver == 0 && now?.State.RadioLoHz != before.RadioLoHz)
        {
            Do(StepFrequency, RestoreCentre);
        }

        now = Peek(receiver);
        if (now?.Rx.Mode != rxBefore.Mode)
            Do(StepMode, () => SetMode(receiver, rxBefore.Mode));

        now = Peek(receiver);
        if (now?.Rx.FilterLowHz != rxBefore.FilterLowHz || now?.Rx.FilterHighHz != rxBefore.FilterHighHz)
            Do(StepFilter, () => SetFilter(receiver, rxBefore.FilterLowHz, rxBefore.FilterHighHz, rxBefore.FilterPresetName));

        if (_toolbar is not null && run.StepBefore is { } stepBefore && _toolbar.CurrentStepHz != stepBefore)
            Do(StepTuneStep, () => _toolbar.Save(stepHz: stepBefore));

        var state = _radio.Snapshot();
        if (state.RitEnabled != before.RitEnabled || state.RitHz != before.RitHz)
            Do(StepRit, () => _radio.SetRit(before.RitEnabled, before.RitHz));

        state = _radio.Snapshot();
        if (state.XitEnabled != before.XitEnabled || state.XitHz != before.XitHz)
            Do(StepXit, () => _radio.SetXit(before.XitEnabled, before.XitHz));

        now = Peek(receiver);
        if (now?.Rx.SplitEnabled != rxBefore.SplitEnabled || now?.Rx.SplitTxHz != rxBefore.SplitTxHz)
            Do(StepSplit, () =>
            {
                _radio.SetSplit(receiver, rxBefore.SplitEnabled);
                // SetSplit seeds a zero TX dial from the receiver frequency
                // even when turning split off, so the dial is always put back.
                _radio.SetSplitFrequency(receiver, rxBefore.SplitTxHz);
            });

        if (!SameFm(_radio.Snapshot().Fm, before.Fm))
            Do(StepFm, () => _radio.RestoreFmConfigForMemory(before.Fm ?? FmConfig.Default));

        if (!SameSquelch(_radio.Snapshot().Squelch, before.Squelch))
            Do(StepSquelch, () => _radio.SetSquelch(before.Squelch ?? new SquelchConfig()));
    }

    private void Back(string step, Action call)
    {
        var fault = Fault(RollbackPrefix + step);
        RequireNothingKeyed();
        if (fault == RecallStepFault.None) Attempt(step, call);
    }

    // A rollback step that throws is logged and the rollback continues; the
    // original failure is what the caller gets.
    private void Attempt(string step, Action call)
    {
        try
        {
            call();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "memory.recall rollback step failed step={Step}", step);
        }
    }

    private void RequireNothingKeyed()
    {
        if (ActiveKeySource() is { } keySource) throw new KeySeenDuringRollback(keySource);
    }

    private bool IsRestored(Run run, StateDto before, StationMemoryReceiver rxBefore)
    {
        var state = _radio.Snapshot();
        if (!StationMemoryReceiver.TryRead(state, run.Receiver, out var rx)) return false;
        return rx.FrequencyHz == rxBefore.FrequencyHz
            && rx.Mode == rxBefore.Mode
            && rx.FilterLowHz == rxBefore.FilterLowHz
            && rx.FilterHighHz == rxBefore.FilterHighHz
            && rx.SplitEnabled == rxBefore.SplitEnabled
            && rx.SplitTxHz == rxBefore.SplitTxHz
            && (run.Receiver != 0 || state.RadioLoHz == before.RadioLoHz)
            && state.RitEnabled == before.RitEnabled
            && state.RitHz == before.RitHz
            && state.XitEnabled == before.XitEnabled
            && state.XitHz == before.XitHz
            && SameFm(state.Fm, before.Fm)
            && SameSquelch(state.Squelch, before.Squelch)
            && _toolbar?.CurrentStepHz == run.StepBefore
            // A dial moved on another receiver is a failed rollback.
            && ReceiverDials(state).SequenceEqual(ReceiverDials(before))
            // Compared only: a band crossing recalls the destination band's
            // drive and zoom, and the rollback never writes either.
            && state.DrivePct == before.DrivePct
            && state.TunePct == before.TunePct
            && state.ZoomLevel == before.ZoomLevel;
    }

    private static IEnumerable<(int Index, long VfoHz)> ReceiverDials(StateDto state) =>
        (state.Receivers ?? []).Select(receiver => (receiver.Index, receiver.VfoHz));

    private static bool SameSquelch(SquelchConfig? a, SquelchConfig? b) =>
        (a ?? new SquelchConfig()) == (b ?? new SquelchConfig());

    // FmConfig carries its per-band offsets as a dictionary, which record
    // equality compares by reference.
    private static bool SameFm(FmConfig? a, FmConfig? b)
    {
        var left = (a ?? FmConfig.Default).Normalized();
        var right = (b ?? FmConfig.Default).Normalized();
        if (left with { RepeaterOffsetsHz = null } != right with { RepeaterOffsetsHz = null }) return false;
        var leftOffsets = left.RepeaterOffsetsHz ?? new Dictionary<string, long>();
        var rightOffsets = right.RepeaterOffsetsHz ?? new Dictionary<string, long>();
        return leftOffsets.Count == rightOffsets.Count
            && leftOffsets.All(entry => rightOffsets.TryGetValue(entry.Key, out long value) && value == entry.Value);
    }

    // ---- the keyed-source predicate ----

    // The first source that holds the key, or null. The transition lock the
    // recall holds does not stop radio PTT-IN, serial PTT, the firmware CW
    // keyer or PA calibration, so this is asked before every step.
    private string? ActiveKeySource()
    {
        if (_tx.IsTwoToneOn) return "twoTone";
        if (_tx.MoxOwner is { } moxOwner) return $"moxOwner:{moxOwner}";
        if (_tx.TunOwner is { } tunOwner) return $"tunOwner:{tunOwner}";
        if (_tx.IsMoxOn) return "mox";
        if (_tx.IsTunOn) return "tun";
        if (_radio.IsMox) return "wireMox";
        if (_radio.IsTxActive()) return "txActive";
        if (_externalPtt is { IsKeyed: true }) return "externalPtt";
        if (_externalPtt is { IsCwKeyDown: true }) return "cwKey";
        // Any in-flight transmit intent or transition, a remote-lease key in
        // progress included.
        if (!_tx.TryRunWithTransmitIdle(static () => { }, out _)) return "transition";
        if (_tx.IsPaCalibrationLeaseActive || _radio.IsPaCalibrationInvariantLeaseActive) return "paCalibration";
        return null;
    }

    // ---- receivers ----

    // The RX2 mode and filter setters act on RX1 while RX2 is off, so a
    // receiver that was closed mid-recall stops the step instead.
    private void RequireReceiver(int receiver)
    {
        if (receiver != 0) Read(receiver);
    }

    private void SetMode(int receiver, RxMode mode)
    {
        RequireReceiver(receiver);
        if (receiver == 0) _radio.SetMode(mode, TxVfo.A);
        else if (receiver == 1) _radio.SetMode(mode, TxVfo.B);
        else _radio.SetReceiver(receiver, mode: mode);
    }

    // RX1 stays on the operator path: it honours CTUN and applies the
    // destination band's transverter, RIT/XIT and squelch memory exactly as a
    // favorite recall does.
    private void SetFrequency(int receiver, long hz)
    {
        if (receiver == 0) _radio.SetVfo(hz);
        else if (receiver == 1) _radio.SetVfoB(hz);
        else _radio.SetReceiver(receiver, vfoHz: hz);
    }

    private void SetFilter(int receiver, int lowHz, int highHz, string? presetName)
    {
        RequireReceiver(receiver);
        if (receiver == 0) _radio.SetFilter(lowHz, highHz, presetName, TxVfo.A);
        else if (receiver == 1) _radio.SetFilter(lowHz, highHz, presetName, TxVfo.B);
        else _radio.SetReceiver(receiver, filterLowHz: lowHz, filterHighHz: highHz, filterPresetName: presetName);
    }

    private Reading? Peek(int receiver)
    {
        var state = _radio.Snapshot();
        return StationMemoryReceiver.TryRead(state, receiver, out var rx)
            ? new Reading(state, rx, _toolbar?.CurrentStepHz)
            : null;
    }

    private Reading Read(int receiver) =>
        Peek(receiver) ?? throw new InvalidOperationException($"Receiver {receiver + 1} is no longer enabled.");

    // True when every one of the paths holds the memory's value in a reading.
    private static Func<Reading, bool> Holds(StationMemoryDto memory, params string[] paths) =>
        reading => paths.All(path => path switch
        {
            "frequencyHz" => reading.Rx.FrequencyHz == memory.FrequencyHz,
            "mode" or "split.txMode" => reading.Rx.Mode.ToString() == memory.Mode,
            "filterLowHz" => reading.Rx.FilterLowHz == memory.FilterLowHz,
            "filterHighHz" => reading.Rx.FilterHighHz == memory.FilterHighHz,
            "tuneStepHz" => reading.StepHz == memory.TuneStepHz,
            "split.enabled" => reading.Rx.SplitEnabled == memory.Split?.Enabled,
            "split.txFrequencyHz" => reading.Rx.SplitTxHz == memory.Split?.TxFrequencyHz,
            "rit.enabled" => reading.State.RitEnabled == memory.Rit?.Enabled,
            "rit.offsetHz" => reading.State.RitHz == memory.Rit?.OffsetHz,
            "xit.enabled" => reading.State.XitEnabled == memory.Xit?.Enabled,
            "xit.offsetHz" => reading.State.XitHz == memory.Xit?.OffsetHz,
            "squelch.enabled" => (reading.State.Squelch ?? new SquelchConfig()).Enabled == memory.Squelch?.Enabled,
            "squelch.level" => (reading.State.Squelch ?? new SquelchConfig()).Level == memory.Squelch?.Level,
            _ => path.StartsWith("fm.", StringComparison.Ordinal)
                && memory.Fm is { } fm
                && FmMemories.Capture((reading.State.Fm ?? FmConfig.Default).Normalized(), memory.FrequencyHz)
                    == FmMemories.Normalize(fm, memory.FrequencyHz),
        });

    // ---- capture ----

    private static StationMemoryLiveState? Live(StateDto state, int receiverIndex, int? stepHz) =>
        StationMemoryReceiver.TryRead(state, receiverIndex, out var rx)
            ? ToLive(new Reading(state, rx, stepHz), receiverIndex)
            : null;

    private static StationMemoryLiveState ToLive(Reading reading, int receiverIndex)
    {
        var (state, rx, stepHz) = reading;
        // XIT and the FM configuration are station-wide and belong to the
        // transmit receiver, so they are captured only from it.
        bool transmitReceiver = receiverIndex == state.TxReceiverIndex;
        return new StationMemoryLiveState(
            receiverIndex,
            rx.FrequencyHz,
            rx.Mode.ToString(),
            rx.FilterLowHz,
            rx.FilterHighHz,
            stepHz,
            // Captured when off as well: split is not reset by a band change,
            // so a memory that says nothing would leave the transmitter on an
            // older split dial.
            Split: rx.SplitEnabled
                ? new StationMemorySplit(true, rx.SplitTxHz > 0 ? rx.SplitTxHz : null)
                : new StationMemorySplit(false),
            Rit: receiverIndex == 0 ? new StationMemoryOffset(state.RitEnabled, state.RitHz) : null,
            Xit: transmitReceiver ? new StationMemoryOffset(state.XitEnabled, state.XitHz) : null,
            Digital: null,
            RxAntenna: null,
            TxAntenna: null,
            Squelch: state.Squelch is { } squelch ? new StationMemorySquelch(squelch.Enabled, squelch.Level) : null,
            Fm: rx.Mode == RxMode.FM && transmitReceiver
                ? FmMemories.Capture((state.Fm ?? FmConfig.Default).Normalized(), rx.FrequencyHz)
                : null);
    }

    private static StationMemoryWrite Draft(StationMemoryLiveState live, StationMemoryCaptureRequest request)
    {
        // An extra receiver's edges are stored as given; a pair the library
        // would refuse is left out of the draft.
        bool filter = StationMemoryValidator.IsFilterPair(live.FilterLowHz, live.FilterHighHz);
        return new StationMemoryWrite(
            request.Name ?? DraftName(live.FrequencyHz),
            live.FrequencyHz,
            request.Group,
            request.Notes,
            live.Mode,
            filter ? live.FilterLowHz : null,
            filter ? live.FilterHighHz : null,
            live.TuneStepHz,
            Split: live.Split,
            Rit: live.Rit,
            Xit: live.Xit,
            Digital: live.Digital,
            RxAntenna: live.RxAntenna,
            TxAntenna: live.TxAntenna,
            Squelch: live.Squelch,
            Fm: live.Fm,
            CatChannel: request.CatChannel,
            ShortcutSlot: request.ShortcutSlot);
    }

    // The receiver fields come from the capture; name, group, notes and
    // assignments stay unless the request supplies them.
    private static StationMemoryWrite Overwrite(
        StationMemoryDto existing,
        StationMemoryWrite draft,
        StationMemoryCaptureRequest request) =>
        draft with
        {
            Name = request.Name ?? existing.Name,
            Group = request.Group ?? existing.Group,
            Notes = request.Notes ?? existing.Notes,
            ScanSkip = existing.ScanSkip,
            SourceMode = existing.SourceMode,
            CatChannel = request.CatChannel ?? existing.CatChannel,
            ShortcutSlot = request.ShortcutSlot ?? existing.ShortcutSlot,
        };

    // "20m 14200.0 kHz", the name the band stack suggests for a new entry.
    private static string DraftName(long frequencyHz)
    {
        string khz = (frequencyHz / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " kHz";
        return BandUtils.FreqToBand(frequencyHz) is { } band ? $"{band} {khz}" : khz;
    }

    // ---- refusals and the journal ----

    private SemaphoreSlim RecallGate() => _recallGates.GetOrAdd(StationKey, static _ => new SemaphoreSlim(1, 1));

    private static StationMemoryRecallRefusedException InProgress() =>
        new(StatusCodes.Status409Conflict, StationMemoryErrorCodes.RecallInProgress, "Another memory recall is in progress.");

    private static StationMemoryRecallRefusedException NotFound(string id) =>
        new(
            StatusCodes.Status404NotFound,
            StationMemoryErrorCodes.NotFound,
            $"No memory '{id}' exists.",
            new StationMemoryErrorDetails(Field: "memory"));

    private static StationMemoryRecallRefusedException RevisionConflict(StationMemoryDto memory) =>
        new(
            StatusCodes.Status409Conflict,
            StationMemoryErrorCodes.RevisionConflict,
            "The memory changed since it was read.",
            new StationMemoryErrorDetails(CurrentRevision: memory.Revision));

    private static StationMemoryRecallRefusedException Unavailable() =>
        new(
            StatusCodes.Status503ServiceUnavailable,
            StationMemoryErrorCodes.StationUnavailable,
            StationMemoryCapabilityResolver.NotConnected);

    private static StationMemoryRecallRefusedException Locked() =>
        new(
            StatusCodes.Status409Conflict,
            StationMemoryErrorCodes.StationLocked,
            "The VFO is locked. Unlock it to recall a memory.");

    private static StationMemoryRecallRefusedException Keyed(string keySource) =>
        new(
            StatusCodes.Status409Conflict,
            StationMemoryErrorCodes.TransmitActive,
            "The station is transmitting. Recall the memory when it is back in receive.",
            new StationMemoryErrorDetails(KeySource: keySource));

    private static StationMemoryRecallRefusedException CapabilityRefused(StationMemoryCapabilityReport report) =>
        new(
            StatusCodes.Status409Conflict,
            StationMemoryErrorCodes.CapabilityRefused,
            "This station cannot apply every field of the memory.",
            new StationMemoryErrorDetails(Report: report));

    /// <summary>The error code of a library exception, or null for anything else.</summary>
    internal static string? ErrorCode(Exception ex) => ex switch
    {
        StationMemoryRecallRefusedException refusal => refusal.Code,
        StationMemoryValidationException => StationMemoryErrorCodes.Validation,
        StationMemoryNotFoundException => StationMemoryErrorCodes.NotFound,
        StationMemoryConflictException conflict => conflict.Code,
        _ => null,
    };

    private void Journal(
        string operation,
        string origin,
        string? memoryId,
        int? revision,
        string result,
        string? code = null,
        string? failedStep = null,
        bool? rollbackSucceeded = null) =>
        MemoryOperationJournal.Write(
            _log,
            _history,
            new MemoryOperationEntry(
                _clock.GetUtcNow(),
                operation,
                origin,
                memoryId,
                revision,
                Generation: null,
                result,
                code,
                failedStep,
                rollbackSucceeded,
                Count: null));

    private readonly record struct Reading(StateDto State, StationMemoryReceiver Rx, int? StepHz);

    private sealed class Run(StationMemoryDto memory, int receiver, RxMode mode, IReadOnlyList<string> plan)
    {
        private readonly HashSet<string> _planned = new(plan, StringComparer.Ordinal);

        public StationMemoryDto Memory { get; } = memory;
        public int Receiver { get; } = receiver;
        public RxMode Mode { get; } = mode;
        /// <summary>The supported present paths, in canonical order.</summary>
        public IReadOnlyList<string> Plan { get; } = plan;
        public StateDto? Before { get; set; }
        public int? StepBefore { get; set; }
        public StateDto? Arrival { get; set; }
        public StationMemoryReceiver? Reasserted { get; set; }
        public string Current { get; set; } = StepSnapshot;
        /// <summary>The steps whose setter was attempted.</summary>
        public HashSet<string> Ran { get; } = new(StringComparer.Ordinal);
        /// <summary>The read-back of every step that passed, asked once more
        /// at the end.</summary>
        public List<Func<Reading, bool>> Checks { get; } = [];

        public bool Has(string path) => _planned.Contains(path);
    }

    private sealed class KeySeenDuringRollback(string keySource) : Exception
    {
        public string KeySource { get; } = keySource;
    }
}

/// <summary>Throw: the step throws before its setter. SkipCall: the setter is
/// not called, so the read-back mismatches exactly as it does when a setter
/// refuses by leaving state unchanged.</summary>
internal enum RecallStepFault
{
    None,
    Throw,
    SkipCall,
}

public sealed class StationMemoryRecallRefusedException(
    int status,
    string code,
    string message,
    StationMemoryErrorDetails? details = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public StationMemoryErrorDetails? Details { get; } = details;
}
