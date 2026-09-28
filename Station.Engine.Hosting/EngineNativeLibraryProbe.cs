// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Zeus.Server;

/// <summary>
/// Locates, ABI-checks and selects one engine-owned GPL native library (RADE,
/// WSPR). A replacement placed in the documented override directory wins over
/// the bundled RID artifact, so an operator can exercise the GPL right to swap
/// the binary. An invalid replacement fails closed instead of silently
/// selecting the bundled copy.
/// </summary>
internal sealed class EngineNativeLibraryProbe
{
    private readonly object _gate = new();
    private readonly string _baseName;
    private readonly string _overrideSubdirectory;
    private readonly IReadOnlyList<string> _requiredExports;
    private bool _probed;
    private bool _loadable;
    private string? _selectedPath;
    private string? _selectedSha256;
    private string? _failure;

    /// <param name="baseName">Library name without prefix or extension, e.g. "zeus_wspr".</param>
    /// <param name="overrideEnvironmentVariable">Variable naming an explicit replacement directory.</param>
    /// <param name="overrideSubdirectory">Folder under <c>DataDir/native-overrides</c>.</param>
    /// <param name="requiredExports">Every export the managed binding calls.</param>
    public EngineNativeLibraryProbe(
        string baseName,
        string overrideEnvironmentVariable,
        string overrideSubdirectory,
        IReadOnlyList<string> requiredExports)
    {
        _baseName = baseName;
        OverrideEnvironmentVariable = overrideEnvironmentVariable;
        _overrideSubdirectory = overrideSubdirectory;
        _requiredExports = requiredExports;
    }

    public string OverrideEnvironmentVariable { get; }
    public string? SelectedPath => _selectedPath;
    public string? SelectedSha256 => _selectedSha256;
    public string? Failure => _failure;

    public static string CurrentRid => RuntimeRid();

    public string DefaultOverrideDirectory() =>
        Path.Combine(PrefsDbPath.DataDir, "native-overrides", _overrideSubdirectory, RuntimeRid());

    public string NativeFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"lib{_baseName}.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"lib{_baseName}.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"{_baseName}.dll";
        return $"lib{_baseName}";
    }

    public bool TryProbe(Assembly assembly)
    {
        if (_probed) return _loadable;
        lock (_gate)
        {
            if (_probed) return _loadable;
            ProbeLocked(assembly);
            _probed = true;
            return _loadable;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _probed = false;
            _loadable = false;
            _selectedPath = null;
            _selectedSha256 = null;
            _failure = null;
        }
    }

    public IntPtr ResolveLibrary(Assembly assembly)
    {
        if (!TryProbe(assembly) || string.IsNullOrEmpty(_selectedPath))
            return IntPtr.Zero;
        return NativeLibrary.TryLoad(_selectedPath, out var handle)
            ? handle
            : IntPtr.Zero;
    }

    private void ProbeLocked(Assembly assembly)
    {
        _loadable = false;
        _selectedPath = null;
        _selectedSha256 = null;
        _failure = null;

        string fileName = NativeFileName();
        string? explicitOverride = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        string replacementDirectory = string.IsNullOrWhiteSpace(explicitOverride)
            ? DefaultOverrideDirectory()
            : Path.GetFullPath(explicitOverride);
        string replacement = Path.Combine(replacementDirectory, fileName);

        // An operator-provided replacement is authoritative. Wrong architecture
        // or ABI must leave the feature unavailable rather than hiding the
        // failure by falling through to a bundled artifact.
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

        _failure ??= $"{fileName} was not found for {RuntimeRid()}";
    }

    private bool TrySelect(string path, out string? error)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad(path, out handle))
            {
                error = $"native loader rejected {Path.GetFileName(path)}";
                return false;
            }
            foreach (var export in _requiredExports)
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
}
