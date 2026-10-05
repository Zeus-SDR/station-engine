// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>A lease's fixed native-radio channel. Constrained audio uses the
/// selected receiver without split, XIT or a transverter. Receiver 0 is RX1.
/// RX2 keeps the radio's legacy VFO B transmit projection; every other
/// receiver keeps VFO A. The product owns its audio-tone offset.</summary>
public sealed record ProductPluginTxChannelConstraint(
    long DialFrequencyHz,
    string Mode,
    int Receiver = 0)
{
    internal bool IsValid => DialFrequencyHz is > 0 and <= 60_000_000
        && Mode is "USB" or "LSB" or "DIGU" or "DIGL"
        && (uint)Receiver < 10;

    internal bool Matches(StateDto state, bool transverterActive)
    {
        if (!IsValid || transverterActive || state.XitEnabled || state.SplitEnabled)
            return false;
        if (state.TxReceiverIndex != Receiver)
            return false;
        var expectedVfo = Receiver == 1 ? TxVfo.B : TxVfo.A;
        if (state.TxVfo != expectedVfo)
            return false;
        var row = Find(state, Receiver);
        if (row?.SplitEnabled == true)
            return false;
        if (Receiver == 0)
        {
            return state.VfoHz == DialFrequencyHz
                && string.Equals(state.Mode.ToString(), Mode, StringComparison.Ordinal);
        }
        return row is { Enabled: true, Name: null }
            && row.VfoHz == DialFrequencyHz
            && string.Equals(row.Mode.ToString(), Mode, StringComparison.Ordinal);
    }

    internal string OperatorText =>
        $"This audio transmission requires RX{Receiver + 1} at {DialFrequencyHz / 1_000_000.0:F6} MHz {Mode}, with SPLIT, XIT and transverter disabled.";

    private static ReceiverDto? Find(StateDto state, int receiver)
    {
        if (state.Receivers is not { } rows) return null;
        foreach (var row in rows)
        {
            if (row.Index == receiver) return row;
        }
        return null;
    }
}
