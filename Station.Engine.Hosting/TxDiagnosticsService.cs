// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Dsp;
using Zeus.Protocol1;
using Zeus.Protocol2;

namespace Zeus.Server;

/// <summary>
/// Owns the TX diagnostics that can be observed entirely inside the station
/// engine. Product hosts can compose additional hardware and plugin details
/// around this snapshot without making the standalone engine depend on them.
/// </summary>
public sealed class TxDiagnosticsService
{
    private readonly TxIqRing _ring;
    private readonly ITxIqSource _source;
    private readonly TxAudioIngest _ingest;
    private readonly DspPipelineService _dsp;
    private readonly TxService _tx;
    private readonly RadioService _radio;
    private readonly StreamingHub _hub;

    public TxDiagnosticsService(
        TxIqRing ring,
        ITxIqSource source,
        TxAudioIngest ingest,
        DspPipelineService dsp,
        TxService tx,
        RadioService radio,
        StreamingHub hub)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
        _dsp = dsp ?? throw new ArgumentNullException(nameof(dsp));
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    }

    internal TxDiagnosticsCoreSnapshot Snapshot()
    {
        var generatedUtc = DateTimeOffset.UtcNow;
        bool hostTxActive = _tx.IsMoxOn || _tx.IsTunOn || _tx.IsTwoToneOn;
        bool txStageActive = hostTxActive || _radio.Snapshot().TxMonitorEnabled;
        var stage = _dsp.CurrentEngine?.GetTxStageMeters() ?? TxStageMeters.Silent;

        return new TxDiagnosticsCoreSnapshot(
            GeneratedUtc: generatedUtc,
            IqSourceType: _source.GetType().FullName,
            IqSourceIsRing: ReferenceEquals(_source, _ring),
            Ring: new TxRingDiagnosticsDto(
                _ring.TotalWritten,
                _ring.TotalRead,
                _ring.Count,
                _ring.Dropped,
                _ring.Capacity,
                _ring.RecentMag),
            MicUplink: _hub.MicInboundDiagnosticsSnapshot(generatedUtc),
            Ingest: new TxIngestDiagnosticsDto(
                _ingest.TotalMicSamples,
                _ingest.TotalTxBlocks,
                _ingest.DroppedFrames),
            Protocol2: _dsp.ActiveP2Client?.TxIqDiagnosticsSnapshot(),
            Stage: BuildTxStageDiagnostics(stage, txStageActive));
    }

    internal static TxStageDiagnosticsDto BuildTxStageDiagnostics(
        TxStageMeters stage,
        bool hostTxActive)
    {
        bool hasLevelEvidence =
            IsUsableTxLevel(stage.MicPk)
            || IsUsableTxLevel(stage.EqPk)
            || IsUsableTxLevel(stage.LvlrPk)
            || IsUsableTxLevel(stage.CfcPk)
            || IsUsableTxLevel(stage.CompPk)
            || IsUsableTxLevel(stage.AlcPk)
            || IsUsableTxLevel(stage.OutPk);
        string status = hostTxActive
            ? hasLevelEvidence ? "active" : "waiting-for-stage-meters"
            : "idle";
        var density = BuildTxStageDensityDiagnostics(stage, hostTxActive, hasLevelEvidence);

        return new TxStageDiagnosticsDto(
            SchemaVersion: 1,
            Source: "wdsp-txa-meter-ring",
            Status: status,
            HostTxActive: hostTxActive,
            MicPkDbfs: TxLevelDb(stage.MicPk),
            MicAvDbfs: TxLevelDb(stage.MicAv),
            EqPkDbfs: TxLevelDb(stage.EqPk),
            EqAvDbfs: TxLevelDb(stage.EqAv),
            LvlrPkDbfs: TxLevelDb(stage.LvlrPk),
            LvlrAvDbfs: TxLevelDb(stage.LvlrAv),
            LvlrGrDb: TxGainReductionDb(stage.LvlrGr),
            CfcPkDbfs: TxLevelDb(stage.CfcPk),
            CfcAvDbfs: TxLevelDb(stage.CfcAv),
            CfcGrDb: TxGainReductionDb(stage.CfcGr),
            CompPkDbfs: TxLevelDb(stage.CompPk),
            CompAvDbfs: TxLevelDb(stage.CompAv),
            AlcPkDbfs: TxLevelDb(stage.AlcPk),
            AlcAvDbfs: TxLevelDb(stage.AlcAv),
            AlcGrDb: TxGainReductionDb(stage.AlcGr),
            OutPkDbfs: TxLevelDb(stage.OutPk),
            OutAvDbfs: TxLevelDb(stage.OutAv),
            OutputHeadroomDb: density.OutputHeadroomDb,
            OutputCrestFactorDb: density.OutputCrestFactorDb,
            DensityStatus: density.Status,
            DensityTone: density.Tone,
            DensityRecommendation: density.Recommendation,
            DiagnosticRecommendation: status switch
            {
                "active" => "WDSP TXA stage meters are live; use Mic/Leveler/CFC/ALC/OUT peaks and gain reduction to tune station audio and spectral density.",
                "waiting-for-stage-meters" => "TX is keyed but WDSP TXA stage meters have not published yet; keep feeding mic/two-tone IQ and verify the TX ingest path.",
                _ => "TXA stage meters are idle; key MOX/TUN or enable the TX monitor path to evaluate mic, leveler, CFC, ALC, and output quality.",
            });
    }

    private static TxStageDensityDiagnostics BuildTxStageDensityDiagnostics(
        TxStageMeters stage,
        bool hostTxActive,
        bool hasLevelEvidence)
    {
        double? outPk = TxLevelDb(stage.OutPk);
        double? outAv = TxLevelDb(stage.OutAv);
        double alcGr = TxGainReductionDb(stage.AlcGr);
        double cfcGr = TxGainReductionDb(stage.CfcGr);
        double? headroom = outPk is { } pk ? Math.Round(Math.Max(0.0, -pk), 1) : null;
        double? crest = outPk is { } pk2 && outAv is { } av
            ? Math.Round(Math.Max(0.0, pk2 - av), 1)
            : null;

        if (!hostTxActive)
        {
            return new(
                headroom,
                crest,
                "idle",
                "standby",
                "TXA density diagnostics are idle; key MOX/TUN or enable TX monitor before judging on-air spectral density.");
        }

        if (!hasLevelEvidence || outPk is null || outAv is null || headroom is null || crest is null)
        {
            return new(
                headroom,
                crest,
                "waiting-for-stage-meters",
                "verify",
                "TX is keyed but output density cannot be evaluated until WDSP TXA output peak and average meters are live.");
        }

        if (headroom < 1.0 || stage.OutPk > -0.5f)
        {
            return new(
                headroom,
                crest,
                "clip-risk",
                "protect",
                "TX output is within 1 dB of digital full scale; reduce mic gain, drive, CFC, or ALC pressure before increasing density.");
        }

        if (alcGr >= 8.0)
        {
            return new(
                headroom,
                crest,
                "alc-heavy",
                "protect",
                "ALC is carrying heavy gain reduction; back down mic gain or upstream compression so the leveler/CFC shape density before ALC clamps the waveform.");
        }

        if (crest > 14.0 || outAv < -24.0)
        {
            return new(
                headroom,
                crest,
                "underfilled",
                "optimize",
                "TX output has high crest factor or low average level; raise mic/leveler drive or add gentle CFC/compression while preserving headroom.");
        }

        if (crest < 4.0 && cfcGr >= 6.0)
        {
            return new(
                headroom,
                crest,
                "over-dense",
                "protect",
                "TX output is very dense with strong CFC gain reduction; reduce CFC/compression if speech edges sound flat or adjacent-channel splatter rises.");
        }

        return new(
            headroom,
            crest,
            "density-optimized",
            "ready",
            "TX output density and headroom are in the target window; confirm with RF power, PureSignal feedback, and an off-air monitor.");
    }

    private static bool IsUsableTxLevel(float value) =>
        float.IsFinite(value) && value > -300.0f;

    private static double? TxLevelDb(float value) =>
        IsUsableTxLevel(value) ? Math.Round(value, 1) : null;

    private static double TxGainReductionDb(float value)
    {
        if (!float.IsFinite(value)) return 0.0;
        return Math.Round(Math.Max(0.0, value), 1);
    }
}

