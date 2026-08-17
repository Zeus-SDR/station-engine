// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Hosting-only zoom request. <see cref="CenterHz"/> is optional so existing
/// callers that send only <c>{ "level": n }</c> retain identical behavior.
/// </summary>
public sealed record SpectralZoomSetRequest(int Level, long? CenterHz = null);

/// <summary>Maps engine-owned receiver configuration and tuning routes.</summary>
public static class ReceiverControlEndpoints
{
    public static IEndpointRouteBuilder MapReceiverConfigurationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // Configure any receiver by index for full multi-DDC (RX1=0, RX2=1,
        // RX3+=2..). The reserved external slot is routed through the product-
        // neutral control port; standalone binds that port to a no-op.
        endpoints.MapPost("/api/receivers/{index:int}", (
            int index,
            ReceiverSetRequest req,
            RadioService radio,
            IExternalReceiverControlPort external) =>
        {
            if (index < 0 || index > WireContract.KiwiReceiverIndex)
                return Results.BadRequest(new
                {
                    error = $"receiver index out of range (0..{WireContract.KiwiReceiverIndex})",
                });

            if (index == WireContract.KiwiReceiverIndex)
            {
                log.LogInformation(
                    "api.receivers.kiwi vfoHz={VfoHz} mode={Mode} low={Low} high={High}",
                    req.VfoHz,
                    req.Mode,
                    req.FilterLowHz,
                    req.FilterHighHz);
                external.SetTuning(
                    req.VfoHz,
                    req.Mode,
                    req.FilterLowHz,
                    req.FilterHighHz,
                    radio.Snapshot().CtunEnabled);
                return Results.Ok(radio.Snapshot());
            }

            log.LogInformation(
                "api.receivers index={Index} enabled={Enabled} vfoHz={VfoHz} adc={Adc} mode={Mode}",
                index,
                req.Enabled,
                req.VfoHz,
                req.AdcSource,
                req.Mode);
            return Results.Ok(radio.SetReceiver(
                index,
                enabled: req.Enabled,
                vfoHz: req.VfoHz,
                adcSource: req.AdcSource,
                mode: req.Mode,
                filterLowHz: req.FilterLowHz,
                filterHighHz: req.FilterHighHz,
                afGainDb: req.AfGainDb,
                filterPresetName: req.FilterPresetName,
                zoomLevel: req.ZoomLevel));
        });

        endpoints.MapPost("/api/receivers/{index:int}/mute", (
            int index,
            MuteSetRequest req,
            RadioService radio,
            IExternalReceiverControlPort external) =>
        {
            if (index < 0 || index > WireContract.KiwiReceiverIndex)
                return Results.BadRequest(new
                {
                    error = $"receiver index out of range (0..{WireContract.KiwiReceiverIndex})",
                });

            log.LogInformation(
                "api.receivers.mute index={Index} muted={Muted}",
                index,
                req.Muted);
            if (index == WireContract.KiwiReceiverIndex)
            {
                external.SetMuted(req.Muted);
                return Results.Ok(radio.Snapshot());
            }

            return Results.Ok(radio.SetReceiverMuted(index, req.Muted));
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapReceiverLoEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/receivers/{index:int}/lo", (
            int index,
            RadioLoSetRequest req,
            RadioService radio,
            DspPipelineService pipeline,
            IExternalReceiverControlPort external) =>
        {
            if (index < 0 || index > WireContract.KiwiReceiverIndex)
                return Results.BadRequest(new
                {
                    error = $"receiver index out of range (0..{WireContract.KiwiReceiverIndex})",
                });
            bool validFrequency = index == WireContract.KiwiReceiverIndex
                ? req.Hz is >= 0 and <= TransverterFrequencyConverter.MaximumRadioFrequencyHz
                : radio.IsExternalFrequencyAvailable(req.Hz, allowZero: true);
            if (!validFrequency)
            {
                log.LogInformation(
                    "api.receivers.lo rejected index={Index} hz={Hz}",
                    index,
                    req.Hz);
                return Results.BadRequest(new { error = "hz is outside the receiver's available range" });
            }

            log.LogInformation("api.receivers.lo index={Index} hz={Hz}", index, req.Hz);
            if (index == WireContract.KiwiReceiverIndex)
            {
                external.SetCenter(req.Hz);
                return Results.Ok(radio.Snapshot());
            }
            if (index <= 0)
                return Results.Ok(radio.SetRadioLo(req.Hz));

            pipeline.RequestSecondaryLo(index, req.Hz);
            return Results.Ok(radio.Snapshot());
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapModeEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/mode", (
            ModeSetRequest req,
            RadioService radio,
            IAudioModemPort modem) =>
        {
            if (req.Receiver is not (0 or 1))
                return Results.BadRequest(new { error = $"unknown receiver {req.Receiver}" });
            if (req.Mode == RxMode.FreeDv && !modem.Available)
                return Results.Conflict(new
                {
                    error = "FreeDV plugin is not installed or active.",
                });

            var receiver = req.Receiver == 1 ? TxVfo.B : TxVfo.A;
            log.LogInformation(
                "api.mode mode={Mode} receiver={Receiver}",
                req.Mode,
                receiver);
            return Results.Ok(radio.SetMode(req.Mode, receiver));
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapSpectralZoomEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/rx/zoom", (
            SpectralZoomSetRequest req,
            RadioService radio,
            DspPipelineService pipeline,
            IExternalReceiverControlPort external) =>
        {
            log.LogInformation(
                "api.rx.zoom level={Level} centerHz={CenterHz}",
                req.Level,
                req.CenterHz);
            int maxZoom = radio.CurrentMaxDisplayZoomLevel;
            if (req.Level < RadioService.MinDisplayZoomLevel
                || req.Level > maxZoom)
            {
                return Results.BadRequest(new
                {
                    error =
                        $"zoom level must be in [{RadioService.MinDisplayZoomLevel},{maxZoom}]; got {req.Level}",
                });
            }

            if (req.CenterHz is < 0 or > 60_000_000)
            {
                return Results.BadRequest(new
                {
                    error = $"zoom center must be in [0,60000000] Hz; got {req.CenterHz}",
                });
            }

            return Results.Ok(pipeline.ApplyWidebandZoomRequest(req.Level, req.CenterHz));
        });

        return endpoints;
    }
}
