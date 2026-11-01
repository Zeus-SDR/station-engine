// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Diagnostics.CodeAnalysis;
using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

/// <summary>
/// The two-tone IMD source for a transmitter whose PureSignal feedback arrives
/// through the external pair intake. The main panadapter there belongs to the
/// radio's own receiver, which hears its own transmitter and reads nothing like
/// the amplifier output, so the IMD readout measures the PureSignal feedback
/// analyzer instead: the same pixels "Monitor PA output" displays, captured
/// whether or not that toggle is on.
/// </summary>
public partial class DspPipelineService
{
    /// <summary>A feedback spectrum older than this is from an earlier key.</summary>
    internal const long PsFeedbackImdMaxAgeMs = 1_000;

    private readonly object _psFeedbackImdLock = new();
    private float[]? _psFeedbackImdScratch;
    private float[]? _psFeedbackImdSnapshot;
    private long _psFeedbackImdCenterHz;
    private double _psFeedbackImdHzPerPixel;
    private long _psFeedbackImdCapturedMs;
    private bool _psFeedbackImdValid;

    // HPSDR boards never satisfy ExternalFeedbackTransmitting, so their display
    // path and analyzer reads stay exactly as before.
    private bool PsFeedbackImdCaptureActive =>
        _appliedTwoToneEnabled && _appliedPsEnabled && ExternalFeedbackTransmitting;

    /// <summary>
    /// Called by Tick after the display surfaces chose their sources. When the
    /// panadapter already shows the feedback analyzer this tick, its finished
    /// pixels are copied; otherwise the analyzer is read into a private buffer,
    /// so the displayed frame is untouched and nothing else reads that analyzer
    /// on this path (the PS Monitor read above runs first in the same tick).
    /// </summary>
    private void CapturePsFeedbackImdSpectrum(IDspEngine engine, float[] panBuf, bool panIsFeedback,
        double txCalDb, long centerHz, double hzPerPixel, long tickMs)
    {
        if (!PsFeedbackImdCaptureActive) return;
        float[] source;
        if (panIsFeedback)
        {
            // Already offset, flipped to display order and sanitized.
            source = panBuf;
        }
        else
        {
            var scratch = _psFeedbackImdScratch ??= new float[_panadapterWidth];
            if (!engine.TryGetPsFeedbackDisplayPixels(DisplayPixout.Panadapter, scratch)) return;
            AddDbOffset(scratch, txCalDb);
            Array.Reverse(scratch);
            SanitizeDisplayBuffer(scratch);
            source = scratch;
        }

        lock (_psFeedbackImdLock)
        {
            var snapshot = _psFeedbackImdSnapshot ??= new float[_panadapterWidth];
            Array.Copy(source, snapshot, _panadapterWidth);
            _psFeedbackImdCenterHz = centerHz;
            _psFeedbackImdHzPerPixel = hzPerPixel;
            _psFeedbackImdCapturedMs = tickMs;
            _psFeedbackImdValid = true;
        }
    }

    /// <summary>
    /// The latest PureSignal feedback spectrum as an IMD measurement input, or
    /// false when none was captured within <see cref="PsFeedbackImdMaxAgeMs"/>.
    /// </summary>
    internal bool TryGetPsFeedbackImdInput(double? expectedToneSpacingHz,
        [NotNullWhen(true)] out ImdMeasureRequest? input)
    {
        input = null;
        lock (_psFeedbackImdLock)
        {
            if (!_psFeedbackImdValid || _psFeedbackImdSnapshot is not { } snapshot) return false;
            if (Environment.TickCount64 - _psFeedbackImdCapturedMs > PsFeedbackImdMaxAgeMs) return false;
            var db = new double?[snapshot.Length];
            for (int i = 0; i < snapshot.Length; i++) db[i] = snapshot[i];
            input = new ImdMeasureRequest
            {
                Db = db,
                Width = snapshot.Length,
                CenterHz = _psFeedbackImdCenterHz,
                HzPerPixel = _psFeedbackImdHzPerPixel,
                ExpectedToneSpacingHz = expectedToneSpacingHz,
            };
            return true;
        }
    }
}
