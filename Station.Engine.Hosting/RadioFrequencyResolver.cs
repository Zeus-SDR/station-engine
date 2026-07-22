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
    public static long TxFrequencyHz(StateDto state) => state.TxReceiverIndex switch
    {
        <= 0 => state.VfoHz,
        1 => state.Rx2().VfoHz,
        int i => state.Receivers is { } rs && i < rs.Count ? rs[i].VfoHz : state.VfoHz,
    };
}
