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

using System.Text.Json;
using Zeus.Contracts;

namespace Zeus.Server.PublicListen;

/// <summary>
/// Wire codec for <see cref="MsgType.PublicStationStatus"/> (0x42): the
/// standard 16-byte header followed by camelCase UTF-8 JSON of
/// <see cref="PublicStationStatusDto"/>. Shared by the engine (producer) and
/// the host (which rewrites listeners/maxListeners before forwarding).
/// </summary>
public static class PublicStationStatusCodec
{
    public const int CurrentVersion = 1;

    // Web defaults = camelCase property names, case-insensitive reads.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Encode(PublicStationStatusDto status, uint seq, double tsUnixMs)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(status, JsonOptions);
        if (json.Length > ushort.MaxValue) throw new InvalidOperationException("PublicStationStatus JSON too large.");
        var frame = new byte[WireFormat.HeaderSize + json.Length];
        WireFormat.WriteHeader(frame, MsgType.PublicStationStatus, 0, (ushort)json.Length, seq, tsUnixMs);
        json.CopyTo(frame, WireFormat.HeaderSize);
        return frame;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> frame,
        out PublicStationStatusDto? status,
        out uint seq,
        out double tsUnixMs)
    {
        status = null;
        seq = 0;
        tsUnixMs = 0;
        if (frame.Length < WireFormat.HeaderSize || frame[0] != (byte)MsgType.PublicStationStatus)
            return false;
        WireFormat.ReadHeader(frame, out _, out _, out var payloadLen, out seq, out tsUnixMs);
        if (frame.Length < WireFormat.HeaderSize + payloadLen) return false;
        try
        {
            status = JsonSerializer.Deserialize<PublicStationStatusDto>(
                frame.Slice(WireFormat.HeaderSize, payloadLen), JsonOptions);
            return status is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
