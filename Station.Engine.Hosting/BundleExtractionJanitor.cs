// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Diagnostics;
using System.Text.RegularExpressions;

// Shared source: compiled into StationEngine (via this project), and linked into
// ZeusProduct, Zeus.SupportAgent and Zeus.RtlSdr.Sidecar. It must stay
// dependency-free (BCL only) because the two sidecars carry no logging stack.
#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting;
#else
namespace Zeus.Server;
#endif

/// <summary>
/// Removes the extraction directories that earlier builds of this executable left
/// behind in the .NET single-file extraction cache.
/// </summary>
/// <remarks>
/// <para>
/// Official Zeus executables are published as single-file bundles with
/// <c>IncludeAllContentForSelfExtract</c>. On every launch the .NET host unpacks
/// the bundle to <c>$DOTNET_BUNDLE_EXTRACT_BASE_DIR/&lt;app&gt;/&lt;bundle-id&gt;</c>,
/// defaulting to <c>$HOME/.net</c> on macOS and Linux and <c>%TEMP%\.net</c> on
/// Windows. Every build has a new bundle id, and .NET never deletes the old ones,
/// so each engine or product update leaves several hundred files behind.
/// </para>
/// <para>
/// Deleting a directory that no process is using is always safe: the host checks
/// an existing extraction file by file and re-extracts anything missing, and
/// extracts a missing directory from scratch. The only risk is deleting a
/// directory a running process still loads from, so a pass deletes nothing
/// unless every gate below proves the directory is stale:
/// </para>
/// <list type="bullet">
///   <item>This process is itself a self-extracted bundle of the expected app,
///   inside the extraction cache (otherwise nothing at all is touched).</item>
///   <item>The directory is a sibling of this process's own extraction, under
///   the same app folder, is not this process's own directory, and is a plain
///   directory (never a link) whose name the .NET host could have written.</item>
///   <item>It was not written in the last <see cref="SettlingAge"/>, which covers
///   an extraction another process is performing or has just committed.</item>
///   <item>No other process of this app from a different executable is running.
///   Re-checked immediately before each directory is moved, and again after the
///   move; a late starter gets its directory moved straight back.</item>
///   <item>On Linux, no readable process maps a file from it, which catches an
///   instance launched under another name.</item>
/// </list>
/// <para>
/// A directory is first renamed to a trash name (atomic; on Windows it fails
/// while a file inside is in use), then deleted. A trash directory left by an
/// interrupted pass is deleted on the next pass. Every failure is contained:
/// cache maintenance can never block or crash the host.
/// </para>
/// </remarks>
internal sealed partial class BundleExtractionJanitor
{
    internal const string ExtractBaseDirEnvironmentVariable = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";
    internal const string DefaultExtractBaseDirName = ".net";
    internal const string TrashPrefix = ".zeus-prune-";
    internal static readonly TimeSpan SettlingAge = TimeSpan.FromMinutes(15);

    // Linux reports a 15-character comm name and macOS a 16-character one, so a
    // longer app name ("Zeus.SupportAgent", "Zeus.RtlSdr.Sidecar") can surface
    // truncated. Matching on the shared prefix only ever makes the pass more
    // conservative.
    private const int TruncatedProcessNameLength = 15;

    internal readonly record struct RunningProcess(int Id, string? Name, string? ExecutablePath);

    internal readonly record struct PassResult(int Deleted, int Skipped, string? Rejected)
    {
        public static PassResult Reject(string reason) => new(0, 0, reason);
    }

    private readonly string _appName;
    private readonly Action<string>? _log;
    private readonly Func<string?> _getProcessPath;
    private readonly Func<string> _getBaseDirectory;
    private readonly Func<string?> _getExtractBaseOverride;
    private readonly Func<int> _getCurrentProcessId;
    private readonly Func<IReadOnlyList<RunningProcess>> _listProcesses;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string, string> _moveDirectory;
    private readonly Action<string> _deleteDirectory;
    private readonly Func<string, bool> _isDirectoryMapped;

