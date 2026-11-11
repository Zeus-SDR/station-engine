// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// What the connected station can apply from a memory. A stored field is not
/// proof the radio supports it: every recall is judged against this resolver
/// first, and anything it cannot vouch for is reported, never dropped.
/// </summary>
public static class StationMemoryCapabilityResolver
{
    internal const long MaxOffsetHz = 99_999;   // RIT / XIT range of RadioService

    internal const string NotConnected = "The station is not connected.";
    private const string FrequencyOutOfRange =
        "The frequency is outside the radio's range and the enabled transverter profiles.";
    private const string NoMode = "This memory has no Zeus mode yet. Choose a mode before recalling it.";
    private const string NoFreeDv = "FreeDV is not available on this station.";
    private const string FilterTooNarrow = "The filter is narrower than the radio allows.";
    private const string StepTooLarge = "The tuning step is larger than the toolbar accepts.";
    private const string NoStep = "The tuning step cannot be set on this station.";
    private const string SplitOutOfRange = "The split transmit frequency is outside the radio's range.";
    private const string SplitTxMode =
        "Zeus transmits in the receive mode; a separate split transmit mode cannot be applied.";
    private const string RitReceiver = "RIT applies to receiver 1 only.";
    private const string RitRange = "The RIT offset is beyond 99999 Hz.";
    private const string XitReceiver = "XIT applies to the transmit receiver only.";
    private const string XitRange = "The XIT offset is beyond 99999 Hz.";
    private const string NoDigital = "Digital offsets are not available on this station.";
    private const string NoAntenna =
        "Antennas are chosen per band on this station and are not changed by a memory.";
    private const string FmReceiver = "FM repeater settings apply to the transmit receiver only.";

    private static readonly string[] FmPaths =
    [
        "fm.repeaterShift",
        "fm.repeaterReverse",
        "fm.repeaterOffsetHz",
        "fm.deviationHz",
        "fm.ctcssEnabled",
        "fm.ctcssToneHz",
        "fm.rxCtcssToneHz",
        "fm.dcsTxEnabled",
        "fm.dcsCode",
        "fm.rxDcsCode",
        "fm.dcsInverted",
        "fm.rxToneSquelch",
    ];

    /// <summary>The canonical leaf paths, in the order every field list of a
    /// response is sorted in.</summary>
    public static readonly IReadOnlyList<string> FieldPaths =
    [
        "frequencyHz",
        "mode",
        "filterLowHz",
        "filterHighHz",
        "tuneStepHz",
        "split.enabled",
        "split.txFrequencyHz",
        "split.txMode",
        "rit.enabled",
        "rit.offsetHz",
        "xit.enabled",
        "xit.offsetHz",
        "digital.rttyMarkHz",
        "digital.rttyShiftHz",
        "digital.diglOffsetHz",
        "digital.diguOffsetHz",
        "rxAntenna",
        "txAntenna",
        "squelch.enabled",
        "squelch.level",
        .. FmPaths,
    ];

    private static readonly Dictionary<string, int> PathOrder = FieldPaths
        .Select((path, index) => (path, index))
        .ToDictionary(entry => entry.path, entry => entry.index, StringComparer.Ordinal);

    /// <summary>The paths a memory holds a value for, in canonical order.</summary>
    public static IReadOnlyList<string> PresentFields(StationMemoryDto memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var present = new List<string>(FieldPaths.Count) { "frequencyHz" };
        if (memory.Mode is not null) present.Add("mode");
        if (memory.FilterLowHz is not null) present.Add("filterLowHz");
        if (memory.FilterHighHz is not null) present.Add("filterHighHz");
        if (memory.TuneStepHz is not null) present.Add("tuneStepHz");
        if (memory.Split is { } split)
        {
            present.Add("split.enabled");
            // With split off the other two values are informational: neither
            // applied nor judged.
            if (split.Enabled && split.TxFrequencyHz is not null) present.Add("split.txFrequencyHz");
            if (split.Enabled && split.TxMode is not null) present.Add("split.txMode");
        }
        if (memory.Rit is not null) present.AddRange(["rit.enabled", "rit.offsetHz"]);
        if (memory.Xit is not null) present.AddRange(["xit.enabled", "xit.offsetHz"]);
        if (memory.Digital is { } digital)
        {
            if (digital.RttyMarkHz is not null) present.Add("digital.rttyMarkHz");
            if (digital.RttyShiftHz is not null) present.Add("digital.rttyShiftHz");
            if (digital.DiglOffsetHz is not null) present.Add("digital.diglOffsetHz");
            if (digital.DiguOffsetHz is not null) present.Add("digital.diguOffsetHz");
        }
        if (memory.RxAntenna is not null) present.Add("rxAntenna");
        if (memory.TxAntenna is not null) present.Add("txAntenna");
        if (memory.Squelch is not null) present.AddRange(["squelch.enabled", "squelch.level"]);
        // An fm object on a memory of another mode is stored but inert.
        if (memory.Fm is { } fm && IsFm(memory))
        {
            foreach (var path in FmPaths)
            {
                if (path == "fm.rxCtcssToneHz" && fm.RxCtcssToneHz is null) continue;
                if (path == "fm.rxDcsCode" && fm.RxDcsCode is null) continue;
                present.Add(path);
            }
        }
        return present;
    }

