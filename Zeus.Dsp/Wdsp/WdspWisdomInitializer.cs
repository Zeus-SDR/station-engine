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

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;

namespace Zeus.Dsp.Wdsp;

/// <summary>
/// Owns the one-shot WDSPwisdom FFTW plan-cache initialisation. Singleton —
/// the WDSP wisdom file at $LOCALAPPDATA/Zeus/wdspWisdom01 is process-global
/// state, so there's nothing to parallelise.
/// </summary>
public sealed class WdspWisdomInitializer
{
    /// <summary>
    /// Set to exactly "1" to skip the WDSPwisdom bake entirely (Phase goes
    /// straight to Ready; FFT plans build on demand). Test-host escape hatch:
    /// the FFTW_PATIENT bake across sizes 64..262144 takes many minutes on CI
    /// runners and must never run inside a test process. Inert in production.
    /// </summary>
    internal const string SkipEnvironmentVariable = "ZEUS_WISDOM_SKIP";

    // WDSP's wisdom file inside the wisdom directory, and its sidecar stamp.
    // The historical ".version" name remains for an in-place migration from
    // engine-version values to native-library content hashes.
    internal const string WisdomFileName = "wdspWisdom01";
    internal const string VersionStampFileName = WisdomFileName + ".version";
    internal const string NativeStampPrefix = "native:";

    // Operator-facing summary streamed as the WisdomStatus trailer on failure;
    // the actionable detail stays in zeus-app.log.
    internal const string FailureStatus =
        "FFT setup failed — DSP will use the slow path. See zeus-app.log.";

    // 250 ms is fast enough that the splash text never feels stale, slow enough
    // that we're not P/Invoking 40×/s into wisdom_get_status() while FFTW is
    // doing real work on a background thread.
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly object AmbientGate = new();
    private static WdspWisdomInitializer? s_ambientInitializer;

    private readonly ILogger _log;
    private readonly Action _ensureResolverRegistered;
    private readonly Func<string, int> _runWisdom;
    private readonly Func<string> _getWisdomStatus;
    private readonly Func<string> _getWisdomDirectory;
    private readonly Func<string> _getStamp;
    private readonly object _gate = new();
    private readonly object _statusGate = new();
    private Task? _task;
    private int _phase = (int)WisdomPhase.Idle;
    private string _status = string.Empty;

    public WdspWisdomInitializer(ILogger<WdspWisdomInitializer>? logger = null)
        : this(
            logger,
            WdspNativeLoader.EnsureResolverRegistered,
            NativeMethods.WDSPwisdom,
            GetNativeWisdomStatus)
    {
    }

    internal WdspWisdomInitializer(
        ILogger<WdspWisdomInitializer>? logger,
        Action ensureResolverRegistered,
        Func<string, int> runWisdom,
        Func<string> getWisdomStatus,
        Func<string>? getWisdomDirectory = null,
        Func<string>? getStamp = null)
    {
        _log = logger ?? NullLogger<WdspWisdomInitializer>.Instance;
        _ensureResolverRegistered = ensureResolverRegistered ?? throw new ArgumentNullException(nameof(ensureResolverRegistered));
        _runWisdom = runWisdom ?? throw new ArgumentNullException(nameof(runWisdom));
        _getWisdomStatus = getWisdomStatus ?? throw new ArgumentNullException(nameof(getWisdomStatus));
        _getWisdomDirectory = getWisdomDirectory ?? GetDefaultWisdomDirectory;
        _getStamp = getStamp ?? ResolveCurrentStamp;
        RegisterAmbient(this);
    }

    public WisdomPhase Phase => (WisdomPhase)Volatile.Read(ref _phase);

    /// <summary>
    /// Live WDSP wisdom status text (e.g. "Planning COMPLEX FORWARD FFT size
    /// 1024"), updated at <see cref="StatusPollInterval"/> while
    /// <see cref="Phase"/> is <see cref="WisdomPhase.Building"/>. Empty until
    /// the first poll lands.
    /// </summary>
    public string Status => Volatile.Read(ref _status) ?? string.Empty;

    public event Action<WisdomPhase>? PhaseChanged;

    /// <summary>Fires when the WDSP status string changes during a build,
    /// so the UI can stream sub-step progress without polling.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// Blocks until the process-registered WDSP wisdom initializer has finished
    /// when one exists. No-op for bare engines in tests and tools that construct
    /// <see cref="WdspDspEngine"/> without the hosting singleton.
    /// </summary>
    public static void WaitUntilReady()
    {
        WdspWisdomInitializer? initializer;
        lock (AmbientGate)
            initializer = s_ambientInitializer;

        initializer?.WaitUntilReadyInstance();
    }

    internal static void ResetAmbientForTests()
    {
        lock (AmbientGate)
            s_ambientInitializer = null;
    }

