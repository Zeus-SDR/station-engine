// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;

namespace Station.AudioIpc.Spike;

public enum AudioIpcTransportKind
{
    SharedMemory,
    Udp,
}

public sealed record AudioIpcEndpoint(
    AudioIpcTransportKind Kind,
    string SessionId,
    string? MapPath = null,
    string? InputSignalName = null,
    string? OutputSignalName = null,
    int UdpPort = 0)
{
    public string ToCommandLine()
    {
        return Kind switch
        {
            AudioIpcTransportKind.SharedMemory =>
                $"--transport shm --session {SessionId} --map \"{MapPath}\" --input-signal {InputSignalName} --output-signal {OutputSignalName}",
            AudioIpcTransportKind.Udp =>
                $"--transport udp --session {SessionId} --port {UdpPort}",
            _ => throw new ArgumentOutOfRangeException(),
        };
    }
}

public interface IAudioBlockRoundTripClient : IDisposable
{
    AudioIpcEndpoint Endpoint { get; }
    bool TryRoundTrip(ReadOnlySpan<float> input, Span<float> output, TimeSpan timeout, out long roundTripTicks);
}

public interface IAudioBlockServer : IDisposable
{
    void Run(float gain, CancellationToken cancellationToken);
}

public static class AudioIpcProtocol
{
    public const int SampleRate = 48_000;
    public const int SamplesPerBlock = 960;
    public const int BlockPeriodMilliseconds = 20;
    public const uint Magic = 0x5A_41_49_50; // "ZAIP"
    public const ushort Version = 1;
    public const int PacketHeaderBytes = 32;
    public const int PayloadBytes = SamplesPerBlock * sizeof(float);
    public const int PacketBytes = PacketHeaderBytes + PayloadBytes;

    public static long Timestamp() => Stopwatch.GetTimestamp();

    public static long ElapsedTicks(long started) => Stopwatch.GetTimestamp() - started;

    public static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    public static ulong SessionToken(string sessionId)
    {
        // Deterministic FNV-1a token. This is collision protection for stale
        // local datagrams, not an entitlement or security credential.
        ulong hash = 14695981039346656037UL;
        foreach (var c in sessionId)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    public static void WritePacket(Span<byte> packet, long sequence, ulong sessionToken, ReadOnlySpan<float> samples)
    {
        if (packet.Length < PacketBytes || samples.Length != SamplesPerBlock)
            throw new ArgumentException("Audio IPC blocks must contain exactly 960 float32 samples.");

        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[6..], SamplesPerBlock);
        BinaryPrimitives.WriteInt64LittleEndian(packet[8..], sequence);
        BinaryPrimitives.WriteInt64LittleEndian(packet[16..], Timestamp());
        BinaryPrimitives.WriteUInt64LittleEndian(packet[24..], sessionToken);
        samples.CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(packet[PacketHeaderBytes..PacketBytes]));
    }

    public static bool TryReadPacket(ReadOnlySpan<byte> packet, ulong sessionToken, out long sequence, Span<float> samples)
    {
        sequence = 0;
        if (packet.Length != PacketBytes || samples.Length < SamplesPerBlock ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]) != Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]) != SamplesPerBlock ||
            BinaryPrimitives.ReadUInt64LittleEndian(packet[24..]) != sessionToken)
        {
            return false;
        }

        sequence = BinaryPrimitives.ReadInt64LittleEndian(packet[8..]);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(packet[PacketHeaderBytes..]).CopyTo(samples);
        return true;
    }
}
