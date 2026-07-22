// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.Cat;
using Zeus.Server.Tci;

namespace Zeus.StationEngine;

public partial class Program
{
    private const string CorsPolicyName = "ZeusLinkLocalAttach";
    internal const string StationAccessTokenEnvironmentVariable = "ZEUS_STATION_ACCESS_TOKEN";

    public static async Task<int> Main(string[] args)
    {
        WebApplication app;
        try
        {
            app = Build(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"StationEngine: {ex.Message}");
            Console.Error.WriteLine(
                "usage: StationEngine --port <1..65535> [--native-audio-output <true|false>]");
            return 2;
        }

        await using (app.ConfigureAwait(false))
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>Builds the standalone engine application; the caller owns its lifecycle.</summary>
    public static WebApplication Build(string[] args)
    {
        var options = ParseOptions(args);
        var port = options.Port;
        PrepareEnginePreferences();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

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
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.ConfigureTciListener(tciEnabled, tciBindAddress, tciPort);
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
        builder.Services.AddCors(options => options.AddPolicy(
            CorsPolicyName,
            StationEngineEndpoints.ConfigureCors));
        builder.Services.AddStationEngine(new StationEngineHostingOptions(
            NativeAudioOutputEnabled: options.NativeAudioOutputEnabled));

        var app = builder.Build();
        app.UseCors(CorsPolicyName);
        app.UseStationAccessTokenAuthorization(
            Environment.GetEnvironmentVariable(StationAccessTokenEnvironmentVariable));
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
        });
        app.UseTciServer(tciEnabled, tciPort);

        AnchorWdspDataFiles(app);
        WireEngineBroadcasts(app);
        app.MapStationEngineEndpoints();
        return app;
    }

    internal static int ParsePort(IReadOnlyList<string> args) => ParseOptions(args).Port;

    private static StationEngineCommandLineOptions ParseOptions(IReadOnlyList<string> args)
    {
        int? port = null;
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
            NativeAudioOutputEnabled: nativeAudioOutputEnabled ?? false);
    }

    private sealed record StationEngineCommandLineOptions(
        int Port,
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
