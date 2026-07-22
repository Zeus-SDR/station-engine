// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Dsp.Wdsp;
using Zeus.Protocol1;

namespace Zeus.Server;

public sealed record StationEngineHostingOptions(
    bool NativeAudioOutputEnabled = false);

/// <summary>Registers the complete standalone station-engine runtime.</summary>
public static class StationEngineHostingExtensions
{
    /// <summary>
    /// Adds engine-owned stores, services, stream transports, and hosted
    /// lifecycles. Product integration ports are always bound to their null
    /// implementations.
    /// </summary>
    public static IServiceCollection AddStationEngine(
        this IServiceCollection services,
        StationEngineHostingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new StationEngineHostingOptions();

        services.AddHttpClient();

        services.AddSingleton<StationEngineCapabilitiesService>();
        services.AddSingleton<IProductStreamSource>(_ => NullProductStreamSource.Instance);
        // Real (not null) sink: the SPA's clientErrorBeacon prefers the /ws
        // diagnostic frame, so a null sink would silently discard uncaught
        // webview errors exactly when the engine is the only API origin.
        services.AddSingleton<ClientLogRateLimiter>();
        services.AddSingleton<EngineClientDiagnosticSink>();
        services.AddSingleton<IClientDiagnosticSink>(sp =>
            sp.GetRequiredService<EngineClientDiagnosticSink>());
        services.AddSingleton<IAudioModemPort, NullAudioModemPort>();
        services.AddSingleton<ProductAudioRingPort>();
        services.AddSingleton<IProductTxAudioPort>(sp => sp.GetRequiredService<ProductAudioRingPort>());
        services.AddSingleton<ITxAudioPreviewProcessor, NullTxAudioPreviewProcessor>();
        services.AddSingleton<IExternalReceiverSource, NullExternalReceiverSource>();
        services.AddSingleton<IExternalReceiverControlPort, NullExternalReceiverControlPort>();
        services.AddSingleton<IExternalRxAudioSource, NullExternalRxAudioSource>();
        services.AddSingleton<IExternalRadioSidecar, NullExternalRadioSidecar>();
        services.AddSingleton<IInitialTxAudioConfigSource, NullInitialTxAudioConfigSource>();

        services.AddSingleton<DspSettingsStore>();
        services.AddSingleton<PaSettingsStore>();
        services.AddSingleton<FilterPresetStore>();
        services.AddSingleton<PreferredRadioStore>();
        services.AddSingleton(sp => new PsSettingsStore(
            sp.GetRequiredService<ILogger<PsSettingsStore>>(),
            PrefsDbPath.EngineGet()));
        services.AddSingleton<RadioStateStore>();
        // Workspace layouts: the store defaults to the product zeus-prefs.db
        // (where existing desktop installs keep their layouts), so the engine
        // must pass its own database explicitly — attach-session layouts live
        // in the engine-owned prefs DB like every other engine store.
        services.AddSingleton(sp => new LayoutStore(
            sp.GetRequiredService<ILogger<LayoutStore>>(),
            PrefsDbPath.EngineGet()));
        services.AddSingleton<CwSettingsStore>();
        services.AddSingleton<AntennaSettingsStore>();
        services.AddSingleton<AudioSettingsStore>();
        services.AddSingleton<Nr3ModelStore>();
        services.AddSingleton<CfcPresetStore>();
        services.AddSingleton<Hl2GpioSettingsStore>();
        services.AddSingleton<BandMemoryStore>();
        services.AddSingleton<BandStackStore>();
        services.AddSingleton<RfFilterSettingsStore>();
        services.AddSingleton<PttSettingsStore>();
        services.AddSingleton<AudioDeviceSettingsStore>();
        services.AddSingleton<RadioSpeakerSettingsStore>();
        services.AddSingleton<TxFidelityPolicyStore>();
        services.AddSingleton<BandPlanStore>();
        services.AddSingleton<BandPrefsStore>();
        services.AddSingleton<BandPlanService>();
        services.AddSingleton<IBandPlanService>(sp => sp.GetRequiredService<BandPlanService>());

        // Operator UI preference stores (theme / display / toolbar / NR
        // disclosure / operator identity / layout pins). These deliberately
        // keep their default PrefsDbPath.Get() product database — the same
        // file the product host has always used for these collections — so an
        // operator's saved look-and-feel follows them between the desktop host
        // and a standalone engine on the same machine, with no migration.
        services.AddSingleton<ThemeSettingsStore>();
        services.AddSingleton<DisplaySettingsStore>();
        services.AddSingleton<ToolbarSettingsStore>();
        services.AddSingleton<NrUiPrefsStore>();
        services.AddSingleton<BottomPinStore>();
        services.AddSingleton<PanWfSplitStore>();
        services.AddSingleton<OperatorIdentityStore>();

        services.AddSingleton<TxIqRing>();
        services.AddSingleton<ITxIqSource>(sp => sp.GetRequiredService<TxIqRing>());
        services.AddSingleton<RxAudioRing>();
        services.AddSingleton<IRxAudioSource>(sp => sp.GetRequiredService<RxAudioRing>());
        services.AddSingleton<RxAudioMuteState>();

        services.AddSingleton<StreamingHub>();
        services.AddSingleton<RadioService>();
        services.AddSingleton<RadioReclaimService>();
        services.AddSingleton<
            Zeus.Protocol1.Discovery.IRadioDiscovery,
            Zeus.Protocol1.Discovery.RadioDiscoveryService>();
        services.AddSingleton<
            Zeus.Protocol2.Discovery.IRadioDiscovery,
            Zeus.Protocol2.Discovery.RadioDiscoveryService>();
        services.AddSingleton<IRadioDiscoveryExtension, NullRadioDiscoveryExtension>();

        services.AddSingleton<RadioSpeakerAudioSink>();
        services.AddSingleton<SaturnSpeakerAudioSink>();
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<NativeAudioSink>(
            sp,
            options.NativeAudioOutputEnabled));
        services.AddSingleton<WebSocketAudioSink>();
        services.AddSingleton<IRxAudioSink>(sp => sp.GetRequiredService<RadioSpeakerAudioSink>());
        services.AddSingleton<IRxAudioSink>(sp => sp.GetRequiredService<SaturnSpeakerAudioSink>());
        services.AddSingleton<IRxAudioSink>(sp => sp.GetRequiredService<WebSocketAudioSink>());
        if (options.NativeAudioOutputEnabled)
        {
            services.AddSingleton<IRxAudioSink>(sp => sp.GetRequiredService<NativeAudioSink>());
        }
        services.AddSingleton<IPreviewAudioSink>(sp => sp.GetRequiredService<NativeAudioSink>());

