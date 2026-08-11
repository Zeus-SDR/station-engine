// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

internal sealed record NativeHostAudioState(
    AudioHostApi Backend,
    AudioDeviceSettings Settings,
    AsioSessionRuntime? AsioRuntime,
    string? AsioError,
    bool InputEnabled,
    bool OutputEnabled);

internal interface ISystemHostAudioController
{
    void Start(AudioDeviceSettings settings);
    void Stop();
}

internal sealed class SystemHostAudioController(
    NativeAudioSink? sink,
    NativeMicCapture? mic) : ISystemHostAudioController
{
    public void Start(AudioDeviceSettings settings)
    {
        sink?.SetExternalOutputState(false, null, 0, 0);
        mic?.SetExternalInputState(false, null, 0, 0);
        var outputStarted = false;
        try
        {
            sink?.ActivateSystemOutput(settings.OutputDeviceId);
            outputStarted = sink is not null;
            mic?.ActivateSystemInput(settings.InputDeviceId, settings.InputChannel);
        }
        catch
        {
            if (outputStarted) sink?.DeactivateSystemOutput();
            throw;
        }
    }

    public void Stop()
    {
        Exception? first = null;
        try { mic?.DeactivateSystemInput(); }
        catch (Exception ex) { first = ex; }
        try { sink?.DeactivateSystemOutput(); }
        catch when (first is not null) { /* preserve the first failure */ }
        if (first is not null) throw first;
    }
}

