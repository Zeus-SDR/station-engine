// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace Zeus.Dsp.Wdsp;

/// <summary>
/// How the version of the loaded libwdsp resolved. Ordered least-to-most
/// informative; serialised by name (see <see cref="WdspEngineVersion.StatusToken"/>)
/// so downstream consumers key off a stable string rather than an ordinal.
/// </summary>
public enum WdspEngineVersionState : byte
{
    /// <summary>libwdsp could not be loaded at all; there is no engine to report on.</summary>
    NativeUnavailable = 0,

    /// <summary>
    /// libwdsp loaded but does not export <c>GetWDSPVersion</c>. Treated as a
    /// capability gap, not an engine mismatch — the same way a libwdsp without
    /// the SBNR or RNNR symbols is handled.
    /// </summary>
    SymbolMissing = 1,

    /// <summary>The loaded engine reported a version below <see cref="WdspEngineVersion.RequiredRaw"/>.</summary>
    Mismatch = 2,

    /// <summary>The loaded engine reported <see cref="WdspEngineVersion.RequiredRaw"/> or newer.</summary>
    Ok = 3,
}

/// <summary>
/// The version of the libwdsp actually loaded into this process, as reported by
/// <c>GetWDSPVersion</c> (native/wdsp/version.c). The raw value is the version
/// number times 100 — v2.10 reports 210 — because WDSP versions always carry
/// exactly two digits to the right of the decimal point.
///
/// Zeus targets WDSP 2.10. A stale libwdsp loads silently and every
/// WDSP-2.10-dependent behaviour then misbehaves with no other signal, which is
/// why this value is logged at DSP start and published in diagnostics.
/// </summary>
public readonly record struct WdspEngineVersion
{
    /// <summary>Raw value Zeus requires: WDSP v2.10.</summary>
    public const int RequiredRaw = 210;

    /// <summary>Human form used whenever no version could be read.</summary>
    public const string UnknownDisplay = "unknown";

    private WdspEngineVersion(WdspEngineVersionState state, int raw)
    {
        State = state;
        Raw = raw;
    }

    /// <summary>How the probe resolved.</summary>
    public WdspEngineVersionState State { get; }

    /// <summary>
    /// Raw integer straight off <c>GetWDSPVersion</c>, or 0 when the symbol or
    /// the library was unavailable. Logged verbatim so a field report carries
    /// the number the engine actually returned, not just our interpretation.
    /// </summary>
    public int Raw { get; }

    /// <summary>libwdsp is not loadable at all.</summary>
    public static readonly WdspEngineVersion NativeUnavailable =
        new(WdspEngineVersionState.NativeUnavailable, 0);

    /// <summary>libwdsp loaded but predates the <c>GetWDSPVersion</c> export.</summary>
    public static readonly WdspEngineVersion SymbolMissing =
        new(WdspEngineVersionState.SymbolMissing, 0);

    /// <summary>
    /// Classifies a raw <c>GetWDSPVersion</c> return value. Anything at or above
    /// <see cref="RequiredRaw"/> is <see cref="WdspEngineVersionState.Ok"/>;
    /// everything else — including a nonsensical zero or negative — is a
    /// mismatch, because it is definitively not the 2.10 engine Zeus targets.
    /// </summary>
    public static WdspEngineVersion FromRaw(int raw) =>
        new(raw >= RequiredRaw ? WdspEngineVersionState.Ok : WdspEngineVersionState.Mismatch, raw);

    /// <summary>Human form of <see cref="Raw"/>: 210 renders as "2.10", 114 as "1.14".</summary>
    public string Display => Format(Raw);

    /// <summary>Human form of the version Zeus requires ("2.10").</summary>
    public static string RequiredDisplay => Format(RequiredRaw);

    /// <summary>
    /// True only for a genuine engine mismatch — a libwdsp that loaded, answered
    /// the version query, and answered with something older than Zeus requires.
    /// A missing symbol or an unloadable library is a capability gap and does
    /// NOT set this flag; those cannot distinguish "old engine" from "no engine".
    /// </summary>
    public bool IsMismatch => State == WdspEngineVersionState.Mismatch;

    /// <summary>Stable lowerCamel token published in diagnostics JSON.</summary>
    public string StatusToken => State switch
    {
        WdspEngineVersionState.Ok => "ok",
        WdspEngineVersionState.Mismatch => "mismatch",
        WdspEngineVersionState.SymbolMissing => "symbolMissing",
        _ => "nativeUnavailable",
    };

    /// <summary>
    /// Renders a raw WDSP version integer in the two-decimal form WDSP itself
    /// documents. Non-positive values have no meaningful rendering and come back
    /// as <see cref="UnknownDisplay"/>.
    /// </summary>
    public static string Format(int raw)
    {
        if (raw <= 0) return UnknownDisplay;
        int major = raw / 100;
        int minor = raw % 100;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{major}.{minor:00}");
    }

    /// <summary>
    /// Pure resolution logic behind <c>WdspDspEngine.ResolveEngineVersion()</c>,
    /// with every native touch behind a delegate so it can be exercised without
    /// loading a real library. Order matters: the library has to load before an
    /// export probe means anything, and the export has to exist before the call
    /// is safe to make.
    ///
    /// <paramref name="getVersion"/> is still wrapped defensively — the export
    /// probe and the call are separate operations, and a library swapped
    /// underneath us between them would otherwise throw straight through DSP
    /// start.
    /// </summary>
    internal static WdspEngineVersion Resolve(
        Func<bool> nativeLoadable,
        Func<bool> symbolAvailable,
        Func<int> getVersion)
    {
        ArgumentNullException.ThrowIfNull(nativeLoadable);
        ArgumentNullException.ThrowIfNull(symbolAvailable);
        ArgumentNullException.ThrowIfNull(getVersion);

        if (!nativeLoadable()) return NativeUnavailable;
        if (!symbolAvailable()) return SymbolMissing;

        try
        {
            return FromRaw(getVersion());
        }
        catch (EntryPointNotFoundException)
        {
            return SymbolMissing;
        }
        catch (DllNotFoundException)
        {
            return NativeUnavailable;
        }
    }
}
