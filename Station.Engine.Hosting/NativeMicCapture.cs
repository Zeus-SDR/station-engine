// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// Desktop-mode TX mic capture: replaces the browser → WS MicPcm uplink with
// a direct miniaudio capture device feed into TxAudioIngest. The two paths
// share the existing TxAudioIngest entry point (OnMicPcmBytes) so the WDSP
// TXA chain, IQ ring, and protocol packers don't know which transport
// supplied the audio.
//
// Frame shape: f32le little-endian, 960 samples mono @ 48 kHz (20 ms
// blocks), matching the browser worklet's MicPcm payload exactly. The
// AudioWorklet on the SPA emits the same shape; this just produces it from
// the selected OS input device instead of getUserMedia.

using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Desktop-mode hosted service that opens the selected OS input device via
/// miniaudio and feeds 960-sample (20 ms @ 48 kHz mono) f32le blocks into
/// <see cref="TxAudioIngest.OnMicPcmBytes"/>. Service mode never registers
/// this service so the .dylib never loads from the server-only build.
///
/// <para>The mic device runs continuously once started; gating on MOX /
/// monitor state happens downstream in <see cref="TxAudioIngest"/>, exactly
/// as the existing browser path does. This keeps the capture warm so the
/// first key-up doesn't lose the first ~20 ms of audio waiting for the OS
/// to spin up the input device.</para>
///
/// <para>Downmix: when the OS hands us a stereo / multichannel input
/// (USB mics often default to stereo), the default remains averaging across
/// channels into a mono sample. An operator may instead select one channel.
/// Resample: miniaudio's internal resampler converts to 48 kHz
/// if the device's native rate is something else (44.1 kHz common on Mac
/// laptops). Both happen behind the data callback so this code only deals
/// with 48 kHz mono frames.</para>
/// </summary>
internal sealed class NativeMicCapture : IHostedService, IDisposable
{
    private const int MicBlockSamples = 960;        // 20 ms @ 48 kHz, matches browser worklet
    private const int MicBlockBytes = MicBlockSamples * 4;

    // A native capture-device open can block inside miniaudio indefinitely when
    // Windows reports a stale / exclusive / malformed endpoint. Keep that off
    // the host startup path and bound individual selected/default attempts.
    private static readonly TimeSpan DeviceOpenTimeout = TimeSpan.FromSeconds(5);

    private readonly TxAudioIngest _ingest;
    private readonly ITxAudioPreviewProcessor _previewProcessor;
    private readonly ILogger<NativeMicCapture> _log;
    private readonly AudioDeviceSettingsStore? _deviceSettings;
    private readonly INativeMicInputFactory _inputFactory;
    private readonly StreamingHub? _streamingHub;

    private INativeMicInput? _input;
    private readonly object _deviceSync = new();
    private volatile bool _shutdown;
    private volatile bool _intentionalStop;
    private int _deviceGeneration;
    private int _recoveryEpoch;
    private int _recoveryScheduled = -1;
    private readonly CancellationTokenSource _recoveryCancellation = new();
    private readonly CancellationToken _recoveryToken;
    private bool _disposed;
    private string? _activeInputDeviceId;
    private string? _inputError;
    private int _sampleRate;
    private int _channels;
    // -1 means the compatibility default: mix every capture channel.
    private int _inputChannel = -1;
    private readonly float[] _accum = new float[MicBlockSamples];
    private int _accumFill;
    // Which capture channel the partially filled block was sourced from, so a
    // selection change mid-block is discarded rather than spliced. -1 = mix all.
    private int _accumChannel = -1;
    // Pre-allocated payload buffer reused for every flush; OnMicPcmBytes
    // takes ReadOnlyMemory<byte> but doesn't retain it, so reuse is safe.
    private readonly byte[] _payload = new byte[MicBlockBytes];

    private long _totalSamplesIn;
    private long _totalBlocksOut;

    // Suppression counter for plugin-preview faults so a buggy plugin
    // can't spam the log from the miniaudio worker thread. Matches the
    // 4-suppression pattern used at the WDSP TX seam.
    private int _previewErrLogged;

