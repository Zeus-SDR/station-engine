// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps native host audio playback, mute, and device-selection routes.</summary>
public static class NativeAudioEndpoints
{
    public static IEndpointRouteBuilder MapNativeAudioEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        // Native RX audio (miniaudio) — desktop-mode mute control. The
        // Mute/Unmute button in the Photino window POSTs here to silence
        // the OS playback device. Standalone hosts retain a disabled sink for
        // truthful diagnostics, while desktop mode enables it. The SPA uses
        // its in-browser AudioContext path whenever native output is disabled.
        endpoints.MapGet("/api/audio/native", (IServiceProvider sp) =>
        {
            var sink = sp.GetService<NativeAudioSink>();
            var hostAudio = sp.GetService<NativeHostAudioCoordinator>()?.State;
            return Results.Ok(new
            {
                supported = sink?.OutputEnabled == true,
                muted = sink?.IsMuted ?? false,
                backend = (hostAudio?.Backend ?? AudioHostApi.System).ToString().ToLowerInvariant(),
                asio = hostAudio?.AsioRuntime,
                asioError = hostAudio?.AsioError,
                diagnostics = sink?.GetDiagnostics(),
            });
        });
        endpoints.MapPost("/api/audio/native/mute", (NativeMuteRequest body, IServiceProvider sp) =>
        {
            var sink = sp.GetService<NativeAudioSink>();
            if (sink?.OutputEnabled != true)
                return Results.NotFound(new { error = "native audio not active in this host mode" });
            sink.SetMuted(body.Muted);
            return Results.Ok(new { supported = true, muted = sink.IsMuted });
        });
        // Engine-global RX master mute, readable and writable in every host
        // mode (unlike the native-mute route above, which 404s without an
        // enabled NativeAudioSink). Server-mode browser clients mute through
        // here so they can keep their AudioContext decoding: the sinks drop
        // band frames while the mute-exempt lane (Recorder local playback,
        // TX Monitor preview) keeps flowing.
        endpoints.MapGet("/api/audio/mute", (RxAudioMuteState mute) =>
            Results.Ok(new { muted = mute.IsMuted }));
        endpoints.MapPost("/api/audio/mute", (NativeMuteRequest body, RxAudioMuteState mute) =>
        {
            mute.SetMuted(body.Muted);
            return Results.Ok(new { muted = mute.IsMuted });
        });
        endpoints.MapGet("/api/audio/devices", GetNativeAudioDevices);
        endpoints.MapPut("/api/audio/devices", SetNativeAudioDevices);
        endpoints.MapPost("/api/audio/asio/control-panel", async (
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            var coordinator = sp.GetService<NativeHostAudioCoordinator>();
            if (coordinator is null)
                return Results.NotFound(new { error = "native audio not active in this host mode" });
            try
            {
                await coordinator.ShowAsioControlPanelAsync(ct);
                return Results.Ok(new { opened = true });
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
                                       or DllNotFoundException or EntryPointNotFoundException
                                       or BadImageFormatException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return endpoints;
    }

    private static IResult GetNativeAudioDevices(IServiceProvider sp)
    {
        var sink = sp.GetService<NativeAudioSink>();
        if (sink?.OutputEnabled != true) sink = null;
        var mic = sp.GetService<NativeMicCapture>();
        var coordinator = sp.GetService<NativeHostAudioCoordinator>();
        if (sink is null && mic is null)
        {
            return Results.Ok(new NativeAudioDevicesResponse(
                Supported: false,
                InputDeviceId: null,
                OutputDeviceId: null,
                ActiveInputDeviceId: null,
                ActiveOutputDeviceId: null,
                Inputs: [],
                Outputs: [],
                Error: null));
        }

        try
        {
            var snapshot = EnumerateDevices(sp);
            return Results.Ok(BuildNativeAudioDevicesResponse(
                sink,
                mic,
                snapshot,
                supported: true,
                error: mic?.InputError,
                coordinator));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            bool asioAvailable = coordinator?.EnumerateAsioDrivers().Count > 0;
            return Results.Ok(BuildNativeAudioDevicesResponse(
                sink,
                mic,
                MiniAudioDeviceSnapshot.Empty,
                supported: asioAvailable,
                error: ex.Message,
                coordinator));
        }
    }

    internal static async Task<IResult> SetNativeAudioDevices(
        NativeAudioDevicesSetRequest body,
        IServiceProvider sp,
        CancellationToken ct)
    {
        if (body?.InputChannel is < -1)
            return Results.BadRequest(new { error = "input channel must be -1 or greater" });
        if (body?.AsioInputChannel is < 0)
            return Results.BadRequest(new { error = "ASIO input channel must be zero or greater" });
        if (body?.AsioOutputChannel is < 0)
            return Results.BadRequest(new { error = "ASIO output channel must be zero or greater" });

        var sink = sp.GetService<NativeAudioSink>();
        if (sink?.OutputEnabled != true) sink = null;
        var mic = sp.GetService<NativeMicCapture>();
        var coordinator = sp.GetService<NativeHostAudioCoordinator>();
        if (sink is null && mic is null)
            return Results.NotFound(new { error = "native audio not active in this host mode" });

        string? inputDeviceId = NormalizeDeviceId(body?.InputDeviceId);
        string? outputDeviceId = NormalizeDeviceId(body?.OutputDeviceId);

        AudioHostApi requestedBackend;
        if (!TryParseBackend(body?.Backend, coordinator?.State.Backend ?? AudioHostApi.System, out requestedBackend))
            return Results.BadRequest(new { error = "backend must be 'system' or 'asio'" });

        MiniAudioDeviceSnapshot snapshot;
        try
        {
            snapshot = EnumerateDevices(sp);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (requestedBackend == AudioHostApi.System)
                return Results.BadRequest(new { error = $"native audio device enumeration unavailable: {ex.Message}" });
            snapshot = MiniAudioDeviceSnapshot.Empty;
        }

        // Only validate/apply the System side that is actually changing. A carried-over
        // (unchanged) id — even one now stale because its device was unplugged or
        // the prefs DB came from another machine — must NOT block a change to the
        // other side. This is the #1128 snap-back: selecting an OUTPUT device was
        // rejected with an INPUT-device error because the previously-saved mic was
        // gone, so RX audio could never be pointed at a real Windows device.
        var plan = NativeAudioDevicePlan.Plan(
            hasMic: mic is not null,
            currentInput: mic?.ConfiguredInputDeviceId,
            requestedInput: inputDeviceId,
            availableInputIds: snapshot.Inputs.Select(d => d.Id).ToArray(),
            hasSink: sink is not null,
            currentOutput: sink?.ConfiguredOutputDeviceId,
            requestedOutput: outputDeviceId,
            availableOutputIds: snapshot.Outputs.Select(d => d.Id).ToArray());

        if (requestedBackend == AudioHostApi.System && plan.InputError is not null)
            return Results.BadRequest(new { error = plan.InputError });
        if (requestedBackend == AudioHostApi.System && plan.OutputError is not null)
            return Results.BadRequest(new { error = plan.OutputError });

        if (coordinator is not null)
        {
            var current = coordinator.State.Settings;
            string? asioDriverId = NormalizeDeviceId(body?.AsioDriverId) ?? current.AsioDriverId;
            var proposed = current with
            {
                Backend = requestedBackend,
                InputDeviceId = inputDeviceId,
                OutputDeviceId = outputDeviceId,
                InputChannel = body?.InputChannel switch
                {
                    null => current.InputChannel,
                    -1 => null,
                    _ => body.InputChannel,
                },
                AsioDriverId = asioDriverId,
                AsioInputChannel = body?.AsioInputChannel ?? current.AsioInputChannel,
                AsioOutputChannel = body?.AsioOutputChannel ?? current.AsioOutputChannel,
            };

            if (requestedBackend == AudioHostApi.Asio)
            {
                if (!OperatingSystem.IsWindows())
                    return Results.BadRequest(new { error = "ASIO is available only on Windows" });
                if (string.IsNullOrWhiteSpace(asioDriverId))
                    return Results.BadRequest(new { error = "select an ASIO driver first" });
                var drivers = coordinator.EnumerateAsioDrivers();
                if (!drivers.Any(driver => string.Equals(driver.Id, asioDriverId, StringComparison.OrdinalIgnoreCase)))
                    return Results.BadRequest(new { error = "ASIO driver is not in the current driver list" });
            }

            try
            {
                await coordinator.ApplyAsync(proposed, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
                                       or DllNotFoundException or EntryPointNotFoundException
                                       or BadImageFormatException or PlatformNotSupportedException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            return Results.Ok(BuildNativeAudioDevicesResponse(
                sink,
                mic,
                snapshot,
                supported: true,
                error: mic?.InputError,
                coordinator));
        }

        if (plan.ApplyInput)
            await mic!.SetInputDeviceAsync(inputDeviceId, ct);
        if (plan.ApplyOutput)
            await sink!.SetOutputDeviceAsync(outputDeviceId, ct);
        if (mic is not null)
            await ApplyRequestedInputChannelAsync(mic, body?.InputChannel, ct);

        return Results.Ok(BuildNativeAudioDevicesResponse(
            sink,
            mic,
            snapshot,
            supported: true,
            error: mic?.InputError,
            coordinator: null));
    }

    internal static NativeAudioDevicesResponse BuildNativeAudioDevicesResponse(
        NativeAudioSink? sink,
        NativeMicCapture? mic,
        MiniAudioDeviceSnapshot snapshot,
        bool supported,
        string? error,
        NativeHostAudioCoordinator? coordinator)
    {
        IReadOnlyList<AsioDriverInfo> asioDrivers = coordinator?.EnumerateAsioDrivers() ?? [];
        var host = coordinator?.State;
        var runtime = host?.AsioRuntime;
        string? selectedDriverId = host?.Settings.AsioDriverId;
        if (!asioDrivers.Any(driver => string.Equals(driver.Id, selectedDriverId, StringComparison.OrdinalIgnoreCase)))
            selectedDriverId = null;
        AsioDriverProbe? probe = coordinator?.ProbeAsioDriver(selectedDriverId);
        IReadOnlyList<AsioChannelInfo> asioInputs = probe?.Inputs ?? runtime?.Inputs ?? [];
        IReadOnlyList<AsioChannelInfo> asioOutputs = probe?.Outputs ?? runtime?.Outputs ?? [];
        int configuredInput = host?.Settings.AsioInputChannel ?? 0;
        int configuredOutput = host?.Settings.AsioOutputChannel ?? 0;
        int effectiveInput = asioInputs.Any(channel => channel.Index == configuredInput)
            ? configuredInput
            : asioInputs.FirstOrDefault()?.Index ?? 0;
        int effectiveOutput = asioOutputs.Any(channel => channel.Index == configuredOutput)
                              && asioOutputs.Any(channel => channel.Index == configuredOutput + 1)
            ? configuredOutput
            : FirstSupportedOutputPair(asioOutputs) ?? 0;
        return new NativeAudioDevicesResponse(
            Supported: supported,
            InputDeviceId: mic?.ConfiguredInputDeviceId,
            OutputDeviceId: sink?.ConfiguredOutputDeviceId,
            ActiveInputDeviceId: mic?.ActiveInputDeviceId,
            ActiveOutputDeviceId: sink?.ActiveOutputDeviceId,
            Inputs: snapshot.Inputs.Select(ToNativeAudioDeviceDto).ToArray(),
            Outputs: snapshot.Outputs.Select(ToNativeAudioDeviceDto).ToArray(),
            Error: error,
            InputDiagnostics: mic?.GetDiagnostics(),
            InputChannels: mic?.Channels is > 0 ? mic.Channels : null,
            InputSampleRate: mic?.SampleRate is > 0 ? mic.SampleRate : null,
            InputChannel: mic?.InputChannel,
            Backend: (host?.Backend ?? AudioHostApi.System).ToString().ToLowerInvariant(),
            AvailableBackends: OperatingSystem.IsWindows() && asioDrivers.Count > 0
                ? ["system", "asio"]
                : ["system"],
            AsioDrivers: asioDrivers,
            AsioDriverId: selectedDriverId,
            AsioInputs: asioInputs,
            AsioOutputs: asioOutputs,
            AsioInputChannel: effectiveInput,
            AsioOutputChannel: effectiveOutput,
            AsioSampleRate: runtime?.SampleRate ?? (probe?.Supports48000 == true ? 48_000 : null),
            AsioBufferFrames: runtime?.BufferFrames
                              ?? (probe is null ? null : checked((int)probe.BufferPreferredFrames)),
            AsioInputLatencyFrames: runtime?.InputLatencyFrames,
            AsioOutputLatencyFrames: runtime?.OutputLatencyFrames,
            AsioError: host?.AsioError,
            AsioSupports48000: probe?.Supports48000,
            AsioBufferMinFrames: probe?.BufferMinFrames,
            AsioBufferMaxFrames: probe?.BufferMaxFrames,
            AsioBufferPreferredFrames: probe?.BufferPreferredFrames,
            AsioBufferGranularity: probe?.BufferGranularity);
    }

    private static int? FirstSupportedOutputPair(IReadOnlyList<AsioChannelInfo> outputs)
    {
        foreach (var channel in outputs)
        {
            if (outputs.Any(next => next.Index == channel.Index + 1)) return channel.Index;
        }
        return null;
    }

    internal static Task ApplyRequestedInputChannelAsync(
        NativeMicCapture mic,
        int? inputChannel,
        CancellationToken ct)
    {
        // Request semantics deliberately differ from the response: omitted/null
        // leaves the current selection unchanged, while -1 explicitly selects
        // mix-all. Responses continue to use null to report mix-all.
        return inputChannel is null
            ? Task.CompletedTask
            : mic.SetInputChannelAsync(inputChannel == -1 ? null : inputChannel, ct);
    }

    private static MiniAudioDeviceSnapshot EnumerateDevices(IServiceProvider sp) =>
        sp.GetService<INativeAudioDeviceEnumerator>()?.Enumerate()
        ?? MiniAudioDevices.Enumerate();

    internal static NativeAudioDeviceDto ToNativeAudioDeviceDto(MiniAudioDeviceInfo device) =>
        new(
            device.Id,
            device.Name,
            device.IsDefault,
            VirtualAudioCableCatalog.Match(device.Name));

    private static string? NormalizeDeviceId(string? deviceId)
    {
        var trimmed = deviceId?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static bool TryParseBackend(
        string? value,
        AudioHostApi current,
        out AudioHostApi backend)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            backend = current;
            return true;
        }
        if (string.Equals(value.Trim(), "system", StringComparison.OrdinalIgnoreCase))
        {
            backend = AudioHostApi.System;
            return true;
        }
        if (string.Equals(value.Trim(), "asio", StringComparison.OrdinalIgnoreCase))
        {
            backend = AudioHostApi.Asio;
            return true;
        }
        backend = default;
        return false;
    }
}

internal sealed record NativeMuteRequest(bool Muted);
internal sealed record NativeAudioDevicesSetRequest(
    string? InputDeviceId,
    string? OutputDeviceId,
    // Request only: null/omitted preserves the selection; -1 selects mix-all;
    // non-negative values select a zero-based capture channel.
    int? InputChannel = null,
    string? Backend = null,
    string? AsioDriverId = null,
    int? AsioInputChannel = null,
    int? AsioOutputChannel = null);
internal sealed record NativeAudioDeviceDto(
    string Id,
    string Name,
    bool IsDefault,
    VirtualAudioCableMatchDto? VirtualCable = null);
internal sealed record VirtualAudioCableMatchDto(
    string Vendor,
    string Product,
    string InstallUrl);
internal sealed record NativeAudioDevicesResponse(
    bool Supported,
    string? InputDeviceId,
    string? OutputDeviceId,
    string? ActiveInputDeviceId,
    string? ActiveOutputDeviceId,
    IReadOnlyList<NativeAudioDeviceDto> Inputs,
    IReadOnlyList<NativeAudioDeviceDto> Outputs,
    string? Error,
    // Optional capture-flow counters (additive; null when no mic capture is
    // registered). Lets a remote session tell "capture flowing but silent"
    // from "no capture callbacks" — both otherwise read as a floored meter.
    NativeMicCaptureDiagnostics? InputDiagnostics = null,
    uint? InputChannels = null,
    uint? InputSampleRate = null,
    // Response only: null reports mix-all; non-negative values are zero-based.
    int? InputChannel = null,
    string Backend = "system",
    IReadOnlyList<string>? AvailableBackends = null,
    IReadOnlyList<AsioDriverInfo>? AsioDrivers = null,
    string? AsioDriverId = null,
    IReadOnlyList<AsioChannelInfo>? AsioInputs = null,
    IReadOnlyList<AsioChannelInfo>? AsioOutputs = null,
    int AsioInputChannel = 0,
    int AsioOutputChannel = 0,
    int? AsioSampleRate = null,
    int? AsioBufferFrames = null,
    int? AsioInputLatencyFrames = null,
    int? AsioOutputLatencyFrames = null,
    string? AsioError = null,
    bool? AsioSupports48000 = null,
    uint? AsioBufferMinFrames = null,
    uint? AsioBufferMaxFrames = null,
    uint? AsioBufferPreferredFrames = null,
    int? AsioBufferGranularity = null);
