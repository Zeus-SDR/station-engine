// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Product-selected TX-audio scalars applied before the initial radio state is built.
/// </summary>
public sealed record InitialTxAudioConfig(
    CfcConfig? Cfc,
    TxLevelingConfig? TxLeveling,
    TxPhaseRotatorConfig? TxPhaseRotator,
    int MicGainDb,
    double LevelerMaxGainDb,
    int TxFilterLowHz,
    int TxFilterHighHz);

/// <summary>Supplies an optional product-selected initial TX-audio configuration.</summary>
public interface IInitialTxAudioConfigSource
{
    InitialTxAudioConfig? GetInitialConfig();
}

/// <summary>Standalone default that leaves persisted engine settings unchanged.</summary>
public sealed class NullInitialTxAudioConfigSource : IInitialTxAudioConfigSource
{
    public InitialTxAudioConfig? GetInitialConfig() => null;
}
