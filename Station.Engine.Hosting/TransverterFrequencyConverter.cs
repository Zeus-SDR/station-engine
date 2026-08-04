// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Converts between the radio's physical IF frequency and the RF frequency
/// presented for an additive transverter. No radio or DSP state belongs here.
/// </summary>
public static class TransverterFrequencyConverter
{
    public const long MinimumRadioFrequencyHz = 1;
    public const long MaximumRadioFrequencyHz = 60_000_000;

    public static long OffsetHz(TransverterSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return checked(settings.RfFrequencyHz - settings.IfFrequencyHz);
    }

    public static long ToRfHz(long ifHz, TransverterSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Enabled ? checked(ifHz + OffsetHz(settings)) : ifHz;
    }

    /// <summary>
    /// Convert an external RF command back to physical IF. Returns false rather
    /// than relying on RadioService's clamp when the requested RF frequency is
    /// outside the transverter's radio-tunable IF range.
    /// </summary>
    public static bool TryToIfHz(
        long rfHz,
        TransverterSettingsDto settings,
        out long ifHz)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            ifHz = settings.Enabled
                ? checked(rfHz - OffsetHz(settings))
                : rfHz;
        }
        catch (OverflowException)
        {
            ifHz = 0;
            return false;
        }

        return ifHz is >= MinimumRadioFrequencyHz and <= MaximumRadioFrequencyHz;
    }

    public static bool TryValidate(
        TransverterSettingsDto settings,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.IfFrequencyHz is < MinimumRadioFrequencyHz or > MaximumRadioFrequencyHz)
        {
            error = $"ifFrequencyHz must be between {MinimumRadioFrequencyHz} and {MaximumRadioFrequencyHz}";
            return false;
        }

        // This first implementation intentionally models the common additive
        // transverter case (for example 28 MHz IF -> 144 MHz RF). High-side LO
        // spectral inversion needs an explicit direction setting rather than a
        // surprising negative offset hidden in these two anchors.
        if (settings.RfFrequencyHz < settings.IfFrequencyHz)
        {
            error = "rfFrequencyHz must be greater than or equal to ifFrequencyHz";
            return false;
        }

        try
        {
            long offset = OffsetHz(settings);
            _ = checked(MaximumRadioFrequencyHz + offset);
        }
        catch (OverflowException)
        {
            error = "the transverter frequency offset is too large";
            return false;
        }

        error = null;
        return true;
    }
}
