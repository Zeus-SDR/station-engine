// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Plugins.Contracts;

/// <summary>
/// Host-owned operator callsign/grid resolver. Plugins pass their own
/// service-local fallback fields and the host applies the same precedence used
/// by core endpoints: shared operator override, then the plugin fallback, then
/// QRZ home station.
/// </summary>
public interface IOperatorIdentityProvider
{
    OperatorIdentitySnapshot Resolve(string? secondaryCall = null, string? secondaryGrid = null);
}

public sealed record OperatorIdentitySnapshot(string Callsign, string Grid);
