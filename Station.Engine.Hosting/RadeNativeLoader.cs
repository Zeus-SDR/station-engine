// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Zeus.Server;

/// <summary>
/// Engine-only resolver and ABI probe for the RADE native shim. A replacement
/// placed in the documented override directory wins over the bundled RID
/// artifact. An invalid replacement fails closed instead of silently selecting
/// the bundled copy.
/// </summary>
internal static class RadeNativeLoader
{
    internal const string OverrideDirectoryEnvironmentVariable =
        "ZEUS_RADE_NATIVE_OVERRIDE_DIR";

    private static readonly string[] RequiredExports =
    [
        "zeus_rade_global_init", "zeus_rade_global_shutdown",
        "zeus_rade_open", "zeus_rade_close", "zeus_rade_nin",
        "zeus_rade_nin_max", "zeus_rade_max_pcm_per_rx", "zeus_rade_rx",
        "zeus_rade_sync", "zeus_rade_freq_offset", "zeus_rade_snr_db",
        "zeus_rade_get_eoo_callsign", "zeus_rade_n_speech_samples",
        "zeus_rade_n_tx_out", "zeus_rade_n_tx_eoo_out", "zeus_rade_tx",
        "zeus_rade_tx_eoo", "zeus_rade_set_tx_callsign",
    ];

    private static readonly object Gate = new();
    private static bool _probed;
    private static bool _loadable;
    private static string? _selectedPath;
    private static string? _selectedSha256;
    private static string? _failure;

    internal static bool TryProbeRade()
    {
        EngineNativeLibraryResolver.EnsureRegistered();
        if (_probed) return _loadable;
        lock (Gate)
        {
            if (_probed) return _loadable;
            ProbeLocked(typeof(RadeNativeMethods).Assembly);
            _probed = true;
            return _loadable;
        }
    }

    internal static string CurrentRid => RuntimeRid();
    internal static string? SelectedPath => _selectedPath;
    internal static string? SelectedSha256 => _selectedSha256;
    internal static string? Failure => _failure;

    internal static string DefaultOverrideDirectory() =>
        Path.Combine(PrefsDbPath.DataDir, "native-overrides", "rade", RuntimeRid());

    internal static void ResetProbe()
    {
        lock (Gate)
        {
            _probed = false;
            _loadable = false;
            _selectedPath = null;
            _selectedSha256 = null;
            _failure = null;
        }
    }

    internal static IntPtr ResolveLibrary(Assembly assembly)
    {
        if (!TryProbeRade() || string.IsNullOrEmpty(_selectedPath))
            return IntPtr.Zero;
        return NativeLibrary.TryLoad(_selectedPath, out var handle)
            ? handle
            : IntPtr.Zero;
    }

    private static void ProbeLocked(Assembly assembly)
    {
        _loadable = false;
        _selectedPath = null;
        _selectedSha256 = null;
        _failure = null;

        string fileName = NativeFileName();
        string? explicitOverride = Environment.GetEnvironmentVariable(
            OverrideDirectoryEnvironmentVariable);
        string replacementDirectory = string.IsNullOrWhiteSpace(explicitOverride)
            ? DefaultOverrideDirectory()
            : Path.GetFullPath(explicitOverride);
        string replacement = Path.Combine(replacementDirectory, fileName);

        // An operator-provided replacement is authoritative. Wrong architecture
        // or ABI must leave RADE unavailable rather than hiding the failure by
        // falling through to a bundled artifact.
        if (File.Exists(replacement))
        {
            TrySelect(replacement, out _failure);
            return;
        }
        if (!string.IsNullOrWhiteSpace(explicitOverride))
        {
            _failure = $"configured replacement {fileName} was not found";
            return;
        }

        foreach (var candidate in BundledCandidates(assembly, fileName))
        {
            if (!File.Exists(candidate)) continue;
            if (TrySelect(candidate, out var error)) return;
            _failure = error;
        }

        if (_failure is null)
            _failure = $"{fileName} was not found for {RuntimeRid()}";
    }

    private static bool TrySelect(string path, out string? error)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad(path, out handle))
            {
                error = $"native loader rejected {Path.GetFileName(path)}";
                return false;
            }
            foreach (var export in RequiredExports)
            {
                if (NativeLibrary.TryGetExport(handle, export, out _)) continue;
                error = $"{Path.GetFileName(path)} is missing required export {export}";
                return false;
            }

            _selectedPath = Path.GetFullPath(path);
            using var stream = File.OpenRead(_selectedPath);
            _selectedSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            _loadable = true;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or BadImageFormatException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero) NativeLibrary.Free(handle);
        }
    }

    private static IEnumerable<string> BundledCandidates(Assembly assembly, string fileName)
    {
        string rid = RuntimeRid();
        string? assemblyDirectory = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "runtimes", rid, "native", fileName);
            yield return Path.Combine(assemblyDirectory, fileName);
        }

        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private static string RuntimeRid()
    {
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "unsupported",
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{architecture}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{architecture}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{architecture}";
        return $"unknown-{architecture}";
    }

    private static string NativeFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libzeus_rade.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libzeus_rade.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "zeus_rade.dll";
        return "libzeus_rade";
    }
}
