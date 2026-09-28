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

    internal static bool TryProbeRade()
    {
        EngineNativeLibraryResolver.EnsureRegistered();
        return Probe.TryProbe(typeof(RadeNativeMethods).Assembly);
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
