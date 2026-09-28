// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Zeus.Server;

/// <summary>Result of one slot decode.</summary>
internal readonly record struct WsprSlotDecodeResult(IReadOnlyList<WsprRawSpot> Spots, string? Error);

/// <summary>Decodes one captured two-minute slot. Seam for tests.</summary>
internal interface IWsprSlotDecoder
{
    bool Available { get; }
    string? Version { get; }
    string? UnavailableReason { get; }

    /// <param name="samples12k">114 s of 12 kHz mono audio.</param>
    /// <param name="dialHz">USB dial frequency of the receiver during the slot.</param>
    /// <param name="slotStartUtc">UTC start of the slot (even minute).</param>
    /// <param name="deep">Use the decoder's deeper candidate search.</param>
    WsprSlotDecodeResult Decode(float[] samples12k, long dialHz, DateTime slotStartUtc, bool deep);
}

/// <summary>
/// The vendored wsprd through the zeus_wspr shim. The decoder keeps about
/// 0.8 MB of arrays on the stack, more than a macOS secondary thread (512 KB)
/// or the Windows default (1 MB) provides, so every decode runs on its own
/// thread with <see cref="DecodeStackBytes"/> of stack, whoever the caller is.
/// </summary>
internal sealed class NativeWsprSlotDecoder : IWsprSlotDecoder
{
    internal const int DecodeStackBytes = 16 * 1024 * 1024;
    private const int MaxSpots = 128;
    private string _dataDirectory;
    private readonly Lazy<(bool Available, string? Version, string? Reason)> _probe;

    /// <param name="dataDirectory">Persistent directory for the decoder's callsign hash table.</param>
    public NativeWsprSlotDecoder(string dataDirectory)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _probe = new Lazy<(bool, string?, string?)>(Probe, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Engine default: <c>DataDir/wspr</c>, beside the prefs databases.</summary>
    public static string DefaultDataDirectory() => Path.Combine(PrefsDbPath.DataDir, "wspr");

    public bool Available => _probe.Value.Available;
    public string? Version => _probe.Value.Version;
    public string? UnavailableReason => _probe.Value.Reason;

    private (bool, string?, string?) Probe()
    {
        // Only a non-ASCII path needs resolving (and creating first, since a
        // short name exists only for an existing directory).
        if (OperatingSystem.IsWindows() && !WsprDataDirectory.IsAscii(_dataDirectory))
        {
            _dataDirectory = WsprDataDirectory.PrepareNonAscii(
                _dataDirectory,
                WsprDataDirectory.WindowsShortPath,
                WsprDataDirectory.WindowsFallback);
        }
        if (!WsprDataDirectory.IsNativeSafe(_dataDirectory, OperatingSystem.IsWindows()))
            return (false, null, "WSPR data directory has no ASCII path on this Windows system");
        if (WsprDataDirectory.NativeLength(_dataDirectory) > WsprNativeMethods.MaxDataDirLength)
            return (false, null, $"WSPR data directory path is longer than {WsprNativeMethods.MaxDataDirLength} bytes");
        if (!WsprNativeLoader.TryProbe())
            return (false, null, WsprNativeLoader.Failure ?? "zeus_wspr is unavailable");
        try
        {
            int abi = WsprNativeMethods.zeus_wspr_abi_version();
            if (abi != WsprNativeMethods.AbiVersion)
                return (false, null, $"zeus_wspr ABI {abi} does not match the engine's {WsprNativeMethods.AbiVersion}");
            string? version = Marshal.PtrToStringUTF8(WsprNativeMethods.zeus_wspr_version());
            return (true, version, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return (false, null, ex.Message);
        }
    }

    public WsprSlotDecodeResult Decode(float[] samples12k, long dialHz, DateTime slotStartUtc, bool deep)
    {
        WsprSlotDecodeResult result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = DecodeOnLargeStack(samples12k, dialHz, slotStartUtc, deep); }
            catch (Exception ex) { failure = ex; }
        }, DecodeStackBytes)
        {
            IsBackground = true,
            Name = "wspr-native-decode",
        };
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private unsafe WsprSlotDecodeResult DecodeOnLargeStack(float[] samples12k, long dialHz, DateTime slotStartUtc, bool deep)
    {
        if (!Available) return new([], UnavailableReason ?? "zeus_wspr is unavailable");
        try
        {
            Directory.CreateDirectory(_dataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new([], $"WSPR data directory is not writable: {ex.Message}");
        }

        string label = slotStartUtc.ToString("yyMMdd_HHmm", CultureInfo.InvariantCulture);
        var spots = new WsprNativeMethods.Spot[MaxSpots];
        int count;
        fixed (float* samples = samples12k)
        fixed (WsprNativeMethods.Spot* output = spots)
        {
            count = WsprNativeMethods.zeus_wspr_decode(
                samples,
                samples12k.Length,
                WsprNativeMethods.SampleRate,
                dialHz / 1e6,
                _dataDirectory,
                label,
                deep ? WsprNativeMethods.FlagDeep : 0,
                output,
                MaxSpots);
        }

        if (count < 0) return new([], DescribeError(count));

        var result = new WsprRawSpot[count];
        for (int i = 0; i < count; i++)
        {
            ref var spot = ref spots[i];
            string message;
            fixed (byte* text = spot.Message)
                message = ReadMessage(text);
            result[i] = new WsprRawSpot(
                spot.SnrDb,
                spot.DtSec,
                (long)Math.Round(spot.FreqMhz * 1e6),
                spot.DriftHz,
                message);
        }
        return new(result, null);
    }

    private static unsafe string ReadMessage(byte* text)
    {
        int length = 0;
        while (length < 32 && text[length] != 0) length++;
        return Encoding.ASCII.GetString(text, length).Trim();
    }

    internal static string DescribeError(int code) => code switch
    {
        WsprNativeMethods.ErrorArgs => "zeus_wspr rejected its arguments",
        WsprNativeMethods.ErrorRate => "zeus_wspr requires 12 kHz audio",
        WsprNativeMethods.ErrorDir => "WSPR data directory is missing, unwritable, or too long",
        WsprNativeMethods.ErrorIo => "zeus_wspr could not write the slot audio",
        WsprNativeMethods.ErrorDecoder => "the WSPR decoder failed",
        _ => $"zeus_wspr returned {code}",
    };
}

/// <summary>
/// Chooses a data directory the native decoder can open. The vendored wsprd
/// opens its files (slot WAV, hash table, spot list) with the narrow C runtime,
/// which on Windows interprets paths in the ANSI code page, so a profile path
/// with characters outside it fails there while the same UTF-8 path works on
/// macOS and Linux. On Windows the directory must therefore be pure ASCII: the
/// preferred path if it already is, else its 8.3 short form, else a directory
/// under ProgramData.
/// </summary>
internal static partial class WsprDataDirectory
{
    /// <summary>The chosen directory, and whether it is the preferred directory itself.</summary>
    internal readonly record struct Choice(string Path, bool IsPreferredDirectory);

