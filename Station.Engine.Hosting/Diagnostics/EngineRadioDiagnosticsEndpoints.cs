// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Read-only snapshot of radio identity already held by the station engine.
/// This route performs no discovery, radio I/O, or state mutation.
/// </summary>
public static class EngineRadioDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapEngineRadioDiagnosticsEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/api/diagnostics/radio", (IServiceProvider services) =>
            Results.Ok(EngineRadioDiagnosticsSnapshot.Capture(
                services.GetService<RadioService>(),
                services.GetService<PreferredRadioStore>())));
        return endpoints;
    }
}

internal sealed record EngineRadioCapabilitiesSnapshot(
    int RxAdcCount,
    bool MkiiBpf,
    bool HasVolts,
    int MaxPowerWatts);

internal sealed record EngineRadioCalibrationSnapshot(
    double BridgeVolt,
    double RefVoltage,
    int AdcCalOffset,
    double MaxWatts);

internal sealed record EngineRadioDiagnosticsSnapshot(
    bool Available,
    string? Reason,
    bool Connected,
    string DiscoveredBoardKind,
    string ConnectedBoardKind,
    string EffectiveBoardKind,
    string OrionMkIIVariant,
    string? PreferredBoard,
    bool? OverrideDetection,
    string Protocol,
    int SampleRate,
    int ReceiverCount,
    int MaxReceivers,
    EngineRadioCapabilitiesSnapshot Capabilities,
    int PaDefaultMaxPowerWatts,
    EngineRadioCalibrationSnapshot Calibration,
    string? Firmware)
{
    internal static object Capture(
        RadioService? radio,
        PreferredRadioStore? preferredRadioStore)
    {
        if (radio is null)
        {
            return new
            {
                available = false,
                reason = "RadioService is not registered in this engine host.",
            };
        }

        var state = radio.Snapshot();
        var connectedBoard = radio.ConnectedBoardKind;
        var effectiveBoard = radio.EffectiveBoardKind;
        var variant = radio.EffectiveOrionMkIIVariant;
        var capabilities = BoardCapabilitiesTable.For(effectiveBoard, variant);
        var calibration = RadioCalibrations.For(effectiveBoard, variant);
        return new EngineRadioDiagnosticsSnapshot(
            Available: true,
            Reason: null,
            Connected: radio.IsConnected,
            DiscoveredBoardKind: radio.DiscoveredBoardKind.ToString(),
            ConnectedBoardKind: connectedBoard.ToString(),
            EffectiveBoardKind: effectiveBoard.ToString(),
            OrionMkIIVariant: variant.ToString(),
            PreferredBoard: preferredRadioStore?.Get()?.ToString(),
            OverrideDetection: preferredRadioStore?.GetOverrideDetection(),
            Protocol: state.ConnectedProtocol switch
            {
                "P1" => "Protocol 1",
                "P2" => "Protocol 2",
                "P3" => "Protocol 3",
                _ => "none",
            },
            SampleRate: state.SampleRate,
            ReceiverCount: state.Receivers?.Count ?? 0,
            MaxReceivers: state.MaxReceivers,
            Capabilities: new EngineRadioCapabilitiesSnapshot(
                capabilities.RxAdcCount,
                capabilities.MkiiBpf,
                capabilities.HasVolts,
                capabilities.MaxPowerWatts),
            PaDefaultMaxPowerWatts: PaDefaults.GetMaxPowerWatts(
                effectiveBoard,
                variant),
            Calibration: new EngineRadioCalibrationSnapshot(
                calibration.BridgeVolt,
                calibration.RefVoltage,
                calibration.AdcCalOffset,
                calibration.MaxWatts),
            Firmware: radio.ConnectedFirmware);
    }
}
