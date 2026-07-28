// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Dsp.Wdsp;

internal static class WdspNativeLoader
{
    private static readonly object Gate = new();
    private static bool _registered;
    private static bool _probedLoadable;
    private static bool _loadable;
    private static readonly Dictionary<string, bool> ExportCache = new(StringComparer.Ordinal);

    internal static void EnsureResolverRegistered()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
            _registered = true;
        }
    }

    internal static bool TryProbe()
    {
        EnsureResolverRegistered();
        if (_probedLoadable) return _loadable;
        lock (Gate)
        {
            if (_probedLoadable) return _loadable;
            if (TryResolve(typeof(NativeMethods).Assembly, out var handle, out _))
            {
                NativeLibrary.Free(handle);
                _loadable = true;
            }
            else
            {
                _loadable = false;
            }
            _probedLoadable = true;
            return _loadable;
        }
    }

    internal static bool TryProbeExport(string symbolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolName);

        EnsureResolverRegistered();
        lock (Gate)
        {
            if (ExportCache.TryGetValue(symbolName, out bool available))
                return available;

            if (!TryResolve(typeof(NativeMethods).Assembly, out var handle, out _))
            {
                ExportCache[symbolName] = false;
                return false;
            }

            try
            {
                available = NativeLibrary.TryGetExport(handle, symbolName, out _);
                ExportCache[symbolName] = available;
                return available;
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeMethods.LibraryName) return IntPtr.Zero;
        return TryResolve(assembly, out var handle, out _) ? handle : IntPtr.Zero;
    }

    /// <summary>
    /// Resolves the same loadable WDSP candidate used by the P/Invoke resolver
    /// and returns its filesystem path. Bare-name system probing cannot reveal
    /// a content path, so that case deliberately returns false.
    /// </summary>
    internal static bool TryGetResolvedNativeLibraryPath(out string? path)
    {
        lock (Gate)
        {
            bool loaded = TryResolve(typeof(NativeMethods).Assembly, out var handle, out path);
            if (loaded)
                NativeLibrary.Free(handle);
            return loaded && path is not null;
        }
    }

    private static bool TryResolve(Assembly assembly, out IntPtr handle, out string? resolvedPath)
    {
        foreach (var candidate in CandidatePaths(assembly))
        {
            if (!File.Exists(candidate)) continue;
            PreloadFftwDependencies(Path.GetDirectoryName(candidate));
            if (NativeLibrary.TryLoad(candidate, out handle))
            {
                resolvedPath = Path.GetFullPath(candidate);
                return true;
            }
        }
        // Bare-name default probing (system paths / loader defaults): no
        // preload, and the loaded image's content path is unknown here.
        resolvedPath = null;
        return NativeLibrary.TryLoad(NativeMethods.LibraryName, assembly, null, out handle);
    }

    // FFTW runtime dependencies shipped beside libwdsp. On Linux the bundled
    // libwdsp.so declares NEEDED libfftw3.so.3/libfftw3f.so.3 with no RPATH,
    // and on macOS it references /opt/homebrew/opt/fftw/ absolute install
    // names; on machines without a system FFTW the load fails and the DSP
    // silently falls back to the synthetic engine. Loading the sibling FFTW
    // libs by absolute path first registers their SONAME/install name
    // process-wide, so the linker reuses them when libwdsp resolves.
    // Deployment constraint: on macOS this only works while the bundled
    // libfftw*.dylib keep their original LC_ID_DYLIB install names matching
    // libwdsp's references; if a future build step rewrites install names
    // (install_name_tool) it must keep libwdsp and the FFTW dylibs
    // consistent, e.g. by repointing both to @loader_path.
    internal static IReadOnlyList<string> FftwDependencyFileNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new[] { "libfftw3.3.dylib", "libfftw3f.3.dylib" };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new[] { "libfftw3.so.3", "libfftw3f.so.3" };
        // Windows links FFTW statically into wdsp.dll; unknown platforms get
        // no preload either.
        return Array.Empty<string>();
    }

    /// <summary>
    /// Returns the bundled FFTW dependencies that exist beside the resolved
    /// WDSP image, in stable double/float order. Windows links FFTW statically
    /// into wdsp.dll, so there are no sibling paths there and hashing wdsp.dll
    /// alone covers both WDSP and FFTW content.
    /// </summary>
    internal static IReadOnlyList<string> FftwDependencyFilePaths(string wdspPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wdspPath);

        string? directory = Path.GetDirectoryName(wdspPath);
        if (string.IsNullOrEmpty(directory))
            return Array.Empty<string>();

        return FftwDependencyFileNames()
            .Select(fileName => Path.Combine(directory, fileName))
            .Where(File.Exists)
            .ToArray();
    }

    private static void PreloadFftwDependencies(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return;
        foreach (var fileName in FftwDependencyFileNames())
        {
            var fullPath = Path.Combine(directory, fileName);
            if (!File.Exists(fullPath)) continue;
            // Deliberately never freed: the pin keeps the SONAME/install name
            // registered for the process lifetime so libwdsp's dependency
            // resolution reuses this image. Missing files or load failures
            // are ignored, falling back to today's system-libs behaviour.
            if (NativeLibrary.TryLoad(fullPath, out _))
                Debug.WriteLine($"wdsp.loader preloaded FFTW dependency {fullPath}");
        }
    }

    private static IEnumerable<string> CandidatePaths(Assembly assembly)
    {
        string rid = CurrentRid();
        string fileName = NativeFileName();
        string? asmDir = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrEmpty(asmDir))
        {
            yield return Path.Combine(asmDir, "runtimes", rid, "native", fileName);
            yield return Path.Combine(asmDir, fileName);
        }

        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        yield return Path.Combine(baseDir, fileName);
    }

    private static string CurrentRid()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        return $"unknown-{arch}";
    }

    private static string NativeFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libwdsp.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libwdsp.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "wdsp.dll";
        return "libwdsp";
    }
}
