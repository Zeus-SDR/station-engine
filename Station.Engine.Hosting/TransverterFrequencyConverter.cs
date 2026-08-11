// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Converts between physical radio IF and operator-facing RF. The band model
/// follows Thetis: IF = RF - LO + error.
/// </summary>
public static class TransverterFrequencyConverter
{
    public const int BandCount = 14;
    public const int MinimumBandId = 0;
    public const int MaximumBandId = BandCount - 1;
    public const int MaximumButtonTextLength = 5;
    public const long MinimumRadioFrequencyHz = 1;
    // Native operator tuning remains capped at 60 MHz. A configured
    // transverter profile may deliberately use the wider Thetis-compatible
    // hardware IF range without making 60..150 MHz a native tuning domain.
    public const long MaximumRadioFrequencyHz = 60_000_000;
    public const long MaximumTransverterIfFrequencyHz = 150_000_000;
    public const long MaximumRfFrequencyHz = 99_000_000_000;
    public const long MinimumLoOffsetHz = -144_000_000;
    public const long MaximumLoOffsetHz = 99_000_000_000;
    public const long MaximumAbsoluteLoErrorHz = 1_000_000;
    public const double MinimumRxGainDb = -100;
    public const double MaximumRxGainDb = 100;

    /// <summary>Legacy additive anchor offset retained for old callers.</summary>
    public static long OffsetHz(TransverterSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return checked(settings.RfFrequencyHz - settings.IfFrequencyHz);
    }

    public static long ToRfHz(long ifHz, TransverterSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled) return ifHz;

        if (settings.Bands is null)
            return checked(ifHz + OffsetHz(settings));

        if (TryResolveActiveBand(settings, out var active)
            || TryResolveBandByIfHz(ifHz, settings, out active))
            return checked(ifHz + active.LoOffsetHz - active.LoErrorHz);

