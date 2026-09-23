// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Globalization;
using Zeus.Contracts;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// GET /api/diagnostics/operator — read-only snapshot of the operator settings
/// that shape transmit and receive behaviour, plus the recent transmit
/// key/release history, for the Submit-an-Issue report. Issue #2374 could not
/// be diagnosed because the report carried no settings and its log held only
/// idle receive lines. Every value is read from state the engine already
/// holds; the route performs no radio I/O and mutates nothing.
/// </summary>
public static class EngineOperatorDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapEngineOperatorDiagnosticsEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/api/diagnostics/operator", (IServiceProvider services) =>
            Results.Ok(EngineOperatorDiagnosticsSnapshot.Capture(
                services.GetService<RadioService>(),
                services.GetService<TxService>(),
                services.GetService<DspPipelineService>(),
                services.GetService<DisplaySettingsStore>(),
                services.GetService<AudioDeviceSettingsStore>(),
                services.GetService<ExternalPttService>())));
        return endpoints;
    }
}

/// <summary>One human-readable setting. Labels and values are rendered by the
/// product as-is, so a new setting needs no product change.</summary>
internal sealed record EngineOperatorSetting(string Label, string Value);

internal sealed record EngineOperatorDiagnosticsSnapshot(
    bool Available,
    string? Reason,
    IReadOnlyList<EngineOperatorSetting> Settings,
    IReadOnlyList<TransmitHistoryEntry> TransmitHistory)
{
    internal static EngineOperatorDiagnosticsSnapshot Capture(
        RadioService? radio,
        TxService? tx,
        DspPipelineService? pipeline,
        DisplaySettingsStore? display,
        AudioDeviceSettingsStore? audioDevices,
        ExternalPttService? externalPtt)
    {
        var history = tx?.History.Snapshot() ?? [];
        if (radio is null)
        {
            return new EngineOperatorDiagnosticsSnapshot(
                Available: false,
                Reason: "RadioService is not registered in this engine host.",
                Settings: [],
                TransmitHistory: history);
        }

        var settings = new List<EngineOperatorSetting>(40);
        void Add(string label, string value) => settings.Add(new(label, value));
        void AddOptional(string label, Func<string> read)
        {
            // A settings store that fails to read must not cost the operator
            // the rest of the report.
            try { Add(label, read()); }
            catch (Exception ex) { Add(label, $"not available ({ex.GetType().Name})"); }
        }

        var state = radio.Snapshot();
        int txDspRateHz = DspPipelineService.ResolveTxDspRateHz(pipeline?.CurrentEngine);

        Add("Mode", state.Mode.ToString());
        Add("VFO", Hz(state.VfoHz));
        Add("Split", state.SplitEnabled ? $"on, TX {Hz(state.SplitTxHz)}" : "off");
        Add("RX filter", $"{Int(state.FilterLowHz)} to {Int(state.FilterHighHz)} Hz");
        Add("RX filter taps", FilterTaps(state.RxFilterWindow, state.RxFilterPhase, 48_000));
        Add("TX filter", $"{Int(state.TxFilterLowHz)} to {Int(state.TxFilterHighHz)} Hz");
        Add("TX filter taps", FilterTaps(state.TxFilterWindow, state.TxFilterPhase, txDspRateHz));
        Add("TX DSP rate", $"{Int(txDspRateHz)} Hz");
        Add("AGC", Agc(state));
        Add("Drive", $"{Int(state.DrivePct)}% (max {Int(state.DriveMaxPct)}%, tune {Int(state.TunePct)}%)");
        Add("TX pre-key delay", $"{Int(state.TxMoxPreKeyDelayMs)} ms");
        Add("TX tail delay", $"{Int(state.TxMoxTailDelayMs)} ms");
        Add("Post-TX RX mute", $"{Int(state.TxPostTxRxMuteDelayMs)} ms");
        Add("TX timeout", state.TxTimeoutSec > 0 ? $"{Int(state.TxTimeoutSec)} s" : "off");
        Add("TX audio source", state.TxAudioSource.ToString());
        Add("Mic gain", $"{Int(state.MicGainDb)} dB");
        Add("TX monitor", OnOff(state.TxMonitorEnabled));
        Add("Roger beep", OnOff(state.RogerBeepEnabled));
        Add("CFC", OnOff(state.Cfc?.Enabled == true));
        Add("TX leveler", OnOff(state.TxLeveling?.LevelerEnabled == true));
        // Read-only view of PureSignal state already published in StateDto.
        Add("PureSignal", state.PsEnabled
            ? $"armed ({(state.PsAuto ? "auto" : "single")}, MOX delay {D(state.PsMoxDelaySec)} s, correcting {OnOff(state.PsCorrecting)})"
            : "not armed");
        Add("Two-tone", state.TwoToneEnabled
            ? $"on ({D(state.TwoToneFreq1)} / {D(state.TwoToneFreq2)} Hz)"
            : "off");
        if (display is not null)
        {
            AddOptional("Display FFT size", () =>
            {
                var sizes = display.Get();
                return $"RX {FftSize(sizes.RxDisplayFftSize)}, TX {FftSize(sizes.TxDisplayFftSize)}";
            });
        }
        if (audioDevices is not null)
        {
            AddOptional("Audio devices", () =>
            {
                var devices = audioDevices.Get();
                return Redaction.Scrub(
                    $"backend {devices.Backend}, input {DeviceOrDefault(devices.InputDeviceId)}, output {DeviceOrDefault(devices.OutputDeviceId)}");
            });
        }
        if (externalPtt is not null)
        {
            AddOptional("PTT input", () =>
            {
                var ptt = externalPtt.Snapshot();
                return ptt.Available
                    ? $"available ({ptt.Protocol}), hang {Int(ptt.HangTimeMs)} ms"
                    : "not available";
            });
        }

        return new EngineOperatorDiagnosticsSnapshot(
            Available: true,
            Reason: null,
            Settings: settings,
            TransmitHistory: history);
    }

    /// <summary>Tap count plus, for linear phase, the exact single-FIR group
    /// delay (taps − 1) / (2 × rate). A large linear FIR holds several
    /// seconds of audio in flight, which is what an operator feels as a slow
    /// unkey.</summary>
    internal static string FilterTaps(BandpassWindow window, FilterPhaseMode phase, int dspRateHz)
    {
        int taps = DspPipelineService.ResolveFilterTapCount(window);
        if (phase != FilterPhaseMode.Linear)
            return $"{Int(taps)}, minimum phase";
        double delayMs = (taps - 1.0) / (2.0 * dspRateHz) * 1000.0;
        return $"{Int(taps)}, linear phase ({delayMs.ToString("0.#", CultureInfo.InvariantCulture)} ms per FIR)";
    }

    private static string Agc(StateDto state)
    {
        var mode = state.Agc?.Mode.ToString() ?? "default";
        var auto = state.AutoAgcEnabled ? ", auto" : string.Empty;
        return $"{mode}, top {D(state.AgcTopDb)} dB{auto}";
    }

    private static string FftSize(int? size) => size is { } value ? Int(value) : "default";

    private static string DeviceOrDefault(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "system default" : id;

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string Hz(long hz) => $"{hz.ToString(CultureInfo.InvariantCulture)} Hz";

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string D(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
