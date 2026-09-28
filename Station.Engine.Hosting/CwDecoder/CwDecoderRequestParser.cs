// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Buffers.Binary;

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Remote.Transport;
#else
namespace Zeus.Server;
#endif

/// <summary>
/// Client → server CW decoder request. The original 2-byte form still enables
/// or disables the refcounted decoder. The 5-byte form adds the audio tone to
/// listen on and whether that tone is locked.
///
/// <code>
/// [0x25][enable:u8]
/// [0x25][enable:u8][flags:u8][targetHz:u16 LE]
/// </code>
///
/// <c>flags</c> bit 0 locks the tone (no auto search). <c>targetHz</c> 0 follows
/// the radio's CW pitch; any other value is clamped to 200..3000 Hz.
/// A disable never carries a target: closing one client must not move the tone
/// the latest enabling request set. Target fields on a disable are ignored.
/// The hub itself returns to the CW pitch when the last listener lets go.
/// </summary>
public readonly record struct CwDecoderRequest(bool Enable, bool HasTarget, int TargetHz, bool Locked)
{
    public const byte TypeByte = 0x25;
    public const byte LockFlag = 0x01;
    public const int MinTargetHz = 200;
    public const int MaxTargetHz = 3000;
    public const int LegacyByteLength = 2;
    public const int ExtendedByteLength = 5;
}

public static class CwDecoderRequestParser
{
    public static bool TryParse(ReadOnlySpan<byte> frame, out CwDecoderRequest request)
    {
        request = default;
        if (frame.Length != CwDecoderRequest.LegacyByteLength
            && frame.Length != CwDecoderRequest.ExtendedByteLength)
            return false;
        if (frame[0] != CwDecoderRequest.TypeByte)
            return false;

        bool enable = frame[1] != 0;
        // Legacy frames have no target. A disable drops any target bytes so
        // the tone stays with the most recent enabling request.
        if (!enable || frame.Length == CwDecoderRequest.LegacyByteLength)
        {
            request = new CwDecoderRequest(enable, false, 0, false);
            return true;
        }

        bool locked = (frame[2] & CwDecoderRequest.LockFlag) != 0;
        int raw = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(3, 2));
        int target = raw == 0 ? 0 : Math.Clamp(raw, CwDecoderRequest.MinTargetHz, CwDecoderRequest.MaxTargetHz);
        request = new CwDecoderRequest(enable, true, target, locked);
        return true;
    }

    public static byte[] Encode(bool enable, int targetHz, bool locked)
    {
        int hz = targetHz <= 0
            ? 0
            : Math.Clamp(targetHz, CwDecoderRequest.MinTargetHz, CwDecoderRequest.MaxTargetHz);
        var frame = new byte[CwDecoderRequest.ExtendedByteLength];
        frame[0] = CwDecoderRequest.TypeByte;
        frame[1] = (byte)(enable ? 1 : 0);
        frame[2] = (byte)(locked ? CwDecoderRequest.LockFlag : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3, 2), (ushort)hz);
        return frame;
    }

    /// <summary>
    /// Demand frame for the refcounted decoder. Disable is always the legacy
    /// 2-byte form. An enable carries a tone only when the caller has one.
    /// </summary>
    public static byte[] EncodeDemand(bool enable, bool includeTarget, int targetHz, bool locked)
    {
        if (!enable || !includeTarget)
        {
            var legacy = new byte[CwDecoderRequest.LegacyByteLength];
            legacy[0] = CwDecoderRequest.TypeByte;
            legacy[1] = (byte)(enable ? 1 : 0);
            return legacy;
        }

        return Encode(true, targetHz, locked);
    }
}
