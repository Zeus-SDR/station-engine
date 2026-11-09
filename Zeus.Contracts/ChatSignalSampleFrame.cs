// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Contracts;

/// <summary>
/// Internal station-observer sample. Tick bounds cover the meter and analyzer
/// reads, not the preceding integration interval of the averaged spectrum.
/// Ticks are meaningful only on the engine host. Sequence advances for every
/// emitted measurement attempt, including invalid estimates.
/// </summary>
public readonly record struct ChatSignalSampleFrame(ulong StreamId, ulong Sequence,
    long BeginTickMs, long EndTickMs, float SignalAvgDbm, float SnrDb,
    long ContextGeneration = 0, long VfoHz = 0, int Mode = 0,
    int FilterLowHz = 0, int FilterHighHz = 0, bool RitEnabled = false, long RitHz = 0)
{
    public const int ByteLength = 78;
    public void Serialize(IBufferWriter<byte> writer)
    {
        var s = writer.GetSpan(ByteLength);
        s[0] = (byte)MsgType.ChatSignalSample;
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(1, 8), StreamId);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(9, 8), Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(17, 8), BeginTickMs);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(25, 8), EndTickMs);
        BinaryPrimitives.WriteSingleLittleEndian(s.Slice(33, 4), SignalAvgDbm);
        BinaryPrimitives.WriteSingleLittleEndian(s.Slice(37, 4), SnrDb);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(41, 8), ContextGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(49, 8), VfoHz);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(57, 4), Mode);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(61, 4), FilterLowHz);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(65, 4), FilterHighHz);
        s[69] = RitEnabled ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(70, 8), RitHz);
        writer.Advance(ByteLength);
    }
}
