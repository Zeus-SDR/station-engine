// SPDX-License-Identifier: GPL-2.0-or-later
//
// Dedicated capture route for TX Testing Tools virtual audio cables. This is
// intentionally separate from NativeMicCapture: selecting it must never alter
// the operator's microphone device or radio audio-source setting.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>
/// Opens a selected virtual audio cable and feeds its real-time PCM into the
/// ordinary TX processing path while the operator has MOX/PTT keyed. This
/// service never keys MOX and starts disabled on every application launch.
/// </summary>
internal sealed class VirtualCableTxCapture : IHostedService, IDisposable
{
    private const int BlockSamples = 960;
    private const int BlockBytes = BlockSamples * sizeof(float);
    private const int QueueBlocks = 4;
    private static readonly TimeSpan MaximumBlockAge = TimeSpan.FromMilliseconds(80);

    private readonly TxAudioIngest _ingest;
    private readonly AudioDeviceSettingsStore _settings;
    private readonly INativeMicInputFactory _inputFactory;
    private readonly ILogger<VirtualCableTxCapture> _log;
    private readonly object _deviceSync = new();
    private readonly SemaphoreSlim _configureGate = new(1, 1);
    private readonly object _captureSync = new();
    private readonly object _queueSync = new();
    private readonly AutoResetEvent _queueReady = new(false);
    private readonly float[][] _queue = CreateQueue();
    private readonly long[] _queueGenerations = new long[QueueBlocks];
    private readonly long[] _queueTimestamps = new long[QueueBlocks];
    private readonly Thread _worker;
    private readonly byte[] _payload = new byte[BlockBytes];
    private readonly Func<long, long, bool> _blockValidator;

    private INativeMicInput? _input;
    private string? _activeInputDeviceId;
    private string? _error;
    private float[] _accum = new float[BlockSamples];
    private int _accumFill;
    private float[] _workerBlock = new float[BlockSamples];
    private int _queueRead;
    private int _queueWrite;
    private int _queueCount;
    private int _enabled;
    private int _stopping;
    private long _generation;
    private bool _disposed;

    public VirtualCableTxCapture(
        TxAudioIngest ingest,
        AudioDeviceSettingsStore settings,
        ILogger<VirtualCableTxCapture> log)
        : this(ingest, settings, log, new NativeMicInputFactory())
    {
    }