internal static class TxDiagnosticsEndpoints
{
    internal static IEndpointRouteBuilder MapTxDiagnosticsEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tx/diag", (TxDiagnosticsService diagnostics) =>
            Results.Ok(diagnostics.Snapshot()));
        return endpoints;
    }
}

internal sealed record TxDiagnosticsCoreSnapshot(
    DateTimeOffset GeneratedUtc,
    string? IqSourceType,
    bool IqSourceIsRing,
    TxRingDiagnosticsDto Ring,
    TxMicUplinkDiagnosticsDto MicUplink,
    TxIngestDiagnosticsDto Ingest,
    Protocol2TxIqDiagnostics? Protocol2,
    TxStageDiagnosticsDto Stage);

internal sealed record TxRingDiagnosticsDto(
    long TotalWritten,
    long TotalRead,
    int Count,
    long Dropped,
    int Capacity,
    double RecentMag);

internal sealed record TxIngestDiagnosticsDto(
    long TotalMicSamples,
    long TotalTxBlocks,
    long DroppedFrames);

internal sealed record TxStageDiagnosticsDto(
    int SchemaVersion,
    string Source,
    string Status,
    bool HostTxActive,
    double? MicPkDbfs,
    double? MicAvDbfs,
    double? EqPkDbfs,
    double? EqAvDbfs,
    double? LvlrPkDbfs,
    double? LvlrAvDbfs,
    double LvlrGrDb,
    double? CfcPkDbfs,
    double? CfcAvDbfs,
    double CfcGrDb,
    double? CompPkDbfs,
    double? CompAvDbfs,
    double? AlcPkDbfs,
    double? AlcAvDbfs,
    double AlcGrDb,
    double? OutPkDbfs,
    double? OutAvDbfs,
    double? OutputHeadroomDb,
    double? OutputCrestFactorDb,
    string DensityStatus,
    string DensityTone,
    string DensityRecommendation,
    string DiagnosticRecommendation);

internal sealed record TxStageDensityDiagnostics(
    double? OutputHeadroomDb,
    double? OutputCrestFactorDb,
    string Status,
    string Tone,
    string Recommendation);