    public BundleExtractionJanitor(string appName, Action<string>? log = null)
        : this(appName, log, null, null, null, null, null, null, null, null, null)
    {
    }

    internal BundleExtractionJanitor(
        string appName,
        Action<string>? log,
        Func<string?>? getProcessPath,
        Func<string>? getBaseDirectory,
        Func<string?>? getExtractBaseOverride,
        Func<int>? getCurrentProcessId,
        Func<IReadOnlyList<RunningProcess>>? listProcesses,
        Func<DateTime>? utcNow,
        Action<string, string>? moveDirectory,
        Action<string>? deleteDirectory,
        Func<string, bool>? isDirectoryMapped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        _appName = appName;
        _log = log;
        _getProcessPath = getProcessPath ?? (() => Environment.ProcessPath);
        _getBaseDirectory = getBaseDirectory ?? (() => AppContext.BaseDirectory);
        _getExtractBaseOverride = getExtractBaseOverride
            ?? (() => Environment.GetEnvironmentVariable(ExtractBaseDirEnvironmentVariable));
        _getCurrentProcessId = getCurrentProcessId ?? (() => Environment.ProcessId);
        _listProcesses = listProcesses ?? ListRunningProcesses;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _moveDirectory = moveDirectory ?? Directory.Move;
        _deleteDirectory = deleteDirectory ?? (path => Directory.Delete(path, recursive: true));
        _isDirectoryMapped = isDirectoryMapped ?? IsMappedByAnyProcess;
    }

    /// <summary>Default wait after a web host reports started, so the pass stays clear of connect.</summary>
    internal static readonly TimeSpan HostStartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Queues one pass once a host signals it is ready (for example
    /// <c>IHostApplicationLifetime.ApplicationStarted</c>).
    /// </summary>
    public static void StartWhenReady(CancellationToken applicationStarted, string appName, Action<string>? log)
    {
        try
        {
            applicationStarted.Register(() => StartBackgroundPass(appName, log, HostStartupDelay));
        }
        catch (Exception ex)
        {
            TryLog(log, $"bundle-extract.prune could not be scheduled: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs one pass on a low-priority background thread so it can never extend
    /// startup, and dies with the process so it can never extend shutdown.
    /// </summary>
    public static void StartBackgroundPass(string appName, Action<string>? log = null, TimeSpan delay = default)
    {
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    if (delay > TimeSpan.Zero) Thread.Sleep(delay);
                    new BundleExtractionJanitor(appName, log).Run();
                }
                catch (Exception ex)
                {
                    TryLog(log, $"bundle-extract.prune background pass failed: {ex.GetType().Name}: {ex.Message}");
                }
            })
            {
                IsBackground = true,
                Name = "bundle-extract-prune",
                Priority = ThreadPriority.Lowest,
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            TryLog(log, $"bundle-extract.prune could not start: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal PassResult Run()
    {
        try
        {
            if (!TryResolveOwnExtraction(out var appFolder, out var ownDirectory, out var rejected))
            {
                Log($"bundle-extract.prune skipped: {rejected}");
                return PassResult.Reject(rejected);
            }

            return Prune(appFolder, ownDirectory);
        }
        catch (Exception ex)
        {
            Log($"bundle-extract.prune pass aborted: {ex.GetType().Name}: {ex.Message}");
            return PassResult.Reject("aborted");
        }
    }

    private bool TryResolveOwnExtraction(out string appFolder, out string ownDirectory, out string rejected)
    {
        appFolder = ownDirectory = rejected = "";

        var processPath = _getProcessPath();
        if (string.IsNullOrWhiteSpace(processPath))
        {
            rejected = "process path unavailable";
            return false;
        }
        if (!NamesEqual(ExecutableHostName(processPath), _appName))
        {
            // Covers `dotnet StationEngine.dll`, test hosts and renamed binaries.
            rejected = $"executable '{Path.GetFileName(processPath)}' is not {_appName}";
            return false;
        }

        var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_getBaseDirectory()));
        var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(processPath));
        if (executableDirectory is null || PathsEqual(baseDirectory, executableDirectory))
        {
            rejected = "not a self-extracted single-file bundle";
            return false;
        }

        var own = new DirectoryInfo(baseDirectory);
        var app = own.Parent;
        var extractBase = app?.Parent;
        if (app is null || extractBase is null || !NamesEqual(app.Name, _appName))
        {
            rejected = $"base directory '{baseDirectory}' is not under an extraction folder for {_appName}";
            return false;
        }
        if (!IsExtractionBase(extractBase))
        {
            rejected = $"'{extractBase.FullName}' is not the .NET bundle extraction directory";
            return false;
        }
        if (!IsHostWrittenName(own.Name))
        {
            rejected = $"own extraction name '{own.Name}' is not a bundle id";
            return false;
        }

        appFolder = app.FullName;
        ownDirectory = own.FullName;
        return true;
    }

    private bool IsExtractionBase(DirectoryInfo candidate)
    {
        var configured = _getExtractBaseOverride();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                // The host resolves a relative override against its working
                // directory; compare against what this process actually got.
                return PathsEqual(candidate.FullName, Path.GetFullPath(configured), whenUnknown: false);
            }
            catch
            {
                return false;
            }
        }

