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
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Contracts;

/// <summary>
/// Public Listening spectrum frame (MsgType 0x41). A compact, u8-quantized
/// panadapter/waterfall row for anonymous listeners — roughly 1/8 the size of
/// a float32 <see cref="DisplayFrame"/> at the same width. Layout is pinned in
/// docs/designs/public-listening.md:
/// <code>
/// [16-byte WireFormat header]
/// streamId:u8 flags:u8 bins:u16le centerHz:i64le hzPerBin:f32le floorDb:f32le stepDb:f32le
/// pan:u8[bins]?  wf:u8[bins]?
/// </code>
/// dB = floorDb + code × stepDb.
/// </summary>
public readonly record struct PublicSpectrumFrame(
    uint Seq,
    double TsUnixMs,
    byte StreamId,
    PublicSpectrumFlags Flags,
    ushort Bins,
    long CenterHz,
    float HzPerBin,
    float FloorDb,
    float StepDb,
    ReadOnlyMemory<byte> Pan,
    ReadOnlyMemory<byte> Wf)
{
    public const int BodyHeaderSize = 24;
    public const int MaxBins = 4096;
    public const float DefaultStepDb = 0.5f;

    public bool HasPan => (Flags & PublicSpectrumFlags.PanPresent) != 0;
    public bool HasWf => (Flags & PublicSpectrumFlags.WfPresent) != 0;

    public int BodyByteLength => BodyHeaderSize + (HasPan ? Bins : 0) + (HasWf ? Bins : 0);

    public int TotalByteLength => WireFormat.HeaderSize + BodyByteLength;

    public void Serialize(IBufferWriter<byte> writer)
    {
        if (Bins > MaxBins) throw new InvalidOperationException($"Bins must be <= {MaxBins}.");
        if (HasPan && Pan.Length != Bins) throw new InvalidOperationException("Pan length must equal Bins.");
        if (HasWf && Wf.Length != Bins) throw new InvalidOperationException("Wf length must equal Bins.");

        int total = TotalByteLength;
        var span = writer.GetSpan(total);
        WireFormat.WriteHeader(span, MsgType.PublicSpectrum, 0, checked((ushort)BodyByteLength), Seq, TsUnixMs);

        var body = span.Slice(WireFormat.HeaderSize, BodyByteLength);
        body[0] = StreamId;
        body[1] = (byte)Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(2, 2), Bins);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(4, 8), CenterHz);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(12, 4), HzPerBin);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(16, 4), FloorDb);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(20, 4), StepDb);

        int offset = BodyHeaderSize;
        if (HasPan)
        {
            Pan.Span.CopyTo(body.Slice(offset, Bins));
            offset += Bins;
        }
        if (HasWf)
            Wf.Span.CopyTo(body.Slice(offset, Bins));

        writer.Advance(total);
    }

    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>(TotalByteLength);
        Serialize(buffer);
        return buffer.WrittenSpan.ToArray();
    }

    public static PublicSpectrumFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        WireFormat.ReadHeader(bytes, out var msgType, out _, out var payloadLen, out var seq, out var ts);
        if (msgType != MsgType.PublicSpectrum)
            throw new InvalidDataException($"expected PublicSpectrum, got {msgType}");
        if (payloadLen < BodyHeaderSize || bytes.Length < WireFormat.HeaderSize + payloadLen)
            throw new InvalidDataException("PublicSpectrum frame truncated");

        var body = bytes.Slice(WireFormat.HeaderSize, payloadLen);
        byte streamId = body[0];
        var flags = (PublicSpectrumFlags)body[1];
        ushort bins = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2));
        long centerHz = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(4, 8));
        float hzPerBin = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(12, 4));
        float floorDb = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(16, 4));
        float stepDb = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(20, 4));

        bool hasPan = (flags & PublicSpectrumFlags.PanPresent) != 0;
        bool hasWf = (flags & PublicSpectrumFlags.WfPresent) != 0;
        int expected = BodyHeaderSize + (hasPan ? bins : 0) + (hasWf ? bins : 0);
        if (payloadLen < expected) throw new InvalidDataException("PublicSpectrum bins exceed payload");

        int offset = BodyHeaderSize;
        byte[] pan = [];
        byte[] wf = [];
        if (hasPan)
        {
            pan = body.Slice(offset, bins).ToArray();
            offset += bins;
        }
        if (hasWf)
            wf = body.Slice(offset, bins).ToArray();

        return new PublicSpectrumFrame(seq, ts, streamId, flags, bins, centerHz, hzPerBin, floorDb, stepDb, pan, wf);
    }
}

[Flags]
public enum PublicSpectrumFlags : byte
{
    None = 0,
    PanPresent = 1,
    WfPresent = 2,
    TransmitHold = 4,
    // 8 reserved: Deflated (Phase 4).
}
