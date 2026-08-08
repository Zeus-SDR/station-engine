// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Selects the Protocol-2 display source for the shared wideband zoom value.
/// The overview comes from the ADC snapshot stream. Once that requested span
/// fits inside the negotiated sample rate, a pipeline-owned hidden DDC and
/// RX-only WDSP analyzer become the source. This preserves normal panadapter
/// resolution without retuning or borrowing an operator receiver.
/// </summary>
internal static class P2WidebandZoomPolicy
{
    internal const double WidebandSpanHz = 60_000_000.0;
    internal const int MaxDdcZoomLevel = 32;
    internal const int MaxGlobalZoomLevel = 40_960;

    private const int HandoffBase = 5;

    // Analyzer FFT bounds for the wideband detail channel. The floor is the
    // engine-wide RX default, so shallow detail zooms render byte-identically
    // to before. The ceiling makes FFT bins exactly pixel-dense at
    // MaxDdcZoomLevel on a 2048-pixel display (2048 x 32 = 65,536 points,
    // ~2.9 Hz bins at 192 kHz) while holding the capture aperture to ~340 ms
    // at that rate, so the waterfall keeps its time texture. WDSP's analyzer
    // is allocated for up to 262,144 points; deeper sizes only pay off once a
    // zoom deeper than 32 exists to show them.
    internal const int MinDetailAnalyzerFftSize = 16_384;
    internal const int MaxDetailAnalyzerFftSize = 65_536;

    /// <summary>
    /// Pick the WDSP analyzer FFT size for the hidden detail DDC so that true
    /// spectral bins stay at or below one display pixel at the current DDC
    /// zoom (bin width = rate / fft, pixel width = rate / (pixels x zoom)).
    /// Power-of-two ladder, clamped to
    /// [<see cref="MinDetailAnalyzerFftSize"/>, <see cref="MaxDetailAnalyzerFftSize"/>].
    /// </summary>
    internal static int DetailAnalyzerFftSize(int pixelWidth, int ddcZoomLevel)
    {
        if (pixelWidth <= 0) return MinDetailAnalyzerFftSize;
        long ideal = (long)pixelWidth * Math.Clamp(ddcZoomLevel, 1, MaxDdcZoomLevel);
        int fft = MinDetailAnalyzerFftSize;
        while (fft < ideal && fft < MaxDetailAnalyzerFftSize)
            fft = checked(fft * 2);
        return fft;
    }

    internal static P2WidebandZoomPlan Resolve(int sampleRateHz, int globalZoomLevel)
    {
        int globalZoom = Math.Clamp(globalZoomLevel, 1, MaxGlobalZoomLevel);
        if (sampleRateHz <= 0)
        {
            return new P2WidebandZoomPlan(
                UseDdcDetail: false,
                DdcZoomLevel: Math.Clamp(globalZoom, 1, MaxDdcZoomLevel),
                HandoffZoomLevel: int.MaxValue,
                RequestedSpanHz: WidebandSpanHz / globalZoom);
        }

        // Round the geometric crossover up to the next 5*2^n preset. For the
        // normal 192 kHz G2 rate this maps 312.5 -> 320x. The same ladder is
        // used by the client, so every wheel notch lands on an exact source
        // transition or a native 2x WDSP detail step.
        int handoff = HandoffBase;
        double crossover = WidebandSpanHz / sampleRateHz;
        while (handoff < crossover && handoff < MaxGlobalZoomLevel)
            handoff = checked(handoff * 2);
        handoff = Math.Min(handoff, MaxGlobalZoomLevel);
        double requestedSpanHz = WidebandSpanHz / globalZoom;
        bool useDdcDetail = globalZoom >= handoff;

        // The WDSP zoom is an integer crop of the live DDC. Nearest-step mapping
        // makes the canonical 320/640/1280/... ladder resolve to 1/2/4/... at
        // 192 kHz while remaining rate-derived for every supported P2 rate.
        int ddcZoom = useDdcDetail
            ? Math.Clamp(
                (int)Math.Round(sampleRateHz / requestedSpanHz, MidpointRounding.AwayFromZero),
                1,
                MaxDdcZoomLevel)
            : Math.Clamp(globalZoom, 1, MaxDdcZoomLevel);

        return new P2WidebandZoomPlan(
            useDdcDetail,
            ddcZoom,
            handoff,
            requestedSpanHz);
    }

    internal static int MaxGlobalZoomForRate(int sampleRateHz)
    {
        if (sampleRateHz <= 0) return MaxGlobalZoomLevel;
        var plan = Resolve(sampleRateHz, 1);
        return Math.Clamp(
            plan.HandoffZoomLevel * MaxDdcZoomLevel,
            1,
            MaxGlobalZoomLevel);
    }

    internal static int MaxOverviewZoomForRate(int sampleRateHz)
    {
        int handoff = Resolve(sampleRateHz, 1).HandoffZoomLevel;
        int overview = 1;
        while (overview <= RadioService.LegacyMaxDisplayZoomLevel / 2 &&
               overview * 2 < handoff)
        {
            overview *= 2;
        }
        return overview;
    }

    internal static P2WidebandDetailSource ResolveDetailSource(
        int baseDdc,
        bool rx2Enabled,
        bool diversitySourceEnabled,
        int extraReceiverCount,
        int sampleRateHz,
        long targetCenterHz,
        double requestedSpanHz)
    {
        // The hidden stream must extend the already-contiguous user run. RX2's
        // DDC counts as occupied when diversity is consuming it even though the
        // RX2 UI is off; extras are only valid behind a visible RX2.
        int occupied = 1;
        if (rx2Enabled || diversitySourceEnabled) occupied++;
        if (rx2Enabled) occupied += Math.Max(0, extraReceiverCount);
        int candidate = baseDdc + occupied;
        if (candidate is >= 2 and < Zeus.Protocol2.Protocol2Client.MaxRxDdc)
        {
            return new P2WidebandDetailSource(
                P2WidebandDetailSourceKind.HiddenDdc,
                candidate,
                ReceiverIndex: Zeus.Protocol2.Protocol2Client.DisplayReceiverIndex,
                SourceCenterHz: targetCenterHz);
        }

        _ = sampleRateHz;
        _ = requestedSpanHz;
        // Full-capacity fallback stays on the rate-derived overview ceiling.
        // Never borrow an occupied operator stream or couple display work into RX/audio.
        return P2WidebandDetailSource.None;
    }
}

internal readonly record struct P2WidebandZoomPlan(
    bool UseDdcDetail,
    int DdcZoomLevel,
    int HandoffZoomLevel,
    double RequestedSpanHz);

internal enum P2WidebandDetailSourceKind
{
    None,
    HiddenDdc,
}

internal readonly record struct P2WidebandDetailSource(
    P2WidebandDetailSourceKind Kind,
    int DdcIndex,
    int ReceiverIndex,
    long SourceCenterHz)
{
    internal static readonly P2WidebandDetailSource None = new(
        P2WidebandDetailSourceKind.None,
        DdcIndex: -1,
        ReceiverIndex: -1,
        SourceCenterHz: 0);

    internal bool IsAvailable => Kind != P2WidebandDetailSourceKind.None;
}