        return NamesEqual(candidate.Name, DefaultExtractBaseDirName);
    }

    private PassResult Prune(string appFolder, string ownDirectory)
    {
        var deleted = 0;
        var skipped = 0;

        foreach (var candidate in Directory.EnumerateDirectories(appFolder, "*", SearchOption.TopDirectoryOnly).ToList())
        {
            var name = Path.GetFileName(candidate);
            try
            {
                if (name.StartsWith(TrashPrefix, StringComparison.Ordinal))
                {
                    if (IsLink(candidate)) { skipped++; continue; }
                    _deleteDirectory(candidate);
                    deleted++;
                    Log($"bundle-extract.prune deleted leftover trash {candidate}");
                    continue;
                }

                if (PathsEqual(candidate, ownDirectory) || !IsHostWrittenName(name) || IsLink(candidate))
                    continue;

                var lastTouched = LastTouchedUtc(candidate);
                if (_utcNow() - lastTouched < SettlingAge)
                {
                    skipped++;
                    Log($"bundle-extract.prune kept recent {candidate} (written {lastTouched:O})");
                    continue;
                }

                // Re-checked per directory, immediately before the atomic move, so
                // an old build launched mid-pass keeps its extraction.
                if (OtherInstanceRunning(out var reason))
                {
                    skipped++;
                    Log($"bundle-extract.prune stopped: {reason}");
                    break;
                }

                if (_isDirectoryMapped(candidate))
                {
                    skipped++;
                    Log($"bundle-extract.prune kept {candidate}: a running process has files from it loaded");
                    continue;
                }

                var trash = Path.Combine(appFolder, TrashPrefix + name + "-" + Guid.NewGuid().ToString("N")[..8]);
                _moveDirectory(candidate, trash);

                // An old build that started between the check and the move may be
                // verifying this very extraction; hand it back untouched.
                if (OtherInstanceRunning(out var lateReason))
                {
                    _moveDirectory(trash, candidate);
                    skipped++;
                    Log($"bundle-extract.prune restored {candidate} and stopped: {lateReason}");
                    break;
                }

                _deleteDirectory(trash);
                deleted++;
                Log($"bundle-extract.prune deleted stale extraction {candidate}");
            }
            catch (Exception ex)
            {
                // In use on Windows, permissions, a racing delete: keep it for a later pass.
                skipped++;
                Log($"bundle-extract.prune skipped {candidate}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Log($"bundle-extract.prune completed app={_appName} folder={appFolder} deleted={deleted} skipped={skipped}");
        return new PassResult(deleted, skipped, null);
    }

    private bool OtherInstanceRunning(out string reason)
    {
        reason = "";
        IReadOnlyList<RunningProcess> processes;
        try
        {
            processes = _listProcesses();
        }
        catch (Exception ex)
        {
            reason = $"process list unavailable ({ex.GetType().Name})";
            return true;
        }

        var ownId = _getCurrentProcessId();
        // Read our own path through the same API as everyone else's, so a
        // symlinked launch path (macOS /var -> /private/var) still compares equal.
        var ownPath = processes.FirstOrDefault(p => p.Id == ownId).ExecutablePath ?? _getProcessPath();
        foreach (var process in processes)
        {
            if (process.Id == ownId || !NameMatchesApp(process.Name)) continue;

            // Plugin and imaging workers re-launch this very executable, so they
            // share this process's extraction. A different path is a different
            // build (Zeus Link keeps one directory per version), and an unreadable
            // path cannot be proven to be ours.
            if (process.ExecutablePath is null || ownPath is null
                || !PathsEqual(process.ExecutablePath, ownPath, whenUnknown: false))
            {
                reason = $"another {_appName} is running (pid {process.Id}, {process.ExecutablePath ?? "path unknown"})";
                return true;
            }
        }

        return false;
    }

    private bool NameMatchesApp(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (NamesEqual(processName, _appName)) return true;
        return processName.Length >= TruncatedProcessNameLength
               && _appName.StartsWith(processName, FileSystemComparison);
    }

    /// <summary>
    /// Linux only: whether any readable process maps a file from <paramref name="directory"/>.
    /// The runtime maps managed assemblies and native libraries, so this sees an
    /// instance whatever name it was launched under (a renamed copy or symlink).
    /// Other users' processes are unreadable and cannot use this user's cache.
    /// </summary>
    internal static bool IsMappedByAnyProcess(string directory)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + "/";
        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(processDirectory), out _)) continue;
            try
            {
                foreach (var line in File.ReadLines(Path.Combine(processDirectory, "maps")))
                {
                    if (line.Contains(prefix, StringComparison.Ordinal)) return true;
                }
            }
            catch
            {
                // Exited mid-scan or another user's process.
            }
        }

        return false;
    }

    /// <summary>Processes whose name could be this app, with their executable paths.</summary>
    internal IReadOnlyList<RunningProcess> ListRunningProcesses()
    {
        var result = new List<RunningProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? name;
                try { name = process.ProcessName; }
                catch { continue; } // Exited, or another user's process: cannot use this user's cache.

                // Reading a path is the expensive part (and throws for system
                // processes on Windows), so only read it for possible instances.
                if (!NameMatchesApp(name)) continue;

                string? path = null;
                try { path = process.MainModule?.FileName; }
                catch { /* unreadable: treated as a different build */ }

                result.Add(new RunningProcess(process.Id, name, path));
            }
        }

        return result;
    }

    private static DateTime LastTouchedUtc(string directory)
    {
        var info = new DirectoryInfo(directory);
        var written = info.LastWriteTimeUtc;
        var created = info.CreationTimeUtc;
        return written > created ? written : created;
    }

    private static bool IsLink(string directory) =>
        new DirectoryInfo(directory).LinkTarget is not null
        || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;

    /// <summary>The app folder name the host derives from the bundle file name.</summary>
    internal static string ExecutableHostName(string processPath)
    {
        var fileName = Path.GetFileName(processPath);
        // Mirrors the host's strip_executable_ext: only Windows strips ".exe".
        // Path.GetFileNameWithoutExtension would turn "Zeus.SupportAgent" into "Zeus".
        return OperatingSystem.IsWindows() && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }

    /// <summary>
    /// Bundle ids are base64url hashes and in-progress extractions use the process
    /// id in hex; anything else in the app folder was not written by the host.
    /// </summary>
    internal static bool IsHostWrittenName(string name) => HostWrittenName().IsMatch(name);

    private static StringComparison FileSystemComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool NamesEqual(string left, string right) =>
        string.Equals(left, right, FileSystemComparison);

    /// <param name="whenUnknown">
    /// The answer when the comparison cannot complete, chosen per caller so that
    /// an unknown always keeps the directory.
    /// </param>
    private static bool PathsEqual(string left, string right, bool whenUnknown = true)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                FileSystemComparison);
        }
        catch
        {
            return whenUnknown;
        }
    }

    private void Log(string message) => TryLog(_log, message);

    private static void TryLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never break the pass */ }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostWrittenName();
}
