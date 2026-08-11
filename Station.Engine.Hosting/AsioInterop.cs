// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Server;

internal static partial class AsioInterop
{
    internal const string LibraryName = "zeus_asio";

    internal static void EnsureResolverRegistered()
        => EngineNativeLibraryResolver.EnsureRegistered();

    internal static IntPtr ResolveLibrary(Assembly assembly)
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        string fileName = "zeus_asio.dll";
        foreach (string root in new[]
                 {
                     Path.GetDirectoryName(assembly.Location) ?? string.Empty,
                     AppContext.BaseDirectory,
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string ridPath = Path.Combine(root, "runtimes", $"win-{arch}", "native", fileName);
            if (File.Exists(ridPath) && NativeLibrary.TryLoad(ridPath, out var ridHandle))
                return ridHandle;
            string flatPath = Path.Combine(root, fileName);
            if (File.Exists(flatPath) && NativeLibrary.TryLoad(flatPath, out var flatHandle))
                return flatHandle;
        }
        return NativeLibrary.TryLoad(LibraryName, assembly, null, out var handle)
            ? handle
            : IntPtr.Zero;
    }

    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_version")]
    internal static partial IntPtr Version();
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_drivers_create")]
    internal static partial IntPtr DriversCreate();
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_drivers_count")]
    internal static partial uint DriversCount(IntPtr snapshot);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_drivers_id")]
    internal static partial IntPtr DriversId(IntPtr snapshot, uint index);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_drivers_name")]
    internal static partial IntPtr DriversName(IntPtr snapshot, uint index);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_drivers_destroy")]
    internal static partial void DriversDestroy(IntPtr snapshot);

    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr ProbeCreate(string driverId);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_destroy")]
    internal static partial void ProbeDestroy(IntPtr probe);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_input_count")]
    internal static partial uint ProbeInputCount(IntPtr probe);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_output_count")]
    internal static partial uint ProbeOutputCount(IntPtr probe);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_input_name")]
    internal static partial IntPtr ProbeInputName(IntPtr probe, uint index);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_output_name")]
    internal static partial IntPtr ProbeOutputName(IntPtr probe, uint index);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_input_supported")]
    internal static partial int ProbeInputSupported(IntPtr probe, uint index);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_output_supported")]
    internal static partial int ProbeOutputSupported(IntPtr probe, uint index);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_supports_48000")]
    internal static partial int ProbeSupports48000(IntPtr probe);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_buffer_min")]
    internal static partial uint ProbeBufferMin(IntPtr probe);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_buffer_max")]
    internal static partial uint ProbeBufferMax(IntPtr probe);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_buffer_preferred")]
    internal static partial uint ProbeBufferPreferred(IntPtr probe);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_probe_buffer_granularity")]
    internal static partial int ProbeBufferGranularity(IntPtr probe);

    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr SessionCreate(
        string driverId,
        int inputChannel,
        int outputFirstChannel,
        uint bufferFrames,
        uint captureRingFrames,
        uint playbackRingFrames);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_start")]
    internal static partial int SessionStart(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_stop")]
    internal static partial int SessionStop(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_control_panel")]
    internal static partial int SessionControlPanel(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_destroy")]
    internal static partial void SessionDestroy(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_playback_free")]
    internal static partial uint PlaybackFree(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_playback_write")]
    internal static unsafe partial uint PlaybackWrite(IntPtr session, float* stereo, uint frames);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_capture_available")]
    internal static partial uint CaptureAvailable(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_capture_read")]
    internal static unsafe partial uint CaptureRead(IntPtr session, float* mono, uint frames);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_wait")]
    internal static unsafe partial int SessionWait(IntPtr session, uint timeoutMs, uint* events);

    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_sample_rate")]
    internal static partial uint SampleRate(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_buffer_frames")]
    internal static partial uint BufferFrames(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_input_latency_frames")]
    internal static partial uint InputLatencyFrames(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_output_latency_frames")]
    internal static partial uint OutputLatencyFrames(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_input_channel_count")]
    internal static partial uint InputChannelCount(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_output_channel_count")]
    internal static partial uint OutputChannelCount(IntPtr session);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_input_channel_name")]
    internal static partial IntPtr InputChannelName(IntPtr session, uint index);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_output_channel_name")]
    internal static partial IntPtr OutputChannelName(IntPtr session, uint index);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_session_get_status")]
    internal static partial int GetStatus(IntPtr session, ref AsioNativeStatus status);
    [LibraryImport(LibraryName, EntryPoint = "zeus_asio_last_error")]
    internal static unsafe partial uint LastError(IntPtr session, byte* utf8, uint capacity);
}

[StructLayout(LayoutKind.Sequential)]
internal struct AsioNativeStatus
{
    internal uint StructSize;
    internal uint Flags;
    internal uint PendingEvents;
    internal uint SampleRate;
    internal uint BufferFrames;
    internal uint InputLatencyFrames;
    internal uint OutputLatencyFrames;
    internal ulong CallbackCount;
    internal ulong CaptureFrames;
    internal ulong PlaybackFrames;
    internal ulong CaptureOverrunCount;
    internal ulong CaptureOverrunFrames;
    internal ulong PlaybackUnderrunCount;
    internal ulong PlaybackUnderrunFrames;
    internal ulong ResetRequestCount;
    internal ulong ResyncRequestCount;
    internal int LastAsioError;
    internal uint Reserved;
}
