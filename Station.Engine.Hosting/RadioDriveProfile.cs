// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Per-radio quirks in how the drive-level byte actually makes it from
// Zeus to RF. Every HPSDR radio ostensibly speaks the same protocol 1 /
// protocol 2 wire format, but what each one DOES with the byte we put
// in C0=0x12, C1=drive_level varies:
//
//   - Hermes / ANAN-10 / ANAN-100 / Orion / G2  — use all 8 bits.
//     Fine-grained drive control; the classic Thetis/piHPSDR math
//     (target watts → dBm − PA gain → volts @ 50 Ω → byte/255 × 0.8 V)
//     maps cleanly to output power.
//
//   - Hermes-Lite 2 — DriveFilter C1 bits [7:4] are a 0.5 dB step
//     attenuator (code 15 = 0 dB, code 0 = -7.5 dB, not silence).
//     EncodeDrive picks the lowest step that covers the target
//     amplitude and scales TX IQ so output power tracks the slider.
//     PureSignal uses that same pair. The DDC3 reference is
//     compensated by 1/IqScale because the DAC tap is before the
//     attenuator. See HermesLite2DriveProfile and pihpsdr radio.c:2572.
//
// IMPORTANT for anyone touching TX / PA / drive-byte code:
//
//   Go through this abstraction. Do not hard-code an 8-bit linear
//   voltage model and expect it to "just work" on every board — it
//   won't on HL2, and the silence on the bench can eat a day before
//   you realise the bytes you're computing aren't the bytes the
//   radio is honouring. Add new board quirks by implementing
//   IRadioDriveProfile and extending RadioDriveProfiles.For.
//
// Reference implementations:
//   - piHPSDR radio.c:2809-2828 (8-bit, no HL2 quantisation — HL2
//     users happen to land drive high enough that nibble 0xF is
//     reached, which is why their "it works" setups work).
//   - Thetis console.cs:46801-46841 (8-bit, no HL2-specific branch).
//   - mi0bot/openhpsdr-thetis — HL2-specific fork; look there for
//     further HL2 quirks Zeus may need to mirror.

using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

/// <summary>
/// Encapsulates per-board drive-byte encoding. Implementations convert a
/// calibrated drive % / PA-gain / max-watts triple into the final byte that
/// will be written to C0=0x12, C1 on the wire.
/// </summary>
public interface IRadioDriveProfile
{
    /// <summary>
    /// Board this profile targets. Diagnostic only.
    /// </summary>
    string BoardLabel { get; }

    /// <summary>
    /// Produce the byte to send in the DriveFilter C1 slot.
    /// </summary>
    /// <param name="drivePct">Operator slider position, 0..100.</param>
    /// <param name="paGainDb">Per-band PA calibration value from PaSettingsStore.
    /// <b>Interpretation depends on the profile:</b>
    /// <see cref="FullByteDriveProfile"/> reads it as dB forward gain
    /// (Hermes / ANAN / Orion convention). <see cref="HermesLite2DriveProfile"/>
    /// reads it as a per-band output percentage (0..100), matching mi0bot
    /// Thetis — see that class's comment for the full derivation.
    /// The DTO field name is retained for storage compatibility across
    /// boards, not because the semantics are uniform.</param>
    /// <param name="maxWatts">Rated PA output watts. 0 triggers the legacy
    /// straight-percent-to-byte mapping that pre-dates the PA math on
    /// FullByte profiles. Ignored on HL2 — percentage-based math doesn't
    /// consult rated watts.</param>
    byte EncodeDriveByte(int drivePct, double paGainDb, double maxWatts);

    /// <summary>
    /// Drive register byte plus the TX IQ scale that together produce the
    /// requested output. Hermes-Lite 2, including PureSignal, uses this
    /// pair. Other boards return <see cref="TxDriveOutput.FromLegacyByte"/>
    /// of <see cref="EncodeDriveByte"/>.
    /// </summary>
    TxDriveOutput EncodeDrive(int drivePct, double paGainDb, double maxWatts);

