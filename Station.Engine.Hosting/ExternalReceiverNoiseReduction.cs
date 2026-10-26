// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;
using Zeus.Dsp.Wdsp;

namespace Zeus.Server;

internal interface IExternalReceiverNoiseReductionChannel : IDisposable
{
    int WarmupSamples => 0;
    void Apply(NrConfig config, RxMode sourceMode);
    void Feed(ReadOnlySpan<float> mono);
    int Read(Span<float> mono);
}

internal sealed class ExternalReceiverNoiseReduction : IDisposable
{
    private sealed class Channel(NrConfig config, string? modelPath, RxMode sourceMode) : IExternalReceiverNoiseReductionChannel
    {
        private readonly WdspAudioNoiseReduction _dsp = new(config, modelPath, sourceMode);
        // Keep native filter/slew startup out of operator playback.
        public int WarmupSamples => 4800;
        public void Apply(NrConfig config, RxMode sourceMode) => _dsp.Apply(config, sourceMode);
        public void Feed(ReadOnlySpan<float> mono) => _dsp.Feed(mono);
        public int Read(Span<float> mono) => _dsp.Read(mono);
        public void Dispose() => _dsp.Dispose();
    }

    // Only input missing this long stops the channel. A shorter gap (a busy
    // host, a late packet) keeps it: stopping hands playback to unprocessed
    // audio and the restart re-warms the native channel (#3215).
    internal const int InputStallMs = 1_000;
    private readonly object _gate = new();
    private readonly Func<NrMode, string?> _unavailable;
    private readonly Func<NrConfig, RxMode, IExternalReceiverNoiseReductionChannel> _create;
    private readonly ILogger _log;
    private readonly Func<long> _now;
    private IExternalReceiverNoiseReductionChannel? _channel;
    private NrConfig _wanted = new();
    private NrConfig? _applied;
    // Only NNR in CW reads the source mode; any other mode is kept as DSB so
    // a USB/LSB change never re-applies the reducers.
    private RxMode _wantedMode = RxMode.DSB;
    private RxMode _appliedMode = RxMode.DSB;
    private bool _enabled;
    private bool _disposed;
    private string? _lastUnavailable;
    private long _retryAt;
    private long _lastInput;
    private int _warmupRemaining;

    internal ExternalReceiverNoiseReduction(WdspWisdomInitializer? wisdom, Nr3ModelStore? models, ILogger log)
        : this(mode => UnavailableReason(mode, wisdom?.Phase ?? WisdomPhase.Idle,
            () => WdspDspEngine.NativeLibraryLoadable,
            () => WdspDspEngine.Nr3RnnrAvailable, () => WdspDspEngine.Nr4SbnrAvailable,
            () => WdspDspEngine.NnrAvailable, () => models?.GetActiveModelPath() is not null),
            (config, mode) => new Channel(config, models?.GetActiveModelPath(), mode), log) { }

    internal static string? UnavailableReason(NrMode mode, WisdomPhase wisdom, Func<bool> native,
        Func<bool> nr3, Func<bool> nr4, Func<bool> nnr, Func<bool> model) =>
        wisdom != WisdomPhase.Ready ? "wisdom-not-ready"
        : !native() ? "native-library"
        : mode == NrMode.Sbnr && !nr4() ? "nr4-exports"
        : mode == NrMode.Rnnr && (!nr3() || !model()) ? "nr3-model-or-exports"
        : mode == NrMode.Nnr && !nnr() ? "nnr-exports" : null;

    internal ExternalReceiverNoiseReduction(Func<NrMode, string?> unavailable,
        Func<NrConfig, IExternalReceiverNoiseReductionChannel> create, ILogger log, Func<long>? now = null)
        : this(unavailable, (config, _) => create(config), log, now) { }

    internal ExternalReceiverNoiseReduction(Func<NrMode, string?> unavailable,
        Func<NrConfig, RxMode, IExternalReceiverNoiseReductionChannel> create, ILogger log, Func<long>? now = null)
    {
        _unavailable = unavailable;
        _create = create;
        _log = log;
        // Milliseconds; tests drive the stall clock directly.
        _now = now ?? (() => Environment.TickCount64);
    }

