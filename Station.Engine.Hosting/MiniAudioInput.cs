// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Runtime.InteropServices;
using System.Text;

namespace Zeus.Server;

/// <summary>
/// Thin managed wrapper around a miniaudio capture device. Mirrors
/// <see cref="MiniAudioOutput"/> in shape — owns the GC roots for the
/// unmanaged callback delegates so they survive across the FFI boundary.
///
/// The data callback runs on miniaudio's audio worker thread. The buffer is
/// interleaved float32, sized <c>frameCount * channels</c>. The buffer is
/// only valid for the duration of the callback — copy out, don't hold a
/// reference.
/// </summary>
internal interface INativeMicInput : IDisposable
{
    uint SampleRate { get; }
    uint Channels { get; }
    bool IsRunning { get; }
    void Start();
    void Stop();
}

internal interface INativeMicInputFactory
{
    INativeMicInput Create(
        MiniAudioInput.FramesCallback onFrames,
        Action<int> onNotify,
        string? deviceIdHex);

    string? ResolveDefaultInputDeviceId();
}

internal sealed class NativeMicInputFactory : INativeMicInputFactory
{
    public INativeMicInput Create(
        MiniAudioInput.FramesCallback onFrames,
        Action<int> onNotify,
        string? deviceIdHex) =>
        new MiniAudioInput(
            onFrames,
            onNotify,
            deviceIdHex ?? (DisplayHardwareProfile.Current().IsNativeG2 ? ResolveDefaultInputDeviceId() : null),
            preferSampleRate: 48_000,
            preferChannels: 1,
            periodFrames: 480,
            periods: 2);

    public string? ResolveDefaultInputDeviceId() =>
        SelectDefaultInputDeviceId(MiniAudioDevices.Enumerate().Inputs, DisplayHardwareProfile.Current().IsNativeG2);

    internal static string? SelectDefaultInputDeviceId(IReadOnlyList<MiniAudioDeviceInfo> inputs, bool nativeG2)
    {
        if (!nativeG2) return inputs.FirstOrDefault(device => device.IsDefault)?.Id;
        var microphones = inputs.Where(device => !IsOutputMonitor(device)).ToArray();
        return (microphones.FirstOrDefault(device => device.IsDefault) ?? microphones.FirstOrDefault())?.Id
            ?? throw new InvalidOperationException("No microphone is available. Output monitors are not selected automatically on G2.");
    }

    private static bool IsOutputMonitor(MiniAudioDeviceInfo device)
    {
        // PulseAudio device IDs are UTF-8 names in the miniaudio ID buffer;
        // inspect the stable ID because display names may be localized.
        try
        {
            var id = Encoding.UTF8.GetString(Convert.FromHexString(device.Id)).TrimEnd('\0');
            if (id.EndsWith(".monitor", StringComparison.Ordinal)) return true;
        }
        catch (FormatException) { }
        return device.Name.StartsWith("Monitor of ", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class MiniAudioInput : INativeMicInput
{
    internal delegate void FramesCallback(ReadOnlySpan<float> input, uint frameCount, uint channels);

    private readonly FramesCallback _onFrames;
    private readonly Action<int>? _onNotify;
    private readonly MiniAudioInterop.CaptureCallback _nativeDataCb;
    private readonly MiniAudioInterop.NotifyCallback _nativeNotifyCb;
    private IntPtr _handle;
    private bool _started;
    private bool _disposed;

    public uint SampleRate { get; private set; }
    public uint Channels { get; private set; }
    public bool IsRunning => _started;

    public MiniAudioInput(
        FramesCallback onFrames,
        Action<int>? onNotify = null,
        string? deviceIdHex = null,
        uint preferSampleRate = 48_000,
        uint preferChannels = 1,
        uint periodFrames = 480,
        uint periods = 2)
    {
        _onFrames = onFrames ?? throw new ArgumentNullException(nameof(onFrames));
        _onNotify = onNotify;

        MiniAudioInterop.EnsureResolverRegistered();

        _nativeDataCb = NativeDataCallback;
        _nativeNotifyCb = NativeNotifyCallback;

        _handle = CreateNativeHandle(
            preferSampleRate,
            preferChannels,
            periodFrames,
            periods,
            deviceIdHex,
            Marshal.GetFunctionPointerForDelegate(_nativeDataCb),
            Marshal.GetFunctionPointerForDelegate(_nativeNotifyCb),
            IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                deviceIdHex is null
                    ? "miniaudio: failed to open default capture device (zeus_ma_input_create returned NULL)."
                    : "miniaudio: failed to open selected capture device (zeus_ma_input_create_for_device returned NULL).");
        }

        SampleRate = MiniAudioInterop.InputSampleRate(_handle);
        Channels = MiniAudioInterop.InputChannels(_handle);
    }

    private static IntPtr CreateNativeHandle(
        uint preferSampleRate,
        uint preferChannels,
        uint periodFrames,
        uint periods,
        string? deviceIdHex,
        IntPtr dataCallback,
        IntPtr notifyCallback,
        IntPtr user)
    {
        if (string.IsNullOrWhiteSpace(deviceIdHex))
        {
            return MiniAudioInterop.InputCreate(
                preferSampleRate,
                preferChannels,
                periodFrames,
                periods,
                dataCallback,
                notifyCallback,
                user);
        }

        IntPtr deviceIdPtr = Marshal.StringToCoTaskMemUTF8(deviceIdHex);
        try
        {
            return MiniAudioInterop.InputCreateForDevice(
                preferSampleRate,
                preferChannels,
                periodFrames,
                periods,
                deviceIdPtr,
                dataCallback,
                notifyCallback,
                user);
        }
        finally
        {
            Marshal.FreeCoTaskMem(deviceIdPtr);
        }
    }

    public void Start()
    {
        if (_started || _handle == IntPtr.Zero) return;
        int rc = MiniAudioInterop.InputStart(_handle);
        if (rc != 0)
        {
            throw new InvalidOperationException("miniaudio: failed to start capture device.");
        }
        _started = true;
    }

    public void Stop()
    {
        if (!_started || _handle == IntPtr.Zero) return;
        MiniAudioInterop.InputStop(_handle);
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            MiniAudioInterop.InputDestroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private unsafe void NativeDataCallback(IntPtr user, IntPtr inBuffer, uint frameCount, uint channels)
    {
        if (inBuffer == IntPtr.Zero || frameCount == 0 || channels == 0) return;
        int total = checked((int)(frameCount * channels));
        var span = new ReadOnlySpan<float>((void*)inBuffer, total);
        try
        {
            _onFrames(span, frameCount, channels);
        }
        catch
        {
            // Swallow at the FFI boundary; the host-level service that owns
            // this MiniAudioInput should be the one to surface errors.
        }
    }

    private void NativeNotifyCallback(IntPtr user, int kind)
    {
        try { _onNotify?.Invoke(kind); } catch { /* swallow */ }
    }
}
