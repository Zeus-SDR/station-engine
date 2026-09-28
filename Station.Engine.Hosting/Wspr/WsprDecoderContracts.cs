// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// Wire shapes of the engine WSPR decoder routes (docs/designs/wspr-return.md,
// "Engine contract"). Serialised camelCase by the engine's JSON options. The
// proprietary product mirrors these records on its side of the process wall;
// it never references this assembly.

namespace Zeus.Server;

/// <summary>Body of <c>PUT /api/wspr/decoder</c>.</summary>
public sealed record WsprDecoderConfig(
    bool Enabled,
    IReadOnlyList<int>? Receivers = null,
    string? Depth = null,
    double ClockOffsetMs = 0);

/// <summary>Per-receiver status inside <see cref="WsprDecoderStatus"/>.</summary>
public sealed record WsprReceiverStatus(
    int Receiver,
    bool Enabled,
    long DialHz,
    string Mode,
    bool Usable,
    string? Reason,
    long? CapturingSlotStartUnixMs);

/// <summary>Response of <c>GET</c>/<c>PUT /api/wspr/decoder</c>.</summary>
public sealed record WsprDecoderStatus(
    bool Available,
    string? Version,
    bool Enabled,
    string Depth,
    IReadOnlyList<WsprReceiverStatus> Receivers,
    long LastSeq,
    string? LastError,
    long? LeaseExpiresUnixMs);

/// <summary>One decoded spot as the native decoder reported it.</summary>
public sealed record WsprRawSpot(
    float SnrDb,
    float DtSec,
    long FrequencyHz,
    int DriftHz,
    string Message);

/// <summary>One finished slot for one receiver: decoded or skipped.</summary>
public sealed record WsprDecodedSlot(
    long Seq,
    int Receiver,
    long SlotStartUnixMs,
    long DialHz,
    int DecodeMs,
    string Outcome,
    string? SkipReason,
    IReadOnlyList<WsprRawSpot> Spots);

/// <summary>Response of <c>GET /api/wspr/decoder/slots?after=</c>.</summary>
public sealed record WsprSlotBatch(long LastSeq, IReadOnlyList<WsprDecodedSlot> Slots);

/// <summary>String constants of the decoder contract.</summary>
public static class WsprDecoderWire
{
    public const string DepthNormal = "normal";
    public const string DepthDeep = "deep";

    public const string OutcomeDecoded = "decoded";
    public const string OutcomeSkipped = "skipped";

    /// <summary>MOX or TUN was active during the slot (our own transmission).</summary>
    public const string SkipTx = "tx";
    /// <summary>Dial or mode changed after the first second of the slot.</summary>
    public const string SkipRetuned = "retuned";
    /// <summary>The receiver is not in USB or DIGU.</summary>
    public const string SkipMode = "mode";
    /// <summary>More than 2 % of the slot's audio was missing.</summary>
    public const string SkipGap = "gap";
    /// <summary>Capture started after the slot began.</summary>
    public const string SkipPartial = "partial";
    /// <summary>The previous decode was still running.</summary>
    public const string SkipBusy = "busy";
    /// <summary>The native decoder failed; see <see cref="WsprDecoderStatus.LastError"/>.</summary>
    public const string SkipError = "error";
}
