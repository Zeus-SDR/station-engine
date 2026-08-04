// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Text.RegularExpressions;

namespace Zeus.Server;

/// <summary>
/// Removes stale version directories from a launcher-managed station-engine cache.
/// Every gate and deletion is best-effort so cache maintenance can never block startup.
/// </summary>
internal sealed partial class EngineCacheJanitor
{
    internal const string StationAccessTokenEnvironmentVariable = "ZEUS_STATION_ACCESS_TOKEN";
    private const string ChannelMarkerFileName = ".channel-v0.json";
    private const string VerifiedCacheRecordFileName = "verified.json";
    private const string MigratedVerifiedCacheRecordFileName = "verified-v1.json";
    // A freshly installed version, whether an upgrade or downgrade/rollback, must
    // survive the current cycle and be re-evaluated on a later engine start.
    private static readonly TimeSpan VerifiedInstallSettlingAge = TimeSpan.FromMinutes(15);

    private readonly ILogger<EngineCacheJanitor> _log;
    private readonly Action<string, string> _moveDirectory;
    private readonly Action<string> _deleteDirectory;
    private readonly Func<string, bool> _hasSettledVerifiedEngineInstall;
    private readonly Func<string, DateTime> _getLastWriteTimeUtc;
    private readonly Func<string?> _getProcessPath;
    private readonly Func<string> _getBaseDirectory;
    private readonly Func<string?> _getStationAccessToken;

    public EngineCacheJanitor(ILogger<EngineCacheJanitor> log)
        : this(
            log,
            Directory.Move,
            path => Directory.Delete(path, recursive: true))
    {
    }

    internal EngineCacheJanitor(
        ILogger<EngineCacheJanitor> log,
        Action<string, string> moveDirectory,
        Action<string> deleteDirectory,
        Func<string, bool>? hasSettledVerifiedEngineInstall = null,
        Func<string, DateTime>? getLastWriteTimeUtc = null,
        Func<string?>? getProcessPath = null,
        Func<string>? getBaseDirectory = null,
        Func<string?>? getStationAccessToken = null)
    {
        _log = log;
        _moveDirectory = moveDirectory;
        _deleteDirectory = deleteDirectory;
        _getLastWriteTimeUtc = getLastWriteTimeUtc ?? File.GetLastWriteTimeUtc;
        _getProcessPath = getProcessPath ?? (() => Environment.ProcessPath);
        _getBaseDirectory = getBaseDirectory ?? (() => AppContext.BaseDirectory);
        _getStationAccessToken = getStationAccessToken ?? (() =>
            Environment.GetEnvironmentVariable(StationAccessTokenEnvironmentVariable));
        _hasSettledVerifiedEngineInstall =
            hasSettledVerifiedEngineInstall ?? HasSettledVerifiedEngineInstall;
    }

