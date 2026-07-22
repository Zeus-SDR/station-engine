// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Protocol1;

/// <summary>
/// Low-rate retained observation of the paired Protocol-1 PureSignal streams.
/// Updated at the existing feedback diagnostic cadence, not per packet.
/// </summary>
public readonly record struct PsFeedbackObservation(
    float RxPeak,
    float TxPeak,
    int EffectiveAttenuationDb,
    DateTimeOffset ObservedAt,
    long BlocksDelivered);
