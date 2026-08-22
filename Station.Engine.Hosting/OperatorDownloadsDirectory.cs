// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server;

internal enum OperatorPlatform
{
    Windows,
    MacOS,
    Linux,
}

internal sealed record SavedBackupFile(
    string Path,
    string FileName,
    string Directory,
    long SizeBytes);

internal interface IOperatorDownloadsDirectoryProbe
{
    IEnumerable<string> ReadLines(string path);
    void CreateDirectory(string path);
    bool FileExists(string path);
    void WriteNewFile(string path, byte[] bytes);
    void MoveFile(string source, string destination);
    void DeleteFile(string path);
}

internal sealed class PhysicalOperatorDownloadsDirectoryProbe
    : IOperatorDownloadsDirectoryProbe
{
    public IEnumerable<string> ReadLines(string path) => File.ReadLines(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool FileExists(string path) => File.Exists(path);

    public void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        stream.Write(bytes);
    }

    public void MoveFile(string source, string destination) =>
        File.Move(source, destination);

    public void DeleteFile(string path) => File.Delete(path);
}

/// <summary>
/// Saves an operator-requested backup to the first usable downloads or
/// fallback directory. Environment and filesystem access are injectable so
/// every platform branch can be exercised without using the real profile.
/// </summary>
internal sealed class OperatorDownloadsDirectory
{
    private const int MaximumFileNameAttempts = 100;
    private readonly OperatorPlatform _platform;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string> _getUserProfile;
    private readonly string _prefsDataDirectory;
    private readonly IOperatorDownloadsDirectoryProbe _probe;
    private readonly Func<DateTime> _utcNow;

    internal OperatorDownloadsDirectory()
        : this(
            DetectPlatform(),
            Environment.GetEnvironmentVariable,
            () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            PrefsDbPath.DataDir,
            new PhysicalOperatorDownloadsDirectoryProbe(),
            () => DateTime.UtcNow)
    {
    }

