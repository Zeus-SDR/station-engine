// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus.Contracts;

/// <summary>
/// Wire frame for the WSJT-X inbound Reply push (MsgType.WsjtxReply, 0x3C). Sent
/// by WsjtxUdpBroadcaster whenever GridTracker / JTAlert return a Reply (type 4)
/// datagram on Zeus's outbound socket — the operator clicked a Call Roster entry and wants
/// Zeus to answer that station. Payload: [type:1][UTF-8 JSON of
/// <see cref="WsjtxInboundReplyDto"/>]. Same JSON-envelope shape as ChatEvent /
/// MidiLearn; low-rate control-plane frame, additive only.
/// </summary>
public static class WsjtxInboundReplyFrame
{
    /// <summary>camelCase, no indentation — matches the project's web JSON
    /// convention (JsonSerializerDefaults.Web).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode(WsjtxInboundReplyDto dto)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        var frame = new byte[1 + json.Length];
        frame[0] = (byte)MsgType.WsjtxReply;
        json.CopyTo(frame, 1);
        return frame;
    }
}

/// <summary>
/// The DTO WsjtxUdpBroadcaster broadcasts to the frontend. Carries the raw
/// decoded WSJT-X message text (which the frontend's parseFt8Message re-parses
/// so the click-to-call path stays a single implementation), plus a sequence
/// number the frontend uses to fire its React effect exactly once per reply.
/// </summary>
public sealed record WsjtxInboundReplyDto(
    // Monotonic per-link sequence — the frontend keys its useEffect on this
    // so a re-render never re-fires the same reply.
    long Seq,
    // Wall-clock UTC ms when the datagram was received (informational; the
    // sequencer's slot-parity comes from Slot below, not this timestamp).
    long ReceivedAtUnixMs,
    // Exact instance id in the Reply header — GridTracker echoes back the one
    // Zeus sent in its Heartbeat/Status. Purely diagnostic.
    string InstanceId,
    // Mode string from the datagram ("FT8", "FT4", …). Empty if not present.
    string Mode,
    // The raw decoded FT8/FT4 message text (e.g. "CQ K1ABC FN42" or "K1ABC K9LA -07").
    // The frontend re-parses this with parseFt8Message to extract the target call —
    // same code path the FT8 decode-table click uses.
    string Message,
    // Audio offset (Hz) — the "click on this decode line" audio bin.
    int AudioOffsetHz,
    // Slot parity hint the sequencer needs to answer in the opposite window:
    // "even" or "odd". Derived from the QTime ms-since-midnight and the mode's
    // period (FT8 = 15s, FT4 = 7.5s). Empty when unknown.
    string Slot,
    // SNR reported by the sender's decoder (dB).
    int Snr);
