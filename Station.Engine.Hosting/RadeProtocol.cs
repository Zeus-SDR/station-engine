// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// RADE protocol vocabulary shared by the engine modem, its service and the
// station endpoints. RADE V1 is the original FreeDV neural waveform; RADE V2
// is the second (upstream BETA, may change incompatibly). The station wire
// carries these as short strings ("V1", "V2", "Auto") so an older peer that
// does not know them simply ignores the field and keeps today's V1 behaviour.

namespace Zeus.Server;

/// <summary>A concrete RADE protocol. <see cref="None"/> = no protocol in force.</summary>
internal enum RadeProtocol
{
    None = 0,
    V1 = 1,
    V2 = 2,
}

/// <summary>What the product asked the engine to run.</summary>
internal enum RadeProtocolSelection
{
    /// <summary>RADE V1 only (the pre-V2 behaviour, and the default for an old product).</summary>
    V1 = 1,
    /// <summary>RADE V2 only.</summary>
    V2 = 2,
    /// <summary>
    /// Receive V1 and V2 side by side and play whichever holds sync; transmit
    /// the protocol last locked on receive (see <see cref="RadeProtocols.ResolveTx"/>).
    /// </summary>
    Auto = 3,
}

internal static class RadeProtocols
{
    internal const string V1Wire = "V1";
    internal const string V2Wire = "V2";
    internal const string AutoWire = "Auto";

    /// <summary>
    /// Parse the select request's protocol. Absent (an older product) means
    /// V1, exactly what that product always got. Anything unrecognised is
    /// rejected rather than silently mapped to a protocol the operator did not
    /// pick.
    /// </summary>
    internal static bool TryParseSelection(string? wire, out RadeProtocolSelection selection)
    {
        selection = RadeProtocolSelection.V1;
        if (string.IsNullOrWhiteSpace(wire)) return true;
        switch (wire.Trim().ToUpperInvariant())
        {
            case "V1":
            case "1":
                selection = RadeProtocolSelection.V1;
                return true;
            case "V2":
            case "2":
                selection = RadeProtocolSelection.V2;
                return true;
            case "AUTO":
                selection = RadeProtocolSelection.Auto;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Parse the operator's preferred protocol (advisory, used only as the
    /// Auto TX fallback). Anything other than V1/V2 means "no preference".
    /// </summary>
    internal static RadeProtocol ParsePreferred(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire)) return RadeProtocol.None;
        return wire.Trim().ToUpperInvariant() switch
        {
            "V1" or "1" => RadeProtocol.V1,
            "V2" or "2" => RadeProtocol.V2,
            _ => RadeProtocol.None,
        };
    }

    internal static string? ToWire(RadeProtocol protocol) => protocol switch
    {
        RadeProtocol.V1 => V1Wire,
        RadeProtocol.V2 => V2Wire,
        _ => null,
    };

    internal static string ToWire(RadeProtocolSelection selection) => selection switch
    {
        RadeProtocolSelection.V2 => V2Wire,
        RadeProtocolSelection.Auto => AutoWire,
        _ => V1Wire,
    };

    /// <summary>
    /// The protocol the NEXT over transmits. Manual selections are literal.
    /// Auto transmits the protocol last locked on receive this session, else
    /// the operator's preferred RADE protocol, else V1 — never V2 on a library
    /// that cannot encode it, and never V2 when the transmitter rides LSB
    /// (<paramref name="txRidesLsb"/>: the TX carrier's effective sideband):
    /// V2 is USB-only, so a V2 over there would be on the wrong sideband for
    /// every V2 receiver.
    /// </summary>
    internal static RadeProtocol ResolveTx(
        RadeProtocolSelection selection,
        RadeProtocol lastLocked,
        RadeProtocol preferred,
        bool v2Available,
        bool txRidesLsb = false)
    {
        RadeProtocol candidate = selection switch
        {
            RadeProtocolSelection.V1 => RadeProtocol.V1,
            RadeProtocolSelection.V2 => RadeProtocol.V2,
            _ => lastLocked != RadeProtocol.None
                ? lastLocked
                : preferred != RadeProtocol.None ? preferred : RadeProtocol.V1,
        };
        if (selection == RadeProtocolSelection.Auto && candidate == RadeProtocol.V2
            && (!v2Available || txRidesLsb))
            return RadeProtocol.V1;
        return candidate;
    }

    internal static string LibraryDescription(RadeProtocolSelection selection, bool active, bool v2Available)
    {
        if (!active)
            return v2Available ? "RADE V1 + V2 beta (radae_c)" : "RADE V1 (radae_c)";
        return selection switch
        {
            RadeProtocolSelection.V2 => "RADE V2 beta (radae_c)",
            RadeProtocolSelection.Auto when v2Available => "RADE V1 + V2 beta (radae_c)",
            _ => "RADE V1 (radae_c)",
        };
    }
}

/// <summary>
/// Decides, per received block, which RADE decoder's speech is played when
/// one or both run on the same input. Pure and allocation-free (the RX hot
/// path owns it); time is the caller's monotonic 8 kHz sample clock so the
/// rule is deterministic under test.
/// <para>
/// Measured behaviour it encodes: a V2 receiver never syncs on a V1 signal,
/// but a V1 receiver CAN falsely sync on a strong V2 signal for up to about
/// a second, emitting garbage while it does. So V2 wins whenever it holds
/// sync, and a V1 lock is trusted only once it has held continuously for the
/// false-lock window with V2 unsynced throughout. A V1-only receiver (manual
/// RADEV1) has nothing to be confused with and is trusted immediately, exactly
/// as before V2 existed.
/// </para>
/// </summary>
internal sealed class RadeRxArbiter
{
    /// <summary>1.5 s at the 8 kHz modem rate: comfortably past the ~1 s V1 false-lock.</summary>
    internal const long DefaultFalseLockWindowSamples = 12_000;

    private readonly long _windowSamples;
    private long _v1SyncSince = -1;
    private long _v2LastSynced = long.MinValue;

    internal RadeRxArbiter(long falseLockWindowSamples = DefaultFalseLockWindowSamples)
    {
        _windowSamples = falseLockWindowSamples;
    }

    internal RadeProtocol Arbitrate(long clock, bool v1Present, bool v1Synced, bool v2Present, bool v2Synced)
    {
        bool v1 = v1Present && v1Synced;
        if (v1)
        {
            if (_v1SyncSince < 0) _v1SyncSince = clock;
        }
        else
        {
            _v1SyncSince = -1;
        }

        if (v2Present && v2Synced)
        {
            _v2LastSynced = clock;
            return RadeProtocol.V2;
        }
        if (!v1) return RadeProtocol.None;
        if (!v2Present) return RadeProtocol.V1;

        long since = _v2LastSynced == long.MinValue
            ? _v1SyncSince
            : Math.Max(_v1SyncSince, _v2LastSynced);
        return clock - since >= _windowSamples ? RadeProtocol.V1 : RadeProtocol.None;
    }

    internal void Reset()
    {
        _v1SyncSince = -1;
        _v2LastSynced = long.MinValue;
    }
}
