// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using Zeus.Contracts;

namespace Zeus.Server.Tci;

internal static class TciExtendedFrequency
{
    public static string Command(
        StateDto state,
        TransverterSettingsDto? transverterSettings = null) => Command(
        TransverterFrequencyConverter.ToRfHz(
            RadioFrequencyResolver.TxFrequencyHz(state),
            transverterSettings ?? new TransverterSettingsDto()),
        state.Rx2Enabled,
        TxUsesVfoB(state));

    internal static bool TxUsesVfoB(StateDto state) =>
        state.TxReceiverIndex == 1 || RadioFrequencyResolver.IsSplitEnabledForTx(state);

    public static string Command(long frequencyHz, bool rx2Enabled, bool txOnVfoB)
    {
        var band = BandUtils.FreqToBand(frequencyHz) is { } name ? $"b{name}" : "bgen";
        return $"tx_frequency_ex:{frequencyHz.ToString(CultureInfo.InvariantCulture)},{band}," +
               $"{rx2Enabled.ToString().ToLowerInvariant()},{txOnVfoB.ToString().ToLowerInvariant()};";
    }
}
