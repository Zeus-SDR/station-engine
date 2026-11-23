// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Reflection;

namespace Zeus.Server;

/// <summary>
/// Engine-only resolver and ABI probe for the RADE native shim. A replacement
/// placed in the documented override directory wins over the bundled RID
/// artifact. An invalid replacement fails closed instead of silently selecting
/// the bundled copy. The probing itself lives in <see cref="EngineNativeLibraryProbe"/>.
/// </summary>
internal static class RadeNativeLoader
{
    internal const string OverrideDirectoryEnvironmentVariable =
        "ZEUS_RADE_NATIVE_OVERRIDE_DIR";

    private static readonly EngineNativeLibraryProbe Probe = new(
        RadeNativeMethods.LibraryName,
        OverrideDirectoryEnvironmentVariable,
        "rade",
        [
            "zeus_rade_global_init", "zeus_rade_global_shutdown",
            "zeus_rade_open", "zeus_rade_close", "zeus_rade_nin",
            "zeus_rade_nin_max", "zeus_rade_max_pcm_per_rx", "zeus_rade_rx",
            "zeus_rade_sync", "zeus_rade_freq_offset", "zeus_rade_snr_db",
            "zeus_rade_get_eoo_callsign", "zeus_rade_n_speech_samples",
            "zeus_rade_n_tx_out", "zeus_rade_n_tx_eoo_out", "zeus_rade_tx",
            "zeus_rade_tx_eoo", "zeus_rade_set_tx_callsign",
        ]);

    // RADE V2 is an optional capability of the shim (ABI 2). A V1-only
    // library has none of these exports and stays fully usable for V1.
    internal static readonly string[] V2Exports =
    [
        "zeus_rade_abi_version", "zeus_rade_open_protocol", "zeus_rade_protocol",
    ];

    internal static bool TryProbeRade()
    {
        EngineNativeLibraryResolver.EnsureRegistered();
        return Probe.TryProbe(typeof(RadeNativeMethods).Assembly);
    }

    /// <summary>
    /// True when the selected RADE library can also run RADE V2. A missing
    /// export means "V2 unavailable", never a crash: the ABI call is made only
    /// after the export is proven present on the very library P/Invoke binds.
    /// </summary>
    internal static bool TryProbeRadeV2()
    {
        ProbedAbiVersion = null;
        if (!TryProbeRade()) return false;
        string? path = Probe.SelectedPath;
        if (string.IsNullOrEmpty(path) || !EngineNativeLibraryProbe.LibraryHasExports(path, V2Exports))
            return false;
        try
        {
            int abi = ReadAbiVersion();
            ProbedAbiVersion = abi;
            return abi >= 2;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the shim's ABI revision. Called only after the export is proven
    /// present. Replaceable by tests (the probe must act on the value READ, not
    /// assume ABI 2 from the exports alone).
    /// </summary>
    internal static Func<int> ReadAbiVersion { get; set; } = static () => RadeNativeMethods.zeus_rade_abi_version();

    /// <summary>The ABI revision the last V2 probe read from the library (null if it could not).</summary>
    internal static int? ProbedAbiVersion { get; private set; }

    // Pre-encoder mic conditioning and the TX mic meter arrived after the
    // first V1 shim. An older replacement library without them still runs
    // RADE; Zeus just skips those calls.
    internal static readonly string[] MicExports =
    [
        "zeus_rade_set_mic_agc_enabled", "zeus_rade_set_mic_agc_target_db",
        "zeus_rade_set_mic_eq_enabled", "zeus_rade_set_mic_eq_bass_db",
        "zeus_rade_set_mic_eq_mid_db", "zeus_rade_set_mic_eq_treble_db",
        "zeus_rade_tx_mic_level_db", "zeus_rade_tx_mic_clip",
    ];

    /// <summary>True when the selected library exports every optional mic call.</summary>
    internal static bool TryProbeRadeMic()
    {
        if (!TryProbeRade()) return false;
        string? path = Probe.SelectedPath;
        return !string.IsNullOrEmpty(path) && EngineNativeLibraryProbe.LibraryHasExports(path, MicExports);
    }

    internal static string CurrentRid => EngineNativeLibraryProbe.CurrentRid;
    internal static string? SelectedPath => Probe.SelectedPath;
    internal static string? SelectedSha256 => Probe.SelectedSha256;
    internal static string? Failure => Probe.Failure;

    internal static string DefaultOverrideDirectory() => Probe.DefaultOverrideDirectory();

    internal static void ResetProbe() => Probe.Reset();

    internal static IntPtr ResolveLibrary(Assembly assembly) =>
        Probe.ResolveLibrary(assembly);
}
