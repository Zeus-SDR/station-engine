// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Zeus.Contracts;

/// <summary>
/// Wire frame for <see cref="CwEngineStatus"/>. Broadcast on every state
/// edge of the host-side CW engine so the macro pad can show in-flight
/// text + queue depth without polling. Format:
///
/// <code>
/// [type:1=0x30][state:u8][wpm:u16 LE][queueDepth:u16 LE]
/// [textLen:u16 LE][reserved:u8][text:UTF-8 textLen bytes][abortSeq:i32 LE]
/// [reasonLen:u16 LE][reason:UTF-8][jobLen:u16 LE][jobId:ASCII]
/// </code>
///
/// 9-byte fixed header + variable text payload + a 4-byte abort counter
/// appended after the text. Text is capped at <see cref="MaxTextBytes"/> so
/// a runaway macro can't blow the wire — the frontend reconstructs
/// per-character position from <see cref="Wpm"/> + the local-arrival
/// timestamp (cheap to do client-side; no need to push per-character
/// progress frames at audio rate).
///
/// Wire-frozen: state is <see cref="CwEngineState"/> as a byte; future
/// additions append-only at the tail. <see cref="AbortSeq"/> follows the
/// text. <see cref="Reason"/> follows the counter and is omitted when empty
/// and there is no job id. A job id writes an explicit zero reason length
/// first, so an older parser does not read the id as the reason. A frame
/// that ends at the text (older engines) reads as abort sequence 0, no
/// reason, and no job id. A truncated or invalid job tail leaves the job
/// id empty and does not reject the rest of the frame.
/// </summary>
public readonly record struct CwEngineStatusFrame(
    CwEngineState State,
    int Wpm,
    int QueueDepth,
    string Text,
    int AbortSeq = 0,
    string? Reason = null,
    string? JobId = null)
{
    /// <summary>Hard cap on the text payload — well above any realistic CW
    /// macro length. Senders that hand us a longer string get truncated to
    /// this size; the frame stays under one MTU on a typical LAN.</summary>
    public const int MaxTextBytes = 512;

    public const int HeaderByteLength = 9;

    /// <summary>Abort counter appended after the text. Older frames omit it.</summary>
    public const int AbortSeqByteLength = 4;

    /// <summary>Length prefix of the optional reason that follows the counter.</summary>
    public const int ReasonLengthByteLength = 2;

    /// <summary>Cap on the reason tail. Longer text is truncated.</summary>
    public const int MaxReasonBytes = 128;

    /// <summary>Length prefix of the optional job id that follows the reason.</summary>
    public const int JobLengthByteLength = 2;

    /// <summary>
    /// Bytes <see cref="Serialize"/> writes. The reason tail is omitted when
    /// the reason is empty and there is no job id. A job id always writes
    /// the reason length first, including a zero length when the reason is
    /// empty.
    /// </summary>
    public static int SerializedLength(in CwEngineStatusFrame frame)
    {
        int textBytes = Encoding.UTF8.GetByteCount(frame.Text ?? string.Empty);
        if (textBytes > MaxTextBytes) textBytes = MaxTextBytes;
        int reasonBytes = Encoding.UTF8.GetByteCount(frame.Reason ?? string.Empty);
        if (reasonBytes > MaxReasonBytes) reasonBytes = MaxReasonBytes;
        bool job = CwJobIds.IsValid(frame.JobId);
        int length = HeaderByteLength + textBytes + AbortSeqByteLength;
        if (reasonBytes > 0 || job)
            length += ReasonLengthByteLength + reasonBytes;
        if (job)
            length += JobLengthByteLength + CwJobIds.MaxLength;
        return length;
    }

    public void Serialize(IBufferWriter<byte> writer)
    {
        // Encode text first so we know the actual byte count (UTF-8 may be
        // longer than .Length for non-ASCII macros).
        var rawBytes = Encoding.UTF8.GetBytes(Text ?? string.Empty);
        int textBytes = Math.Min(rawBytes.Length, MaxTextBytes);
        var reasonRaw = Encoding.UTF8.GetBytes(Reason ?? string.Empty);
        int reasonBytes = Math.Min(reasonRaw.Length, MaxReasonBytes);
        bool job = CwJobIds.IsValid(JobId);
        int total = SerializedLength(this);
        var span = writer.GetSpan(total);
        span[0] = (byte)MsgType.CwEngineStatus;
        span[1] = (byte)State;
        // Clamp Wpm / QueueDepth to u16 so a logic bug upstream can't write
        // a wider integer and silently truncate at the bit boundary.
        ushort wpmU16 = (ushort)Math.Clamp(Wpm, 0, ushort.MaxValue);
        ushort depthU16 = (ushort)Math.Clamp(QueueDepth, 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2, 2), wpmU16);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), depthU16);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), (ushort)textBytes);
        span[8] = 0;
        if (textBytes > 0)
            rawBytes.AsSpan(0, textBytes).CopyTo(span.Slice(HeaderByteLength, textBytes));
        int abortAt = HeaderByteLength + textBytes;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(abortAt, AbortSeqByteLength), AbortSeq);
        if (reasonBytes > 0 || job)
        {
            int reasonAt = abortAt + AbortSeqByteLength;
            BinaryPrimitives.WriteUInt16LittleEndian(
                span.Slice(reasonAt, ReasonLengthByteLength), (ushort)reasonBytes);
            if (reasonBytes > 0)
                reasonRaw.AsSpan(0, reasonBytes).CopyTo(span.Slice(reasonAt + ReasonLengthByteLength, reasonBytes));
            if (job)
            {
                int jobAt = reasonAt + ReasonLengthByteLength + reasonBytes;
                var jobRaw = Encoding.ASCII.GetBytes(JobId!);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    span.Slice(jobAt, JobLengthByteLength), (ushort)CwJobIds.MaxLength);
                jobRaw.AsSpan(0, CwJobIds.MaxLength).CopyTo(span.Slice(jobAt + JobLengthByteLength, CwJobIds.MaxLength));
            }
        }
        writer.Advance(total);
    }

    public static CwEngineStatusFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderByteLength)
            throw new InvalidDataException(
                $"CwEngineStatusFrame requires ≥{HeaderByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.CwEngineStatus)
            throw new InvalidDataException(
                $"expected CwEngineStatus (0x{(byte)MsgType.CwEngineStatus:X2}), got 0x{bytes[0]:X2}");
        var state = (CwEngineState)bytes[1];
        int wpm = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4, 2));
        int textLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
        if (HeaderByteLength + textLen > bytes.Length)
            throw new InvalidDataException(
                $"CwEngineStatusFrame textLen {textLen} exceeds payload");
        string text = textLen == 0
            ? string.Empty
            : Encoding.UTF8.GetString(bytes.Slice(HeaderByteLength, textLen));
        int tail = HeaderByteLength + textLen;
        int abortSeq = bytes.Length >= tail + AbortSeqByteLength
            ? BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(tail, AbortSeqByteLength))
            : 0;
        string? reason = null;
        string? jobId = null;
        int reasonAt = tail + AbortSeqByteLength;
        if (bytes.Length >= reasonAt + ReasonLengthByteLength)
        {
            int reasonLen = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.Slice(reasonAt, ReasonLengthByteLength));
            if ((uint)reasonLen <= (uint)MaxReasonBytes)
            {
                int reasonEnd = reasonAt + ReasonLengthByteLength + reasonLen;
                if (bytes.Length >= reasonEnd)
                {
                    if (reasonLen > 0)
                        reason = Encoding.UTF8.GetString(bytes.Slice(reasonAt + ReasonLengthByteLength, reasonLen));
                    int jobAt = reasonEnd;
                    if (bytes.Length >= jobAt + JobLengthByteLength)
                    {
                        int jobLen = BinaryPrimitives.ReadUInt16LittleEndian(
                            bytes.Slice(jobAt, JobLengthByteLength));
                        int jobEnd = jobAt + JobLengthByteLength + jobLen;
                        if (jobLen == CwJobIds.MaxLength && bytes.Length >= jobEnd)
                        {
                            string raw = Encoding.ASCII.GetString(bytes.Slice(jobAt + JobLengthByteLength, jobLen));
                            if (CwJobIds.IsValid(raw)) jobId = raw;
                        }
                    }
                }
            }
        }
        return new CwEngineStatusFrame(state, wpm, depth, text, abortSeq, reason, jobId);
    }

    /// <summary>Lift a <see cref="CwEngineStatus"/> into the wire shape.</summary>
    public static CwEngineStatusFrame FromStatus(CwEngineStatus s) =>
        new(s.State, s.Wpm, s.QueueDepth, s.Text ?? string.Empty, s.AbortSeq, s.Reason, s.JobId);
}