    internal VirtualCableTxCapture(
        TxAudioIngest ingest,
        AudioDeviceSettingsStore settings,
        ILogger<VirtualCableTxCapture> log,
        INativeMicInputFactory inputFactory)
    {
        _ingest = ingest;
        _settings = settings;
        _log = log;
        _inputFactory = inputFactory;
        _blockValidator = IsBlockCurrent;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "zeus-virtual-cable-tx",
        };
        _worker.Start();
    }

    public bool Enabled => Volatile.Read(ref _enabled) != 0;
    public string? ConfiguredInputDeviceId => _settings.GetVirtualCableInputDeviceId();
    public string? ActiveInputDeviceId { get { lock (_deviceSync) return _activeInputDeviceId; } }
    public string? Error => Volatile.Read(ref _error);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _configureGate.Wait(cancellationToken);
        try
        {
            Disable(preserveSelection: true);
            lock (_queueSync)
            {
                _stopping = 1;
                _queueCount = 0;
                _queueReady.Set();
            }
        }
        finally
        {
            _configureGate.Release();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Changes only the test-tools route. A missing or failed selection leaves
    /// the ordinary audio input alone and does not activate the cable source.
    /// </summary>
    public async Task ConfigureAsync(bool enabled, string? inputDeviceId, CancellationToken cancellationToken = default)
    {
        await _configureGate.WaitAsync(cancellationToken);
        try
        {
        string? normalized = Normalize(inputDeviceId);
        _settings.SetVirtualCableInputDeviceId(normalized);
        Disable(preserveSelection: true);
        if (!enabled) return;
        if (normalized is null)
        {
            Volatile.Write(ref _error, "Select a virtual cable input first.");
            return;
        }

        lock (_deviceSync)
        {
            if (_disposed) return;
            INativeMicInput? input = null;
            try
            {
                int callbackGeneration = checked((int)Interlocked.Read(ref _generation));
                input = _inputFactory.Create(
                    OnCaptureData,
                    kind => OnDeviceNotification(kind, callbackGeneration),
                    normalized);
                input.Start();
                _input = input;
                _activeInputDeviceId = normalized;
                Volatile.Write(ref _error, null);
                Volatile.Write(ref _enabled, 1);
                _ingest.SetVirtualCableEnabled(true);
                _log.LogInformation("audio.virtual-cable.tx capture opened device={DeviceId} rate={Rate}Hz channels={Channels}",
                    normalized, input.SampleRate, input.Channels);
            }
            catch (Exception ex)
            {
                try { input?.Stop(); }
                catch (Exception stopEx) { _log.LogWarning(stopEx, "audio.virtual-cable.tx failed input stop threw"); }
                try { input?.Dispose(); }
                catch (Exception disposeEx) { _log.LogWarning(disposeEx, "audio.virtual-cable.tx failed input dispose threw"); }
                Volatile.Write(ref _error, $"Unable to open the selected virtual cable: {ex.Message}");
                _log.LogWarning(ex, "audio.virtual-cable.tx capture open failed device={DeviceId}", normalized);
            }
        }
        }
        finally
        {
            _configureGate.Release();
        }
    }

    internal void OnCaptureData(ReadOnlySpan<float> input, uint frameCount, uint channels)
    {
        if (!Enabled || frameCount == 0 || channels == 0) return;
        lock (_captureSync)
        {
            if (!Enabled) return;
            long generation = Interlocked.Read(ref _generation);
            int frames = checked((int)frameCount);
            int channelCount = checked((int)channels);
            int source = 0;
            while (source < frames)
            {
                int take = Math.Min(BlockSamples - _accumFill, frames - source);
                if (channelCount == 1)
                {
                    for (int i = 0; i < take; i++)
                        _accum[_accumFill + i] = NativeMicCapture.SanitizeCapturedSample(input[source + i]);
                }
                else
                {
                    float inverseChannels = 1f / channelCount;
                    for (int i = 0; i < take; i++)
                        _accum[_accumFill + i] = NativeMicCapture.DownmixCapturedFrame(
                            input, (source + i) * channelCount, channelCount, inverseChannels);
                }
                _accumFill += take;
                source += take;
                if (_accumFill == BlockSamples)
                    EnqueueAccumulatedBlock(generation);
            }
        }
    }

    private void EnqueueAccumulatedBlock(long generation)
    {
        lock (_queueSync)
        {
            if (!Enabled || generation != Interlocked.Read(ref _generation) || _stopping != 0)
            {
                _accumFill = 0;
                return;
            }
            if (_queueCount == QueueBlocks)
            {
                _queueRead = (_queueRead + 1) % QueueBlocks;
                _queueCount--;
            }
            var replacement = _queue[_queueWrite];
            _queue[_queueWrite] = _accum;
            _accum = replacement;
            _queueGenerations[_queueWrite] = generation;
            _queueTimestamps[_queueWrite] = System.Diagnostics.Stopwatch.GetTimestamp();
            _queueWrite = (_queueWrite + 1) % QueueBlocks;
            _queueCount++;
            _accumFill = 0;
            _queueReady.Set();
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            long generation = 0;
            long timestamp = 0;
            bool haveBlock = false;
            lock (_queueSync)
            {
                if (_queueCount != 0)
                {
                    var replacement = _queue[_queueRead];
                    _queue[_queueRead] = _workerBlock;
                    _workerBlock = replacement;
                    generation = _queueGenerations[_queueRead];
                    timestamp = _queueTimestamps[_queueRead];
                    _queueRead = (_queueRead + 1) % QueueBlocks;
                    _queueCount--;
                    haveBlock = true;
                }
                else if (_stopping != 0) return;
            }
            if (!haveBlock)
            {
                _queueReady.WaitOne();
                continue;
            }
            if (!IsBlockCurrent(generation, timestamp)) continue;
            MemoryMarshal.AsBytes(_workerBlock.AsSpan()).CopyTo(_payload);
            if (!IsBlockCurrent(generation, timestamp)) continue;
            try
            {
                _ingest.OnMicPcmBytesFromVirtualCable(
                    _payload,
                    new MicBlockValidity(_blockValidator, generation, timestamp));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "audio.virtual-cable.tx ingest threw on flush");
            }
        }
    }

    private void Disable(bool preserveSelection)
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _enabled, 0);
        _ingest.SetVirtualCableEnabled(false);
        lock (_captureSync) _accumFill = 0;
        lock (_queueSync) _queueCount = 0;
        lock (_deviceSync)
        {
            try { _input?.Stop(); }
            catch (Exception ex) { _log.LogWarning(ex, "audio.virtual-cable.tx stop threw"); }
            try { _input?.Dispose(); }
            catch (Exception ex) { _log.LogWarning(ex, "audio.virtual-cable.tx dispose threw"); }
            _input = null;
            _activeInputDeviceId = null;
        }
        if (!preserveSelection) _settings.SetVirtualCableInputDeviceId(null);
    }

    private void OnDeviceNotification(int kind, int callbackGeneration)
    {
        if (kind != 2 || callbackGeneration != Interlocked.Read(ref _generation)) return;
        Volatile.Write(ref _error, "The virtual cable stopped. Enable it again after the device is available.");
        _ = Task.Run(async () =>
        {
            await _configureGate.WaitAsync();
            try { Disable(preserveSelection: true); }
            finally { _configureGate.Release(); }
        });
    }

    private bool IsBlockCurrent(long generation, long timestamp) =>
        Enabled
        && generation == Interlocked.Read(ref _generation)
        && System.Diagnostics.Stopwatch.GetElapsedTime(timestamp) <= MaximumBlockAge;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (_worker != Thread.CurrentThread) _worker.Join(TimeSpan.FromSeconds(2));
        _queueReady.Dispose();
        _configureGate.Dispose();
    }

    private static float[][] CreateQueue()
    {
        var queue = new float[QueueBlocks][];
        for (int i = 0; i < queue.Length; i++) queue[i] = new float[BlockSamples];
        return queue;
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
