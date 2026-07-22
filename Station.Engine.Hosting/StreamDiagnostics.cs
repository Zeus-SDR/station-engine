// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

internal sealed record TxMicUplinkDiagnosticsDto(
    int SchemaVersion,
    string Status,
    bool SubscriberAttached,
    int ClientCount,
    int ExpectedFrameSamples,
    int ExpectedFrameBytes,
    long TotalFrames,
    long TotalSamples,
    long TotalBytes,
    long LastFrameBytes,
    long LastFrameSamples,
    double? LastFrameAgeMs,
    DateTimeOffset? LastFrameUtc,
    long InvalidFrames,
    long OversizeMessages,
    long UnknownFrames,
    string DiagnosticRecommendation);
