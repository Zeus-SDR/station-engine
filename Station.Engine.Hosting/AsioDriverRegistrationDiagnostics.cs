// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;

namespace Zeus.Server;

/// <summary>Which registry view an ASIO COM registration is read from.</summary>
internal enum AsioRegistryView
{
    Registry64,
    Registry32,
}

/// <summary>
/// The registry reads behind <see cref="AsioDriverRegistrationDiagnostics"/>,
/// seammed so the diagnosis logic is unit-testable on every OS with a fake.
/// </summary>
internal interface IAsioRegistrationReader
{
    /// <summary>Default value of HKLM\SOFTWARE\Classes\CLSID\{clsid}\InprocServer32
    /// in the given view (the COM server DLL path); null when the key or value
    /// is absent.</summary>
    string? ReadInprocServer32(string clsid, AsioRegistryView view);

    /// <summary>Whether the value names a fully qualified path. Bare file names
    /// and relative paths resolve through the DLL search path instead, so their
    /// existence cannot be checked here.</summary>
    bool IsFullyQualifiedPath(string path);

    bool FileExists(string path);
}

/// <summary>
/// Explains why CoCreateInstance failed for an ASIO driver CLSID by reading the
/// driver's COM registration in the 64-bit and 32-bit registry views. Runs only
/// after a probe or open has already failed with the native "Could not
/// instantiate the selected ASIO driver" message. Read-only and best effort:
/// every entry point catches everything rather than masking the real error.
/// </summary>
internal static class AsioDriverRegistrationDiagnostics
{
    /// <summary>Prefix shared by the native probe and session-open instantiation
    /// errors (native/asio/zeus_asio.cpp); diagnosis runs only for these.</summary>
    internal const string InstantiationFailureMarker = "Could not instantiate the selected ASIO driver";

    /// <summary>Appends a plain-language cause to a native ASIO instantiation
    /// failure message. Returns the original message unchanged when the failure
    /// is not an instantiation failure or no cause can be determined.</summary>
    internal static string EnrichInstantiationFailureMessage(string message, string clsid, string? driverName)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return message;
            if (!message.Contains(InstantiationFailureMarker, StringComparison.Ordinal)) return message;
            var cause = TryDescribeInstantiationFailure(
                new WindowsAsioRegistrationReader(), clsid, driverName);
            return cause is null ? message : $"{message} {cause}";
        }
        catch
        {
            return message;
        }
    }

    /// <summary>Determines the likely cause; never throws. Returns null when the
    /// registration cannot be read at all.</summary>
    internal static string? TryDescribeInstantiationFailure(
        IAsioRegistrationReader reader, string clsid, string? driverName)
    {
        try
        {
            return DescribeInstantiationFailure(reader, clsid, driverName);
        }
        catch
        {
            return null;
        }
    }

    internal static string DescribeInstantiationFailure(
        IAsioRegistrationReader reader, string clsid, string? driverName)
    {
        string name = string.IsNullOrWhiteSpace(driverName) ? "The driver" : driverName!;
        if (NormalizePath(reader.ReadInprocServer32(clsid, AsioRegistryView.Registry64)) is { } path64)
        {
            return !reader.IsFullyQualifiedPath(path64) || reader.FileExists(path64)
                ? $"Its registration looks valid ({path64}). The driver may be in use by another program, its device may be disconnected, or it refused to load. Close other audio programs, reconnect the interface, and try again."
                : $"The driver file {path64} is missing. Reinstall the driver.";
        }
        if (NormalizePath(reader.ReadInprocServer32(clsid, AsioRegistryView.Registry32)) is not null)
        {
            return $"{name} is installed only as a 32-bit driver. Zeus is 64-bit: install the manufacturer's 64-bit ASIO driver.";
        }
        return $"{name} is listed as an ASIO driver but its COM class is not registered. Reinstall the driver.";
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim();
        // Registration values may wrap the path in one pair of double quotes.
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1];
        var expanded = Environment.ExpandEnvironmentVariables(trimmed);
        return expanded.Length == 0 ? null : expanded;
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsAsioRegistrationReader : IAsioRegistrationReader
    {
        public string? ReadInprocServer32(string clsid, AsioRegistryView view)
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                view == AsioRegistryView.Registry64
                    ? Microsoft.Win32.RegistryView.Registry64
                    : Microsoft.Win32.RegistryView.Registry32);
            using var key = hklm.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid}\InprocServer32");
            return key?.GetValue(null) as string;
        }

        public bool IsFullyQualifiedPath(string path) => Path.IsPathFullyQualified(path);

        public bool FileExists(string path) => File.Exists(path);
    }
}
