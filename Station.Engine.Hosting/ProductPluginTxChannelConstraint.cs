// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>A lease's fixed native-radio channel. Constrained audio uses RX1
/// without split, XIT or a transverter; the product owns its audio-tone offset.</summary>
public sealed record ProductPluginTxChannelConstraint(long DialFrequencyHz, string Mode)
{
    internal bool IsValid => DialFrequencyHz is > 0 and <= 60_000_000
        && Mode is "USB" or "LSB" or "DIGU" or "DIGL";

    internal bool Matches(StateDto state, bool transverterActive) =>
        IsValid
        && !transverterActive
        && state.TxReceiverIndex == 0
        && state.TxVfo == TxVfo.A
        && !state.SplitEnabled
        && !state.XitEnabled
        && state.VfoHz == DialFrequencyHz
        && string.Equals(state.Mode.ToString(), Mode, StringComparison.Ordinal);

    internal string OperatorText =>
        $"This audio transmission requires RX1 at {DialFrequencyHz / 1_000_000.0:F6} MHz {Mode}, with SPLIT, XIT and transverter disabled.";
}
