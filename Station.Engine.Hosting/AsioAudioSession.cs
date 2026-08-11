// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Zeus.Server;

internal sealed record AsioDriverInfo(string Id, string Name);
internal sealed record AsioChannelInfo(int Index, string Name);
internal sealed record AsioDriverProbe(
    string DriverId,
    bool Supports48000,
    uint BufferMinFrames,
    uint BufferMaxFrames,
    uint BufferPreferredFrames,
    int BufferGranularity,
    IReadOnlyList<AsioChannelInfo> Inputs,
    IReadOnlyList<AsioChannelInfo> Outputs);

internal sealed record AsioSessionConfig(
    string DriverId,
    int InputChannel,
    int OutputFirstChannel,
    bool EnableInput,
    bool EnableOutput,
    uint BufferFrames = 0);

internal sealed record AsioSessionRuntime(
    string DriverId,
    int SampleRate,
    int BufferFrames,
    int InputLatencyFrames,
    int OutputLatencyFrames,
    IReadOnlyList<AsioChannelInfo> Inputs,
    IReadOnlyList<AsioChannelInfo> Outputs,
    AsioSessionCounters Counters);

internal sealed record AsioSessionCounters(
    ulong CallbackCount,
    ulong CaptureFrames,
    ulong PlaybackFrames,
    ulong CaptureOverrunCount,
    ulong CaptureOverrunFrames,
    ulong PlaybackUnderrunCount,
    ulong PlaybackUnderrunFrames,
    ulong ResetRequestCount,
    ulong ResyncRequestCount,
    int LastAsioError);

internal interface IAsioSession : IDisposable
{
    AsioSessionRuntime Runtime { get; }
    event Action? RecoveryRequested;
    void Start();
    void Stop();
    void ShowControlPanel();
}

internal interface IAsioSessionFactory
{
    IReadOnlyList<AsioDriverInfo> EnumerateDrivers();
    AsioDriverProbe Probe(string driverId);
    IAsioSession Open(
        AsioSessionConfig config,
        NativeAudioSink? sink,
        NativeMicCapture? mic);
}

internal sealed class AsioSessionFactory : IAsioSessionFactory
{
    public IReadOnlyList<AsioDriverInfo> EnumerateDrivers()
    {
        if (!OperatingSystem.IsWindows()) return [];
        AsioInterop.EnsureResolverRegistered();
        IntPtr snapshot = AsioInterop.DriversCreate();
        if (snapshot == IntPtr.Zero)
            throw new InvalidOperationException(ReadLastError(IntPtr.Zero, "ASIO driver enumeration failed."));
        try
        {
            uint count = AsioInterop.DriversCount(snapshot);
            var drivers = new List<AsioDriverInfo>(checked((int)count));
            for (uint i = 0; i < count; i++)
            {
                string? id = Marshal.PtrToStringUTF8(AsioInterop.DriversId(snapshot, i));
                string? name = Marshal.PtrToStringUTF8(AsioInterop.DriversName(snapshot, i));
                if (!string.IsNullOrWhiteSpace(id))
                    drivers.Add(new(id, string.IsNullOrWhiteSpace(name) ? id : name));
            }
            return drivers;
        }
        finally
        {
            AsioInterop.DriversDestroy(snapshot);
        }
    }

    public AsioDriverProbe Probe(string driverId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ASIO is available only on Windows.");
        if (string.IsNullOrWhiteSpace(driverId))
            throw new ArgumentException("An ASIO driver must be selected.", nameof(driverId));
        AsioInterop.EnsureResolverRegistered();
        IntPtr probe = AsioInterop.ProbeCreate(driverId);
        if (probe == IntPtr.Zero)
            throw new InvalidOperationException(ReadLastError(IntPtr.Zero, "The ASIO driver could not be probed."));
        try
        {
            int supports48000 = AsioInterop.ProbeSupports48000(probe);
            if (supports48000 < 0)
                throw new InvalidOperationException(ReadLastError(IntPtr.Zero, "The ASIO driver format probe failed."));
            return new(
                driverId,
                supports48000 == 1,
                AsioInterop.ProbeBufferMin(probe),
                AsioInterop.ProbeBufferMax(probe),
                AsioInterop.ProbeBufferPreferred(probe),
                AsioInterop.ProbeBufferGranularity(probe),
                ReadProbeChannels(probe, AsioInterop.ProbeInputCount(probe), AsioInterop.ProbeInputName,
                    AsioInterop.ProbeInputSupported),
                ReadProbeChannels(probe, AsioInterop.ProbeOutputCount(probe), AsioInterop.ProbeOutputName,
                    AsioInterop.ProbeOutputSupported));
        }
        finally
        {
            AsioInterop.ProbeDestroy(probe);
        }
    }