        services.AddSingleton<WdspWisdomInitializer>();
        services.AddSingleton<WisdomBootstrapService>();
        services.AddSingleton<DspPipelineService>(sp =>
        {
            Func<TxAudioIngest?> txIngestFactory = () => sp.GetService<TxAudioIngest>();
            return ActivatorUtilities.CreateInstance<DspPipelineService>(sp, txIngestFactory);
        });
        services.AddSingleton<FrequencyCalibrationService>();
        services.AddSingleton<ImdMeasureService>();
        services.AddSingleton<TxService>();
        services.AddSingleton<TxAudioIngest>();
        services.AddSingleton<TxAudioIngestStartup>();
        services.AddSingleton<TxMicMeterService>();
        services.AddSingleton<SignalJammerTxSource>();
        services.AddSingleton<TxMetersService>();
        services.AddSingleton<TxTuneDriver>();
        services.AddSingleton<CwSidetoneSource>(sp =>
        {
            var source = new CwSidetoneSource();
            var settings = sp.GetRequiredService<CwSettingsStore>().Get();
            source.SetPitchHz(settings.SidetoneHz);
            source.SetGainDb(settings.SidetoneGainDb);
            return source;
        });
        services.AddSingleton<CwEngine>();
        services.AddSingleton<ExternalPttService>();
        services.AddSingleton<NativeMicCapture>();

        // Each hosted registration aliases the singleton used by endpoints
        // and other engine services, so there is exactly one instance of each.
        // Native sources start after the core pipeline and therefore stop
        // before it. This prevents an asynchronous device-open callback from
        // creating a preview DSP engine after shutdown has already begun.
        services.AddHostedService(sp => sp.GetRequiredService<SaturnSpeakerAudioSink>());
        services.AddHostedService(sp => sp.GetRequiredService<WisdomBootstrapService>());
        // Push persisted display settings into the DSP before DspPipelineService
        // (or any later connection worker) starts — mirrors the product host's
        // registration order so the DSP-applied subset (TX display calibration,
        // FFT/wideband/frame-rate/decimation/waterfall settings) is live from
        // boot instead of silently running at defaults until the first edit.
        services.AddHostedService<DisplaySettingsApplyService>();
        services.AddHostedService(sp => sp.GetRequiredService<DspPipelineService>());
        services.AddHostedService(sp => sp.GetRequiredService<TxAudioIngestStartup>());
        services.AddHostedService(sp => sp.GetRequiredService<TxMicMeterService>());
        services.AddHostedService(sp => sp.GetRequiredService<SignalJammerTxSource>());
        services.AddHostedService(sp => sp.GetRequiredService<TxMetersService>());
        services.AddHostedService(sp => sp.GetRequiredService<TxTuneDriver>());
        services.AddHostedService(sp => sp.GetRequiredService<CwEngine>());
        services.AddHostedService<PsAutoAttenuateService>();
        services.AddHostedService(sp => sp.GetRequiredService<ExternalPttService>());
        services.AddHostedService(sp => sp.GetRequiredService<NativeAudioSink>());
        services.AddHostedService(sp => sp.GetRequiredService<NativeMicCapture>());

        services.AddTciServices();
        services.AddCatServices();

        return services;
    }
}