    /// <summary>
    /// Resolves an Xvtr dBm rating into the profile's PA-gain domain. Most
    /// radios use dB gain and need no adjustment; HL2 uses output percent.
    /// </summary>
    double ResolveXvtrGain(double paGainDb, double maxWatts, int radioMaxWatts);
}

/// <summary>
/// Shared watts → drive-byte math used by every profile as the baseline.
/// Pure function, deterministic, unit-tested. Operator-facing calibration
/// lives in PaSettingsStore; this does not touch storage.
///
/// Reference: Thetis <c>console.cs:46801-46841</c>, piHPSDR
/// <c>radio.c:2809-2828</c>.
/// </summary>
internal static class DriveByteMath
{
    public static byte ComputeFullByte(int drivePct, double paGainDb, double maxWatts)
    {
        drivePct = Math.Clamp(drivePct, 0, 100);
        if (maxWatts <= 0)
        {
            return (byte)(drivePct * 255 / 100);
        }

        double targetWatts = maxWatts * drivePct / 100.0;
        if (targetWatts <= 0) return 0;

        double sourceWatts = targetWatts / Math.Pow(10.0, paGainDb / 10.0);
        double sourceVolts = Math.Sqrt(sourceWatts * 50.0);
        double norm = Math.Clamp(sourceVolts / 0.8, 0.0, 1.0);
        return (byte)Math.Round(norm * 255.0);
    }

    /// <summary>
    /// Converts an RF level in dBm to watts without discarding sub-watt Xvtr
    /// ratings through integer rounding.
    /// </summary>
    public static double WattsFromDbm(double dbm) => Math.Pow(10.0, (dbm - 30.0) / 10.0);
}

/// <summary>
/// Default 8-bit profile for Hermes, ANAN-10/100/100D/200D/8000D, Orion,
/// Orion MkII (G1/G2/G2-1K) and anything else that honours the full drive
/// byte. No quantisation — the computed byte goes straight to the wire.
/// </summary>
public sealed class FullByteDriveProfile : IRadioDriveProfile
{
    public static readonly FullByteDriveProfile Instance = new();
    private FullByteDriveProfile() { }

    public string BoardLabel => "FullByte (8-bit)";

    public byte EncodeDriveByte(int drivePct, double paGainDb, double maxWatts)
        => DriveByteMath.ComputeFullByte(drivePct, paGainDb, maxWatts);

    public TxDriveOutput EncodeDrive(int drivePct, double paGainDb, double maxWatts)
        => TxDriveOutput.FromLegacyByte(EncodeDriveByte(drivePct, paGainDb, maxWatts));

    public double ResolveXvtrGain(double paGainDb, double maxWatts, int radioMaxWatts)
    {
        _ = maxWatts;
        _ = radioMaxWatts;
        return paGainDb;
    }
}

/// <summary>
/// Hermes-Lite 2 profile. HL2 does not use the piHPSDR/Thetis dB drive
/// model. <c>paGainDb</c> is a per-band <b>output percentage</b> (0..100),
/// matching mi0bot openhpsdr-thetis (100 = no band cap; 6 m on the stock
/// PA is about 38.8). <c>maxWatts</c> is ignored. The DTO field stays
/// named <c>PaGainDb</c> so the stored row is shared with other boards.
///
/// The AD9866 TX PGA (DriveFilter C1, only bits [7:4]) is a hardware step
/// attenuator. Code <c>n</c> in 0..15 has gain
/// <c>g(n) = 10^(-(15-n)*0.5/20)</c>: 0 dB at n=15, -7.5 dB at n=0.
/// Code 0 is not silence. <see cref="EncodeDrive"/> picks the lowest step
/// whose gain covers the target amplitude <c>sqrt(slider% × band%)</c> and
/// scales TX IQ by the remainder, so delivered power tracks the slider.
/// pihpsdr does the same split (radio.c:2572). At slider 100 and band 100
/// the result is drive byte 240 and IQ scale exactly 1.0.
///
/// <see cref="EncodeDriveByte"/> is the older register-only mapping
/// (slider × band% quantised to the nearest nibble), kept for legacy
/// callers. PureSignal sends <see cref="EncodeDrive"/>, the same pair as
/// every other HL2 transmission. The protocol client multiplies the DDC3
/// reference by <c>1/IqScale</c> because that tap is before the attenuator.
///
/// Reference:
///   • docs/references/firmware/hermes-lite-2/wiki/Software.md:110
///   • pihpsdr src/radio.c:2572
///   • docs/references/protocol-1/hermes-lite2-protocol.md:51
///   • docs/lessons/hl2-drive-model.md
/// </summary>
public sealed class HermesLite2DriveProfile : IRadioDriveProfile
{
    public static readonly HermesLite2DriveProfile Instance = new();
    private HermesLite2DriveProfile() { }

