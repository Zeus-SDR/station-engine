// SPDX-License-Identifier: GPL-2.0-or-later
//
// Runtime display-performance defaults and normalization bounds. Startup
// options provide the initial cap; persisted operator preferences can tune the
// same display path without rebuilding.

using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace Zeus.Server;

public sealed record DisplayPerformanceSnapshot(
    string Profile,
    double MaxFrameRateHz,
    bool LowPower,
    bool PreferWebglWaterfall,
    int RxAnalyzerFftSize,
    int PanadapterWidth,
    int? DefaultConnectSampleRateHz);

public readonly record struct DisplayHardwareProfile(
    Architecture ProcessArchitecture,
    Architecture OSArchitecture,
    int ProcessorCount,
    bool IsLinuxOs,
    string? DeviceTreeModel)
{
    private const string DeviceTreeModelPath = "/proc/device-tree/model";

    private static readonly string[] KnownSbcModelMarkers =
    [
        "raspberry pi",
        "radxa",
        "rockchip",
        "rk3588",
        "rk356",
        "orange pi",
        "banana pi",
        "odroid",
        "khadas",
        "pine64",
        "allwinner",
        "amlogic",
        "libre computer",
    ];

    public static DisplayHardwareProfile Current()
    {
        var probe = new DisplayHardwareProfile(
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.OSArchitecture,
            Environment.ProcessorCount,
            OperatingSystem.IsLinux(),
            DeviceTreeModel: null);

        return probe.IsLinuxArm64
            ? probe with { DeviceTreeModel = ReadDeviceTreeModel() }
            : probe;
    }

    // The single authority for "is this a Linux arm64 host" — the device-tree
    // read gate, the Pi-class test, and the startup diagnostics log must never
    // diverge on this predicate.
    public bool IsLinuxArm64 =>
        IsLinuxOs &&
        (ProcessArchitecture == Architecture.Arm64 || OSArchitecture == Architecture.Arm64);

    // Pi-class means Linux arm64 boards targeted by the low-power profile.
    // Core count catches Pi 4/5 and Pi CM4/CM5 systems; the device-tree model
    // catches many-core SBC swaps such as the Radxa CM5 / RK3588 class that
    // core count misses. Ampere/Graviton servers and VMs have no matching
    // device-tree model. A Linux arm64 VM with <=4 vCPUs is still treated as a
    // Pi, unchanged from today; ZEUS_DISPLAY_PROFILE=normal opts anything out.
    public bool IsPiClass =>
        IsLinuxArm64 &&
        (ProcessorCount <= 4 || IsKnownSbcModel(DeviceTreeModel));

    public static bool IsKnownSbcModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;

        return KnownSbcModelMarkers.Any(marker =>
            model.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadDeviceTreeModel()
    {
        try
        {
            var model = SanitizeDeviceTreeModel(File.ReadAllText(DeviceTreeModelPath));
            return model.Length == 0 ? null : model;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The device-tree model payload is NUL-terminated and may carry stray
    // whitespace around the NUL; strip every NUL before trimming so the
    // logged value is clean text.
    public static string SanitizeDeviceTreeModel(string raw) =>
        raw.Replace("\0", string.Empty).Trim();
}

public static class DisplayPerformanceOptions
{
    public const double DefaultFrameRateHz = 30.0;
    public const double LowPowerFrameRateHz = 15.0;
    public const double MinFrameRateHz = 1.0;
    public const double MaxFrameRateHz = 640.0;
    public const int DefaultDisplayDecimation = 1;
    public const int MinDisplayDecimation = 1;
    public const int MaxDisplayDecimation = 16;
    public const int DefaultWaterfallUpdatePeriod = 1;
    public const int MinWaterfallUpdatePeriod = 1;
    public const int MaxWaterfallUpdatePeriod = 1000;
    public const int DefaultRxAnalyzerFftSize = 16_384;
    public const int LowPowerRxAnalyzerFftSize = 8_192;
    public const int DefaultPanadapterWidth = 2_048;
    public const int LowPowerPanadapterWidth = 1_024;

    public static DisplayPerformanceSnapshot Resolve(
        IConfiguration? configuration = null,
        Func<string, string?>? environment = null,
        DisplayHardwareProfile? hardware = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var hw = hardware ?? DisplayHardwareProfile.Current();

        // Force the WebGL waterfall/panadapter independently of frame rate. On a
        // host whose WebKit build ships no functional WebGPU backend (the G2
        // appliance's WebKitGTK), the frontend would otherwise attempt WebGPU at
        // >= 30 fps, surface an "unavailable" notice, and fall back with degraded
        // colouring. This lets the appliance run a clean 30 fps (LowPower=false)
        // AND pin WebGL, instead of dropping below 30 just to get PreferWebgl.
        var forceWebgl =
            IsTruthy(environment("ZEUS_PREFER_WEBGL")) ||
            IsTruthy(configuration?["Zeus:Display:PreferWebgl"]);

        if (TryParseFrameRate(environment("ZEUS_DISPLAY_MAX_FPS"), out var envFps) ||
            TryParseFrameRate(configuration?["Zeus:Display:MaxFps"], out envFps) ||
            TryParseFrameRate(configuration?["Zeus:Display:FrameRateHz"], out envFps))
        {
            return new DisplayPerformanceSnapshot(
                Profile: Math.Abs(envFps - DefaultFrameRateHz) < 0.0001 ? "normal" : "custom",
                MaxFrameRateHz: envFps,
                LowPower: envFps < DefaultFrameRateHz,
                PreferWebglWaterfall: forceWebgl || envFps < DefaultFrameRateHz,
                RxAnalyzerFftSize: ResolveRxAnalyzerFftSize(DefaultRxAnalyzerFftSize, configuration, environment),
                PanadapterWidth: ResolvePanadapterWidth(DefaultPanadapterWidth, configuration, environment),
                DefaultConnectSampleRateHz: ResolveDefaultConnectSampleRateHz(configuration));
        }

        var profile = FirstNonBlank(
            environment("ZEUS_DISPLAY_PROFILE"),
            configuration?["Zeus:Display:Profile"]);
        // Unset profile defaults to auto so Pi-class hosts get low-power out
        // of the box; any explicit profile string (normal/full/unknown)
        // defeats auto by its mere presence, and the explicit low-power
        // flags keep their pre-auto-default meaning unchanged.
        var auto = profile is null || IsAutoProfile(profile);
        var autoPi = auto && hw.IsPiClass;
        var lowPower = autoPi ||
            IsLowPowerProfile(profile) ||
            IsTruthy(environment("ZEUS_LOW_POWER_DISPLAY")) ||
            IsTruthy(configuration?["Zeus:Display:LowPower"]);

        if (lowPower)
        {
            return new DisplayPerformanceSnapshot(
                Profile: auto ? "auto->low-power" : "low-power",
                MaxFrameRateHz: LowPowerFrameRateHz,
                LowPower: true,
                PreferWebglWaterfall: true,
                RxAnalyzerFftSize: ResolveRxAnalyzerFftSize(LowPowerRxAnalyzerFftSize, configuration, environment),
                PanadapterWidth: ResolvePanadapterWidth(LowPowerPanadapterWidth, configuration, environment),
                DefaultConnectSampleRateHz: ResolveDefaultConnectSampleRateHz(configuration));
        }

        return new DisplayPerformanceSnapshot(
            Profile: "normal",
            MaxFrameRateHz: DefaultFrameRateHz,
            LowPower: false,
            PreferWebglWaterfall: forceWebgl,
            RxAnalyzerFftSize: ResolveRxAnalyzerFftSize(DefaultRxAnalyzerFftSize, configuration, environment),
            PanadapterWidth: ResolvePanadapterWidth(DefaultPanadapterWidth, configuration, environment),
            DefaultConnectSampleRateHz: ResolveDefaultConnectSampleRateHz(configuration));
    }

    public static bool TryParseFrameRate(string? raw, out double frameRateHz)
    {
        frameRateHz = DefaultFrameRateHz;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!double.TryParse(
                raw.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            !double.IsFinite(parsed))
        {
            return false;
        }

        frameRateHz = Math.Clamp(parsed, MinFrameRateHz, MaxFrameRateHz);
        return true;
    }

    public static double NormalizeFrameRate(double raw) =>
        double.IsFinite(raw)
            ? Math.Clamp(raw, MinFrameRateHz, MaxFrameRateHz)
            : DefaultFrameRateHz;

    public static double NormalizeFrameRate(double? raw, double fallback) =>
        raw.HasValue && double.IsFinite(raw.Value)
            ? NormalizeFrameRate(raw.Value)
            : NormalizeFrameRate(fallback);

    public static int NormalizeDisplayDecimation(int? raw) =>
        raw.HasValue
            ? Math.Clamp(raw.Value, MinDisplayDecimation, MaxDisplayDecimation)
            : DefaultDisplayDecimation;

    public static int NormalizeWaterfallUpdatePeriod(int? raw) =>
        raw.HasValue
            ? Math.Clamp(raw.Value, MinWaterfallUpdatePeriod, MaxWaterfallUpdatePeriod)
            : DefaultWaterfallUpdatePeriod;

    public static int NormalizeRxAnalyzerFftSize(int? raw) => raw switch
    {
        2048 or 4096 or 8192 or 16384 or 32768 => raw.Value,
        _ => DefaultRxAnalyzerFftSize,
    };

    public static int NormalizePanadapterWidth(int? raw) => raw switch
    {
        512 or 1024 or 2048 => raw.Value,
        _ => DefaultPanadapterWidth,
    };

    public static bool IsStock(DisplayPerformanceSnapshot snapshot) =>
        string.Equals(snapshot.Profile, "normal", StringComparison.Ordinal) &&
        Math.Abs(snapshot.MaxFrameRateHz - DefaultFrameRateHz) < 0.0001 &&
        !snapshot.LowPower &&
        !snapshot.PreferWebglWaterfall &&
        snapshot.RxAnalyzerFftSize == DefaultRxAnalyzerFftSize &&
        snapshot.PanadapterWidth == DefaultPanadapterWidth;

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    private static bool IsLowPowerProfile(string? raw) =>
        raw is not null &&
        (string.Equals(raw, "low-power", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "lowpower", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "pi", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "raspberry-pi", StringComparison.OrdinalIgnoreCase));

    private static bool IsAutoProfile(string? raw) =>
        raw is not null &&
        string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase);

    private static bool IsNormalProfile(string? raw) =>
        raw is not null &&
        (string.Equals(raw, "normal", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "full", StringComparison.OrdinalIgnoreCase));

    private static bool IsTruthy(string? raw) =>
        raw is not null &&
        (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase));

    private static int ResolveRxAnalyzerFftSize(
        int profileDefault,
        IConfiguration? configuration,
        Func<string, string?> environment)
    {
        if (TryParseInt(environment("ZEUS_RX_ANALYZER_FFT"), out var envValue))
            return NormalizeRxAnalyzerFftSize(envValue);

        if (TryParseInt(configuration?["Zeus:Display:RxAnalyzerFftSize"], out var configValue))
            return NormalizeRxAnalyzerFftSize(configValue);

        return profileDefault;
    }

    private static int ResolvePanadapterWidth(
        int profileDefault,
        IConfiguration? configuration,
        Func<string, string?> environment)
    {
        if (TryParseInt(environment("ZEUS_PANADAPTER_WIDTH"), out var envValue))
            return NormalizePanadapterWidth(envValue);

        if (TryParseInt(configuration?["Zeus:Display:PanadapterWidth"], out var configValue))
            return NormalizePanadapterWidth(configValue);

        return profileDefault;
    }

    private static int? ResolveDefaultConnectSampleRateHz(IConfiguration? configuration) =>
        TryParseInt(configuration?["Zeus:Display:DefaultConnectSampleRateHz"], out var value) && value > 0
            ? value
            : null;

    private static bool TryParseInt(string? raw, out int value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