    public static StationMemoryCapabilities Resolve(
        RadioService? radio,
        ToolbarSettingsStore? toolbar,
        IAudioModemPort? modem)
    {
        bool connected = radio?.IsConnected == true;
        var live = connected ? MemoryCapabilityState.Supported : MemoryCapabilityState.Unknown;
        long maxHz = radio?.ReceiveOnly == true
            ? StationReceiveOnlyPolicy.MaximumFrequencyHz
            : TransverterFrequencyConverter.MaximumRadioFrequencyHz;
        var settings = connected ? radio!.ActiveTransverterSettingsForMemory : null;
        StationMemoryFrequencySpan[] extra = settings is { Enabled: true, Bands: { } bands }
            ? bands
                .Where(band => band.Enabled)
                .Select(band => new StationMemoryFrequencySpan(band.BeginFrequencyHz, band.EndFrequencyHz))
                .ToArray()
            : [];

        return new StationMemoryCapabilities(
            Available: connected,
            ReasonUnavailable: connected ? null : NotConnected,
            NativeApply: MemoryCapabilityState.Unsupported,
            RadioRead: MemoryCapabilityState.Unsupported,
            RadioWrite: MemoryCapabilityState.Unsupported,
            FrequencyRange: new StationMemoryFrequencyRange(
                live,
                TransverterFrequencyConverter.MinimumRadioFrequencyHz,
                maxHz,
                extra),
            SupportedModes: SupportedModes(modem),
            Filter: live,
            Split: live,
            Rit: live,
            Xit: live,
            AntennaSelection: MemoryCapabilityState.Unsupported,
            FmRepeater: live,
            FmDcs: live,
            FmDeviation: live,
            DigitalOffsets: MemoryCapabilityState.Unsupported,
            Squelch: live,
            TuneStep: !connected
                ? MemoryCapabilityState.Unknown
                : toolbar is null ? MemoryCapabilityState.Unsupported : MemoryCapabilityState.Supported,
            ReceiverSelection: live);
    }