        return ifHz;
    }

    /// <summary>
    /// Convert external RF back to physical IF. An active workspace band is
    /// authoritative; without one, the enabled band whose inclusive RF range
    /// contains the command is selected.
    /// </summary>
    public static bool TryToIfHz(
        long rfHz,
        TransverterSettingsDto settings,
        out long ifHz)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
        {
            ifHz = rfHz;
            return IsRadioFrequency(ifHz);
        }

        if (settings.Bands is null)
        {
            try
            {
                ifHz = checked(rfHz - OffsetHz(settings));
            }
            catch (OverflowException)
            {
                ifHz = 0;
                return false;
            }
            return IsTransverterIfFrequency(ifHz);
        }

        // RF range is authoritative so RX1, RX2, extra receivers, and split TX
        // can use different profiles at the same time. ActiveBandId is a UI /
        // IF-to-RF disambiguation hint, not a global routing lock.
        if (!TryResolveBandByRfHz(rfHz, settings, out var band))
        {
            ifHz = 0;
            return false;
        }

        return TryTranslateToIf(rfHz, band, out ifHz) && IsTransverterIfFrequency(ifHz);
    }

    public static bool TryResolveActiveBand(
        TransverterSettingsDto settings,
        out TransverterBandDto band)
    {
        ArgumentNullException.ThrowIfNull(settings);
        band = null!;
        if (settings.Bands is null || settings.ActiveBandId is not int id)
            return false;

        var match = settings.Bands.FirstOrDefault(item => item.Id == id && item.Enabled);
        if (match is null) return false;
        band = match;
        return true;
    }

    public static bool TryResolveBandByRfHz(
        long rfHz,
        TransverterSettingsDto settings,
        out TransverterBandDto band)
    {
        ArgumentNullException.ThrowIfNull(settings);
        band = null!;
        var match = settings.Bands?
            .Where(item => item.Enabled)
            .OrderBy(item => item.Id)
            .FirstOrDefault(item =>
                rfHz >= item.BeginFrequencyHz && rfHz <= item.EndFrequencyHz);
        if (match is null) return false;
        band = match;
        return true;
    }

    public static bool TryResolveBandByIfHz(
        long ifHz,
        TransverterSettingsDto settings,
        out TransverterBandDto band)
    {
        ArgumentNullException.ThrowIfNull(settings);
        band = null!;
        if (settings.Bands is null) return false;

        foreach (var candidate in settings.Bands.Where(item => item.Enabled).OrderBy(item => item.Id))
        {
            if (!TryTranslateToIf(candidate.BeginFrequencyHz, candidate, out long beginIf)
                || !TryTranslateToIf(candidate.EndFrequencyHz, candidate, out long endIf))
                continue;
            if (ifHz >= beginIf && ifHz <= endIf)
            {
                band = candidate;
                return true;
            }
        }
        return false;
    }

    public static bool TryValidate(
        TransverterSettingsDto settings,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!IsTransverterIfFrequency(settings.IfFrequencyHz))
        {
            error = $"ifFrequencyHz must be between {MinimumRadioFrequencyHz} and {MaximumTransverterIfFrequencyHz}";
            return false;
        }

        if (settings.Bands is null)
        {
            if (settings.RfFrequencyHz < settings.IfFrequencyHz)
            {
                error = "rfFrequencyHz must be greater than or equal to ifFrequencyHz";
                return false;
            }
            try
            {
                long offset = OffsetHz(settings);
                _ = checked(MaximumTransverterIfFrequencyHz + offset);
            }
            catch (OverflowException)
            {
                error = "the legacy transverter frequency offset is too large";
                return false;
            }
            error = null;
            return true;
        }

        if (settings.Bands.Count != BandCount)
        {
            error = $"bands must contain exactly {BandCount} slots";
            return false;
        }

        var ids = new HashSet<int>();
        foreach (var band in settings.Bands)
        {
            if (band.Id is < MinimumBandId or > MaximumBandId || !ids.Add(band.Id))
            {
                error = $"band ids must be unique and between {MinimumBandId} and {MaximumBandId}";
                return false;
            }
            if (band.ButtonText is null || band.ButtonText.Length > MaximumButtonTextLength)
            {
                error = $"band {band.Id} buttonText must contain at most {MaximumButtonTextLength} characters";
                return false;
            }
            if (band.LoOffsetHz is < MinimumLoOffsetHz or > MaximumLoOffsetHz)
            {
                error = $"band {band.Id} loOffsetHz is out of range";
                return false;
            }
            if (band.LoErrorHz is < -MaximumAbsoluteLoErrorHz or > MaximumAbsoluteLoErrorHz)
            {
                error = $"band {band.Id} loErrorHz is out of range";
                return false;
            }
            if (!double.IsFinite(band.RxGainDb)
                || band.RxGainDb is < MinimumRxGainDb or > MaximumRxGainDb)
            {
                error = $"band {band.Id} rxGainDb must be between {MinimumRxGainDb} and {MaximumRxGainDb}";
                return false;
            }
            if (band.Power is < 0 or > 100)
            {
                error = $"band {band.Id} power must be between 0 and 100";
                return false;
            }
            if (!Enum.IsDefined(band.RxAntenna))
            {
                error = $"band {band.Id} rxAntenna is invalid";
                return false;
            }
            if (!band.Enabled) continue;
            if (band.BeginFrequencyHz < 1
                || band.EndFrequencyHz > MaximumRfFrequencyHz
                || band.BeginFrequencyHz > band.EndFrequencyHz)
            {
                error = $"band {band.Id} RF range is invalid";
                return false;
            }
            if (!TryTranslateToIf(band.BeginFrequencyHz, band, out long beginIf)
                || !TryTranslateToIf(band.EndFrequencyHz, band, out long endIf)
                || !IsTransverterIfFrequency(beginIf)
                || !IsTransverterIfFrequency(endIf))
            {
                error = $"band {band.Id} RF range does not translate into the radio IF range";
                return false;
            }
        }

        var enabled = settings.Bands
            .Where(item => item.Enabled)
            .OrderBy(item => item.BeginFrequencyHz)
            .ThenBy(item => item.Id)
            .ToArray();
        for (int i = 1; i < enabled.Length; i++)
        {
            if (enabled[i].BeginFrequencyHz <= enabled[i - 1].EndFrequencyHz)
            {
                error = $"enabled RF ranges overlap between bands {enabled[i - 1].Id} and {enabled[i].Id}";
                return false;
            }
        }

        if (settings.ActiveBandId is int activeId
            && !settings.Bands.Any(item => item.Id == activeId && item.Enabled))
        {
            error = "activeBandId must identify an enabled band";
            return false;
        }

        if (settings.Enabled && settings.ActiveBandId is null)
        {
            error = "activeBandId is required when the transverter is enabled";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryTranslateToIf(
        long rfHz,
        TransverterBandDto band,
        out long ifHz)
    {
        try
        {
            ifHz = checked(rfHz - band.LoOffsetHz + band.LoErrorHz);
            return true;
        }
        catch (OverflowException)
        {
            ifHz = 0;
            return false;
        }
    }

    private static bool IsRadioFrequency(long hz) =>
        hz is >= MinimumRadioFrequencyHz and <= MaximumRadioFrequencyHz;

    private static bool IsTransverterIfFrequency(long hz) =>
        hz is >= MinimumRadioFrequencyHz and <= MaximumTransverterIfFrequencyHz;
}
