// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

public partial class DspPipelineService
{
    private readonly BandwidthSpectrumCache[] _bandwidthRx = Enumerable.Range(0, 8)
        .Select(_ => new BandwidthSpectrumCache()).ToArray();
    private readonly BandwidthSpectrumCache _bandwidthTx = new();
    private readonly float[] _bandwidthScratch = new float[16_384];
    private sealed record BandwidthContext(IDspEngine Engine, int Channel, int Receiver,
        bool Tx, long Center, long Carrier, RxMode Mode, long KeyGeneration);

    private BandwidthContext? BandwidthSourceContext(IDspEngine? engine, StateDto state, int receiver, bool tx)
    {
        if (engine is not IBandwidthSpectrumSource || receiver is < 0 or > 7
            || state.Status != ConnectionStatus.Connected || (tx && (!_keyed || receiver != state.TxReceiverIndex))) return null;
        var rx = receiver == 0 ? RadioFrequencyResolver.TxReceiver(state with { TxReceiverIndex = 0 })
            : receiver == 1 ? state.Rx2()
            : state.Receivers?.FirstOrDefault(r => r.Index == receiver);
        if (rx is null || !rx.Enabled) return null;
        int channel = receiver == 0 ? Volatile.Read(ref _channelId) : Volatile.Read(ref _secondaryRx[receiver].ChannelId);
        if (channel < 0) return null;
        long carrier = tx ? RadioService.TxEffectiveLoHz(state) : rx.VfoHz;
        long center = tx ? carrier : receiver == 0 ? state.RadioLoHz : _secondaryRx[receiver].LoHz;
        return new(engine, channel, receiver, tx, center > 0 ? center : carrier, carrier,
            tx ? RadioFrequencyResolver.TxMode(state) : rx.Mode,
            Interlocked.Read(ref _txSpectrumKeyGeneration));
    }

    public BandwidthSpectrumSnapshot GetBandwidthSpectrumSnapshot(int receiver, bool tx,
        double minOffsetHz = -12000, double maxOffsetHz = 12000)
    {
        minOffsetHz = double.IsFinite(minOffsetHz) ? minOffsetHz : -12000;
        maxOffsetHz = double.IsFinite(maxOffsetHz) ? maxOffsetHz : 12000;
        maxOffsetHz = Math.Max(minOffsetHz, maxOffsetHz);
        var state = _radio.Snapshot();
        if (receiver is < 0 or > 7 || (tx && receiver != state.TxReceiverIndex))
            return new(false, tx ? "digital-tx" : "rx-native", receiver, [], 0, 0, 0, "", 0, 0, 0, 0, 0, "native-display");
        var context = BandwidthSourceContext(Volatile.Read(ref _engine), state, receiver, tx);
        var cache = tx ? _bandwidthTx : _bandwidthRx[Math.Clamp(receiver, 0, 7)];
        return cache.Request(context, tx, receiver, context?.Carrier ?? 0,
            context?.Mode.ToString() ?? "", Environment.TickCount64, minOffsetHz, maxOffsetHz);
    }

    private void CaptureBandwidthSpectra(IDspEngine engine, StateDto state)
    {
        if (engine is not IBandwidthSpectrumSource source) return;
        long now = Environment.TickCount64;
        for (int i = 0; i < 9; i++)
        {
            bool tx = i == 8;
            int receiver = tx ? state.TxReceiverIndex : i;
            var cache = tx ? _bandwidthTx : _bandwidthRx[receiver];
            long until = cache.DemandUntil;
            if (until <= now) continue;
            var context = BandwidthSourceContext(engine, state, receiver, tx);
            if (context is null) continue;
            if (!source.TryGetBandwidthSpectrum(context.Channel, tx, _bandwidthScratch, until, out var info)
                || !info.IsValid) continue;
            cache.Publish(context, _bandwidthScratch.AsSpan(0, info.PixelCount), info.SampleRateHz,
                info.Generation, context.Center, now, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                tx ? Volatile.Read(ref _txDisplayCalOffsetDb) : 0.0);
        }
    }
}

public static class BandwidthSpectrumEndpoints
{
    public static IEndpointRouteBuilder MapBandwidthSpectrum(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/display/bandwidth-spectrum", (DspPipelineService pipeline,
            int? receiver, bool? tx, double? minOffsetHz, double? maxOffsetHz) =>
            Results.Ok(pipeline.GetBandwidthSpectrumSnapshot(receiver ?? 0, tx ?? false,
                minOffsetHz ?? -12000, maxOffsetHz ?? 12000)));
        return endpoints;
    }
}
