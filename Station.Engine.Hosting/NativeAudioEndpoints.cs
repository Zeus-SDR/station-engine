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
            return Results.Ok(new
            {
                supported = sink?.OutputEnabled == true,
                muted = sink?.IsMuted ?? false,
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
        endpoints.MapGet("/api/audio/devices", GetNativeAudioDevices);
        endpoints.MapPut("/api/audio/devices", SetNativeAudioDevices);

        return endpoints;
    }

    private static IResult GetNativeAudioDevices(IServiceProvider sp)
    {
        var sink = sp.GetService<NativeAudioSink>();
        if (sink?.OutputEnabled != true) sink = null;
        var mic = sp.GetService<NativeMicCapture>();
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
            var snapshot = MiniAudioDevices.Enumerate();
            return Results.Ok(BuildNativeAudioDevicesResponse(
                sink,
                mic,
                snapshot,
                supported: true,
                error: null));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Results.Ok(BuildNativeAudioDevicesResponse(
                sink,
                mic,
                MiniAudioDeviceSnapshot.Empty,
                supported: false,
                error: ex.Message));
        }
    }

    internal static async Task<IResult> SetNativeAudioDevices(
        NativeAudioDevicesSetRequest body,
        IServiceProvider sp,
        CancellationToken ct)
    {
        if (body?.InputChannel is < -1)
            return Results.BadRequest(new { error = "input channel must be -1 or greater" });

        var sink = sp.GetService<NativeAudioSink>();
        if (sink?.OutputEnabled != true) sink = null;
        var mic = sp.GetService<NativeMicCapture>();
        if (sink is null && mic is null)
            return Results.NotFound(new { error = "native audio not active in this host mode" });

        string? inputDeviceId = NormalizeDeviceId(body?.InputDeviceId);
        string? outputDeviceId = NormalizeDeviceId(body?.OutputDeviceId);

        MiniAudioDeviceSnapshot snapshot;
        try
        {
            snapshot = MiniAudioDevices.Enumerate();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Results.BadRequest(new { error = $"native audio device enumeration unavailable: {ex.Message}" });
        }

        // Only validate/apply the side that is actually changing. A carried-over
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

        if (plan.InputError is not null)
            return Results.BadRequest(new { error = plan.InputError });
        if (plan.OutputError is not null)
            return Results.BadRequest(new { error = plan.OutputError });

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
            error: null));
    }

    private static NativeAudioDevicesResponse BuildNativeAudioDevicesResponse(
        NativeAudioSink? sink,
        NativeMicCapture? mic,
        MiniAudioDeviceSnapshot snapshot,
        bool supported,
        string? error)
    {
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
            InputChannel: mic?.InputChannel);
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

    private static NativeAudioDeviceDto ToNativeAudioDeviceDto(MiniAudioDeviceInfo device) =>
        new(device.Id, device.Name, device.IsDefault);

    private static string? NormalizeDeviceId(string? deviceId)
    {
        var trimmed = deviceId?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

internal sealed record NativeMuteRequest(bool Muted);
internal sealed record NativeAudioDevicesSetRequest(
    string? InputDeviceId,
    string? OutputDeviceId,
    // Request only: null/omitted preserves the selection; -1 selects mix-all;
    // non-negative values select a zero-based capture channel.
    int? InputChannel = null);
internal sealed record NativeAudioDeviceDto(string Id, string Name, bool IsDefault);
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
    int? InputChannel = null);
