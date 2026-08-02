// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.Cat;
using Zeus.Server.Diagnostics;
using Zeus.Server.Tci;
using Zeus.Plugins.Host;

namespace Zeus.StationEngine;

internal enum StationEngineBindMode
{
    Loopback,
    Lan,
}

public partial class Program
{
    private const string CorsPolicyName = "ZeusLinkLocalAttach";
    internal const string StationAccessTokenEnvironmentVariable = "ZEUS_STATION_ACCESS_TOKEN";

    public static async Task<int> Main(string[] args)
    {
        // Must run before any TX pacing starts — see RaiseTimerResolutionOnWindows.
        RaiseTimerResolutionOnWindows();

        // Eager self-diagnostics: stamp the startup banner into the rolling
        // on-disk log BEFORE anything else can throw. The banner is the only
        // artifact that can prove WHICH engine build actually launched on an
        // operator's machine — without it, "no zeus-app.log" cannot distinguish
        // "the launcher ran an old cached engine" from "the new engine died
        // before its first framework log line". Best-effort: the sink never
        // throws into logging, so an unwritable log dir degrades to
        // in-memory-only rather than blocking launch.
        var diagnosticLogFileSink = new DiagnosticLogFileSink(PrefsDbPath.AppLogPath());
        EngineStartupLog.WriteBanner(diagnosticLogFileSink, args);

        WebApplication app;
        try
        {
            app = Build(args, diagnosticLogFileSink);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"StationEngine: {ex.Message}");
            Console.Error.WriteLine(
                "usage: StationEngine --port <1..65535> [--bind <loopback|lan>] " +
                "[--native-audio-output <true|false>]");
            diagnosticLogFileSink.Dispose();
            return 2;
        }
        catch (Exception ex)
        {
            EngineStartupLog.WriteFatal(diagnosticLogFileSink, "build", ex);
            diagnosticLogFileSink.Dispose();
            return 1;
        }

