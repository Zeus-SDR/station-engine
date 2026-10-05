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
/// [0x25][enable:u8][flags:u8][targetHz:u16 LE][receiver:u8]
/// </code>
///
/// <c>flags</c> bit 0 locks the tone (no auto search). Bit 1
/// (<see cref="CwDecoderRequest.TonePresentFlag"/>) means the tone bytes are
/// an intentional aim, including target 0 (follow the radio CW pitch). A
/// 6-byte enable with bit 1 clear, the lock clear, and a zero tone does not
/// move the tone. <c>targetHz</c> other than 0 is clamped to 200..3000 Hz.
/// A disable never carries a target: closing one client must not move the tone
/// the latest enabling request set. Target fields on a disable are ignored.
/// The hub itself returns to the CW pitch when the last listener of that
/// receiver lets go.
///
/// The 2-byte and 5-byte forms are RX1. They leave
/// <see cref="HasReceiver"/> false. The 6-byte form names the receiver.
/// A receiver outside 0..<see cref="MaxReceivers"/> is rejected. This file is
/// also compiled into the product host, which does not reference the station
/// contract, so <see cref="MaxReceivers"/> is the same cap as
/// WireContract.MaxReceivers (10) kept locally.
/// </summary>
public readonly record struct CwDecoderRequest(
    bool Enable,
    bool HasTarget,
    int TargetHz,
    bool Locked,
    int Receiver = 0,
    bool HasReceiver = false)
{
    public const byte TypeByte = 0x25;
    public const byte LockFlag = 0x01;
    public const byte TonePresentFlag = 0x02;
    public const int MinTargetHz = 200;
    public const int MaxTargetHz = 3000;
    public const int LegacyByteLength = 2;
    public const int ExtendedByteLength = 5;
    public const int ReceiverByteLength = 6;
    public const int MaxReceivers = 10;
}

public static class CwDecoderRequestParser
{
    public static bool TryParse(ReadOnlySpan<byte> frame, out CwDecoderRequest request)
    {
        request = default;
        if (frame.Length != CwDecoderRequest.LegacyByteLength
            && frame.Length != CwDecoderRequest.ExtendedByteLength
            && frame.Length != CwDecoderRequest.ReceiverByteLength)
            return false;
        if (frame[0] != CwDecoderRequest.TypeByte)
            return false;

        bool enable = frame[1] != 0;
        if (frame.Length == CwDecoderRequest.ReceiverByteLength)
        {
            int receiver = frame[5];
            if ((uint)receiver >= CwDecoderRequest.MaxReceivers)
                return false;
            // A scoped disable names the receiver and drops the tone bytes.
            if (!enable)
            {
                request = new CwDecoderRequest(false, false, 0, false, receiver, true);
                return true;
            }

            ReadTone(frame, out int target, out bool locked);
            int rawHz = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(3, 2));
            bool tonePresent = (frame[2] & CwDecoderRequest.TonePresentFlag) != 0
                || locked
                || rawHz != 0;
            request = new CwDecoderRequest(true, tonePresent, target, locked, receiver, true);
            return true;
        }

        // Legacy frames have no receiver and no explicit target on the 2-byte
        // form. A disable drops any target bytes so the tone stays with the
        // most recent enabling request.
        if (!enable || frame.Length == CwDecoderRequest.LegacyByteLength)
        {
            request = new CwDecoderRequest(enable, false, 0, false);
            return true;
        }

        ReadTone(frame, out int legacyTarget, out bool legacyLocked);
        request = new CwDecoderRequest(enable, true, legacyTarget, legacyLocked);
        return true;
    }

    private static void ReadTone(ReadOnlySpan<byte> frame, out int target, out bool locked)
    {
        locked = (frame[2] & CwDecoderRequest.LockFlag) != 0;
        int raw = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(3, 2));
        target = raw == 0 ? 0 : Math.Clamp(raw, CwDecoderRequest.MinTargetHz, CwDecoderRequest.MaxTargetHz);
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

    /// <summary>
    /// Receiver-scoped demand. Receiver 0 stays on the legacy 2-byte or 5-byte
    /// forms so an older engine still sees RX1. Any other supported receiver
    /// is the 6-byte form, including a disable. An out-of-range receiver throws
    /// rather than emitting an RX1 frame.
    /// </summary>
    public static byte[] EncodeReceiver(bool enable, int receiver, bool includeTarget, int targetHz, bool locked)
    {
        if ((uint)receiver >= CwDecoderRequest.MaxReceivers)
            throw new ArgumentOutOfRangeException(nameof(receiver));
        if (receiver == 0)
            return EncodeDemand(enable, includeTarget, targetHz, locked);

        var frame = new byte[CwDecoderRequest.ReceiverByteLength];
        frame[0] = CwDecoderRequest.TypeByte;
        frame[1] = (byte)(enable ? 1 : 0);
        if (enable && includeTarget)
        {
            int hz = targetHz <= 0
                ? 0
                : Math.Clamp(targetHz, CwDecoderRequest.MinTargetHz, CwDecoderRequest.MaxTargetHz);
            frame[2] = (byte)((locked ? CwDecoderRequest.LockFlag : 0) | CwDecoderRequest.TonePresentFlag);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3, 2), (ushort)hz);
        }
        frame[5] = (byte)receiver;
        return frame;
    }
}
