// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Applies persisted display settings before any connection worker can open a
/// DSP channel. Registered by both the product host and the standalone station
/// engine so the DSP-applied subset (TX display calibration, wideband enable,
/// frame-rate cap, decimation, waterfall period) never silently runs at
/// defaults until the operator edits display settings.
/// </summary>
public sealed class DisplaySettingsApplyService : IHostedService
{
    private readonly Func<DisplaySettingsDto> _read;
    private readonly Action<DisplaySettingsDto> _apply;

    public DisplaySettingsApplyService(
        DisplaySettingsStore store,
        DspPipelineService pipeline)
        : this(store.Get, pipeline.ApplyDisplaySettings)
    {
    }

    internal DisplaySettingsApplyService(
        Func<DisplaySettingsDto> read,
        Action<DisplaySettingsDto> apply)
    {
        _read = read;
        _apply = apply;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _apply(_read());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