/// <summary>
/// Owns host-backend selection. ASIO input and output always share one driver
/// session and are stopped/reopened as one transaction.
/// </summary>
internal sealed class NativeHostAudioCoordinator : IHostedService, IDisposable
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly AudioDeviceSettingsStore _settingsStore;
    private readonly NativeAudioSink? _sink;
    private readonly NativeMicCapture? _mic;
    private readonly IAsioSessionFactory _asioFactory;
    private readonly ISystemHostAudioController _system;
    private readonly ILogger<NativeHostAudioCoordinator> _log;
    private readonly TimeSpan _probeTimeout;
    private readonly Dictionary<string, AsioDriverProbe> _probeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _probeFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    private IAsioSession? _asio;
    private Action? _asioRecoveryHandler;
    private AudioHostApi _activeBackend = AudioHostApi.System;
    private AudioDeviceSettings _activeSettings;
    private string? _asioError;
    private int _recoveryScheduled;
    private bool _started;
    private bool _stopping;
    private bool _disposed;

    public NativeHostAudioCoordinator(
        AudioDeviceSettingsStore settingsStore,
        IServiceProvider services,
        IAsioSessionFactory asioFactory,
        ILogger<NativeHostAudioCoordinator> log)
        : this(
            settingsStore,
            services.GetService(typeof(NativeAudioSink)) as NativeAudioSink,
            services.GetService(typeof(NativeMicCapture)) as NativeMicCapture,
            asioFactory,
            log,
            system: null)
    {
    }

    internal NativeHostAudioCoordinator(
        AudioDeviceSettingsStore settingsStore,
        NativeAudioSink? sink,
        NativeMicCapture? mic,
        IAsioSessionFactory asioFactory,
        ILogger<NativeHostAudioCoordinator> log,
        ISystemHostAudioController? system = null,
        TimeSpan? probeTimeout = null)
    {
        _settingsStore = settingsStore;
        _sink = sink;
        _mic = mic;
        _asioFactory = asioFactory;
        _system = system ?? new SystemHostAudioController(sink, mic);
        _log = log;
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        _activeSettings = settingsStore.Get();
        if (sink is null && mic is null)
            throw new InvalidOperationException("A native input or output processor is required.");
    }

    internal NativeHostAudioState State
    {
        get
        {
            AsioSessionRuntime? runtime = null;
            try { runtime = _asio?.Runtime; } catch { /* diagnostics are best effort */ }
            return new(
                _activeBackend,
                _activeSettings,
                runtime,
                Volatile.Read(ref _asioError),
                InputEnabled: _mic is not null,
                OutputEnabled: _sink?.OutputEnabled == true);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) return;
            _started = true;
            _activeSettings = _settingsStore.Get();
            if (_activeSettings.Backend != AudioHostApi.Asio) return;
            try
            {
                StopSystem();
                _activeSettings = PrepareAsioSettings(_activeSettings, forceRefresh: true);
                StartAsio(_activeSettings);
                _settingsStore.Set(_activeSettings);
            }
            catch (Exception ex)
            {
                CleanupAttempt(AudioHostApi.Asio);
                Volatile.Write(ref _asioError, ex.Message);
                _log.LogWarning(ex,
                    "audio.asio startup failed; System audio remains active until ASIO is retried");
                StartSystem(_activeSettings);
            }
        }
        finally
        {
            _changeGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopActive();
            _started = false;
        }
        finally
        {
            _changeGate.Release();
        }
    }

    internal IReadOnlyList<AsioDriverInfo> EnumerateAsioDrivers()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            return _asioFactory.EnumerateDrivers();
        }
        catch (Exception ex) when (IsInteropAvailabilityFailure(ex))
        {
            Volatile.Write(ref _asioError, ex.Message);
            return [];
        }
    }

    internal AsioDriverProbe? ProbeAsioDriver(string? driverId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(driverId)) return null;
        _changeGate.Wait();
        try
        {
            if (_probeCache.TryGetValue(driverId, out var cached)) return cached;
            if (_activeBackend == AudioHostApi.Asio) return null;
            return ProbeAsioDriverCore(driverId, forceRefresh: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
                                   or DllNotFoundException or EntryPointNotFoundException
                                   or BadImageFormatException)
        {
            Volatile.Write(ref _asioError, ex.Message);
            return null;
        }
        finally
        {
            _changeGate.Release();
        }
    }

    internal async Task<NativeHostAudioState> ApplyAsync(
        AudioDeviceSettings proposed,
        CancellationToken cancellationToken)
    {
        Validate(proposed);
        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _activeSettings;
            var previousBackend = _activeBackend;
            StopActive();
            try
            {
                var prepared = proposed.Backend == AudioHostApi.Asio
                    ? PrepareAsioSettings(proposed, forceRefresh: true)
                    : proposed;
                Start(prepared);
                _settingsStore.Set(prepared);
                _activeSettings = prepared;
                Volatile.Write(ref _asioError, null);
                return State;
            }
            catch (Exception applyError)
            {
                CleanupAttempt(proposed.Backend);
                Volatile.Write(ref _asioError, applyError.Message);
                Exception? rollbackError = null;
                try
                {
                    Start(previous with { Backend = previousBackend });
                    _activeSettings = previous;
                }
                catch (Exception ex)
                {
                    rollbackError = ex;
                    try { StartSystem(previous); } catch { /* no further fallback */ }
                }

                if (rollbackError is not null)
                    throw new InvalidOperationException(
                        $"{applyError.Message} The prior audio route also could not be restored: {rollbackError.Message}",
                        applyError);
                throw;
            }
        }
        finally
        {
            _changeGate.Release();
        }
    }

    internal async Task ShowAsioControlPanelAsync(CancellationToken cancellationToken)
    {
        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeBackend != AudioHostApi.Asio || _asio is null)
                throw new InvalidOperationException("ASIO is not the active audio backend.");
            _asio.ShowControlPanel();
        }
        finally
        {
            _changeGate.Release();
        }
    }

    private void Start(AudioDeviceSettings settings)
    {
        if (settings.Backend == AudioHostApi.Asio) StartAsio(settings);
        else StartSystem(settings);
    }

    private AudioDeviceSettings PrepareAsioSettings(AudioDeviceSettings settings, bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(settings.AsioDriverId))
            throw new InvalidOperationException("Select an ASIO driver before enabling ASIO.");
        var probe = ProbeAsioDriverCore(settings.AsioDriverId, forceRefresh);
        if (!probe.Supports48000)
            throw new InvalidOperationException("The ASIO driver does not support the required 48000 Hz sample rate.");

        int inputChannel = settings.AsioInputChannel;
        if (_mic is not null)
        {
            if (probe.Inputs.Count == 0)
                throw new InvalidOperationException("The ASIO driver has no supported PCM input channels.");
            if (!probe.Inputs.Any(channel => channel.Index == inputChannel))
                inputChannel = probe.Inputs[0].Index;
        }

        int outputChannel = settings.AsioOutputChannel;
        if (_sink?.OutputEnabled == true)
        {
            int? firstPair = FirstSupportedOutputPair(probe.Outputs);
            if (firstPair is null)
                throw new InvalidOperationException("The ASIO driver has no adjacent supported PCM output pair.");
            if (!IsSupportedOutputPair(probe.Outputs, outputChannel))
                outputChannel = firstPair.Value;
        }
        return settings with { AsioInputChannel = inputChannel, AsioOutputChannel = outputChannel };
    }

    private AsioDriverProbe ProbeAsioDriverCore(string driverId, bool forceRefresh)
    {
        if (!forceRefresh && _probeCache.TryGetValue(driverId, out var cached)) return cached;
        if (!forceRefresh && _probeFailures.TryGetValue(driverId, out var priorFailure))
            throw new InvalidOperationException(priorFailure);
        try
        {
            var probe = TimeboxedNativeOpen.Run(
                () => _asioFactory.Probe(driverId),
                static _ => { },
                _probeTimeout,
                out var failure);
            if (probe is null)
            {
                if (failure is not null) throw failure;
                throw new TimeoutException(
                    $"The ASIO driver capability probe did not complete within {_probeTimeout.TotalSeconds:0.###} seconds.");
            }
            _probeCache[driverId] = probe;
            _probeFailures.Remove(driverId);
            return probe;
        }
        catch (Exception ex)
        {
            _probeFailures[driverId] = ex.Message;
            throw;
        }
    }

    private static int? FirstSupportedOutputPair(IReadOnlyList<AsioChannelInfo> outputs)
    {
        foreach (var channel in outputs)
        {
            if (outputs.Any(next => next.Index == channel.Index + 1)) return channel.Index;
        }
        return null;
    }

    private static bool IsSupportedOutputPair(IReadOnlyList<AsioChannelInfo> outputs, int first) =>
        outputs.Any(channel => channel.Index == first)
        && outputs.Any(channel => channel.Index == first + 1);

    private void StartSystem(AudioDeviceSettings settings)
    {
        _system.Start(settings);
        _activeBackend = AudioHostApi.System;
    }

    private void StopSystem()
    {
        _system.Stop();
    }

    private void StartAsio(AudioDeviceSettings settings)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ASIO is available only on Windows.");
        if (string.IsNullOrWhiteSpace(settings.AsioDriverId))
            throw new InvalidOperationException("Select an ASIO driver before enabling ASIO.");

        var config = new AsioSessionConfig(
            settings.AsioDriverId,
            settings.AsioInputChannel,
            settings.AsioOutputChannel,
            EnableInput: _mic is not null,
            EnableOutput: _sink?.OutputEnabled == true);
        if (!config.EnableInput && !config.EnableOutput)
            throw new InvalidOperationException("No native ASIO input or output route is enabled in this host mode.");
        var recoveryPending = 0;
        var recoveryHandlerArmed = 0;
        void RecoveryHandler()
        {
            if (Volatile.Read(ref recoveryHandlerArmed) != 0)
                OnAsioRecoveryRequested();
            else
                Interlocked.Exchange(ref recoveryPending, 1);
        }

        var session = TimeboxedNativeOpen.Run(
            () =>
            {
                var opened = _asioFactory.Open(
                    config,
                    config.EnableOutput ? _sink : null,
                    config.EnableInput ? _mic : null);
                try
                {
                    // Subscribe before Start: a driver may synchronously report a
                    // reset/error while its pump thread is being brought online.
                    opened.RecoveryRequested += RecoveryHandler;
                    opened.Start();
                    return opened;
                }
                catch
                {
                    opened.RecoveryRequested -= RecoveryHandler;
                    opened.Dispose();
                    throw;
                }
            },
            opened =>
            {
                opened.RecoveryRequested -= RecoveryHandler;
                opened.Dispose();
            },
            OpenTimeout,
            out var failure);
        if (session is null)
        {
            if (failure is not null) throw failure;
            throw new TimeoutException($"The ASIO driver did not open within {OpenTimeout.TotalSeconds:0} seconds.");
        }
        AsioSessionRuntime runtime;
        try
        {
            runtime = session.Runtime;
            if (runtime.SampleRate != AsioAudioSession.RequiredSampleRate)
                throw new InvalidOperationException(
                    $"The ASIO driver must run at {AsioAudioSession.RequiredSampleRate} Hz.");
        }
        catch
        {
            session.RecoveryRequested -= RecoveryHandler;
            session.Dispose();
            throw;
        }

        _asio = session;
        _asioRecoveryHandler = RecoveryHandler;
        _mic?.SetExternalInputState(config.EnableInput, settings.AsioDriverId, runtime.SampleRate, 1);
        _sink?.SetExternalOutputState(
            config.EnableOutput,
            settings.AsioDriverId,
            runtime.SampleRate,
            config.EnableOutput ? 2 : 0);
        _activeBackend = AudioHostApi.Asio;
        Volatile.Write(ref recoveryHandlerArmed, 1);
        if (Interlocked.Exchange(ref recoveryPending, 0) != 0)
            OnAsioRecoveryRequested();
    }

    private void StopAsio()
    {
        var session = _asio;
        var recoveryHandler = _asioRecoveryHandler;
        _asio = null;
        _asioRecoveryHandler = null;
        _mic?.SetExternalInputState(false, null, 0, 0);
        _sink?.SetExternalOutputState(false, null, 0, 0);
        if (session is null) return;
        if (recoveryHandler is not null)
            session.RecoveryRequested -= recoveryHandler;
        try { session.Stop(); }
        finally { session.Dispose(); }
    }

    private void StopActive()
    {
        if (_asio is not null || _activeBackend == AudioHostApi.Asio) StopAsio();
        else StopSystem();
    }

    private void CleanupAttempt(AudioHostApi attemptedBackend)
    {
        try
        {
            // _asio being non-null wins even if the active-backend marker was
            // not advanced yet; this is the partially-open ASIO failure case.
            if (_asio is not null || attemptedBackend == AudioHostApi.Asio)
                StopAsio();
            else
                StopSystem();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "audio host cleanup after failed route open also failed");
        }
    }

    private void OnAsioRecoveryRequested()
    {
        if (_stopping || Interlocked.CompareExchange(ref _recoveryScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _changeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_stopping || _activeBackend != AudioHostApi.Asio) return;
                    var settings = _activeSettings;
                    try
                    {
                        StopAsio();
                        StartAsio(settings);
                        Volatile.Write(ref _asioError, null);
                    }
                    catch (Exception ex)
                    {
                        Volatile.Write(ref _asioError, ex.Message);
                        _log.LogError(ex, "audio.asio driver recovery failed");
                        // A reset/error callback cannot trigger another retry after the
                        // failed session has been torn down. Fail over immediately to
                        // System audio so RX/TX is not stranded. Keep _activeSettings
                        // and the persisted row on ASIO: the operator's preference is
                        // retained and a later explicit apply/restart retries it.
                        try
                        {
                            CleanupAttempt(AudioHostApi.Asio);
                            StartSystem(_activeSettings);
                        }
                        catch (Exception fallbackError)
                        {
                            _log.LogError(fallbackError,
                                "audio.asio recovery fallback to System audio also failed");
                        }
                    }
                }
                finally
                {
                    _changeGate.Release();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _recoveryScheduled, 0);
            }
        });
    }

    private static void Validate(AudioDeviceSettings settings)
    {
        if (!Enum.IsDefined(settings.Backend))
            throw new ArgumentOutOfRangeException(nameof(settings), "Unknown audio backend.");
        if (settings.InputChannel is < 0 || settings.AsioInputChannel < 0 || settings.AsioOutputChannel < 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Audio channel indices cannot be negative.");
        if (settings.Backend == AudioHostApi.Asio && string.IsNullOrWhiteSpace(settings.AsioDriverId))
            throw new ArgumentException("An ASIO driver is required when ASIO is selected.", nameof(settings));
    }

    private static bool IsInteropAvailabilityFailure(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        or PlatformNotSupportedException or InvalidOperationException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        try { StopActive(); } catch { /* best effort */ }
        _changeGate.Dispose();
    }
}
