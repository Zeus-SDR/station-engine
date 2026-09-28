// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// P/Invoke bindings for the decode-only zeus_wspr shim (native/wspr/zeus_wspr.h)
// over the vendored K1JT/K9AN wsprd (GPL-3). Engine-only: the proprietary
// product and web client never load this library. Resolved by
// WsprNativeLoader; every call site must guard with WsprNativeLoader.TryProbe().

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Zeus.Server;

internal static partial class WsprNativeMethods
{
    // NativeLibrary resolves "zeus_wspr" -> zeus_wspr.dll / libzeus_wspr.so / libzeus_wspr.dylib.
    internal const string LibraryName = "zeus_wspr";

    // Matches ZEUS_WSPR_ABI_VERSION in zeus_wspr.h.
    internal const int AbiVersion = 2;

    internal const int SampleRate = 12000;
    internal const int SlotSamples = 114 * SampleRate;
    internal const int FlagDeep = 0x1;
    internal const int MaxDataDirLength = 180;

    internal const int ErrorArgs = -1;
    internal const int ErrorRate = -2;
    internal const int ErrorDir = -3;
    internal const int ErrorIo = -4;
    internal const int ErrorDecoder = -5;

    /// <summary>Mirrors <c>zeus_wspr_spot_t</c> (double + 3×4 bytes + char[32]).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Spot
    {
        public double FreqMhz;
        public float SnrDb;
        public float DtSec;
        public int DriftHz;
        public fixed byte Message[32];
    }

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int zeus_wspr_decode(
        float* samples,
        int n,
        int sampleRate,
        double dialFreqMhz,
        string dataDir,
        string slotLabel,
        int flags,
        Spot* output,
        int maxResults);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_wspr_abi_version();

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr zeus_wspr_version();
}

/// <summary>Engine-only resolver for <see cref="WsprNativeMethods"/>.</summary>
internal static class WsprNativeLoader
{
    internal const string OverrideDirectoryEnvironmentVariable =
        "ZEUS_WSPR_NATIVE_OVERRIDE_DIR";

    private static readonly EngineNativeLibraryProbe Probe = new(
        WsprNativeMethods.LibraryName,
        OverrideDirectoryEnvironmentVariable,
        "wspr",
        ["zeus_wspr_decode", "zeus_wspr_abi_version", "zeus_wspr_version"]);

    internal static bool TryProbe()
    {
        EngineNativeLibraryResolver.EnsureRegistered();
        return Probe.TryProbe(typeof(WsprNativeMethods).Assembly);
    }

    internal static string? SelectedPath => Probe.SelectedPath;
    internal static string? Failure => Probe.Failure;

    internal static void ResetProbe() => Probe.Reset();

    internal static IntPtr ResolveLibrary(System.Reflection.Assembly assembly) =>
        Probe.ResolveLibrary(assembly);
}
