// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Net;
using Zeus.Contracts;
using P1Radio = Zeus.Protocol1.Discovery.DiscoveredRadio;
using P2Radio = Zeus.Protocol2.Discovery.DiscoveredRadio;

namespace Zeus.Server;

/// <summary>Product-neutral additions to engine-owned P1/P2 discovery.</summary>
public sealed record RadioDiscoveryExtensionResult(
    IReadOnlyDictionary<IPAddress, IReadOnlyDictionary<string, string>> Protocol2Details,
    IReadOnlyList<RadioInfo> AdditionalRadios)
{
    public static RadioDiscoveryExtensionResult Empty { get; } = new(
        new Dictionary<IPAddress, IReadOnlyDictionary<string, string>>(),
        []);
}

/// <summary>
/// Allows the product host to enrich engine discovery without making the
/// standalone engine reference product-owned radio protocols.
/// </summary>
public interface IRadioDiscoveryExtension
{
    Task<RadioDiscoveryExtensionResult> ExtendAsync(
        Task<IReadOnlyList<P1Radio>> protocol1Task,
        Task<IReadOnlyList<P2Radio>> protocol2Task,
        CancellationToken ct);
}

/// <summary>Standalone discovery is exactly the engine-owned P1/P2 set.</summary>
public sealed class NullRadioDiscoveryExtension : IRadioDiscoveryExtension
{
    public Task<RadioDiscoveryExtensionResult> ExtendAsync(
        Task<IReadOnlyList<P1Radio>> protocol1Task,
        Task<IReadOnlyList<P2Radio>> protocol2Task,
        CancellationToken ct) => Task.FromResult(RadioDiscoveryExtensionResult.Empty);
}
