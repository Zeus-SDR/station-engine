// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Server;

/// <summary>
/// The hosting assembly may register only one DllImport resolver. Dispatch all
/// engine-owned native bindings here so miniaudio and RADE cannot race to own
/// the assembly-wide resolver slot.
/// </summary>
internal static class EngineNativeLibraryResolver
{
    private static readonly object Gate = new();
    private static bool _registered;

    internal static void EnsureRegistered()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            NativeLibrary.SetDllImportResolver(
                typeof(EngineNativeLibraryResolver).Assembly,
                Resolve);
            _registered = true;
        }
    }

    private static IntPtr Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (libraryName == MiniAudioInterop.LibraryName)
            return MiniAudioInterop.ResolveLibrary(assembly);
        if (libraryName == RadeNativeMethods.LibraryName)
            return RadeNativeLoader.ResolveLibrary(assembly);
        if (libraryName == AsioInterop.LibraryName)
            return AsioInterop.ResolveLibrary(assembly);
        return IntPtr.Zero;
    }
}
