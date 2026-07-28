// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Douglas J. Cerrato (KB2UKA)

using System.Diagnostics;

namespace Station.AudioRing;

/// <summary>Stable wire constants for the cross-process float32 audio ring.</summary>
public static class AudioRingProtocol
{
    public const int SampleRate = 48_000;
    public const int NominalSamplesPerBlock = 960;
    public const int MaxSamplesPerBlock = 2_048;
    public const int BlockPeriodMilliseconds = 20;
    public const int RingBytes = 131_072;
    public const int MapBytes = RingBytes * 2;
    public const uint Magic = 0x5A_41_52_47; // "ZARG"
    public const ushort Version = 2;

    public static long Timestamp() => Stopwatch.GetTimestamp();

    public static long ElapsedTicks(long started) => Stopwatch.GetTimestamp() - started;

    public static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    internal static ulong SessionToken(string sessionId)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var value in sessionId)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}

/// <summary>Private local resources negotiated through Station Protocol.</summary>
public sealed record AudioRingEndpoint(
    ushort ProtocolVersion,
    string SessionId,
    string MapPath,
    string InputSignalName,
    string OutputSignalName,
    int SampleRate,
    int MaxSamplesPerBlock);

public sealed record ProductAudioAttachRequest(
    string Name,
    string Version,
    int? HttpPort = null);

public sealed record ProductAudioAttachResponse(
    string LeaseId,
    AudioRingEndpoint Ring,
    AudioRingEndpoint? RxRing = null);

public delegate void AudioBlockProcessor(Span<float> block);