    internal static Choice Resolve(
        string preferred, bool isWindows, Func<string, string?> shortPath, Func<string?> fallback)
    {
        if (!isWindows || IsAscii(preferred)) return new(preferred, true);
        if (shortPath(preferred) is { } shortForm && IsAscii(shortForm)) return new(shortForm, true);
        if (fallback() is { } other && IsAscii(other)) return new(other, false);
        return new(preferred, true);
    }

    /// <summary>
    /// Resolve a non-ASCII preferred directory on Windows. A short name exists
    /// only for an existing directory, so the preferred tree is created first;
    /// if the fallback wins, the directories created here are removed again
    /// (only while empty) so no stray profile folders are left behind.
    /// </summary>
    internal static string PrepareNonAscii(string preferred, Func<string, string?> shortPath, Func<string?> fallback)
    {
        string? createdRoot = TopmostMissing(preferred);
        try { Directory.CreateDirectory(preferred); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { createdRoot = null; }
        var choice = Resolve(preferred, isWindows: true, shortPath, fallback);
        if (!choice.IsPreferredDirectory && createdRoot is not null)
            RemoveEmptyTree(preferred, createdRoot);
        return choice.Path;
    }

    /// <summary>The highest ancestor of <paramref name="path"/> (itself included) that does not exist yet.</summary>
    internal static string? TopmostMissing(string path)
    {
        string? missing = null;
        for (string? dir = System.IO.Path.GetFullPath(path); dir is not null && !Directory.Exists(dir);
             dir = System.IO.Path.GetDirectoryName(dir))
        {
            missing = dir;
        }
        return missing;
    }

    /// <summary>Delete <paramref name="leaf"/> and its ancestors up to <paramref name="root"/>, stopping at the first non-empty one.</summary>
    internal static void RemoveEmptyTree(string leaf, string root)
    {
        string stop = System.IO.Path.GetFullPath(root);
        for (string? dir = System.IO.Path.GetFullPath(leaf); dir is not null; dir = System.IO.Path.GetDirectoryName(dir))
        {
            try
            {
                if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
                Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            if (string.Equals(dir, stop, StringComparison.OrdinalIgnoreCase)) return;
        }
    }

    internal static bool IsAscii(string path) => System.Text.Ascii.IsValid(path);

    internal static bool IsNativeSafe(string path, bool isWindows) => !isWindows || IsAscii(path);

    /// <summary>Length as the native shim counts it: UTF-8 bytes.</summary>
    internal static int NativeLength(string path) => Encoding.UTF8.GetByteCount(path);

    internal static string? WindowsShortPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var buffer = new char[1024];
        uint length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    internal static string? WindowsFallback()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(root)) return null;
        string dir = Path.Combine(root, "ZeusSDR", "wspr");
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);
}
