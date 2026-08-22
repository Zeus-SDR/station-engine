// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Contracts;

// Server → client VFO state edge. 18 bytes:
//   [0x26][receiver:u8][vfoHz:i64 LE][radioLoHz:i64 LE]
//
// Broadcast whenever a receiver's dial (VfoHz) or display centre (RadioLoHz)
// moves, from ANY source — front-panel encoder, CAT/TCI, band button, typed
// entry, or the web UI itself. It exists so the frontend learns of a HARDWARE
// tune immediately over the realtime socket instead of waiting for the 1 Hz
// /api/state poll (dial readout) and the ~200-400 ms display-frame echo (pan).
//
// The on-screen tuning gestures already move the display optimistically the
// instant the operator acts; a front-panel knob has no such local path, so its
// display trailed the knob. On receipt the frontend updates the dial readout
// and glides the pan's view-centre to RadioLoHz through the same external-tune
// path a CAT tune uses — the spectrum CONTENT still trails by the unavoidable
// DDC retune + FFT-fill time, but the dial and pan track the knob.
//
// Two frequencies, not one: VfoHz is the dial the operator reads; RadioLoHz is
// the display capture centre (dial ∓ CW pitch, and frozen under CTUN). The pan
// must retarget to RadioLoHz — feeding the raw dial would mis-place the trace
// by the CW pitch and would move the pan under CTUN where the panel must hold
// still. Carrying both keeps the readout and the pan each correct.
public readonly record struct VfoStateFrame(
    byte Receiver,
    long VfoHz,
    long RadioLoHz)
{
    public const int ByteLength = 18;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.VfoState;
        span[1] = Receiver;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(2, 8), VfoHz);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(10, 8), RadioLoHz);
        writer.Advance(ByteLength);
    }

    public static VfoStateFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"VfoStateFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.VfoState)
            throw new InvalidDataException($"expected VfoState (0x{(byte)MsgType.VfoState:X2}), got 0x{bytes[0]:X2}");
        return new VfoStateFrame(
            Receiver: bytes[1],
            VfoHz: BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(2, 8)),
            RadioLoHz: BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(10, 8)));
    }
}
