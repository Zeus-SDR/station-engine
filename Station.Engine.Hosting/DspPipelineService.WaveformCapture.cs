// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Dsp;
using System.Buffers.Binary;

namespace Zeus.Server;

public partial class DspPipelineService
{
    private long _txCaptureTapUntil;
    private readonly WaveformSnapshotCache _micWaveform = new();
    private readonly WaveformSnapshotCache _monitorWaveform = new();
    private sealed record MonitorCaptureContext(WaveformContext Signal, MicBlockSource Source);

    private MonitorCaptureContext? MonitorWaveformContext(IDspEngine? engine, Zeus.Contracts.StateDto state, int receiver)
    {
        if (engine is null || !ReferenceEquals(engine, Volatile.Read(ref _engine))
            || receiver != state.TxReceiverIndex) return null;
        if (engine is SyntheticDspEngine) return null;
        var rx = RadioFrequencyResolver.TxReceiver(state);
        if (rx is null || !rx.Enabled) return null;
        var signal = new WaveformContext(engine, receiver, false, _keyed,
            RadioService.TxEffectiveLoHz(state), rx.Mode, Interlocked.Read(ref _txSpectrumKeyGeneration));
        var ingest = ResolveTxIngest();
        return signal is null || ingest is null ? null : new(signal, ingest.ActiveSource);
    }

    private void ReconcileCaptureMonitor(IDspEngine engine, Zeus.Contracts.StateDto state)
    {
        bool enabled = state.TxMonitorEnabled || _monitorWaveform.IsCaptureActive
            || (_productPluginAudio?.HasLeasedTxMicCapture ?? false);
        if (engine.IsTxMonitorOn == enabled) return;
        engine.SetTxMonitorEnabled(enabled);
    }

    private void CaptureMonitorWaveform(IDspEngine engine, Zeus.Contracts.StateDto state, ReadOnlySpan<float> samples)
    {
        long now = Environment.TickCount64;
        if (!_monitorWaveform.IsDemanded(now)) return;
        _monitorWaveform.Append(MonitorWaveformContext(engine, state, state.TxReceiverIndex),
            AudioOutputRateHz, 1, samples, now, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
    private readonly object _micCaptureSync = new();
    private TxAudioIngest? _micCaptureIngest;
    private sealed record MicCaptureContext(TxAudioIngest Ingest, MicBlockSource Source);
    private MicCaptureContext? _micCaptureContext;

    private void CaptureMicWaveform(ReadOnlyMemory<byte> bytes, MicBlockSource source)
    {
        long now = Environment.TickCount64;
        if (!_micWaveform.IsDemanded(now)) return;
        var context = Volatile.Read(ref _micCaptureContext);
        if (context is null) return;
        object? actual = _keyed ? null : new MicCaptureContext(context.Ingest, source == MicBlockSource.BrowserMic ? MicBlockSource.Host : source);
        Span<float> samples = stackalloc float[bytes.Length / sizeof(float)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Span.Slice(i * sizeof(float), sizeof(float)));
        _micWaveform.Append(actual, 48000, 1, samples, now, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public WaveformCaptureMetadata? StartWaveformCapture(int receiver, bool tx, int durationMs, out string? error, bool mic = false, bool monitor = false)
    {
        if (receiver is < 0 or > 7 || durationMs is not (1000 or 3000 or 5000 or 10000))
        { error = "Invalid receiver or duration"; return null; }
        if (monitor)
        {
            if (mic || tx) { error = "Choose one capture source"; return null; }
            var monitorContext = MonitorWaveformContext(Volatile.Read(ref _engine), _radio.Snapshot(), receiver);
            if (monitorContext is null) { error = "Processed transmit audio is unavailable"; return null; }
            return _monitorWaveform.StartCapture(monitorContext, Environment.TickCount64, receiver, false,
                durationMs, monitorContext.Signal.Mode.ToString(), out error, monitor: true);
        }
        if (mic && (tx || _keyed)) { error = "Stop transmitting before recording microphone input"; return null; }
        if (mic)
        {
            lock (_micCaptureSync)
            {
                var ingest = ResolveTxIngest();
                if (ingest is null) { error = "Microphone input is unavailable"; return null; }
                if (_micCaptureIngest is null)
                {
                    _micCaptureIngest = ingest;
                    ingest.MicPcmTapped += CaptureMicWaveform;
                }
                var micContext = new MicCaptureContext(ingest, ingest.ActiveSource);
                Volatile.Write(ref _micCaptureContext, micContext);
                return _micWaveform.StartCapture(micContext, Environment.TickCount64, receiver, false,
                    durationMs, "Input", out error, microphone: true);
            }
        }
        var engine = Volatile.Read(ref _engine);
        var state = _radio.Snapshot();
        var context = WaveformSourceContext(engine, state, receiver, tx);
        long now = Environment.TickCount64;
        var cache = tx ? _txWaveform : _rxWaveforms[receiver];
        var result = cache.StartCapture(context, now, receiver, tx, durationMs,
            context?.Mode.ToString() ?? "", out error);
        if (result is null) return null;
        if (tx)
        {
            Volatile.Write(ref _requestedTxWaveform, context);
            Volatile.Write(ref _txCaptureTapUntil, now + durationMs + 2000);
            // Uses the existing read-only completed-output observer. No monitor or DSP setting is changed.
            GetWaveformSnapshot(receiver, true, 50);
        }
        else Volatile.Write(ref _requestedRxWaveforms[receiver], context);
        return result;
    }

    private WaveformSnapshotCache? FindWaveformCapture(string id, out WaveformCaptureMetadata? metadata)
    {
        // Route opaque IDs to one lane; status reads must not contend with unrelated producers.
        WaveformSnapshotCache? cache = id.Length == 33 && id[0] == 'p' ? _monitorWaveform
            : id.Length == 33 && id[0] == 'm' ? _micWaveform
            : id.Length == 33 && id[0] == 't' ? _txWaveform
            : id.Length == 34 && id[0] == 'r' && id[1] is >= '0' and <= '7'
                ? _rxWaveforms[id[1] - '0'] : null;
        var found = cache?.FindCapture(id);
        if (found is not null)
        {
            if (ReferenceEquals(cache, _monitorWaveform))
            {
                metadata = cache.GetCapture(id, MonitorWaveformContext(Volatile.Read(ref _engine),
                    _radio.Snapshot(), found.ReceiverIndex), Environment.TickCount64);
                return metadata is null ? null : cache;
            }
            if (ReferenceEquals(cache, _micWaveform))
            {
                var ingest = _micCaptureIngest!;
                metadata = cache.GetCapture(id, _keyed ? null : new MicCaptureContext(ingest, ingest.ActiveSource), Environment.TickCount64);
                return metadata is null ? null : cache;
            }
            metadata = cache!.GetCapture(id, WaveformSourceContext(Volatile.Read(ref _engine),
                _radio.Snapshot(), found.ReceiverIndex, found.Kind == "complex"), Environment.TickCount64);
            return metadata is null ? null : cache;
        }
        metadata = null; return null;
    }
    public WaveformCaptureMetadata? GetWaveformCapture(string id)
    { FindWaveformCapture(id, out var metadata); return metadata; }
    public byte[]? GetWaveformCaptureSamples(string id)
    { return FindWaveformCapture(id, out _)?.GetCaptureBytes(id); }
    public bool CancelWaveformCapture(string id)
    { return FindWaveformCapture(id, out _)?.CancelCapture(id, Environment.TickCount64) == true; }
}
