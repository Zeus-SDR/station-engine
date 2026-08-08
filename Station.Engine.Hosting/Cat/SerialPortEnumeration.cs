// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.IO.Ports;

namespace Zeus.Server.Cat;

/// <summary>
/// Host serial-device enumeration and error description shared by every
/// engine feature that offers a port picker (serial CAT, serial PTT switch).
/// Extracted from <see cref="CatSerialService"/> so both features list the
/// SAME devices — one fix (a new udev alias shape, a platform quirk) applies
/// to both pickers instead of forking.
/// </summary>
internal static class SerialPortEnumeration
{
    /// <summary>All serial device names the host exposes: the standard
    /// SerialPort enumeration (COM* on Windows, /dev/cu.* + /dev/ttyUSB* etc.
    /// elsewhere) plus Linux udev persistent-alias directories and /dev
    /// symlinks that resolve to tty*/cu* nodes. Suggestions only — virtual
    /// pty/com0com pairs are not enumerable.</summary>
    public static IReadOnlyList<string> AvailablePorts()
    {
        // Windows port names are case-insensitive (COM5 == com5); macOS/Linux
        // device paths are case-sensitive, so two paths differing only by case
        // are genuinely distinct devices there — matches PortNameEquals.
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var results = new HashSet<string>(comparer);
        try
        {
            foreach (var p in SerialPort.GetPortNames()) results.Add(p);
        }
        catch
        {
            // GetPortNames can throw on some platforms; suggestions are optional.
        }

        // Linux: the standard TTY enumeration above only sees kernel-assigned
        // /dev/ttyUSB* /dev/ttyACM* nodes. Many stations use persistent udev
        // symlinks — either the by-id/by-path directories udev populates
        // automatically, or user-created aliases like /dev/KPA500. Include both
        // so the dropdown surfaces the same names an operator picked when
        // writing their udev rules.
        if (OperatingSystem.IsLinux())
        {
            AddUdevSerialDir(results, "/dev/serial/by-id");
            AddUdevSerialDir(results, "/dev/serial/by-path");
            AddDevSymlinksToTty(results, "/dev");
        }

        return results
            .OrderBy(x => x, comparer)
            .ToArray();
    }

    // /dev/serial/by-id and /dev/serial/by-path are udev-managed and their
    // entire purpose is TTY aliases — add every entry.
    private static void AddUdevSerialDir(HashSet<string> results, string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFileSystemEntries(dir))
                results.Add(f);
        }
        catch { /* permission or races — suggestions are optional */ }
    }

    // Top-level symlinks in /dev/ can point at anything (null, zero, disk
    // partitions). Only keep the ones whose resolved target basename starts
    // with tty or cu — the only names SerialPort will actually open.
    private static void AddDevSymlinksToTty(HashSet<string> results, string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFileSystemEntries(dir))
            {
                try
                {
                    var attrs = File.GetAttributes(f);
                    if ((attrs & FileAttributes.ReparsePoint) == 0) continue;
                    var target = File.ResolveLinkTarget(f, returnFinalTarget: true);
                    if (target is null) continue;
                    var name = Path.GetFileName(target.FullName);
                    if (name.StartsWith("tty", StringComparison.Ordinal)
                        || name.StartsWith("cu", StringComparison.Ordinal))
                    {
                        results.Add(f);
                    }
                }
                catch { /* skip this entry */ }
            }
        }
        catch { /* permission or races — suggestions are optional */ }
    }

    /// <summary>Port-name equality for conflict checks (serial CAT vs serial
    /// PTT assignment): case-insensitive on Windows (COM5 == com5), exact
    /// Ordinal elsewhere — macOS/Linux device paths are case-sensitive.</summary>
    public static bool PortNameEquals(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    /// <summary>Friendly, actionable message for the common serial-open
    /// failures (busy / missing / permission), shared by the serial CAT and
    /// serial PTT status surfaces.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Port is in use or access denied (another app may hold it; on Linux the user must be in the dialout group)",
        FileNotFoundException => "Port not found",
        ArgumentException => "Invalid port name",
        IOException io => io.Message,
        _ => ex.Message,
    };
}