    /// <summary>
    /// Idempotent. First call kicks off the WDSPwisdom P/Invoke on a worker
    /// thread and returns a Task tracking it. Subsequent calls (including
    /// re-entrance from WdspDspEngine) return the same Task.
    /// </summary>
    public Task EnsureInitializedAsync()
    {
        lock (_gate)
        {
            if (_task is not null) return _task;

            if (ShouldSkipWisdom())
            {
                _log.LogInformation(
                    "wdsp.wisdom skipped via {Variable}; FFT plans will build on demand",
                    SkipEnvironmentVariable);
                SetPhase(WisdomPhase.Ready);
                _task = Task.CompletedTask;
                return _task;
            }

            SetPhase(WisdomPhase.Building);
            _ensureResolverRegistered();
            _ = Task.Run(PollStatusUntilReady);
            _task = Task.Run(RunWisdom);
            return _task;
        }
    }

    private static bool ShouldSkipWisdom() =>
        string.Equals(
            Environment.GetEnvironmentVariable(SkipEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    private static void RegisterAmbient(WdspWisdomInitializer initializer)
    {
        lock (AmbientGate)
        {
            if (s_ambientInitializer is null || s_ambientInitializer.Phase == WisdomPhase.Ready)
                s_ambientInitializer = initializer;
        }
    }

    private static string GetNativeWisdomStatus() =>
        Marshal.PtrToStringUTF8(NativeMethods.wisdom_get_status()) ?? string.Empty;

    private void WaitUntilReadyInstance()
    {
        if (Phase == WisdomPhase.Ready) return;
        EnsureInitializedAsync().GetAwaiter().GetResult();
    }

    private void RunWisdom()
    {
        bool success = false;
        try
        {
            var dir = _getWisdomDirectory();
            Directory.CreateDirectory(dir);

            // Rebake only when the native content that owns the FFTW plan
            // shapes/flags changes. A missing, legacy, corrupt, or unreadable
            // stamp counts as "differs". When the stamp matches we leave the
            // file alone and the native import finishes instantly.
            var stamp = _getStamp();
            _log.LogDebug(
                "wdsp.wisdom stamp mode={Mode}",
                stamp.StartsWith(NativeStampPrefix, StringComparison.Ordinal)
                    ? "native-content"
                    : "version-fallback");
            var stampPath = Path.Combine(dir, VersionStampFileName);
            var wisdomPath = Path.Combine(dir, WisdomFileName);
            if (!StampMatches(stampPath, stamp) && File.Exists(wisdomPath))
            {
                try
                {
                    File.Delete(wisdomPath);
                    _log.LogInformation(
                        "wdsp.wisdom native stamp changed; deleted stale cache for rebake");
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "wdsp.wisdom could not delete stale cache at {Path}", wisdomPath);
                }
            }

            // WDSP's wisdom.c does a plain strcat of "wdspWisdom01" onto the
            // directory without inserting a path separator (see native/wdsp/
            // wisdom.c:49-50). Pass the directory with a trailing separator so
            // the native side produces a valid "<dir>/wdspWisdom01" path
            // instead of "<dir>wdspWisdom01" which lands the wisdom file one
            // level up.
            var dirForNative = dir + Path.DirectorySeparatorChar;

            _log.LogInformation("wdsp.wisdom initialising dir={Dir}", dirForNative);
            int result = _runWisdom(dirForNative);
            var status = _getWisdomStatus();
            _log.LogInformation(
                "wdsp.wisdom ready result={Result} ({Source}) status={Status}",
                result, result == 0 ? "loaded" : "built", status);

            // Best-effort: failure to write the stamp must not fail the run,
            // it just forces a rebake next launch. Never written on failure,
            // so the next launch retries the bake.
            TryWriteStamp(stampPath, stamp);
            success = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "wdsp.wisdom failed — subsequent FFT planning will take the slow path");
        }
        finally
        {
            // Single serialized terminal transition: the phase flip stops the
            // status poller, and publishing the failure summary inside the
            // same gate guarantees the poller cannot clobber it with a stale
            // planner string on its final in-flight iteration.
            lock (_statusGate)
            {
                SetPhase(success ? WisdomPhase.Ready : WisdomPhase.Failed);
                if (!success)
                    SetStatus(FailureStatus);
            }
        }
    }

    private static string GetDefaultWisdomDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zeus");

    private string ResolveCurrentStamp()
    {
        try
        {
            if (WdspNativeLoader.TryGetResolvedNativeLibraryPath(out string? wdspPath)
                && wdspPath is not null)
            {
                return ResolveStamp(
                    wdspPath,
                    WdspNativeLoader.FftwDependencyFilePaths(wdspPath),
                    ResolveVersion,
                    _log);
            }

            return ResolveStamp(null, Array.Empty<string>(), ResolveVersion, _log);
        }
        catch (Exception ex)
        {
            _log.LogDebug(
                ex,
                "wdsp.wisdom native library resolution failed; using informational-version stamp");
            return ResolveFallbackVersion(ResolveVersion, _log);
        }
    }

