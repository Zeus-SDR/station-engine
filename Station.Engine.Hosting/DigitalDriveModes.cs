// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Which digital mode the transmitter is in, for the station-wide per-mode
/// Drive memory. The engine only sees DIGU/DIGL, so FT8 / FT4 / WSPR are told
/// apart by the standard USB dial the TX receiver is tuned to. Those dials are
/// fixed by convention and shared by the Zeus digital workspace and external
/// apps (WSJT-X over CAT), so both land on the same memory. Any other DIGU
/// dial (JS8, RTTY, PSK, ...) is plain DIGU.
///
/// The receiver's own dial is used, never the split TX VFO: WSJT-X split
/// ("Rig") moves only the TX VFO, and "Fake It" moves the dial itself, both
/// in 500 Hz steps of up to ±1.5 kHz to keep the TX audio at 1.5–2 kHz. A
/// dial on one of those steps still belongs to its standard dial, so an FT8
/// transmission high in the passband keeps the FT8 Drive.
/// </summary>
public static class DigitalDriveModes
{
    public const string Ft8 = "FT8";
    public const string Ft4 = "FT4";
    public const string Wspr = "WSPR";
    public const string Digu = "DIGU";
    public const string Digl = "DIGL";
    public const string FreeDv = "FREEDV";

    internal const long SplitStepHz = 500;
    internal const long MaxSplitShiftHz = 1_500;
    // Slack around each 500 Hz step. Under half the closest step-aligned gap
    // between two modes (17 m: FT4 18.1045 vs WSPR 18.1046 MHz is 100 Hz), so
    // the 17 m and 30 m FT4/WSPR pairs never both match one dial.
    internal const long StepToleranceHz = 40;

    // Mirrors DIGITAL_BANDS in zeus-web/src/dsp/digital-segments.ts (WSJT-X
    // FT8/FT4 defaults, WSPRnet WSPR dials). Keep the two in step.
    private static readonly (string Mode, long DialHz)[] Dials =
    [
        (Ft8, 1_840_000), (Wspr, 1_836_600),
        (Ft8, 3_573_000), (Ft4, 3_575_000), (Wspr, 3_568_600),
        (Ft8, 5_357_000), (Wspr, 5_364_700),
        (Ft8, 7_074_000), (Ft4, 7_047_500), (Wspr, 7_038_600),
        (Ft8, 10_136_000), (Ft4, 10_140_000), (Wspr, 10_138_700),
        (Ft8, 14_074_000), (Ft4, 14_080_000), (Wspr, 14_095_600),
        (Ft8, 18_100_000), (Ft4, 18_104_000), (Wspr, 18_104_600),
        (Ft8, 21_074_000), (Ft4, 21_140_000), (Wspr, 21_094_600),
        (Ft8, 24_915_000), (Ft4, 24_919_000), (Wspr, 24_924_600),
        (Ft8, 28_074_000), (Ft4, 28_180_000), (Wspr, 28_124_600),
        (Ft8, 50_313_000), (Ft4, 50_318_000), (Wspr, 50_293_000),
        (Ft8, 144_174_000), (Wspr, 144_489_000),
    ];

    /// <summary>The memory key for a TX mode + TX dial, or null when the mode
    /// is not a digital mode (its Drive stays on the per-band memory).</summary>
    /// <param name="current">The mode the memory is in now. Where a dial sits
    /// on split steps of two modes (only 80 m FT8/FT4, 2 kHz apart), staying
    /// in the current mode wins.</param>
    public static string? KeyFor(RxMode txMode, long txDialHz, string? current = null) => txMode switch
    {
        RxMode.DIGU => DialMode(txDialHz, current) ?? Digu,
        RxMode.DIGL => Digl,
        RxMode.FreeDv => FreeDv,
        _ => null,
    };

    public static string? KeyFor(StateDto state, string? current = null)
    {
        var receiver = RadioFrequencyResolver.TxReceiver(state);
        return KeyFor(receiver.Mode, receiver.VfoHz, current);
    }

    // The standard dial this dial sits on (exactly, or a split step away).
    // Where two modes' windows overlap (80 m FT8/FT4 are 2 kHz apart) the
    // current mode wins, else the smaller shift, else the earlier (FT8) entry.
    private static string? DialMode(long dialHz, string? current)
    {
        string? best = null;
        long bestShift = long.MaxValue;
        foreach (var (mode, dial) in Dials)
        {
            long shift = dialHz - dial;
            if (Math.Abs(shift) > MaxSplitShiftHz + StepToleranceHz) continue;
            long step = (long)Math.Round((double)shift / SplitStepHz) * SplitStepHz;
            if (Math.Abs(shift - step) > StepToleranceHz) continue;
            if (mode == current) return mode;
            if (Math.Abs(shift) < bestShift)
            {
                best = mode;
                bestShift = Math.Abs(shift);
            }
        }
        return best;
    }
}
