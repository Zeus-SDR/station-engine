// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.PublicListen;

/// <summary>
/// Public Listening Phase 2 (docs/designs/public-listening.md): the listener
/// wideband overview, stream 0xF0. An independent consumer of the Protocol 2
/// raw-ADC wideband snapshots that never shares state with the operator's
/// wideband display:
/// <list type="bullet">
///   <item>its own <see cref="WidebandSpectrumAnalyzer"/> (own smoothing state),
///   always the full 0–60 MHz overview (zoom 1, fixed centre), no signal detector;</item>
///   <item>never reads or writes the operator's zoom level or wideband target centre;</item>
///   <item>at most <see cref="MaxFramesPerSecond"/> analyses per second — snapshots
///   in between are skipped without being analysed;</item>
///   <item>debounced demand: listener demand turns the radio's wideband transport
///   on at once but releases it only after <see cref="DemandReleaseDelay"/>, so
///   subscribe/unsubscribe churn does not send a CmdGeneral per toggle.</item>
///   <item>band conditions: while the station reports them (listed + sharing
///   wideband) every analysed, non-transmit row also feeds
///   <see cref="BandConditions"/>; <see cref="Survey"/> pulses demand so they
///   stay fresh with no listener connected.</item>
/// </list>
/// <see cref="UpdateDemand"/> is called from the DSP tick; <see cref="TryProcess"/>
/// only from the wideband analyzer worker.
/// </summary>
internal sealed class PublicWidebandOverview
{
    // Full 4096-bin rows plus the maximum operator-view stream fit the existing 40 KiB/s budget.
    public const int MaxFramesPerSecond = 2;
    public static readonly TimeSpan MinFrameInterval = TimeSpan.FromSeconds(1.0 / MaxFramesPerSecond);
    public static readonly TimeSpan DemandReleaseDelay = TimeSpan.FromSeconds(30);
    private const double MaxAnalysisIntervalMs = 1_000.0;

    private readonly TimeProvider _time;
    private readonly WidebandSpectrumAnalyzer _analyzer = new();
    private readonly float[] _panDb = new float[WidebandSpectrumAnalyzer.DisplayWidth];
    private readonly float[] _wfDb = new float[WidebandSpectrumAnalyzer.DisplayWidth];

    private readonly object _demandGate = new();
    private bool _demandLatched;
    private long _lastDemandTimestamp;

    // Analyzer-worker only.
    private bool _framed;
    private long _lastFrameTimestamp;

    public PublicWidebandOverview(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        BandConditions = new PublicBandConditionsTracker(_time);
        Survey = new PublicBandSurvey(_time);
    }

    /// <summary>Per-band conditions measured from this overview's own analyzer rows.</summary>
    public PublicBandConditionsTracker BandConditions { get; }

    /// <summary>The periodic band-survey demand (scheduled from the DSP tick).</summary>
    public PublicBandSurvey Survey { get; }

    /// <summary>
    /// Debounced public demand. <paramref name="demandedNow"/> true latches the
    /// demand immediately; once it drops the result stays true until
    /// <see cref="DemandReleaseDelay"/> has passed without demand.
    /// </summary>
    public bool UpdateDemand(bool demandedNow)
    {
        long now = _time.GetTimestamp();
        lock (_demandGate)
        {
            if (demandedNow)
            {
                _demandLatched = true;
                _lastDemandTimestamp = now;
                return true;
            }
            if (!_demandLatched) return false;
            if (_time.GetElapsedTime(_lastDemandTimestamp, now) < DemandReleaseDelay) return true;
            _demandLatched = false;
            return false;
        }
    }

    /// <summary>
    /// Analyse one raw ADC snapshot when the frame budget allows and a listener
    /// wants the overview or band conditions are being reported. The overview
    /// row goes to <paramref name="tap"/> only for listener demand; band
    /// conditions take the analyzer's float trace row. While the station is
    /// transmitting nothing is analysed or measured: a listener gets a
    /// data-free TransmitHold frame instead. Returns true when a frame was offered.
    /// </summary>
    public bool TryProcess(ReadOnlySpan<short> samples, int sampleRateHz, IListenerFeedTap tap)
    {
        bool offer = tap.WantsWideband;
        bool measure = tap.BandSurveyEnabled;
        if (!(offer || measure) || samples.Length < 2) return false;

        long now = _time.GetTimestamp();
        double intervalMs = MinFrameInterval.TotalMilliseconds;
        if (_framed)
        {
            var since = _time.GetElapsedTime(_lastFrameTimestamp, now);
            if (since < MinFrameInterval) return false;
            intervalMs = Math.Min(since.TotalMilliseconds, MaxAnalysisIntervalMs);
        }
        _framed = true;
        _lastFrameTimestamp = now;

        double tsUnixMs = (_time.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalMilliseconds;
        if (tap.IsTransmitHeld)
        {
            // Not analysed: a snapshot taken while keyed must never reach a
            // listener, and neither the smoothing state nor the band
            // conditions may absorb it.
            if (!offer) return false;
            tap.OfferWidebandOverview(
                _wfDb, tsUnixMs, WidebandSpectrumAnalyzer.DisplayCenterHz, WidebandSpectrumAnalyzer.HzPerPixel);
            return true;
        }

        var viewport = _analyzer.Analyze(
            samples,
            sampleRateHz,
            _panDb,
            _wfDb,
            zoomLevel: 1,
            WidebandSpectrumAnalyzer.DisplayCenterHz,
            intervalMs);
        // The peak-reduced trace row, before any quantization: a narrow
        // carrier keeps its true height there, so the high percentile sees it.
        if (measure) BandConditions.Ingest(_panDb, viewport.CenterHz, viewport.HzPerPixel);
        if (!offer) return false;
        tap.OfferWidebandOverview(_wfDb, tsUnixMs, viewport.CenterHz, viewport.HzPerPixel);
        return true;
    }
}
