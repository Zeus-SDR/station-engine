// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Contracts;

/// <summary>
/// Persisted config for the outbound WSJT-X "logged QSO" UDP broadcaster. When
/// enabled, every QSO logged through /api/log/entry (auto-logged or manual — NOT
/// ADIF import) is emitted as a WSJT-X NetworkMessage type 12 (LoggedADIF) to
/// <c>Host:Port</c>, which JTAlert / Log4OM / GridTracker / N1MM already
/// understand. NEW network egress — DISABLED by default, loopback target. This
/// is QSO-record push only; CAT/TCI already provide rig control.
/// </summary>
public sealed record WsjtxRuntimeConfig(
    bool Enabled = false,
    string Host = "127.0.0.1",
    int Port = 2237,
    string InstanceId = "WSJT-X",
    // Transport: "unicast" (one logger) or "multicast" (many loggers receive the
    // same stream). Unicast is the back-compatible default; with unicast only the
    // first binder on the port gets datagrams. Multicast uses MulticastGroup +
    // MulticastTtl. All new fields default to today's behaviour (egress OFF, plain
    // unicast) so existing persisted rows and clients are unaffected.
    string Transport = "unicast",
    string MulticastGroup = "224.0.0.73",
    int MulticastTtl = 1,
    // Also emit a structured QSOLogged (type 5) alongside the LoggedADIF (type 12)
    // on each logged QSO. Some tools prefer the structured form.
    bool SendQsoLogged = false,
    // Emit the live stream GridTracker/JTAlert need for map/roster/alerts:
    // Heartbeat (0) ~15 s, Status (1) on change + periodic, Decode (2) per FT8/FT4
    // decode, WSPRDecode (10) per spot. OFF by default — pure additional egress.
    bool SendLiveDecodes = false,
    // Accept inbound WSJT-X Reply (type 4) datagrams — GridTracker / JTAlert send
    // one when the operator clicks a Call Roster entry, and Zeus routes the target
    // callsign into the same click-to-call code path the FT8 decode table uses.
    // NEW inbound network surface — DEFAULT OFF. Replies are received on the
    // same ephemeral socket Zeus uses for outbound traffic, only from the
    // configured unicast peer (multicast deliberately accepts any source), and
    // only Reply (4) is honoured; HaltTx (8), FreeText (9), etc. are dropped.
    bool AcceptInboundReplies = false);

/// <summary>Status/config view for the WSJT-X broadcaster + optional inbound
/// reply path. Config applies live with no configured inbound port: Reply
/// reception follows the outbound socket's lifecycle and is visible through
/// <see cref="InboundListening"/>.</summary>
public sealed record WsjtxStatus(
    bool Enabled,
    string Host,
    int Port,
    string InstanceId,
    string Transport = "unicast",
    string MulticastGroup = "224.0.0.73",
    int MulticastTtl = 1,
    bool SendQsoLogged = false,
    bool SendLiveDecodes = false,
    bool AcceptInboundReplies = false,
    // True only while a receive loop is active on the live outbound UDP socket.
    bool InboundListening = false);
