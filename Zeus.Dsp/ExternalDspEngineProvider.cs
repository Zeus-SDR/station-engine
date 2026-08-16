// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Dsp;

/// <summary>
/// Role requested from an independently distributed DSP provider.
/// </summary>
public enum ExternalDspEngineRole
{
    Transmit = 1,
}

[Flags]
public enum ExternalDspEngineCapabilities
{
    None = 0,
    PureSignal = 1 << 0,
}

/// <summary>
/// Optional capability declaration for an independently distributed provider.
/// Providers that do not implement this interface retain the original TX-only
/// contract and are treated as having no optional capabilities.
/// </summary>
public interface IExternalDspEngineCapabilitiesProvider
{
    ExternalDspEngineCapabilities Capabilities { get; }
}

public readonly record struct ExternalPureSignalRouteSettings(
    bool ExternalFeedback,
    int FeedbackAttenuationDb,
    int CorrectionMode);

/// <summary>
/// Required companion contract when a provider declares the PureSignal
/// capability. It carries radio-route values that are intentionally not part
/// of the generic DSP engine interface.
/// </summary>
public interface IExternalPureSignalRouteSettingsSink
{
    /// <summary>
    /// Cache settings while disarmed. When armed, apply them to the matching
    /// radio route before returning, or throw without reporting success.
    /// </summary>
    void SetPureSignalRouteSettings(ExternalPureSignalRouteSettings settings);
}

public readonly record struct ExternalPureSignalStatus(
    bool Armed,
    bool FeedbackConnected,
    ulong FeedbackBlocksReceived,
    ulong SequenceDiscontinuities,
    ulong LastHardwareTimestamp,
    ulong BridgeGeneration,
    ulong LastBridgeSequence);

/// <summary>
/// Required status surface for a provider that declares PureSignal. Status is
/// read on the DSP tick and must be non-blocking and thread-safe. It lets Zeus
/// reflect a provider/feedback fail-close instead of retaining a stale armed
/// state after a local bridge or sidecar restart.
/// </summary>
public interface IExternalPureSignalStatusSource
{
    ExternalPureSignalStatus GetPureSignalStatus();
}

/// <summary>
/// Host-owned creation parameters passed across the external-provider boundary.
/// The provider remains responsible for its own native binding and binaries.
/// <see cref="PureSignalEnabled"/> is true only when the explicitly selected
/// provider declares that capability. It enables the provider path but never
/// represents operator arm intent; every new connection remains disarmed.
/// </summary>
public sealed record ExternalDspEngineRequest(
    ExternalDspEngineRole Role,
    int SampleRateHz,
    int PixelWidth,
    int TxOutputRateHz,
    bool PureSignalEnabled);

/// <summary>
/// Minimal contract implemented by a separately built and distributed DSP
/// provider assembly. Zeus never probes for providers: the host loads exactly
/// one assembly path only when the operator explicitly selects the external
/// engine. Provider assemblies and their native dependencies are not Zeus
/// artifacts and are not copied, packaged, or otherwise coupled here.
/// </summary>
public interface IExternalDspEngineProvider
{
    /// <summary>Stable diagnostic identifier, such as a product or engine name.</summary>
    string Id { get; }

    /// <summary>
    /// Creates an engine for the requested role. The returned engine is owned
    /// and disposed by Zeus; provider/native library lifetime remains owned by
    /// the external assembly.
    /// </summary>
    IDspEngine CreateEngine(ExternalDspEngineRequest request);
}
