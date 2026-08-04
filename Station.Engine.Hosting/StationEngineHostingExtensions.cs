// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Dsp.Wdsp;
using Zeus.Protocol1;

namespace Zeus.Server;

public sealed record StationEngineHostingOptions(
    bool NativeAudioOutputEnabled = false,
    string? P2AutoConnectEndpoint = null,
    IReadOnlyList<string>? LanHttpsUrls = null);

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

        services.AddSingleton(provider => new StationEngineCapabilitiesService(
            provider.GetRequiredService<IConfiguration>(),
            options.LanHttpsUrls));
        // Windows Firewall status/apply for the Settings control — same
        // service registration as the product host, so the engine's
        // /api/system/windows-firewall routes behave identically.
        services.AddSingleton<IWindowsFirewallService, WindowsFirewallService>();
        services.AddSingleton<IProductStreamSource>(_ => NullProductStreamSource.Instance);
        // Real (not null) sink: the SPA's clientErrorBeacon prefers the /ws
        // diagnostic frame, so a null sink would silently discard uncaught
        // webview errors exactly when the engine is the only API origin.
        services.AddSingleton<ClientLogRateLimiter>();
        services.AddSingleton<EngineClientDiagnosticSink>();
        services.AddSingleton<IClientDiagnosticSink>(sp =>
            sp.GetRequiredService<EngineClientDiagnosticSink>());
        // Engine-owned mode-modem lease (standalone engine only; the desktop
        // host keeps its in-process modem bridge). The TX-service lookup is
        // lazy because TxService -> DspPipelineService -> IAudioModemPort
        // would otherwise form a construction cycle; it is resolved only on
        // the lease-teardown path when a modem-owned key must drop.
        services.AddSingleton<ModeModemLeasePort>(sp =>
            ActivatorUtilities.CreateInstance<ModeModemLeasePort>(
                sp,
                (Func<TxService?>)(() => sp.GetService<TxService>())));
        services.AddSingleton<IAudioModemPort>(sp => sp.GetRequiredService<ModeModemLeasePort>());
        services.AddSingleton<ProductAudioRingPort>();
        services.AddSingleton<IProductTxAudioPort>(sp => sp.GetRequiredService<ProductAudioRingPort>());
        services.AddSingleton<ProductPluginAudioPort>();
        services.AddSingleton<ITxAudioPreviewProcessor, NullTxAudioPreviewProcessor>();
        // KiwiSDR is an engine-owned remote receiver. Register the same concrete
        // external-receiver ports used by the monolithic host so attach-mode
        // clients can configure and open the Kiwi slice instead of receiving
        // 404s from the standalone station engine.
        // Keep the established zeus-prefs.db ownership so existing saved URLs,
        // passwords, feature-profile exports, and restores remain intact.
        services.AddSingleton<KiwiSettingsStore>();
        services.AddSingleton<KiwiSdrService>();
        services.AddSingleton<KiwiDirectoryService>();
        services.AddSingleton<IExternalReceiverSource>(sp =>
            sp.GetRequiredService<KiwiSdrService>());
        services.AddSingleton<IExternalReceiverControlPort>(sp =>
            sp.GetRequiredService<KiwiSdrService>());
        services.AddSingleton<IExternalRxAudioSource>(sp =>
            sp.GetRequiredService<KiwiSdrService>());
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
        services.AddSingleton<StationFavoriteStore>();
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
        // Zeus Link bundle settings mirror (feature toggles + amplifier
        // configs as one opaque product JSON document). Product database, same
        // reasoning as the operator UI families above: it must ride the
        // exportable zeus-prefs.db so the splash Database row can back it up.
        services.AddSingleton<ProductBundleSettingsStore>();
        // Digital workspace prefs (per-mode FT8/FT4/WSPR settings + the Auto-CQ
        // ack stamp). Same collections the desktop host uses, so the rows follow
        // the operator between hosts on the same machine.
        services.AddSingleton<Ft8SettingsStore>();
        services.AddSingleton<OperatorAckStore>();

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
        services.AddProtocol2ConnectionServices();
        services.AddSingleton<FrequencyCalibrationService>();
        services.AddSingleton<ImdMeasureService>();
        services.AddSingleton<TxService>();
        services.AddSingleton<IInstalledFeatureState, NoInstalledFeatureState>();
        services.AddSingleton<TuneCarrierCommandCoordinator>();
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
        services.AddSingleton<EngineCacheJanitor>();

        // Each hosted registration aliases the singleton used by endpoints
        // and other engine services, so there is exactly one instance of each.
        // Native sources start after the core pipeline and therefore stop
        // before it. This prevents an asynchronous device-open callback from
        // creating a preview DSP engine after shutdown has already begun.
        services.AddHostedService<EngineCacheJanitorStartup>();
        services.AddHostedService(sp => sp.GetRequiredService<SaturnSpeakerAudioSink>());
        services.AddHostedService(sp => sp.GetRequiredService<WisdomBootstrapService>());
        // Push persisted display settings into the DSP before DspPipelineService
        // (or any later connection worker) starts — mirrors the product host's
        // registration order so the DSP-applied subset (TX display calibration,
        // FFT/wideband/frame-rate/decimation/waterfall settings) is live from
        // boot instead of silently running at defaults until the first edit.
        services.AddHostedService<DisplaySettingsApplyService>();
        services.AddHostedService(sp => sp.GetRequiredService<KiwiSdrService>());
        services.AddHostedService(sp => sp.GetRequiredService<DspPipelineService>());
        if (!string.IsNullOrWhiteSpace(options.P2AutoConnectEndpoint))
        {
            services.AddSingleton(new P2AutoConnectOptions(options.P2AutoConnectEndpoint));
            services.AddSingleton<P2AutoConnectService>();
            services.AddHostedService(sp =>
                sp.GetRequiredService<P2AutoConnectService>());
        }
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
        services.AddSpeTaurusServices();

        return services;
    }
}