    private static IReadOnlyList<AsioChannelInfo> ReadProbeChannels(
        IntPtr probe,
        uint count,
        Func<IntPtr, uint, IntPtr> getName,
        Func<IntPtr, uint, int> isSupported)
    {
        var channels = new List<AsioChannelInfo>();
        for (uint index = 0; index < count; index++)
        {
            int supported = isSupported(probe, index);
            if (supported < 0)
                throw new InvalidOperationException(ReadLastError(IntPtr.Zero, "The ASIO channel probe failed."));
            if (supported == 0) continue;
            string? name = Marshal.PtrToStringUTF8(getName(probe, index));
            channels.Add(new(
                checked((int)index),
                string.IsNullOrWhiteSpace(name) ? $"Channel {index + 1}" : name));
        }
        return channels;
    }

    public IAsioSession Open(
        AsioSessionConfig config,
        NativeAudioSink? sink,
        NativeMicCapture? mic)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ASIO is available only on Windows.");
        if (string.IsNullOrWhiteSpace(config.DriverId))
            throw new ArgumentException("An ASIO driver must be selected.", nameof(config));
        if (config.EnableInput && mic is null)
            throw new InvalidOperationException("ASIO capture was requested without a native microphone consumer.");
        if (config.EnableOutput && sink is null)
            throw new InvalidOperationException("ASIO playback was requested without a native output consumer.");
        AsioInterop.EnsureResolverRegistered();
        return new AsioAudioSession(config, sink, mic);
    }

    internal static unsafe string ReadLastError(IntPtr session, string fallback)
    {
        uint required = AsioInterop.LastError(session, null, 0);
        if (required == 0) return fallback;
        var bytes = new byte[checked((int)required + 1)];
        fixed (byte* p = bytes)
        {
            AsioInterop.LastError(session, p, (uint)bytes.Length);
        }
        return System.Text.Encoding.UTF8.GetString(bytes, 0, checked((int)required));
    }
}

internal static class AsioPumpScheduler
{
    // Keep at least 10.7 ms of stereo audio queued at 48 kHz. That is eight
    // 64-frame callbacks or four 128-frame callbacks, leaving useful jitter
    // margin without turning the 65k-frame transport ring into added latency.
    internal static uint PlaybackTargetFrames(int driverBufferFrames, uint ringFrames) => Math.Min(
        ringFrames / 2,
        Math.Max(512u, checked((uint)Math.Max(1, driverBufferFrames) * 4u)));

    // Bound synchronous capture consumers to at most two driver callbacks per
    // iteration (and at least 256 frames). Playback is serviced again before
    // another capture batch even when capture remains permanently backlogged.
    internal static uint CaptureBudgetFrames(int driverBufferFrames) =>
        Math.Max(256u, checked((uint)Math.Max(1, driverBufferFrames) * 2u));

    internal static void ServiceIteration(
        bool enableOutput,
        Action fillPlayback,
        bool enableInput,
        Action<uint> drainCapture,
        int driverBufferFrames)
    {
        if (enableOutput) fillPlayback();
        if (enableInput) drainCapture(CaptureBudgetFrames(driverBufferFrames));
    }


    internal static bool CanWritePlayback(Func<bool> isStopping) => !isStopping();
}

internal sealed class AsioAudioSession : IAsioSession
{
    internal const int RequiredSampleRate = 48_000;
    internal const int NativeStatusSize = 112;
    private const uint RingFrames = 65_536;
    private const int PumpFrames = 4_096;

    private readonly AsioSessionConfig _config;
    private readonly NativeAudioSink? _sink;
    private readonly NativeMicCapture? _mic;
    private readonly float[] _captureBuffer = new float[PumpFrames];
    private readonly float[] _playbackBuffer = new float[PumpFrames * 2];
    private readonly object _lifecycleSync = new();
    private IntPtr _handle;
    private Thread? _pump;
    private volatile bool _stopping;
    private bool _started;
    private bool _disposed;
    private AsioSessionRuntime _runtime;