    // g(n) for n = 0..15. Computed once; EncodeDrive does not allocate.
    private static readonly double[] StepGain = BuildStepGain();

    private static double[] BuildStepGain()
    {
        var gain = new double[16];
        for (int n = 0; n < gain.Length; n++)
            gain[n] = Math.Pow(10.0, -(15 - n) * 0.5 / 20.0);
        gain[15] = 1.0;
        return gain;
    }

    public string BoardLabel => "HermesLite2 (%-scale, 4-bit)";

    public byte EncodeDriveByte(int drivePct, double paGainDb, double maxWatts)
    {
        // On HL2 "paGainDb" is a percentage, not decibels (see class-level
        // comment). Clamp to the percentage domain; maxWatts is ignored
        // because the HL2 drive pipeline is slider × band-percentage, no
        // target-watts conversion. Legacy callers only. PureSignal uses
        // EncodeDrive.
        _ = maxWatts;
        int pct = Math.Clamp(drivePct, 0, 100);
        double bandPct = Math.Clamp(paGainDb, 0.0, 100.0);
        double driveNorm = (pct / 100.0) * (bandPct / 100.0);
        byte raw = (byte)Math.Round(driveNorm * 255.0);

        // HL2 gateware reads only bits [31:28] of the drive register — the
        // bottom nibble is silently discarded. Round to the nearest 16-count
        // step so slider motion is honest (each step crosses one real power
        // level). Saturate at 15 so we never overflow.
        int nibble = (int)Math.Round(raw / 16.0);
        if (nibble > 15) nibble = 15;
        return (byte)(nibble * 16);
    }

    public TxDriveOutput EncodeDrive(int drivePct, double paGainDb, double maxWatts)
    {
        int pct = Math.Clamp(drivePct, 0, 100);
        double bandPct = Math.Clamp(paGainDb, 0.0, 100.0);
        double power = (pct / 100.0) * (bandPct / 100.0);
        if (power <= 0.0) return TxDriveOutput.Off;

        // Lowest hardware step whose gain covers the target amplitude.
        // Epsilon keeps an exact hit on g(n) from falling through to n+1.
        double amplitude = Math.Sqrt(power);
        const double gainEpsilon = 1e-9;
        int n = 15;
        for (int i = 0; i < StepGain.Length; i++)
        {
            if (StepGain[i] + gainEpsilon >= amplitude)
            {
                n = i;
                break;
            }
        }

        double scale = amplitude / StepGain[n];
        if (scale < 0.0) scale = 0.0;
        else if (scale > 1.0) scale = 1.0;

        return new TxDriveOutput((byte)(n * 16), scale);
    }

    public double ResolveXvtrGain(double paGainDb, double maxWatts, int radioMaxWatts)
    {
        if (radioMaxWatts <= 0) return paGainDb;
        return Math.Min(paGainDb, maxWatts * 100.0 / radioMaxWatts);
    }
}

/// <summary>
/// Per-board dispatch. Extend this switch whenever a new board needs a
/// non-default drive encoding. Anything not explicitly mapped falls through
/// to <see cref="FullByteDriveProfile"/>, which is the correct choice for
/// every full-Hermes-class radio.
/// </summary>
public static class RadioDriveProfiles
{
    public static IRadioDriveProfile For(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.HermesLite2 => HermesLite2DriveProfile.Instance,
        _                          => FullByteDriveProfile.Instance,
    };
}
