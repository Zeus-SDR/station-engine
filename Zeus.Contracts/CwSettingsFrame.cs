// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus.Contracts;

/// <summary>
/// Wire frame for a saved CW settings snapshot (<see cref="MsgType.CwSettings"/>,
/// 0x40). The engine broadcasts one after <c>PUT /api/cw/settings</c> so every
/// connected console learns the row that was stored, including a pop-out whose
/// keepalive PUT lands after the opener has already re-read. Payload:
/// [type:1][UTF-8 JSON of <see cref="CwSettingsDto"/>]. Same low-rate JSON
/// envelope as <see cref="WsjtxInboundReplyFrame"/>. The keyer mode is a string,
/// matching the REST serializer.
/// </summary>
public static class CwSettingsFrame
{
    /// <summary>camelCase, no indentation — matches the project's web JSON
    /// convention (JsonSerializerDefaults.Web) plus the host's string enums.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Encode(CwSettingsDto settings)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var frame = new byte[1 + json.Length];
        frame[0] = (byte)MsgType.CwSettings;
        json.CopyTo(frame, 1);
        return frame;
    }
}