    public event Action? RecoveryRequested;

    internal AsioAudioSession(
        AsioSessionConfig config,
        NativeAudioSink? sink,
        NativeMicCapture? mic)
    {
        if (Marshal.SizeOf<AsioNativeStatus>() != NativeStatusSize)
            throw new InvalidOperationException("The managed ASIO status ABI layout is invalid.");
        _config = config;
        _sink = sink;
        _mic = mic;
        _handle = AsioInterop.SessionCreate(
            config.DriverId,
            config.EnableInput ? config.InputChannel : -1,
            config.EnableOutput ? config.OutputFirstChannel : -1,
            config.BufferFrames,
            RingFrames,
            RingFrames);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException(
                AsioSessionFactory.ReadLastError(IntPtr.Zero, "The ASIO driver could not be opened."));

        int rate = checked((int)AsioInterop.SampleRate(_handle));
        if (rate != RequiredSampleRate)
        {
            string error = $"The ASIO driver negotiated {rate} Hz; Zeus requires exactly {RequiredSampleRate} Hz.";
            AsioInterop.SessionDestroy(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException(error);
        }
        _runtime = ReadRuntime();
    }

    public AsioSessionRuntime Runtime
    {
        get
        {
            lock (_lifecycleSync)
            {
                if (_handle != IntPtr.Zero) _runtime = ReadRuntime();
                return _runtime;
            }
        }
    }

    public void Start()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _stopping = false;
            if (_config.EnableOutput) FillPlaybackRing();
            int rc = AsioInterop.SessionStart(_handle);
            if (rc != 0)
                throw new InvalidOperationException(
                    AsioSessionFactory.ReadLastError(_handle, "The ASIO driver could not be started."));
            _pump = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "zeus-asio-pump",
                Priority = ThreadPriority.AboveNormal,
            };
            _started = true;
            try
            {
                _pump.Start();
            }
            catch (Exception startError)
            {
                _stopping = true;
                int stopResult = AsioInterop.SessionStop(_handle);
                _pump = null;
                _started = false;
                if (stopResult != 0)
                {
                    throw new AggregateException(
                        startError,
                        new InvalidOperationException(AsioSessionFactory.ReadLastError(
                            _handle,
                            "The ASIO driver also could not be stopped after pump startup failed.")));
                }
                throw;
            }
        }
    }

    public void Stop()
    {
        Thread? pump;
        int stopResult;
        string? stopError = null;
        lock (_lifecycleSync)
        {
            if (!_started || _handle == IntPtr.Zero) return;
            _stopping = true;
            stopResult = AsioInterop.SessionStop(_handle);
            if (stopResult != 0)
                stopError = AsioSessionFactory.ReadLastError(
                    _handle,
                    "The ASIO driver could not be stopped cleanly.");
            pump = _pump;
            _started = false;
        }
        if (pump is not null && pump != Thread.CurrentThread && !pump.Join(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("The ASIO managed pump did not stop within two seconds.");
        if (stopResult != 0)
            throw new InvalidOperationException(stopError);
    }

    public void ShowControlPanel()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (AsioInterop.SessionControlPanel(_handle) != 0)
                throw new InvalidOperationException(
                    AsioSessionFactory.ReadLastError(_handle, "The ASIO control panel could not be opened."));
        }
    }

    private unsafe void PumpLoop()
    {
        try
        {
            while (!_stopping)
            {
                uint events = 0;
                int wait = AsioInterop.SessionWait(_handle, 50, &events);
                if (_stopping) break;
                if (wait < 0 || (events & ((uint)AsioEvents.Error | (uint)AsioEvents.Stopped)) != 0)
                {
                    RecoveryRequested?.Invoke();
                    break;
                }
                if ((events & ((uint)AsioEvents.ResetRequested |
                               (uint)AsioEvents.ResyncRequested |
                               (uint)AsioEvents.SampleRateChanged)) != 0)
                {
                    RecoveryRequested?.Invoke();
                    break;
                }
                AsioPumpScheduler.ServiceIteration(
                    _config.EnableOutput,
                    FillPlaybackRing,
                    _config.EnableInput,
                    DrainCaptureRing,
                    _runtime.BufferFrames);
            }
        }
        finally
        {
            lock (_lifecycleSync)
            {
                _pump = null;
                if (_disposed && _handle != IntPtr.Zero)
                {
                    AsioInterop.SessionDestroy(_handle);
                    _handle = IntPtr.Zero;
                }
            }
        }
    }

    private unsafe void DrainCaptureRing(uint frameBudget)
    {
        while (!_stopping && frameBudget != 0)
        {
            uint available = AsioInterop.CaptureAvailable(_handle);
            if (available == 0) return;
            uint requested = Math.Min(Math.Min(available, frameBudget), (uint)_captureBuffer.Length);
            uint read;
            fixed (float* p = _captureBuffer)
                read = AsioInterop.CaptureRead(_handle, p, requested);
            if (read == 0) return;
            _mic!.AcceptHostMonoCapture(_captureBuffer.AsSpan(0, checked((int)read)));
            frameBudget -= Math.Min(frameBudget, read);
        }
    }

    private unsafe void FillPlaybackRing()
    {
        while (!_stopping)
        {
            uint free = AsioInterop.PlaybackFree(_handle);
            if (free == 0) return;
            uint used = RingFrames > free ? RingFrames - free : 0;
            uint target = AsioPumpScheduler.PlaybackTargetFrames(_runtime.BufferFrames, RingFrames);
            if (used >= target) return;
            uint requested = Math.Min(
                Math.Min(free, target - used),
                (uint)(_playbackBuffer.Length / 2));
            var samples = _playbackBuffer.AsSpan(0, checked((int)requested * 2));
            _sink!.RenderPlaybackForHost(samples, requested, 2);
            if (!AsioPumpScheduler.CanWritePlayback(() => _stopping))
                return;
            uint written;
            fixed (float* p = _playbackBuffer)
                written = AsioInterop.PlaybackWrite(_handle, p, requested);
            if (written == 0) return;
        }
    }

    private AsioSessionRuntime ReadRuntime()
    {
        var inputs = ReadChannels(_handle, AsioInterop.InputChannelCount(_handle), AsioInterop.InputChannelName);
        var outputs = ReadChannels(_handle, AsioInterop.OutputChannelCount(_handle), AsioInterop.OutputChannelName);
        var native = new AsioNativeStatus { StructSize = (uint)Marshal.SizeOf<AsioNativeStatus>() };
        AsioInterop.GetStatus(_handle, ref native);
        return new(
            _config.DriverId,
            checked((int)AsioInterop.SampleRate(_handle)),
            checked((int)AsioInterop.BufferFrames(_handle)),
            checked((int)AsioInterop.InputLatencyFrames(_handle)),
            checked((int)AsioInterop.OutputLatencyFrames(_handle)),
            inputs,
            outputs,
            new(native.CallbackCount, native.CaptureFrames, native.PlaybackFrames,
                native.CaptureOverrunCount, native.CaptureOverrunFrames,
                native.PlaybackUnderrunCount, native.PlaybackUnderrunFrames,
                native.ResetRequestCount, native.ResyncRequestCount, native.LastAsioError));
    }

    private static IReadOnlyList<AsioChannelInfo> ReadChannels(
        IntPtr handle,
        uint count,
        Func<IntPtr, uint, IntPtr> getName)
    {
        var result = new List<AsioChannelInfo>(checked((int)count));
        for (uint i = 0; i < count; i++)
        {
            string? name = Marshal.PtrToStringUTF8(getName(handle, i));
            result.Add(new(checked((int)i), string.IsNullOrWhiteSpace(name) ? $"Channel {i + 1}" : name));
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? stopError = null;
        try { Stop(); }
        catch (Exception ex) { stopError = ex; }
        lock (_lifecycleSync)
        {
            // A timed-out pump still owns the native handle. Quarantine it
            // until PumpLoop's finally proves termination; leaking until then
            // is preferable to destroying underneath a later ring P/Invoke.
            if ((_pump is null || !_pump.IsAlive) && _handle != IntPtr.Zero)
            {
                AsioInterop.SessionDestroy(_handle);
                _handle = IntPtr.Zero;
            }
        }
        if (stopError is not null) throw stopError;
    }

    [Flags]
    private enum AsioEvents : uint
    {
        ResetRequested = 4,
        ResyncRequested = 8,
        SampleRateChanged = 16,
        Stopped = 64,
        Error = 128,
    }
}