    internal OperatorDownloadsDirectory(
        OperatorPlatform platform,
        Func<string, string?> getEnvironmentVariable,
        Func<string> getUserProfile,
        string prefsDataDirectory,
        IOperatorDownloadsDirectoryProbe probe,
        Func<DateTime> utcNow)
    {
        _platform = platform;
        _getEnvironmentVariable = getEnvironmentVariable
            ?? throw new ArgumentNullException(nameof(getEnvironmentVariable));
        _getUserProfile = getUserProfile
            ?? throw new ArgumentNullException(nameof(getUserProfile));
        _prefsDataDirectory = prefsDataDirectory
            ?? throw new ArgumentNullException(nameof(prefsDataDirectory));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    internal SavedBackupFile SaveBackup(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        Exception? lastFailure = null;
        foreach (var directory in Candidates())
        {
            string? temporaryPath = null;
            try
            {
                _probe.CreateDirectory(directory);
                var writtenTemporaryPath = Path.Combine(
                    directory,
                    $".zeus-backup-{Guid.NewGuid():N}.tmp");
                temporaryPath = writtenTemporaryPath;
                // This is the writability proof and the real payload write.
                // The stream is closed by the probe before any move is tried.
                _probe.WriteNewFile(writtenTemporaryPath, bytes);

                var timestamp = _utcNow();
                for (var attempt = 1; attempt <= MaximumFileNameAttempts; attempt++)
                {
                    var fileName = BackupFileName(timestamp, attempt);
                    var finalPath = Path.Combine(directory, fileName);
                    if (_probe.FileExists(finalPath))
                        continue;
                    try
                    {
                        _probe.MoveFile(writtenTemporaryPath, finalPath);
                        temporaryPath = null;
                        return new SavedBackupFile(
                            Path.GetFullPath(finalPath),
                            fileName,
                            Path.GetFullPath(directory),
                            bytes.LongLength);
                    }
                    catch (IOException) when (_probe.FileExists(finalPath))
                    {
                        // A concurrent export won the name after our check.
                        // Reuse the same closed temp file with the next suffix.
                    }
                }

                throw new InvalidOperationException(
                    $"Zeus could not choose a unique backup name after " +
                    $"{MaximumFileNameAttempts} attempts.");
            }
            catch (IOException ex)
            {
                lastFailure = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastFailure = ex;
            }
            finally
            {
                if (temporaryPath is not null)
                    TryDelete(temporaryPath);
            }
        }

        throw new IOException(
            "Zeus could not save the database backup because the Downloads folder, " +
            "user profile, and Zeus data directory are not writable.",
            lastFailure);
    }

    private IReadOnlyList<string> Candidates()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var home = _getUserProfile()?.Trim() ?? string.Empty;

        if (_platform == OperatorPlatform.Linux)
        {
            var configured = _getEnvironmentVariable("XDG_DOWNLOAD_DIR");
            if (string.IsNullOrWhiteSpace(configured))
                configured = ReadXdgDownloadDirectory(home);
            AddCandidate(configured, home, candidates, seen);
        }

        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(home))
            AddCandidate(Path.Combine(home, "Downloads"), home, candidates, seen);
        AddCandidate(home, home, candidates, seen);
        AddCandidate(_prefsDataDirectory, home, candidates, seen);
        return candidates;
    }

    private string? ReadXdgDownloadDirectory(string home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return null;
        try
        {
            var configHome = _getEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
                configHome = Path.Combine(home, ".config");
            else
                configHome = ExpandHome(configHome.Trim(), home);
            var userDirectoriesPath = Path.Combine(configHome, "user-dirs.dirs");
            foreach (var line in _probe.ReadLines(userDirectoriesPath))
            {
                var equals = line.IndexOf('=');
                if (equals < 0
                    || !string.Equals(
                        line[..equals].Trim(),
                        "XDG_DOWNLOAD_DIR",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var value = ParseValue(line[(equals + 1)..]);
                if (!string.IsNullOrWhiteSpace(value))
                    return ExpandHome(value, home);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return null;
    }

    private static string? ParseValue(string source)
    {
        var value = source.Trim();
        if (value.Length == 0)
            return null;
        if (value[0] is '\'' or '"')
        {
            var closing = value.IndexOf(value[0], 1);
            return closing > 0 ? value[1..closing] : null;
        }

        var comment = value.IndexOf('#');
        return (comment >= 0 ? value[..comment] : value).Trim();
    }

    private static string ExpandHome(string value, string home)
    {
        var expanded = value
            .Replace("${HOME}", home, StringComparison.Ordinal)
            .Replace("$HOME", home, StringComparison.Ordinal);
        if (expanded == "~")
            return home;
        if (expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || expanded.StartsWith($"~{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return Path.Combine(home, expanded[2..]);
        }
        return expanded;
    }

    private static void AddCandidate(
        string? candidate,
        string home,
        ICollection<string> candidates,
        ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;
        try
        {
            var expanded = ExpandHome(candidate.Trim(), home);
            var fullPath = Path.GetFullPath(expanded);
            if (seen.Add(fullPath))
                candidates.Add(fullPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Ignore malformed XDG/profile paths and continue through the
            // fallback chain.
        }
    }

    private static string BackupFileName(DateTime timestamp, int attempt)
    {
        var stem = $"zeus-backup-{timestamp:yyyyMMdd-HHmmss}";
        var suffix = attempt == 1 ? string.Empty : $" ({attempt})";
        return $"{stem}{suffix}.zeusdb";
    }

    private void TryDelete(string path)
    {
        try
        {
            _probe.DeleteFile(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static OperatorPlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
            return OperatorPlatform.Windows;
        if (OperatingSystem.IsMacOS())
            return OperatorPlatform.MacOS;
        return OperatorPlatform.Linux;
    }
}