    // The return channel has work while NR, the spectral noise blanker or the
    // NB1/NB2 impulse blanker runs; each of them alone also needs processing.
    internal static bool NeedsProcessing(NrConfig config) =>
        config.NrMode != NrMode.Off || config.SnbEnabled || config.NbMode != NbMode.Off;

    // Control and teardown may wait for native configuration; receive never does.
    internal void Configure(bool enabled, NrConfig config, string reason, RxMode sourceMode = RxMode.DSB)
    {
        lock (_gate)
        {
            var mode = WdspAudioNoiseReduction.ChannelMode(config, sourceMode);
            if (_wanted != config || _wantedMode != mode) _retryAt = 0;
            _enabled = enabled;
            _wanted = config;
            _wantedMode = mode;
            if (!enabled || !NeedsProcessing(config)) Stop(reason);
        }
    }

    internal void Feed(ReadOnlySpan<float> mono)
    {
        // Arrival is independent of native creation/configuration. Raw publication
        // continues even when this optional processing copy cannot take the gate.
        Interlocked.Exchange(ref _lastInput, _now());
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed || !_enabled) return;
            _channel?.Feed(mono);
        }
        finally { Monitor.Exit(_gate); }
    }

    internal (bool Active, NrMode Mode, int? LatencyMs, int Samples) Read(Span<float> output)
    {
        lock (_gate)
        {
            if (_disposed || !_enabled || !NeedsProcessing(_wanted))
                return (false, _wanted.NrMode, null, 0);
            long lastInput = Interlocked.Read(ref _lastInput);
            if (lastInput != 0 && _now() - lastInput > InputStallMs)
            {
                Stop("input-stalled");
                return (false, _wanted.NrMode, null, 0);
            }
            try
            {
                string? unavailable = _channel is null || _wanted.NrMode != _applied?.NrMode
                    ? _unavailable(_wanted.NrMode) : null;
                if (unavailable is not null)
                {
                    Stop(unavailable);
                    Unavailable(unavailable);
                    return (false, _wanted.NrMode, null, 0);
                }
                if (_channel is null)
                {
                    if (_now() < _retryAt) return (false, _wanted.NrMode, null, 0);
                    _channel = _create(_wanted, _wantedMode);
                    // Native open/model load can exceed the stall budget. Give the
                    // newly opened channel a full input interval to start.
                    Interlocked.Exchange(ref _lastInput, _now());
                    _applied = _wanted;
                    _appliedMode = _wantedMode;
                    _warmupRemaining = _channel.WarmupSamples;
                    _lastUnavailable = null;
                    _log.LogInformation("external_receiver.nr.started mode={Mode} latencyMs={LatencyMs}", _wanted.NrMode, (int?)null);
                }
                if (_wanted != _applied || _wantedMode != _appliedMode)
                {
                    // A new reducer, or NNR moving between its voice and CW
                    // networks, restarts the native path: keep that out of playback.
                    if (_wanted.NrMode != _applied?.NrMode || _wantedMode != _appliedMode)
                        _warmupRemaining = _channel.WarmupSamples;
                    _channel.Apply(_wanted, _wantedMode);
                    _applied = _wanted;
                    _appliedMode = _wantedMode;
                }
                int samples = _channel.Read(output);
                int warmup = Math.Min(samples, _warmupRemaining);
                _warmupRemaining -= warmup;
                output.Slice(warmup, samples - warmup).CopyTo(output);
                samples -= warmup;
                return (true, _wanted.NrMode, null, samples);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                or BadImageFormatException or InvalidOperationException)
            {
                Stop("native-error");
                Unavailable("native-error");
                _retryAt = _now() + 60_000;
                return (false, _wanted.NrMode, null, 0);
            }
        }
    }

    private void Unavailable(string reason)
    {
        if (_lastUnavailable == reason) return;
        _lastUnavailable = reason;
        _log.LogInformation("external_receiver.nr.unavailable reason={Reason}", reason);
    }

    private void Stop(string reason)
    {
        if (_channel is null) return;
        _channel.Dispose();
        _channel = null;
        _applied = null;
        _log.LogInformation("external_receiver.nr.stopped reason={Reason}", reason);
    }

    internal void ResetModel()
    {
        lock (_gate)
        {
            _retryAt = 0;
            Stop("model-changed");
            WdspAudioNoiseReduction.InvalidateModelCache();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Stop("detached");
        }
    }
}
