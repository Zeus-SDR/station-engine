// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers;

namespace Zeus.Contracts;

/// <summary>
/// Receive-side Audio Suite master-bypass broadcast. Carries a single boolean
/// for the RX insert chain, independent from the TX Audio Suite bypass frame.
///
/// Payload: <c>[type:1][bypassed:u8]</c> — 2 bytes total.
/// </summary>
public readonly record struct RxAudioMasterBypassFrame(bool Bypassed)
{
    public const int ByteLength = 2;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.RxAudioMasterBypass;
        span[1] = Bypassed ? (byte)1 : (byte)0;
        writer.Advance(ByteLength);
    }

    public static RxAudioMasterBypassFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException(
                $"RxAudioMasterBypassFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.RxAudioMasterBypass)
            throw new InvalidDataException(
                $"expected RxAudioMasterBypass (0x{(byte)MsgType.RxAudioMasterBypass:X2}), got 0x{bytes[0]:X2}");
        return new RxAudioMasterBypassFrame(Bypassed: bytes[1] != 0);
    }
}
