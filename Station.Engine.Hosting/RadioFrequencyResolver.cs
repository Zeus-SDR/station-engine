// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

public static class RadioFrequencyResolver
{
    // Authoritative TX carrier frequency for the selected TX target. Index 0 =
    // RX1 (VFO A), 1 = RX2 (VFO B), >= 2 = an extra DDC read from the projected
    // Receivers[] array. Use this static form on a SNAPSHOT (Receivers
    // populated); internal callers holding RadioService._sync on the
    // un-projected state must use RadioService.TxFrequencyHzLocked, which
    // resolves >= 2 from RadioService._extraReceivers.
    public static ReceiverDto TxReceiver(StateDto state)
    {
        if (state.TxReceiverIndex <= 0)
            return new ReceiverDto(
                0, true, RadioService.ReceiverAdcSource(state, 0), state.VfoHz,
                state.Mode, state.FilterLowHz, state.FilterHighHz,
                state.FilterPresetName, state.Rx1AfGainDb, state.SampleRate,
                state.Rx1Muted, SplitEnabled: state.SplitEnabled,
                TxVfoHz: state.SplitTxHz);

        if (state.TxReceiverIndex == 1)
            return state.Rx2();

        if (state.Receivers is { } receivers)
        {
            for (int i = 0; i < receivers.Count; i++)
                if (receivers[i].Index == state.TxReceiverIndex)
                    return receivers[i];
        }

        return new ReceiverDto(
            0, true, RadioService.ReceiverAdcSource(state, 0), state.VfoHz,
            state.Mode, state.FilterLowHz, state.FilterHighHz,
            state.FilterPresetName, state.Rx1AfGainDb, state.SampleRate,
            state.Rx1Muted, SplitEnabled: state.SplitEnabled,
            TxVfoHz: state.SplitTxHz);
    }

    public static long TxFrequencyHz(StateDto state)
    {
        var receiver = TxReceiver(state);
        return ResolveTxFrequencyHz(
            receiver.Mode, receiver.VfoHz, receiver.SplitEnabled, receiver.TxVfoHz, state.Fm);
    }

    // FM repeater shift (Thetis console.cs:29348-29367, applied to TXFreq on
    // key-up): only while the TX mode is FM, the shift is not Simplex, and
    // split is OFF — split owns the TX dial and Thetis disables the FM
    // shift controls while it is on (console.cs:35630). The dial (VfoHz /
    // TxDialFrequencyHz) never moves; only the transmitted carrier does.
    internal static long ResolveTxFrequencyHz(
        RxMode txMode, long vfoHz, bool splitEnabled, long txVfoHz, FmConfig? fm)
    {
        if (splitEnabled)
            return txVfoHz > 0 ? txVfoHz : vfoHz;
        return vfoHz + FmRepeaterOffsetHz(txMode, vfoHz, fm);
    }

    public static long FmRepeaterOffsetHz(RxMode txMode, long rxHz, FmConfig? fm) =>
        txMode == RxMode.FM ? (fm ?? FmConfig.Default).SignedRepeaterOffsetHz(rxHz) : 0;

    public static long TxDialFrequencyHz(StateDto state)
    {
        var receiver = TxReceiver(state);
        return receiver.TxVfoHz > 0 ? receiver.TxVfoHz : receiver.VfoHz;
    }

    public static RxMode TxMode(StateDto state) => TxReceiver(state).Mode;

    public static bool IsSplitEnabledForTx(StateDto state)
    {
        var receiver = TxReceiver(state);
        return receiver.SplitEnabled && receiver.TxVfoHz > 0;
    }

    /// <summary>Kenwood/Thetis VFO-B projection: RX2 owns B while exposed;
    /// otherwise B is RX1's independent split-TX dial.</summary>
    public static long CatVfoBHz(StateDto state)
    {
        if (state.Rx2Enabled && state.Receivers is { Count: > 1 })
            return state.Receivers[1].VfoHz;
        return state.SplitTxHz > 0 ? state.SplitTxHz : state.VfoHz;
    }
}
