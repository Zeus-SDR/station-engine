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
    // Match the proven SDR-VST3 transport geometry: enough retained blocks to
    // absorb scheduler jitter without forcing a processed/dry splice. Keep the
    // ring dimensions in the wire contract so every peer maps identical bytes.
    public const int SlotCount = 32;
    public const int RingHeaderBytes = 64;
    public const int SlotHeaderBytes = 32;
    public const int SlotBytes = SlotHeaderBytes + MaxSamplesPerBlock * sizeof(float);
    public const int RingBytes = RingHeaderBytes + SlotCount * SlotBytes;
    public const int MapBytes = RingBytes * 2;
    public const uint Magic = 0x5A_41_52_47; // "ZARG"
    // Version 4 expands each direction from 8 to 32 slots. Reject older peers:
    // their smaller mapped layout cannot provide the retained jitter window.
    public const ushort Version = 4;

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

/// <summary>
/// Processes one block and reports how many input blocks the returned audio is
/// delayed. The transport uses the delay only to select the matching retained
/// dry block when a later response is unavailable; it never changes pacing.
/// </summary>
public delegate int DelayAwareAudioBlockProcessor(Span<float> block);