    /// <summary>
    /// Builds the stable native-content stamp. The path arguments form the
    /// test seam, so regression tests use small fake files without loading or
    /// invoking WDSP.
    /// </summary>
    internal static string ResolveStamp(
        string? wdspPath,
        IReadOnlyList<string> fftwDependencyPaths,
        Func<string> getVersion,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(fftwDependencyPaths);
        ArgumentNullException.ThrowIfNull(getVersion);
        logger ??= NullLogger.Instance;

        try
        {
            if (string.IsNullOrWhiteSpace(wdspPath) || !File.Exists(wdspPath))
            {
                logger.LogDebug(
                    "wdsp.wisdom native library path unavailable; using informational-version stamp");
                return ResolveFallbackVersion(getVersion, logger);
            }

            string wdspHash = HashFile(wdspPath);
            var dependencies = new SortedDictionary<string, string>(StringComparer.Ordinal);

            foreach (string dependencyPath in fftwDependencyPaths)
            {
                if (!File.Exists(dependencyPath))
                    continue;

                string fileName = Path.GetFileName(dependencyPath);
                string component = fileName.Contains("fftw3f", StringComparison.OrdinalIgnoreCase)
                    ? "fftw3f"
                    : fileName.Contains("fftw3", StringComparison.OrdinalIgnoreCase)
                        ? "fftw3"
                        : throw new InvalidDataException(
                            $"Unrecognized FFTW dependency file name: {fileName}");
                dependencies[component] = HashFile(dependencyPath);
            }

            var components = new[] { $"wdsp={wdspHash}" }
                .Concat(dependencies.Select(component => $"{component.Key}={component.Value}"));
            return NativeStampPrefix + string.Join(';', components);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "wdsp.wisdom native library hashing failed; using informational-version stamp");
            return ResolveFallbackVersion(getVersion, logger);
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
    }

    private static string ResolveFallbackVersion(Func<string> getVersion, ILogger logger)
    {
        try
        {
            string version = getVersion();
            return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "wdsp.wisdom informational version unavailable; using unknown fallback stamp");
            return "unknown";
        }
    }

    // Informational version used only when the resolved native-library content
    // cannot be hashed. Falling back to this assembly and then a constant keeps
    // the pre-change invalidation semantics without risking startup failure.
    internal static string ResolveVersion()
    {
        var entry = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(entry)) return entry;
        var own = typeof(WdspWisdomInitializer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(own) ? "unknown" : own;
    }

    private bool StampMatches(string stampPath, string stamp)
    {
        try
        {
            return File.Exists(stampPath)
                && string.Equals(File.ReadAllText(stampPath).Trim(), stamp, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "wdsp.wisdom stamp unreadable at {Path}; treating as changed", stampPath);
            return false;
        }
    }

    private void TryWriteStamp(string stampPath, string stamp)
    {
        try
        {
            File.WriteAllText(stampPath, stamp);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "wdsp.wisdom could not write stamp at {Path}; next launch will rebake", stampPath);
        }
    }

    private void SetStatus(string s)
    {
        Volatile.Write(ref _status, s);
        try { StatusChanged?.Invoke(s); }
        catch (Exception ex) { _log.LogDebug(ex, "wdsp.wisdom StatusChanged subscriber threw"); }
    }

    // Polls the native status buffer on a separate thread. When WDSPwisdom
    // loaded a cached file the whole call finishes before we ever poll —
    // that's fine: we exit on the Ready transition with no status update,
    // which is the right behaviour (no build happened, nothing to narrate).
    private void PollStatusUntilReady()
    {
        try
        {
            while (Phase == WisdomPhase.Building)
            {
                var s = _getWisdomStatus();
                s = s.TrimEnd('\r', '\n', ' ', '\t');
                lock (_statusGate)
                {
                    // Re-check under the gate: RunWisdom's terminal
                    // transition (phase flip + failure summary) holds the
                    // same lock, so a poller iteration that started while
                    // Building cannot overwrite the terminal status.
                    if (Phase != WisdomPhase.Building) break;
                    if (!string.Equals(s, Volatile.Read(ref _status), StringComparison.Ordinal))
                    {
                        Volatile.Write(ref _status, s);
                        try { StatusChanged?.Invoke(s); }
                        catch (Exception ex) { _log.LogDebug(ex, "wdsp.wisdom StatusChanged subscriber threw"); }
                    }
                }
                Thread.Sleep(StatusPollInterval);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "wdsp.wisdom status poller exited unexpectedly");
        }
    }

    private void SetPhase(WisdomPhase next)
    {
        var prev = (WisdomPhase)Interlocked.Exchange(ref _phase, (int)next);
        if (prev != next) PhaseChanged?.Invoke(next);
    }
}