        try
        {
            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            // The sink is still alive here (app disposal happens in finally),
            // so the crash lands in zeus-app.log before the process exits.
            EngineStartupLog.WriteFatal(diagnosticLogFileSink, "run", ex);
            return 1;
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Windows' default system timer resolution is ~15.6 ms, which floors the
    // Thread.Sleep waits used by the Protocol-2 TX-IQ sender and the paced TX
    // tail drain. At that granularity the sender can only feed the 192 kHz DUC
    // at ~380 packets/s instead of the required 800, starving the radio's TX
    // FIFO and delaying un-key. macOS and Linux already provide approximately
    // 1 ms resolution. The OS guard also ensures winmm.dll is never resolved
    // on those platforms. timeBeginPeriod(1) raises the Windows process-wide
    // resolution for its lifetime; timeEndPeriod is deliberately omitted
    // because TX pacing needs the resolution until process exit, when Windows
    // restores the default.
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    internal static void RaiseTimerResolutionOnWindows()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            TimeBeginPeriod(1);
        }
    }

    /// <summary>Builds the standalone engine application; the caller owns its lifecycle.</summary>
    public static WebApplication Build(string[] args) => Build(args, diagnosticLogFileSink: null);

    /// <summary>
    /// Builds the application around a caller-provided on-disk log sink, so the
    /// process entry point can stamp the startup banner into the SAME sink
    /// instance before Build runs (DI still owns and disposes it). When
    /// <paramref name="diagnosticLogFileSink"/> is null a fresh sink is created
    /// at the default path.
    /// </summary>
    public static WebApplication Build(string[] args, DiagnosticLogFileSink? diagnosticLogFileSink)
    {
        var options = ParseOptions(args);
        var port = options.Port;
        PrepareEnginePreferences();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Self-diagnostic log capture, mirroring the product host (ZeusHost): a
        // singleton ring buffer retains the last ~4000 formatted log lines and a
        // rolling on-disk sink mirrors the same redacted lines to
        // DataDir/logs/zeus-app.log so the recent log SURVIVES an engine crash —
        // a Zeus Link tester whose engine dies before the panadapter paints
        // otherwise has no log to send. Best-effort: the sink never throws into
        // logging, so an unwritable log dir degrades to in-memory-only rather
        // than blocking launch.
        var diagnosticLogBuffer = new DiagnosticLogBuffer();
        builder.Services.AddSingleton(diagnosticLogBuffer);
        var sink = diagnosticLogFileSink ?? new DiagnosticLogFileSink(PrefsDbPath.AppLogPath());
        // Register through a factory so DI owns and disposes the shared sink.
        builder.Services.AddSingleton<IDiagnosticLogFileSink>(_ => sink);
        builder.Services.AddSingleton<ILoggerProvider>(services =>
            new RingBufferLoggerProvider(
                diagnosticLogBuffer,
                services.GetRequiredService<IDiagnosticLogFileSink>()));

        // TCI shares Kestrel, so its persisted listener selection must be read
        // before the host is built. CAT owns its raw TCP listener but uses the
        // same startup-override contract for parity with the product host.
        var persistedTci = LoadPersistedTci();
        var tciSection = builder.Configuration.GetSection("Tci");
        var tciEnabled = tciSection.GetValue<bool>("Enabled");
        var tciBindAddress = tciSection.GetValue<string?>("BindAddress") ?? "127.0.0.1";
        var tciPort = tciSection.GetValue<int?>("Port") ?? 40001;
        if (persistedTci is not null)
        {
            tciEnabled = persistedTci.Enabled;
            tciBindAddress = persistedTci.BindAddress;
            tciPort = persistedTci.Port;
        }

        var persistedCat = LoadPersistedCat();
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(3));
        builder.WebHost.ConfigureKestrel(server =>
        {
            if (ListenOnAllInterfaces(options.BindMode))
                server.ListenAnyIP(port);
            else
                server.Listen(IPAddress.Loopback, port);
            server.ConfigureTciListener(tciEnabled, tciBindAddress, tciPort);
        });

        builder.Services.Configure<JsonOptions>(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.Configure<TciOptions>(builder.Configuration.GetSection("Tci"));
        if (persistedTci is not null)
        {
            var pendingTci = persistedTci;
            builder.Services.PostConfigure<TciOptions>(options =>
            {
                options.Enabled = pendingTci.Enabled;
                options.BindAddress = pendingTci.BindAddress;
                options.Port = pendingTci.Port;
            });
        }
        builder.Services.Configure<CatOptions>(builder.Configuration.GetSection("Cat"));
        if (persistedCat is not null)
        {
            var pendingCat = persistedCat;
            builder.Services.PostConfigure<CatOptions>(options =>
            {
                options.Enabled = pendingCat.Enabled;
                options.BindAddress = pendingCat.BindAddress;
                options.Port = pendingCat.Port;
                options.AutoReport = pendingCat.AutoReport;
            });
        }
        var httpContextAccessor = new HttpContextAccessor();
        builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);
        builder.Services.AddCors(cors => cors.AddPolicy(
            CorsPolicyName,
            policy => StationEngineEndpoints.ConfigureCors(
                policy,
                allowLanSameHost: options.BindMode == StationEngineBindMode.Lan,
                requestHost: () => httpContextAccessor.HttpContext?.Request.Host.Host)));
        var p2AutoConnectEndpoint = Environment.GetEnvironmentVariable(
            P2AutoConnectService.EndpointEnvironmentVariable);
        builder.Services.AddStationEngine(new StationEngineHostingOptions(
            NativeAudioOutputEnabled: options.NativeAudioOutputEnabled,
            P2AutoConnectEndpoint: string.IsNullOrWhiteSpace(p2AutoConnectEndpoint)
                ? null
                : p2AutoConnectEndpoint));
        builder.Services.AddZeusPlugins(
            prefsDbPathProvider: PrefsDbPath.EngineGet,
            options: new PluginManagerOptions
            {
                HostDataDirectory = Path.GetDirectoryName(PrefsDbPath.LogbookPath()),
                PluginRoot = StationFeaturePluginRoot(),
            });
        // The later registration intentionally replaces the engine's inert
        // fallback so optional hardware behavior follows the live plugin
        // activation state, including uninstall/deactivation without restart.
        builder.Services.AddSingleton<IInstalledFeatureState, PluginFeatureState>();

        var app = builder.Build();
        if (options.BindMode == StationEngineBindMode.Lan)
        {
            app.Logger.LogInformation(
                "station-engine --bind lan opens only the HTTP engine listener; " +
                "TCI and CAT retain their separately configured bind addresses " +
                "(default loopback)");
        }
        app.UseCors(CorsPolicyName);
        app.UseStationAccessTokenAuthorization(
            Environment.GetEnvironmentVariable(StationAccessTokenEnvironmentVariable));
        app.Use(RejectRemotePluginMutations);
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
        });
        app.UseTciServer(tciEnabled, tciPort);

        AnchorWdspDataFiles(app);
        WireEngineBroadcasts(app);
        app.MapStationEngineEndpoints(options.BindMode == StationEngineBindMode.Lan);
        var pluginManager = app.Services.GetRequiredService<PluginManager>();
        pluginManager.StartAsync(default).GetAwaiter().GetResult();
        PluginEndpoints.MapAll(app, pluginManager);
        return app;
    }

    internal static async Task RejectRemotePluginMutations(
        HttpContext context,
        RequestDelegate next)
    {
        var path = context.Request.Path;
        var pathText = path.Value ?? string.Empty;
        var mutatesPluginState = path.StartsWithSegments("/api/plugins/install")
            || string.Equals(pathText, "/api/plugins/checkout", StringComparison.OrdinalIgnoreCase)
            || (HttpMethods.IsDelete(context.Request.Method)
                && pathText.StartsWith("/api/plugins/", StringComparison.OrdinalIgnoreCase)
                && pathText.Count(character => character == '/') == 3);
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (mutatesPluginState
            && (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Plugin installation and removal are available only from this station computer.",
            }).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    internal static int ParsePort(IReadOnlyList<string> args) => ParseOptions(args).Port;

    internal static StationEngineCommandLineOptions ParseOptions(IReadOnlyList<string> args)
    {
        int? port = null;
        StationEngineBindMode? bindMode = null;
        bool? nativeAudioOutputEnabled = null;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--port":
                    if (port is not null)
                        throw new ArgumentException("--port may be specified only once");
                    if (++index >= args.Count
                        || !int.TryParse(args[index], out var parsed)
                        || parsed is < 1 or > 65_535)
                        throw new ArgumentException(
                            "--port requires an integer from 1 through 65535");
                    port = parsed;
                    break;
                case "--bind":
                    if (bindMode is not null)
                        throw new ArgumentException("--bind may be specified only once");
                    if (++index >= args.Count)
                        throw new ArgumentException("--bind requires loopback or lan");
                    bindMode = args[index] switch
                    {
                        "loopback" => StationEngineBindMode.Loopback,
                        "lan" => StationEngineBindMode.Lan,
                        _ => throw new ArgumentException("--bind requires loopback or lan"),
                    };
                    break;
                case "--native-audio-output":
                    if (nativeAudioOutputEnabled is not null)
                        throw new ArgumentException(
                            "--native-audio-output may be specified only once");
                    if (++index >= args.Count || !bool.TryParse(args[index], out var enabled))
                        throw new ArgumentException(
                            "--native-audio-output requires true or false");
                    nativeAudioOutputEnabled = enabled;
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{args[index]}'");
            }
        }

        return new StationEngineCommandLineOptions(
            Port: port ?? throw new ArgumentException("--port is required"),
            BindMode: bindMode ?? StationEngineBindMode.Loopback,
            NativeAudioOutputEnabled: nativeAudioOutputEnabled ?? false);
    }

    internal static bool ListenOnAllInterfaces(StationEngineBindMode options) =>
        options == StationEngineBindMode.Lan;

    internal sealed record StationEngineCommandLineOptions(
        int Port,
        StationEngineBindMode BindMode,
        bool NativeAudioOutputEnabled);

    private static void PrepareEnginePreferences()
    {
        var enginePath = PrefsDbPath.EngineGet();
        if (!PrefsDbPath.EnsureUsable(enginePath))
            throw new InvalidOperationException(
                $"Station engine preferences are corrupt and unavailable at '{enginePath}'.");
        var productPath = PrefsDbPath.Get();
        EnginePrefsDbMigration.RunIfNeeded(productPath, enginePath);
        AudioDevicePrefsMigration.RunIfNeeded(productPath, enginePath);
    }

    private static string StationFeaturePluginRoot()
    {
        var configured = Environment.GetEnvironmentVariable(PluginRoot.EnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var desktopPluginRoot = PluginRoot.DefaultPath();
        var zeusDataDirectory = Path.GetDirectoryName(desktopPluginRoot)
            ?? throw new InvalidOperationException("Could not resolve the Zeus data directory.");
        return Path.Combine(zeusDataDirectory, "features");
    }

    private static TciRuntimeConfig? LoadPersistedTci()
    {
        try
        {
            using var store = new TciConfigStore(NullLogger<TciConfigStore>.Instance);
            return store.Get();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tci.config.bootstrap-load failed: {ex.Message}");
            return null;
        }
    }

    private static CatRuntimeConfig? LoadPersistedCat()
    {
        try
        {
            using var store = new CatConfigStore(NullLogger<CatConfigStore>.Instance);
            return store.Get();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cat.config.bootstrap-load failed: {ex.Message}");
            return null;
        }
    }

    private static void AnchorWdspDataFiles(WebApplication app)
    {
        var baseDirectory = AppContext.BaseDirectory;
        Directory.SetCurrentDirectory(baseDirectory);
        app.Logger.LogInformation(
            "station-engine wdsp data cwd={Cwd} zetaHat.bin={ZetaState} calculus={CalculusState}",
            baseDirectory,
            File.Exists(Path.Combine(baseDirectory, "zetaHat.bin")) ? "present" : "missing",
            File.Exists(Path.Combine(baseDirectory, "calculus")) ? "present" : "missing");
    }

    private static void WireEngineBroadcasts(WebApplication app)
    {
        var hub = app.Services.GetRequiredService<StreamingHub>();
        var wisdom = app.Services.GetRequiredService<Zeus.Dsp.Wdsp.WdspWisdomInitializer>();
        hub.SetWisdomPhase(wisdom.Phase);
        hub.SetWisdomStatus(wisdom.Status);
        wisdom.PhaseChanged += phase => hub.Broadcast(new Zeus.Contracts.WisdomStatusFrame(phase, wisdom.Status));
        wisdom.StatusChanged += status => hub.Broadcast(new Zeus.Contracts.WisdomStatusFrame(wisdom.Phase, status));

        var bandPlan = app.Services.GetRequiredService<BandPlanService>();
        bandPlan.PlanChanged += () => hub.BroadcastBandPlanChanged(bandPlan.CurrentRegion.Id);
    }
}