    internal void Run(CancellationToken cancellationToken)
    {
        try
        {
            var processPath = _getProcessPath();
            var fallbackReason = processPath is null
                ? "process-path-null"
                : string.IsNullOrWhiteSpace(processPath)
                    ? "process-path-blank"
                    : IsDotnetMuxer(processPath)
                        ? "dotnet-muxer"
                        : null;
            var locationSource = fallbackReason is null
                ? "process-path"
                : "base-directory-fallback";
            var baseDirectory = fallbackReason is not null
                ? _getBaseDirectory()
                : Path.GetDirectoryName(processPath);
            _log.LogDebug(
                "engine.cache-prune resolved location processPath={ProcessPath} " +
                "source={LocationSource} fallbackReason={FallbackReason} " +
                "baseDirectory={BaseDirectory}",
                processPath,
                locationSource,
                fallbackReason,
                baseDirectory);
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                _log.LogDebug(
                    "engine.cache-prune rejected gate=base-directory processPath={ProcessPath} " +
                    "source={LocationSource} fallbackReason={FallbackReason} " +
                    "baseDirectory={BaseDirectory}",
                    processPath,
                    locationSource,
                    fallbackReason,
                    baseDirectory);
                return;
            }

            RunForBaseDirectory(
                _getStationAccessToken(),
                baseDirectory,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "engine.cache-prune pass aborted");
        }
    }

    internal void RunForBaseDirectory(
        string? stationAccessToken,
        string baseDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationAccessToken))
        {
            _log.LogDebug(
                "engine.cache-prune rejected gate=station-access-token tokenPresent=false " +
                "baseDirectory={BaseDirectory}",
                baseDirectory);
            return;
        }

        try
        {
            var targetDirectory = new DirectoryInfo(baseDirectory);
            if (string.IsNullOrEmpty(targetDirectory.Name))
            {
                _log.LogDebug(
                    "engine.cache-prune rejected gate=target-directory-name " +
                    "baseDirectory={BaseDirectory} targetDirectory={TargetDirectory} " +
                    "targetDirectoryName={TargetDirectoryName}",
                    baseDirectory,
                    targetDirectory.FullName,
                    targetDirectory.Name);
                return;
            }

            var ownVersionDirectory = targetDirectory.Parent;
            if (ownVersionDirectory is null)
            {
                _log.LogDebug(
                    "engine.cache-prune rejected gate=version-directory-parent " +
                    "baseDirectory={BaseDirectory} targetDirectory={TargetDirectory} " +
                    "candidateVersionDirectory=<null>",
                    baseDirectory,
                    targetDirectory.FullName);
                return;
            }
            if (!IsStrictSemVer(ownVersionDirectory.Name))
            {
                _log.LogDebug(
                    "engine.cache-prune rejected gate=version-directory-semver " +
                    "baseDirectory={BaseDirectory} " +
                    "candidateVersionDirectory={CandidateVersionDirectory} " +
                    "candidateVersionDirectoryName={CandidateVersionDirectoryName} " +
                    "requirement=strict SemVer",
                    baseDirectory,
                    ownVersionDirectory.FullName,
                    ownVersionDirectory.Name);
                return;
            }

            var cacheDirectory = ownVersionDirectory.Parent;
            var channelMarkerPath = cacheDirectory is null
                ? null
                : Path.Combine(cacheDirectory.FullName, ChannelMarkerFileName);
            if (cacheDirectory is null || !File.Exists(channelMarkerPath))
            {
                _log.LogDebug(
                    "engine.cache-prune rejected gate=channel-marker " +
                    "cacheDirectory={CacheDirectory} channelMarkerPath={ChannelMarkerPath}",
                    cacheDirectory?.FullName,
                    channelMarkerPath);
                return;
            }

            PruneCache(
                cacheDirectory.FullName,
                ownVersionDirectory.Name,
                ownVersionDirectory.FullName,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "engine.cache-prune pass aborted");
        }
    }

    internal void PruneCache(
        string cacheDirectory,
        string ownVersionDirectoryName,
        string ownVersionDirectoryPath,
        CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        var trashDeletedCount = 0;
        var skippedCount = 0;

        foreach (var trashPath in Directory.GetDirectories(
                     cacheDirectory,
                     ".prune-*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _deleteDirectory(trashPath);
                trashDeletedCount++;
                _log.LogInformation(
                    "engine.cache-prune deleted leftover trash directory {TrashDirectory}",
                    trashPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                skippedCount++;
                _log.LogWarning(
                    ex,
                    "engine.cache-prune skipped leftover trash directory {TrashDirectory}",
                    trashPath);
            }
        }

        foreach (var candidatePath in Directory.GetDirectories(
                     cacheDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateName = Path.GetFileName(candidatePath);
            if (!IsStrictSemVer(candidateName)
                || FileSystemNamesEqual(candidateName, ownVersionDirectoryName)
                || PathsEqual(candidatePath, ownVersionDirectoryPath)
                || ComparePrecedence(candidateName, ownVersionDirectoryName) >= 0)
            {
                continue;
            }

            var trashSuffix = Guid.NewGuid().ToString("N")[..8];
            var trashPath = Path.Combine(
                cacheDirectory,
                $".prune-{candidateName}-{trashSuffix}");
            try
            {
                if (!_hasSettledVerifiedEngineInstall(candidatePath))
                {
                    skippedCount++;
                    _log.LogDebug(
                        "engine.cache-prune skipped unverified or unsettled version directory " +
                        "{VersionDirectory}",
                        candidatePath);
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                _moveDirectory(candidatePath, trashPath);
                cancellationToken.ThrowIfCancellationRequested();
                _deleteDirectory(trashPath);
                deletedCount++;
                _log.LogInformation(
                    "engine.cache-prune deleted stale version directory {VersionDirectory}",
                    candidatePath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                skippedCount++;
                _log.LogWarning(
                    ex,
                    "engine.cache-prune skipped stale version directory {VersionDirectory}",
                    candidatePath);
            }
        }

        _log.LogInformation(
            "engine.cache-prune completed cache={CacheDirectory} deleted={DeletedCount} " +
            "trashDeleted={TrashDeletedCount} skipped={SkippedCount}",
            cacheDirectory,
            deletedCount,
            trashDeletedCount,
            skippedCount);
    }

    internal static bool IsStrictSemVer(string? value) =>
        value is not null && StrictSemVerPattern().IsMatch(value);

    internal static StringComparison FileSystemComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal static bool FileSystemNamesEqual(string left, string right) =>
        string.Equals(left, right, FileSystemComparison);

    internal static int ComparePrecedence(string left, string right)
    {
        try
        {
            if (!TryParseSemVer(left, out var leftVersion)
                || !TryParseSemVer(right, out var rightVersion))
            {
                return 0;
            }

            var comparison = CompareNumericIdentifier(leftVersion.Major, rightVersion.Major);
            if (comparison != 0) return comparison;
            comparison = CompareNumericIdentifier(leftVersion.Minor, rightVersion.Minor);
            if (comparison != 0) return comparison;
            comparison = CompareNumericIdentifier(leftVersion.Patch, rightVersion.Patch);
            if (comparison != 0) return comparison;

            if (leftVersion.PreRelease.Length == 0)
                return rightVersion.PreRelease.Length == 0 ? 0 : 1;
            if (rightVersion.PreRelease.Length == 0) return -1;

            var sharedLength = Math.Min(
                leftVersion.PreRelease.Length,
                rightVersion.PreRelease.Length);
            for (var index = 0; index < sharedLength; index++)
            {
                comparison = ComparePreReleaseIdentifier(
                    leftVersion.PreRelease[index],
                    rightVersion.PreRelease[index]);
                if (comparison != 0) return comparison;
            }

            return leftVersion.PreRelease.Length.CompareTo(rightVersion.PreRelease.Length);
        }
        catch
        {
            // Invalid or otherwise surprising input must fail closed: equal precedence
            // makes the caller preserve the candidate rather than risk deleting it.
            return 0;
        }
    }

    private static bool TryParseSemVer(string value, out ParsedSemVer version)
    {
        version = default;
        if (!IsStrictSemVer(value)) return false;

        var buildSeparator = value.IndexOf('+');
        var precedence = buildSeparator >= 0 ? value[..buildSeparator] : value;
        var preReleaseSeparator = precedence.IndexOf('-');
        var core = preReleaseSeparator >= 0
            ? precedence[..preReleaseSeparator]
            : precedence;
        var coreParts = core.Split('.');
        if (coreParts.Length != 3) return false;

        var preRelease = preReleaseSeparator >= 0
            ? precedence[(preReleaseSeparator + 1)..].Split('.')
            : [];
        version = new ParsedSemVer(
            coreParts[0],
            coreParts[1],
            coreParts[2],
            preRelease);
        return true;
    }

    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        var leftNumeric = IsNumericIdentifier(left);
        var rightNumeric = IsNumericIdentifier(right);
        if (leftNumeric && rightNumeric) return CompareNumericIdentifier(left, right);
        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
    }

    private static bool IsNumericIdentifier(string value) =>
        value.All(character => character is >= '0' and <= '9');

    private bool HasSettledVerifiedEngineInstall(string versionDirectory)
    {
        var foundTargetDirectory = false;
        foreach (var targetDirectory in Directory.EnumerateDirectories(
                     versionDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            foundTargetDirectory = true;
            var migratedVerifiedPath = Path.Combine(
                targetDirectory,
                MigratedVerifiedCacheRecordFileName);
            var verifiedPath = File.Exists(migratedVerifiedPath)
                ? migratedVerifiedPath
                : Path.Combine(targetDirectory, VerifiedCacheRecordFileName);
            if (!File.Exists(verifiedPath)) return false;

            var lastWriteTimeUtc = _getLastWriteTimeUtc(verifiedPath);
            if (lastWriteTimeUtc.Year <= 1601
                || DateTime.UtcNow - lastWriteTimeUtc
                <= VerifiedInstallSettlingAge)
            {
                return false;
            }
        }

        return foundTargetDirectory;
    }

    private static bool IsDotnetMuxer(string processPath)
    {
        var fileName = Path.GetFileName(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
               || string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            var resolvedLeft = new DirectoryInfo(left)
                                   .ResolveLinkTarget(returnFinalTarget: true)
                                   ?.FullName
                               ?? left;
            var resolvedRight = new DirectoryInfo(right)
                                    .ResolveLinkTarget(returnFinalTarget: true)
                                    ?.FullName
                                ?? right;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolvedLeft)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolvedRight)),
                FileSystemComparison);
        }
        catch
        {
            // An identity check that cannot be completed must preserve the candidate.
            return true;
        }
    }

    [GeneratedRegex(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StrictSemVerPattern();

    private readonly record struct ParsedSemVer(
        string Major,
        string Minor,
        string Patch,
        string[] PreRelease);
}

/// <summary>Runs one cache-pruning pass after startup without extending startup or shutdown.</summary>
internal sealed class EngineCacheJanitorStartup : IHostedService, IDisposable
{
    private readonly EngineCacheJanitor _janitor;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<EngineCacheJanitorStartup> _log;
    private CancellationTokenRegistration _applicationStartedRegistration;

    public EngineCacheJanitorStartup(
        EngineCacheJanitor janitor,
        IHostApplicationLifetime applicationLifetime,
        ILogger<EngineCacheJanitorStartup> log)
    {
        _janitor = janitor;
        _applicationLifetime = applicationLifetime;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _applicationStartedRegistration = _applicationLifetime.ApplicationStarted.Register(
            QueuePrunePass);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _applicationStartedRegistration.Dispose();

    private void QueuePrunePass()
    {
        // Run as soon as the host is ready. The strictly-older gate and per-directory
        // failure containment handle a previous engine that is still draining, while
        // the detached pass cannot extend startup or shutdown.
        _ = RunPrunePassAsync();
    }

    private async Task RunPrunePassAsync()
    {
        try
        {
            await Task.Run(
                    () => _janitor.Run(_applicationLifetime.ApplicationStopping),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "engine.cache-prune background task failed");
        }
    }
}
