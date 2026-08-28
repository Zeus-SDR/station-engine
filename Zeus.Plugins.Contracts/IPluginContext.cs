// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Contracts;

/// <summary>
/// What the host gives a plugin during <see cref="IZeusPlugin.InitializeAsync"/>.
/// Capability-gated: <see cref="RadioController"/> et al. are null unless
/// the manifest declared the matching <see cref="PluginCapabilities"/> AND
/// the user granted it.
/// </summary>
public interface IPluginContext
{
    /// <summary>The plugin's stable id (e.g. <c>com.example.amplifier</c>).</summary>
    string PluginId { get; }

    /// <summary>The plugin's manifest as loaded from disk.</summary>
    PluginManifest Manifest { get; }

    /// <summary>Plugin-scoped logger. Each log line is tagged with the plugin id.</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Absolute path to the plugin's installation directory. Plugins may
    /// read/write files under this root without needing
    /// <see cref="PluginCapabilities.FileSystemRead"/> /
    /// <see cref="PluginCapabilities.FileSystemWrite"/>.
    /// </summary>
    string PluginRootPath { get; }

    /// <summary>
    /// Absolute path to the host data directory containing zeus-prefs.db and
    /// zeus-logbook.db. Default keeps older test doubles source-compatible.
    /// </summary>
    string HostDataDirectory => string.Empty;

    /// <summary>Capabilities actually granted (intersect of manifest + user choice).</summary>
    PluginCapabilities GrantedCapabilities { get; }

    /// <summary>Scoped key/value settings store (LiteDB-backed, isolated per plugin).</summary>
    IPluginSettings Settings { get; }

    /// <summary>
    /// Read-only radio state stream. Null if
    /// <see cref="PluginCapabilities.ReadRadioState"/> was not granted.
    /// </summary>
    IRadioStateReader? Radio { get; }

    /// <summary>
    /// Mutating radio controller. Null if
    /// <see cref="PluginCapabilities.ControlRadio"/> was not granted.
    /// </summary>
    IRadioController? RadioController { get; }

    /// <summary>
    /// Audio playback into the host — local monitor and on-air TX inject.
    /// Null if the host exposes no playback path (e.g. browser mode has no
    /// local-monitor sink). On-air playback only reaches the air while the
    /// operator has MOX engaged; this sink never keys the radio itself.
    /// Default implementation returns null so existing hosts / test doubles
    /// need no change.
    /// </summary>
    IAudioPlaybackSink? Playback => null;

    /// <summary>
    /// Host-mediated QRZ callsign lookup, reusing the operator's stored QRZ
    /// credentials + session + rate-limit gate. Null unless
    /// <see cref="PluginCapabilities.NetworkAccess"/> was granted and the host
    /// has QRZ configured. Default implementation returns null so existing
    /// hosts / test doubles need no change.
    /// </summary>
    IQrzLookup? Qrz => null;

    /// <summary>
    /// Shared operator callsign/grid resolver. Default implementation returns
    /// null so existing hosts and test doubles remain source-compatible.
    /// </summary>
    IOperatorIdentityProvider? OperatorIdentity => null;
}

/// <summary>Key/value persistence scoped to one plugin id.</summary>
public interface IPluginSettings
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Read-only view of the current radio state. Granted by ReadRadioState.</summary>
public interface IRadioStateReader
{
    long FrequencyHz { get; }
    string Mode { get; }
    string Band { get; }
    bool Mox { get; }

    /// <summary>Transmit drive, 0..100. Default keeps existing hosts and test
    /// doubles source-compatible.</summary>
    int DrivePercent => 0;

    /// <summary>Microphone gain in dB.</summary>
    double MicGainDb => 0;

    /// <summary>Live transmit telemetry, or null when the host publishes none.
    /// Default returns null so existing hosts need no change.</summary>
    ITxTelemetry? Telemetry => null;

    event Action<long> FrequencyChanged;
    event Action<string> ModeChanged;
    event Action<bool> MoxChanged;
}

/// <summary>
/// One tick of transmit telemetry.
/// <para>
/// <see cref="WirePeak"/> is the load-bearing field: it is the peak sample
/// actually handed to the radio, so zero while keyed means nothing is going
/// out. PA current, temperature, FIFO depth and packet rate all read healthy
/// in that case, which is what makes the failure hard to see.
/// </para>
/// </summary>
/// <param name="SignalDbm">Receive signal strength.</param>
/// <param name="MicPeakDbfs">Microphone input peak; negative infinity when silent.</param>
/// <param name="WirePeak">Peak transmit sample, 0..32767.</param>
/// <param name="ForwardWatts">Forward power, where the hardware can measure it.</param>
/// <param name="ReflectedWatts">Reflected power, where the hardware can measure it.</param>
/// <param name="PaTempC">Power amplifier temperature in degrees Celsius.</param>
public readonly record struct TxFrame(
    double SignalDbm,
    double MicPeakDbfs,
    int WirePeak,
    double ForwardWatts,
    double ReflectedWatts,
    double PaTempC);

/// <summary>
/// Push feed of transmit telemetry, reached through
/// <see cref="IRadioStateReader.Telemetry"/>. Granted by ReadRadioState.
/// </summary>
public interface ITxTelemetry
{
    event Action<TxFrame> Updated;
}

/// <summary>Mutating radio controller. Granted by ControlRadio.</summary>
public interface IRadioController
{
    Task SetFrequencyAsync(long hz, CancellationToken ct = default);
    Task SetModeAsync(string mode, CancellationToken ct = default);
    Task SetMoxAsync(bool keyed, CancellationToken ct = default);

    // Transmit path. Every member below defaults to a no-op so a host that
    // does not implement it stays source-compatible on ABI 1.

    /// <summary>Key the tune carrier, independent of the audio chain.</summary>
    Task SetTuneAsync(bool on, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Transmit drive, 0..100.</summary>
    Task SetDrivePercentAsync(int percent, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Ceiling on drive, 1..100. Protects the PA and anything after it.</summary>
    Task SetDriveMaxPercentAsync(int percent, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Microphone gain in dB.</summary>
    Task SetMicGainDbAsync(double db, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Leveler maximum gain in dB.</summary>
    Task SetLevelerMaxGainDbAsync(double db, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Where transmit audio comes from, e.g. host or radio microphone.</summary>
    Task SetTxAudioSourceAsync(string source, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Transmit filter edges in Hz.</summary>
    Task SetTxFilterAsync(int lowHz, int highHz, CancellationToken ct = default)
        => Task.CompletedTask;
}
