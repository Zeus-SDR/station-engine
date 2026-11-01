// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

public sealed partial class RadioService
{
    private volatile bool _externalFeedbackRuntime;
    private long _externalFeedbackEpoch;

    internal long BeginExternalFeedbackSession()
    {
        _externalFeedbackRuntime = true;
        return Interlocked.Increment(ref _externalFeedbackEpoch);
    }
    internal void EndExternalFeedbackSession() => _externalFeedbackRuntime = false;

    /// <summary>Per-board PS slot that holds the external feedback HW peak.</summary>
    internal const string ExternalFeedbackPsBoardKey = "external-feedback";

    /// <summary>
    /// The external TX chain's ALC holds voice at full scale, so calcc's top
    /// bucket fills at a 1.0 HW peak; lower lumps the loudest peaks into one
    /// bucket and higher never fills it.
    /// </summary>
    internal const double ExternalFeedbackHwPeakDefault = 1.0;

    /// <summary>
    /// When a product-operated radio first appears in a disconnected session,
    /// load its own HW peak (the persisted value or the external default) and
    /// route later HW peak edits to its slot. A connected HPSDR board, or a
    /// board slot already in use, is left alone.
    /// </summary>
    internal void ApplyExternalFeedbackPsHwPeak()
    {
        if (IsConnected || !string.IsNullOrEmpty(_currentPsBoardKey)) return;
        var persisted = _psStore?.Get();
        bool usingPersisted = persisted?.HwPeakByBoard is { } map
            && map.TryGetValue(ExternalFeedbackPsBoardKey, out double saved)
            && saved > 0.0;
        double peak = usingPersisted ? persisted!.HwPeakByBoard[ExternalFeedbackPsBoardKey] : ExternalFeedbackHwPeakDefault;
        _currentPsBoardKey = ExternalFeedbackPsBoardKey;
        Mutate(s => s.PsHwPeak == peak && s.PsHwPeakDefault == ExternalFeedbackHwPeakDefault
            ? s
            : s with { PsHwPeak = peak, PsHwPeakDefault = ExternalFeedbackHwPeakDefault });
        _log.LogInformation("radio.applyPsHwPeak external key={Key} peak={Peak:F4} default={Default:F4} source={Source}",
            ExternalFeedbackPsBoardKey, peak, ExternalFeedbackHwPeakDefault, usingPersisted ? "persisted" : "default");
    }

    // External runtime values never rewrite the saved HPSDR arm or attenuation.
    internal void ClearExternalFeedbackRuntime(long? epoch = null) => Mutate(s =>
        _externalFeedbackRuntime && (epoch is null || epoch == Interlocked.Read(ref _externalFeedbackEpoch)) &&
        s.Status == ConnectionStatus.Disconnected ? s with
        {
            PsEnabled = false,
            PsCorrecting = false,
            PsFeedbackLevel = 0,
            PsCalState = 0,
            PsCalibrationStalled = false,
        } : s);
}
