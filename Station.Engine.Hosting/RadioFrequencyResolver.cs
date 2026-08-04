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
                state.FilterPresetName, state.RxAfGainDb, state.SampleRate,
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
            state.FilterPresetName, state.RxAfGainDb, state.SampleRate,
            state.Rx1Muted, SplitEnabled: state.SplitEnabled,
            TxVfoHz: state.SplitTxHz);
    }

    public static long TxFrequencyHz(StateDto state)
    {
        var receiver = TxReceiver(state);
        return receiver.SplitEnabled && receiver.TxVfoHz > 0
            ? receiver.TxVfoHz
            : receiver.VfoHz;
    }

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