    public NativeMicCapture(
        TxAudioIngest ingest,
        ITxAudioPreviewProcessor previewProcessor,
        ILogger<NativeMicCapture> log,
        AudioDeviceSettingsStore? deviceSettings = null,
        StreamingHub? streamingHub = null)
    {
        _ingest = ingest;
        _previewProcessor = previewProcessor;
        _log = log;
        _deviceSettings = deviceSettings;
        _streamingHub = streamingHub;
        _inputFactory = new NativeMicInputFactory();
        _recoveryToken = _recoveryCancellation.Token;
        _inputChannel = deviceSettings?.Get().InputChannel ?? -1;
    }

    internal NativeMicCapture(
        TxAudioIngest ingest,
        ITxAudioPreviewProcessor previewProcessor,
        ILogger<NativeMicCapture> log,
        AudioDeviceSettingsStore? deviceSettings,
        INativeMicInputFactory inputFactory,
        StreamingHub? streamingHub = null)
        : this(ingest, previewProcessor, log, deviceSettings, streamingHub)
    {
        _inputFactory = inputFactory;
    }

    public string? ConfiguredInputDeviceId => _deviceSettings?.Get().InputDeviceId;
    public string? ActiveInputDeviceId => _activeInputDeviceId;
    public string? InputError => Volatile.Read(ref _inputError);
    public uint SampleRate => (uint)Math.Max(0, Volatile.Read(ref _sampleRate));
    public uint Channels => (uint)Math.Max(0, Volatile.Read(ref _channels));
    public int? InputChannel
    {
        get
        {
            int channel = Volatile.Read(ref _inputChannel);
            return channel >= 0 ? channel : null;
        }
    }

    /// <summary>
    /// Capture-flow snapshot for remote diagnostics. Distinguishes "capture
    /// open but no callbacks arriving" (counters frozen) from "capture flowing
    /// but the device carries silence" (counters advancing while the mic meter
    /// sits at the floor) — without this both collapse into a -120 dBFS meter.
    /// Single writer (the miniaudio callback thread); Interlocked.Read keeps
    /// the 64-bit reads tear-free on 32-bit targets.
    /// </summary>
    public NativeMicCaptureDiagnostics GetDiagnostics() => new(
        TotalSamplesIn: Interlocked.Read(ref _totalSamplesIn),
        TotalBlocksOut: Interlocked.Read(ref _totalBlocksOut));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configuredDeviceId = ConfiguredInputDeviceId;
        _log.LogInformation(
            "audio.native.tx CAPTURE DEVICE startup resolved={DeviceId}",
            configuredDeviceId ?? "system-default");

