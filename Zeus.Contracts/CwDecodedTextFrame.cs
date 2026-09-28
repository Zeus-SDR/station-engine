// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Zeus.Contracts;

/// <summary>
/// Wire frame for server-side CW receive decoding. Broadcast by
/// CwDecoderService when the decoder emits characters, and as an empty-text
/// status frame while the decoder is active so a client learns the live tone
/// during silence. Format:
///
/// <code>
/// [type:1=0x31][wpm:u16 LE][snrDb:f32 LE][confidence:f32 LE]
/// [textLen:u16 LE][text:UTF-8 textLen bytes]
/// [pitchHz:u16 LE][locked:u8]
/// </code>
///
/// 13-byte fixed header + variable text payload + 3-byte trailer. The trailer
/// is append-only: a decoder that stops after <c>textLen</c> bytes still reads
/// the original fields, and a frame without the trailer decodes with
/// <see cref="PitchHz"/> 0 and <see cref="Locked"/> false.
/// <see cref="PitchHz"/> is the audio tone the decoder is actually on.
/// <see cref="Locked"/> is set when auto search is off.
/// <see cref="Text"/> is the accumulated chunk of characters emitted by the
/// decoder's rate-limited broadcaster at no more than 10 frames per second;
/// the client appends them in order. Decoding happens server-side so it works
/// in the desktop/native-audio host and headless — see CwDecoderService. Text
/// is capped at <see cref="MaxTextBytes"/>.
///
/// Wire-frozen: future additions append-only at the tail.
/// </summary>
public readonly record struct CwDecodedTextFrame(
    string Text,
    int Wpm,
    float SnrDb,
    float Confidence,
    int PitchHz = 0,
    bool Locked = false)
{
    /// <summary>Hard cap on the text payload. One broadcast decodes a handful
    /// of characters at most, so this is comfortably generous and keeps the
    /// frame well under one MTU.</summary>
    public const int MaxTextBytes = 256;

    public const int HeaderByteLength = 13; // type(1) + wpm(2) + snr(4) + conf(4) + textLen(2)
    public const int TrailerByteLength = 3; // pitchHz(2) + locked(1), appended after the text

    public void Serialize(IBufferWriter<byte> writer)
    {
        // Encode text first so we know the actual UTF-8 byte count.
        var rawBytes = Encoding.UTF8.GetBytes(Text ?? string.Empty);
        int textBytes = Math.Min(rawBytes.Length, MaxTextBytes);
        int total = HeaderByteLength + textBytes + TrailerByteLength;
        var span = writer.GetSpan(total);
        span[0] = (byte)MsgType.CwDecodedText;
        // Clamp Wpm to u16 so an upstream logic bug can't silently truncate.
        ushort wpmU16 = (ushort)Math.Clamp(Wpm, 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(1, 2), wpmU16);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(3, 4), SnrDb);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(7, 4), Confidence);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(11, 2), (ushort)textBytes);
        if (textBytes > 0)
            rawBytes.AsSpan(0, textBytes).CopyTo(span.Slice(HeaderByteLength, textBytes));
        int trailer = HeaderByteLength + textBytes;
        ushort pitch = (ushort)Math.Clamp(PitchHz, 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(trailer, 2), pitch);
        span[trailer + 2] = (byte)(Locked ? 1 : 0);
        writer.Advance(total);
    }

    public static CwDecodedTextFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderByteLength)
            throw new InvalidDataException(
                $"CwDecodedTextFrame requires ≥{HeaderByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.CwDecodedText)
            throw new InvalidDataException(
                $"expected CwDecodedText (0x{(byte)MsgType.CwDecodedText:X2}), got 0x{bytes[0]:X2}");
        int wpm = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(1, 2));
        float snr = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(3, 4));
        float conf = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(7, 4));
        int textLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(11, 2));
        if (HeaderByteLength + textLen > bytes.Length)
            throw new InvalidDataException(
                $"CwDecodedTextFrame textLen {textLen} exceeds payload");
        string text = textLen == 0
            ? string.Empty
            : Encoding.UTF8.GetString(bytes.Slice(HeaderByteLength, textLen));
        int trailer = HeaderByteLength + textLen;
        int pitch = 0;
        bool locked = false;
        if (bytes.Length >= trailer + TrailerByteLength)
        {
            pitch = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(trailer, 2));
            locked = bytes[trailer + 2] != 0;
        }
        return new CwDecodedTextFrame(text, wpm, snr, conf, pitch, locked);
    }
}
