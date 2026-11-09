// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Contracts;

/// <summary>A host-monotonic bound on propagation of a local MOX edge.</summary>
public readonly record struct ChatMoxEdgeFrame(ulong StreamId, ulong Sequence,
    long BeginTickMs, long EndTickMs, bool MoxOn, long VfoHz = 0, int Mode = 0, long ContextGeneration = 0)
{
    public const int ByteLength = 54;
    public void Serialize(IBufferWriter<byte> writer)
    {
        var s = writer.GetSpan(ByteLength);
        s[0] = (byte)MsgType.ChatMoxEdge;
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(1, 8), StreamId);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(9, 8), Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(17, 8), BeginTickMs);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(25, 8), EndTickMs);
        s[33] = MoxOn ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(34, 8), VfoHz);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(42, 4), Mode);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(46, 8), ContextGeneration);
        writer.Advance(ByteLength);
    }
}