        var thread = new Thread(() =>
        {
            lock (_deviceSync)
            {
                if (_shutdown || _input != null) return;
                OpenInputLocked(configuredDeviceId);
            }
        })
        {
            IsBackground = true,
            Name = "zeus-mic-start",
        };
        thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_deviceSync)
        {
            _shutdown = true;
            _intentionalStop = true;
            Interlocked.Increment(ref _recoveryEpoch);
            Interlocked.Exchange(ref _recoveryScheduled, -1);
            Interlocked.Increment(ref _deviceGeneration);
            _recoveryCancellation.Cancel();
            try { _input?.Stop(); }
            catch (Exception ex) { _log.LogWarning(ex, "audio.native.tx stop threw"); }
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_deviceSync)
        {
            _shutdown = true;
            _intentionalStop = true;
            Interlocked.Increment(ref _recoveryEpoch);
            Interlocked.Exchange(ref _recoveryScheduled, -1);
            Interlocked.Increment(ref _deviceGeneration);
            _recoveryCancellation.Cancel();
            CloseInputLocked(dispose: true);
        }
        _recoveryCancellation.Dispose();
    }

    public Task SetInputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        string? normalized = NormalizeDeviceId(deviceId);
        _deviceSettings?.SetInputDeviceId(normalized);

        lock (_deviceSync)
        {
            _intentionalStop = true;
            Interlocked.Increment(ref _recoveryEpoch);
            Interlocked.Exchange(ref _recoveryScheduled, -1);
            Interlocked.Increment(ref _deviceGeneration);
            try
            {
                CloseInputLocked(dispose: true);
                ResetAccumulation();
                OpenInputLocked(normalized);
            }
            finally
            {
                _intentionalStop = false;
            }
        }

        return Task.CompletedTask;
    }

    public Task SetInputChannelAsync(
        int? inputChannel,
        CancellationToken cancellationToken = default)
    {
        if (inputChannel is < 0)
            throw new ArgumentOutOfRangeException(nameof(inputChannel));

        _deviceSettings?.SetInputChannel(inputChannel);
        Volatile.Write(ref _inputChannel, inputChannel ?? -1);
        return Task.CompletedTask;
    }

    private void OpenInputLocked(string? requestedDeviceId)
    {
        var selected = !string.IsNullOrWhiteSpace(requestedDeviceId);
        var input = OpenInputTimeboxed(requestedDeviceId, out var failure);
        if (input != null)
        {
            AdoptInputLocked(input, requestedDeviceId);
            return;
        }

        LogOpenGaveUp(selected ? "selected" : "default", failure);
        if (!selected)
        {
            Volatile.Write(
                ref _inputError,
                "No usable microphone input is available. Select an input device or retry.");
            _log.LogWarning("audio.native.tx TX uplink disabled (no usable mic input)");
            ScheduleStartupOpenRecovery(null);
            return;
        }

        var fallback = OpenInputTimeboxed(null, out var fallbackFailure);
        if (fallback != null)
        {
            AdoptInputLocked(fallback, null);
            return;
        }

        LogOpenGaveUp("default fallback", fallbackFailure);
        Volatile.Write(
            ref _inputError,
            "Neither the selected microphone nor the system default input could be opened. TX microphone audio is unavailable.");
        _log.LogWarning("audio.native.tx TX uplink disabled (no usable mic input)");
        ScheduleStartupOpenRecovery(requestedDeviceId);
    }

    // Startup-open recovery: a failed FIRST open must not park the uplink
    // until a relaunch. On macOS a fresh unsigned build re-prompts for the
    // mic and the 5 s open timebox loses the race against the operator's
    // answer — the 2026-07-23 field failure where a working mic died right
    // after an upgrade with TCC already granted. Retry patiently on a
    // background task with the same single-flight and abandon semantics as
    // the device-stop recovery; a device change or shutdown abandons it.
    private static readonly TimeSpan[] StartupOpenBackoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    /// <summary>Test seam: the startup-open recovery pacing (instant in tests).</summary>
    internal Func<TimeSpan, CancellationToken, Task> StartupRecoveryDelay { get; set; } =
        static (delay, ct) => Task.Delay(delay, ct);

    private void ScheduleStartupOpenRecovery(string? configuredDeviceId)
    {
        if (_shutdown) return;
        int recoveryEpoch = Volatile.Read(ref _recoveryEpoch);
        if (Interlocked.CompareExchange(ref _recoveryScheduled, recoveryEpoch, -1) != -1) return;
        var configuredDeviceLabel = configuredDeviceId ?? "default";
        var delay = StartupRecoveryDelay;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await DeviceStopRecovery.RunAsync(
                    delay,
                    (attempt, ct) =>
                    {
                        lock (_deviceSync)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (_shutdown
                                || _intentionalStop
                                || recoveryEpoch != Volatile.Read(ref _recoveryEpoch))
                            {
                                // Abandoned: stop retrying but report success so
                                // the loop simply ends.
                                return Task.FromResult(true);
                            }
                            _log.LogWarning(
                                "audio.native.tx startup-open recovery attempt={Attempt} configuredDeviceId={ConfiguredDeviceId}",
                                attempt, configuredDeviceLabel);
                            _intentionalStop = true;
                            try
                            {
                                Interlocked.Increment(ref _deviceGeneration);
                                CloseInputLocked(dispose: true);
                                ResetAccumulation();
                                OpenInputLocked(configuredDeviceId);
                                bool recovered = _input is not null;
                                if (recovered)
                                    _log.LogInformation(
                                        "audio.native.tx startup-open recovery succeeded attempt={Attempt} configuredDeviceId={ConfiguredDeviceId}",
                                        attempt, configuredDeviceLabel);
                                return Task.FromResult(recovered);
                            }
                            finally
                            {
                                _intentionalStop = false;
                            }
                        }
                    },
                    _recoveryToken,
                    StartupOpenBackoff).ConfigureAwait(false);

                if (result == DeviceRecoveryResult.GaveUp)
                    _log.LogError(
                        "audio.native.tx startup-open recovery gave up configuredDeviceId={ConfiguredDeviceId}; change the input selection or relaunch to retry",
                        configuredDeviceLabel);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "audio.native.tx startup-open recovery worker failed configuredDeviceId={ConfiguredDeviceId}",
                    configuredDeviceLabel);
            }
            finally
            {
                Interlocked.CompareExchange(ref _recoveryScheduled, -1, recoveryEpoch);
            }
        });
    }

    private INativeMicInput? OpenInputTimeboxed(string? requestedDeviceId, out Exception? failure) =>
        TimeboxedNativeOpen.Run(
            () =>
            {
                INativeMicInput? input = null;
                try
                {
                    input = CreateInput(requestedDeviceId);
                    input.Start();
                    return input;
                }
                catch
                {
                    if (input != null)
                    {
                        // Dispose after a failed open is intentional; stale
                        // stopped callbacks from this wrapper must not recover.
                        Interlocked.Increment(ref _deviceGeneration);
                        input.Dispose();
                    }
                    throw;
                }
            },
            input =>
            {
                Interlocked.Increment(ref _deviceGeneration);
                input.Dispose();
            },
            DeviceOpenTimeout,
            out failure);

    private void AdoptInputLocked(INativeMicInput input, string? requestedDeviceId)
    {
        _input = input;
        Volatile.Write(ref _inputError, null);
        _activeInputDeviceId = NormalizeDeviceId(requestedDeviceId) ?? ResolveDefaultInputDeviceId();
        Volatile.Write(ref _sampleRate, checked((int)input.SampleRate));
        Volatile.Write(ref _channels, checked((int)input.Channels));
        _log.LogInformation(
            "audio.native.tx CAPTURE DEVICE resolved={DeviceId} source={Source} rate={Rate}Hz channels={Channels}",
            _activeInputDeviceId ?? "system-default",
            requestedDeviceId is null ? "default" : "saved-selection",
            input.SampleRate, input.Channels);
    }

    private string? ResolveDefaultInputDeviceId()
    {
        try
        {
            return NormalizeDeviceId(_inputFactory.ResolveDefaultInputDeviceId());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "audio.native.tx could not resolve active default input id");
            return null;
        }
    }

    private void LogOpenGaveUp(string which, Exception? failure)
    {
        if (failure != null)
        {
            _log.LogWarning(failure, "audio.native.tx {Which} mic open failed", which);
            return;
        }

        _log.LogWarning(
            "audio.native.tx {Which} mic open timed out after {Timeout:0.#}s; not blocking host startup",
            which, DeviceOpenTimeout.TotalSeconds);
    }

    private INativeMicInput CreateInput(string? deviceId)
    {
        int generation = Interlocked.Increment(ref _deviceGeneration);
        return _inputFactory.Create(
            OnCaptureData,
            kind => OnDeviceNotification(kind, generation),
            NormalizeDeviceId(deviceId));
    }

    private void CloseInputLocked(bool dispose)
    {
        try { _input?.Stop(); }
        catch (Exception ex) { _log.LogWarning(ex, "audio.native.tx stop threw"); }
        if (dispose)
        {
            try { _input?.Dispose(); }
            catch (Exception ex) { _log.LogWarning(ex, "audio.native.tx dispose threw"); }
        }
        _input = null;
        _activeInputDeviceId = null;
        Volatile.Write(ref _sampleRate, 0);
        Volatile.Write(ref _channels, 0);
    }

    private void ResetAccumulation()
    {
        _accumFill = 0;
        _accumChannel = Volatile.Read(ref _inputChannel);
    }

    // Internal for tests: lets the capture-diagnostics test drive the
    // miniaudio data-callback seam directly with synthetic frames.
    internal void OnCaptureData(ReadOnlySpan<float> input, uint frameCount, uint channels)
    {
        int frames = (int)frameCount;
        int ch = (int)channels;
        if (frames == 0 || ch == 0) return;

        // Downmix to mono on the fly. For ch==1 this is a straight copy
        // through the accumulator. The engine-owned TxMicMeterService observes
        // the completed block at TxAudioIngest, after every capture transport
        // has converged on the same source-aware seam.
        int srcIdx = 0;
        int selectedChannel = Volatile.Read(ref _inputChannel);

        // Snapshotting once per callback is not enough on its own. _accumFill
        // survives across callbacks, and most backends hand us less than one
        // 960-sample block at a time — WASAPI shared mode is typically 480
        // frames, ALSA/PulseAudio periods are often 256-512 — so a single
        // on-air block is usually assembled from two or more callbacks. If the
        // operator switched channel between them we would flush a block that
        // splices two different mixer channels together, which radiates as a
        // broadband click. Discard the partial block instead: the cost is under
        // 20 ms of audio on a deliberate channel change, and never a click.
        if (selectedChannel != _accumChannel)
        {
            _accumFill = 0;
            _accumChannel = selectedChannel;
        }

        while (srcIdx < frames)
        {
            int needBeforeFlush = MicBlockSamples - _accumFill;
            int take = Math.Min(needBeforeFlush, frames - srcIdx);

            if (ch == 1)
            {
                for (int i = 0; i < take; i++)
                {
                    float sample = SanitizeCapturedSample(input[srcIdx + i]);
                    _accum[_accumFill + i] = sample;
                }
            }
            else if (selectedChannel >= 0 && selectedChannel < ch)
            {
                for (int i = 0; i < take; i++)
                {
                    // Capture channels are zero-based internally and on the
                    // API wire; the SPA presents them as Channel 1, 2, ...
                    float sample = SanitizeCapturedSample(
                        input[((srcIdx + i) * ch) + selectedChannel]);
                    _accum[_accumFill + i] = sample;
                }
            }
            else
            {
                // Average across channels for each frame in this batch.
                float invCh = 1.0f / ch;
                for (int i = 0; i < take; i++)
                {
                    float mono = DownmixCapturedFrame(input, (srcIdx + i) * ch, ch, invCh);
                    _accum[_accumFill + i] = mono;
                }
            }

            _accumFill += take;
            srcIdx += take;
            Interlocked.Add(ref _totalSamplesIn, take);

            if (_accumFill >= MicBlockSamples)
            {
                // Pre-MOX plugin meter preview tap — runs the audio plugin
                // chain on the same 960-sample mono block we're about to
                // hand to TxAudioIngest so per-plugin IN / OUT / GR meters
                // animate from live mic regardless of MOX state. The bridge
                // short-circuits internally when MOX or TX monitor is on
                // (the WDSP TX path is the canonical chain runner in those
                // cases), or when no plugins are attached. Output samples
                // are discarded — TxAudioIngest still gets the unmodified
                // mic block via FlushBlock, so the on-air audio path is
                // bit-identical to the no-preview case.
                // Only run the host-mic plugin preview when Host is the active
                // source. When a radio jack is selected the host mic is off the
                // air, so its per-plugin IN / OUT / GR preview meters must not
                // animate from it either (same source-awareness as the level
                // meter above).
                if (_ingest.ActiveSource == MicBlockSource.Host)
                {
                    try
                    {
                        _previewProcessor.ProcessPreview(
                            new ReadOnlySpan<float>(_accum, 0, MicBlockSamples),
                            sampleRate: 48_000);
                    }
                    catch (Exception ex)
                    {
                        if (++_previewErrLogged <= 4)
                            _log.LogWarning(ex,
                                "audio.native.tx plugin preview threw (suppressed after 4)");
                    }
                }

                FlushBlock();
                _accumFill = 0;
            }
        }
    }

    private void FlushBlock()
    {
        // Encode f32le. On x64 / arm64 .NET targets, the host float endianness
        // matches the wire format (little-endian), so MemoryMarshal.AsBytes
        // produces the wanted payload directly. BinaryPrimitives is the safe
        // alternative if we ever target a big-endian runtime, but Zeus targets
        // none today.
        var srcBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<float>(_accum, 0, MicBlockSamples));
        srcBytes.CopyTo(_payload);

        try
        {
            _ingest.OnMicPcmBytesFromMic(new ReadOnlyMemory<byte>(_payload));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "audio.native.tx ingest threw on flush");
        }
        _streamingHub?.BroadcastNativeMicPcm(_payload);
        Interlocked.Increment(ref _totalBlocksOut);
    }

    private void OnDeviceNotification(int kind, int generation)
    {
        string label = kind switch
        {
            1 => "started",
            2 => "stopped",
            3 => "rerouted (default device changed)",
            4 => "interruption_began",
            5 => "interruption_ended",
            6 => "unlocked",
            _ => $"kind={kind}",
        };
        _log.LogInformation("audio.native.tx event {Event}", label);
        if (kind == 3 && ConfiguredInputDeviceId is null)
            _activeInputDeviceId = ResolveDefaultInputDeviceId();
        if (kind == 2) ScheduleRecovery(generation);
    }

    private void ScheduleRecovery(int stoppedGeneration)
    {
        if (!DeviceStopRecovery.ShouldSchedule(
                _shutdown,
                _intentionalStop,
                stoppedGeneration,
                Volatile.Read(ref _deviceGeneration))) return;

        int recoveryEpoch = Volatile.Read(ref _recoveryEpoch);
        if (Interlocked.CompareExchange(ref _recoveryScheduled, recoveryEpoch, -1) != -1) return;

        // Device selection can change between the first lock-free check and
        // winning the single-flight flag. Revalidate after capturing its
        // epoch so a stale callback can never reopen the newly selected route.
        if (!DeviceStopRecovery.ShouldSchedule(
                _shutdown,
                _intentionalStop,
                stoppedGeneration,
                Volatile.Read(ref _deviceGeneration)))
        {
            Interlocked.CompareExchange(ref _recoveryScheduled, -1, recoveryEpoch);
            return;
        }
        _ = Task.Run(async () =>
        {
            string? configuredDeviceId = null;
            string configuredDeviceLabel = "default";
            try
            {
                configuredDeviceId = NormalizeDeviceId(ConfiguredInputDeviceId);
                configuredDeviceLabel = configuredDeviceId ?? "default";
                bool abandoned = false;
                var result = await DeviceStopRecovery.RunAsync(
                    static (delay, ct) => Task.Delay(delay, ct),
                    (attempt, ct) =>
                    {
                        lock (_deviceSync)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (_shutdown || _intentionalStop || recoveryEpoch != Volatile.Read(ref _recoveryEpoch))
                            {
                                abandoned = true;
                                return Task.FromResult(true);
                            }

                            _log.LogWarning(
                                "audio.native.tx device-stop recovery attempt={Attempt} configuredDeviceId={ConfiguredDeviceId}",
                                attempt, configuredDeviceLabel);
                            _intentionalStop = true;
                            try
                            {
                                Interlocked.Increment(ref _deviceGeneration);
                                CloseInputLocked(dispose: true);
                                ResetAccumulation();
                                OpenInputLocked(configuredDeviceId);
                                bool recovered = _input is not null;
                                if (recovered)
                                    _log.LogInformation(
                                        "audio.native.tx device-stop recovery succeeded attempt={Attempt} configuredDeviceId={ConfiguredDeviceId}",
                                        attempt, configuredDeviceLabel);
                                return Task.FromResult(recovered);
                            }
                            finally
                            {
                                _intentionalStop = false;
                            }
                        }
                    },
                    _recoveryToken).ConfigureAwait(false);

                if (!abandoned && result == DeviceRecoveryResult.GaveUp)
                    _log.LogError(
                        "audio.native.tx device-stop recovery gave up after 3 attempts configuredDeviceId={ConfiguredDeviceId}",
                        configuredDeviceLabel);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "audio.native.tx device-stop recovery worker failed configuredDeviceId={ConfiguredDeviceId}",
                    configuredDeviceLabel);
            }
            finally
            {
                Interlocked.CompareExchange(ref _recoveryScheduled, -1, recoveryEpoch);
            }
        });
    }

    internal static float SanitizeCapturedSample(float sample)
        => DspPipelineService.SanitizeAudioSample(sample);

    private static string? NormalizeDeviceId(string? deviceId)
    {
        var trimmed = deviceId?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    internal static float DownmixCapturedFrame(
        ReadOnlySpan<float> interleavedSamples,
        int frameBaseIndex,
        int channels,
        float inverseChannels)
    {
        float sum = 0f;
        for (int c = 0; c < channels; c++)
            sum += SanitizeCapturedSample(interleavedSamples[frameBaseIndex + c]);
        return SanitizeCapturedSample(sum * inverseChannels);
    }
}

/// <summary>Point-in-time capture-flow counters surfaced on GET /api/audio/devices.</summary>
internal sealed record NativeMicCaptureDiagnostics(long TotalSamplesIn, long TotalBlocksOut);