    /// <summary>Every present path of <paramref name="memory"/> lands in
    /// exactly one of the two field lists. Unknown fails closed.</summary>
    public static StationMemoryCapabilityReport Report(
        StationMemoryDto memory,
        int receiverIndex,
        RadioService radio,
        ToolbarSettingsStore? toolbar,
        IAudioModemPort? modem)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(radio);
        return Report(memory, receiverIndex, radio, radio.Snapshot(), toolbar, modem);
    }

    internal static StationMemoryCapabilityReport Report(
        StationMemoryDto memory,
        int receiverIndex,
        RadioService radio,
        StateDto state,
        ToolbarSettingsStore? toolbar,
        IAudioModemPort? modem)
    {
        var present = PresentFields(memory);
        var supported = new List<string>(present.Count);
        var unsupported = new List<string>();
        var reasons = new List<string>();
        void Reason(string reason)
        {
            if (!reasons.Contains(reason)) reasons.Add(reason);
        }

        bool connected = radio.IsConnected;
        if (!connected) Reason(NotConnected);
        bool receiverValid = StationMemoryReceiver.TryRead(state, receiverIndex, out _);
        if (!receiverValid)
            Reason($"Receiver {receiverIndex + 1} is not enabled.");
        if (memory.Mode is null) Reason(NoMode);

        foreach (var path in present)
        {
            // Capture writes RIT and XIT even when they are off, so every
            // memory saved on one receiver carries them. Off at 0 Hz there is
            // nothing to apply, and on a receiver the offset does not belong
            // to that is not something to ask the operator about.
            if (receiverValid && IdleOffsetOfAnotherReceiver(path, memory, receiverIndex, state)) continue;
            // Nothing can be vouched for on a receiver that is not there.
            var refusal = receiverValid ? Refusal(path, memory, receiverIndex, radio, state, toolbar, modem) : string.Empty;
            if (refusal is null)
            {
                supported.Add(path);
                continue;
            }
            unsupported.Add(path);
            if (refusal.Length > 0) Reason(refusal);
        }

        bool usable = connected && receiverValid;
        return new StationMemoryCapabilityReport(
            CanRecall: usable && memory.Mode is not null && unsupported.Count == 0,
            SupportedFields: supported,
            UnsupportedFields: unsupported,
            Reasons: reasons,
            CanApplyPartial: usable
                && unsupported.Count > 0
                && supported.Contains("frequencyHz")
                && supported.Contains("mode"));
    }

    private static bool IdleOffsetOfAnotherReceiver(string path, StationMemoryDto memory, int receiverIndex, StateDto state) =>
        path switch
        {
            "rit.enabled" or "rit.offsetHz" => receiverIndex != 0 && memory.Rit is { Enabled: false, OffsetHz: 0 },
            "xit.enabled" or "xit.offsetHz" =>
                receiverIndex != state.TxReceiverIndex && memory.Xit is { Enabled: false, OffsetHz: 0 },
            _ => false,
        };

    /// <summary>Sorted in canonical order with duplicates removed. A path that
    /// is not canonical sorts last, so it can never match a report.</summary>
    internal static IReadOnlyList<string> Canonical(IEnumerable<string>? paths) =>
        (paths ?? [])
            .Where(path => path is not null)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => PathOrder.GetValueOrDefault(path, int.MaxValue))
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

    internal static bool IsFm(StationMemoryDto memory) =>
        string.Equals(memory.Mode, nameof(RxMode.FM), StringComparison.Ordinal);

    private static IReadOnlyList<string> SupportedModes(IAudioModemPort? modem) =>
        modem?.Available == true
            ? StationMemoryValidator.ModeNames
            : StationMemoryValidator.ModeNames
                .Where(name => name != nameof(RxMode.FreeDv))
                .ToArray();

    // Null: supported. Otherwise the reason the path cannot be applied.
    private static string? Refusal(
        string path,
        StationMemoryDto memory,
        int receiverIndex,
        RadioService radio,
        StateDto state,
        ToolbarSettingsStore? toolbar,
        IAudioModemPort? modem)
    {
        switch (path)
        {
            case "frequencyHz":
                return radio.IsExternalFrequencyAvailable(memory.FrequencyHz) ? null : FrequencyOutOfRange;
            case "mode":
                if (!StationMemoryValidator.IsKnownMode(memory.Mode)) return NoMode;
                return memory.Mode == nameof(RxMode.FreeDv) && modem?.Available != true ? NoFreeDv : null;
            case "filterLowHz" or "filterHighHz":
                return memory.FilterLowHz is { } low && memory.FilterHighHz is { } high
                    && (long)high - low >= RadioService.MinFilterWidthHz
                        ? null
                        : FilterTooNarrow;
            case "tuneStepHz":
                if (toolbar is null) return NoStep;
                return memory.TuneStepHz is >= 1 and <= ToolbarSettingsStore.MaxStepHz ? null : StepTooLarge;
            case "split.enabled":
                return null;
            case "split.txFrequencyHz":
                return radio.IsExternalFrequencyAvailable(memory.Split!.TxFrequencyHz!.Value) ? null : SplitOutOfRange;
            case "split.txMode":
                return string.Equals(memory.Split!.TxMode, memory.Mode, StringComparison.Ordinal) ? null : SplitTxMode;
            case "rit.enabled" or "rit.offsetHz":
                if (receiverIndex != 0) return RitReceiver;
                return Math.Abs(memory.Rit!.OffsetHz) <= MaxOffsetHz ? null : RitRange;
            case "xit.enabled" or "xit.offsetHz":
                if (receiverIndex != state.TxReceiverIndex) return XitReceiver;
                return Math.Abs(memory.Xit!.OffsetHz) <= MaxOffsetHz ? null : XitRange;
            case "rxAntenna" or "txAntenna":
                return NoAntenna;
            case "squelch.enabled" or "squelch.level":
                return null;
            default:
                if (path.StartsWith("digital.", StringComparison.Ordinal)) return NoDigital;
                // XIT and the FM configuration are station-wide: recalled onto
                // another receiver they would change the transmitter behind
                // the operator's back.
                if (path.StartsWith("fm.", StringComparison.Ordinal))
                    return receiverIndex == state.TxReceiverIndex ? null : FmReceiver;
                return string.Empty;
        }
    }
}

/// <summary>One receiver's memory-relevant values out of a single state
/// snapshot: RX1 from the flat fields, every other receiver from its
/// projected entry.</summary>
internal readonly record struct StationMemoryReceiver(
    long FrequencyHz,
    RxMode Mode,
    int FilterLowHz,
    int FilterHighHz,
    string? FilterPresetName,
    bool SplitEnabled,
    long SplitTxHz)
{
    /// <summary>False when the receiver is not an enabled hardware receiver
    /// of this station.</summary>
    internal static bool TryRead(StateDto state, int index, out StationMemoryReceiver receiver)
    {
        if (index == 0)
        {
            receiver = new StationMemoryReceiver(
                state.VfoHz,
                state.Mode,
                state.FilterLowHz,
                state.FilterHighHz,
                state.FilterPresetName,
                state.SplitEnabled,
                state.SplitTxHz);
            return true;
        }

        receiver = default;
        if (index < 0 || index >= state.MaxReceivers || state.Receivers is not { } receivers)
            return false;
        foreach (var entry in receivers)
        {
            if (entry.Index != index || !entry.Enabled) continue;
            receiver = new StationMemoryReceiver(
                entry.VfoHz,
                entry.Mode,
                entry.FilterLowHz,
                entry.FilterHighHz,
                entry.FilterPresetName,
                entry.SplitEnabled,
                entry.TxVfoHz);
            return true;
        }
        return false;
    }
}
