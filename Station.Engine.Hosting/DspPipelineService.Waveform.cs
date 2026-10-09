// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

public partial class DspPipelineService
{
    private readonly WaveformSnapshotCache _txWaveform = new();
    private readonly WaveformSnapshotCache[] _rxWaveforms = Enumerable.Range(0, 8).Select(_ => new WaveformSnapshotCache()).ToArray();
    private sealed record WaveformContext(IDspEngine Engine, int Receiver, bool Tx,
        bool Keyed, long Carrier, RxMode Mode, long KeyGeneration);
    private WaveformContext? _requestedTxWaveform;
    private IDspEngine? _waveformTapEngine;
    private TxWaveformHandler? _waveformTapHandler;
    private readonly WaveformContext?[] _requestedRxWaveforms = new WaveformContext?[8];

    private WaveformContext? WaveformSourceContext(IDspEngine? engine, StateDto state, int receiver, bool tx)
    {
        if (engine is null || engine is SyntheticDspEngine || state.Status != ConnectionStatus.Connected
            || receiver is < 0 or > 7 || (tx && (!_keyed || receiver != state.TxReceiverIndex))) return null;
        var rx = tx ? RadioFrequencyResolver.TxReceiver(state)
            : receiver == 0 ? RadioFrequencyResolver.TxReceiver(state with { TxReceiverIndex = 0 })
            : receiver == 1 ? state.Rx2()
            : state.Receivers?.FirstOrDefault(r => r.Index == receiver);
        if (rx is null || !rx.Enabled) return null;
        return new(engine, tx ? state.TxReceiverIndex : receiver, tx, _keyed,
            tx ? RadioService.TxEffectiveLoHz(state) : rx.VfoHz, rx.Mode,
            Interlocked.Read(ref _txSpectrumKeyGeneration));
    }

    public WaveformSnapshot GetWaveformSnapshot(int receiver, bool tx, double windowMs)
    {
        if (receiver is < 0 or > 7)
            return new(false, tx ? "digital-tx" : "rx-demodulated", tx ? "complex" : "real",
                receiver, 0, tx ? 2 : 1, 0, 0, 0, 0, [], 0, 0, "relative", "", 0,
                tx ? "tx-output" : "rx-post-demod");
        var engine = Volatile.Read(ref _engine);
        var state = _radio.Snapshot();
        // A pinned non-transmitting receiver must not replace the active TX lane's demand.
        if (tx && receiver != state.TxReceiverIndex)
            return new(false, "digital-tx", "complex", receiver, 0, 2, 0, 0, 0, 0, [],
                0, 0, "relative", "", 0, "tx-output");
        var context = WaveformSourceContext(engine, state, receiver, tx);
        if (tx) Volatile.Write(ref _requestedTxWaveform, context);
        else if (receiver is >= 0 and < 8) Volatile.Write(ref _requestedRxWaveforms[receiver], context);
        long now = Environment.TickCount64;
        if (tx && engine is ITxWaveformSource source)
        {
            if (!ReferenceEquals(engine, _waveformTapEngine))
            {
                if (_waveformTapEngine is ITxWaveformSource previous) previous.SetTxWaveformTap(null, 0);
                _waveformTapEngine = engine;
                _waveformTapHandler = (rate, samples) => CaptureTxWaveform(engine, rate, samples);
            }
            source.SetTxWaveformTap(context is not null ? _waveformTapHandler : null, now + WaveformSnapshotCache.LeaseMs);
        }
        var cache = tx ? _txWaveform : _rxWaveforms[receiver];
        return cache.Request(context, now, context?.Receiver ?? receiver, tx, windowMs,
            context?.Mode.ToString() ?? "", context?.Carrier ?? 0);
    }

    private void CaptureTxWaveform(IDspEngine engine, int rate, ReadOnlySpan<float> samples)
    {
        if (!ReferenceEquals(engine, Volatile.Read(ref _engine))) return;
        var requested = Volatile.Read(ref _requestedTxWaveform);
        if (requested is null || !requested.Tx) return;
        var current = WaveformSourceContext(Volatile.Read(ref _engine), _radio.Snapshot(), requested.Receiver, true);
        _txWaveform.Append(current, rate, 2, samples, Environment.TickCount64,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void CaptureRxWaveform(IDspEngine engine, StateDto state, int receiver, ReadOnlySpan<float> samples)
    {
        if (receiver is < 0 or >= 8) return;
        if (!_rxWaveforms[receiver].IsDemanded(Environment.TickCount64)) return;
        var requested = Volatile.Read(ref _requestedRxWaveforms[receiver]);
        if (requested is null || requested.Tx || requested.Receiver != receiver) return;
        _rxWaveforms[receiver].Append(WaveformSourceContext(engine, state, receiver, false), AudioOutputRateHz, 1,
            samples, Environment.TickCount64, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}

public static class WaveformEndpoints
{
    public static IEndpointRouteBuilder MapWaveform(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/display/waveform", (DspPipelineService pipeline,
            int? receiver, bool? tx, double? windowMs) =>
            Results.Ok(pipeline.GetWaveformSnapshot(receiver ?? 0, tx ?? false, windowMs ?? 50)));
        return endpoints;
    }
}
