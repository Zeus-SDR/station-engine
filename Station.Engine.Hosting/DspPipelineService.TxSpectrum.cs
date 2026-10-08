// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

public partial class DspPipelineService
{
    private readonly TxSpectrumSnapshotCache _txSpectrum = new();
    private readonly TxSpectrumPixelBroker _txSpectrumPixels = new();
    private readonly TxSpectrumEpochPrimer _txSpectrumPrimer = new();
    private IDspEngine? _txSpectrumReaderEngine;
    private Func<float[], bool>? _txSpectrumNativeReader;
    private bool _txSpectrumMainNativeFresh;
    private float[]? _txSpectrumScratch;
    private long _txSpectrumKeyGeneration;
    private sealed record TxSpectrumContext(IDspEngine Engine, long CenterHz, int Rate,
        int Zoom, int Width, int Receiver, RxMode Mode, long KeyGeneration);

    private TxSpectrumContext? DigitalTxContext(IDspEngine? engine, StateDto state, int sampleRate) =>
        _keyed && state.Status == ConnectionStatus.Connected && engine is not null
        && engine is not SyntheticDspEngine && sampleRate > 0
            ? new(engine, RadioService.TxEffectiveLoHz(state), sampleRate,
                DdcZoomLevel(state.ZoomLevel), _panadapterWidth, state.TxReceiverIndex,
                RadioFrequencyResolver.TxMode(state), Interlocked.Read(ref _txSpectrumKeyGeneration))
            : null;

    public TxSpectrumSnapshot GetTxSpectrumSnapshot(bool transmitActive = true)
    {
        var state = _radio.Snapshot();
        return _txSpectrum.Request(transmitActive ? DigitalTxContext(Volatile.Read(ref _engine), state,
            Volatile.Read(ref _sampleRateHz)) : null, Environment.TickCount64);
    }

    internal static bool RequiresDedicatedTxSpectrumRead(bool requested, bool mainPanValid, string mainSource) =>
        requested && !(mainPanValid && mainSource == "tx");

    private Func<float[], bool> DigitalTxNativeReader(IDspEngine engine)
    {
        if (!ReferenceEquals(engine, _txSpectrumReaderEngine))
        {
            _txSpectrumReaderEngine = engine;
            _txSpectrumNativeReader = pixels => engine.TryGetTxDisplayPixels(DisplayPixout.Panadapter, pixels);
        }
        return _txSpectrumNativeReader!;
    }

    private bool TryGetDigitalTxMainPixels(IDspEngine engine, StateDto state, int sampleRate, float[] pixels)
    {
        var context = DigitalTxContext(engine, state, sampleRate);
        var read = DigitalTxNativeReader(engine);
        if (context is null) { _txSpectrumMainNativeFresh = read(pixels); return _txSpectrumMainNativeFresh; }
        return _txSpectrumPixels.ReadMain(context, pixels, Environment.TickCount64, read,
            out _txSpectrumMainNativeFresh);
    }

    private void CaptureDigitalTxSpectrum(IDspEngine engine, TxSpectrumContext context,
        float[]? freshNativePixels, long nowMs, double captureTimeUnixMs, long generation)
    {
        float[] pixels;
        if (freshNativePixels is not null)
        {
            // A frame borrowed from the dedicated reader has already been published
            // with its real capture age. Replaying it must not renew HTTP freshness.
            if (!_txSpectrumMainNativeFresh) return;
            pixels = freshNativePixels;
        }
        else
        {
            if (_txSpectrumScratch?.Length != context.Width)
                _txSpectrumScratch = new float[context.Width];
            pixels = _txSpectrumScratch;
            if (!_txSpectrumPixels.ReadDedicated(context, pixels, nowMs, DigitalTxNativeReader(engine))) return;
        }
        // SetMox does not clear the native ready flag. The first observed fresh
        // flag in this context may belong to the previous over or geometry.
        if (!_txSpectrumPrimer.Accept(context, nativeFresh: true)) return;
        float hzPerBin = VisibleDdcHzPerPixel(context.Rate, context.Zoom, context.Width);
        int txRate = ResolveTxDspRateHz(engine);
        var support = DigitalTxSupport(context.Width, context.Rate, context.Zoom, txRate, context.CenterHz, hzPerBin);
        // WDSP pads the unsupported aperture with its minimum value. Exclude those
        // manufactured pixels, retaining the original frequency step and odd-width offset.
        _txSpectrum.Publish(context, pixels.AsSpan(support.Start, support.Width), nowMs, captureTimeUnixMs,
            support.CenterHz, hzPerBin, generation, context.CenterHz, context.Mode.ToString());
    }
    internal static (int Start, int Width, double CenterHz) DigitalTxSupport(
        int width, int rxRate, int zoom, int txRate, double centerHz, float hzPerBin)
    {
        int used = Math.Clamp((int)Math.Round(width * Math.Min(1.0,
            (double)txRate * zoom / rxRate), MidpointRounding.AwayFromZero), 2, width);
        int left = (width - used) / 2;
        int right = width - used - left;
        return (left, used, centerHz + (right - left) * hzPerBin / 2.0);
    }
}

public static class TxSpectrumEndpoints
{
    public static IEndpointRouteBuilder MapTxSpectrum(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tx/spectrum", (DspPipelineService pipeline, TxService tx) =>
            Results.Ok(pipeline.GetTxSpectrumSnapshot(tx.IsMoxOn || tx.IsTunOn)));
        return endpoints;
    }
}
