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

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Dsp.Wdsp;

namespace Zeus.Server;

/// <summary>
/// Kicks off WDSPwisdom on a worker thread at app start so first-connect
/// isn't blocked for ~2 minutes while FFTW runs FFTW_PATIENT across sizes
/// 64..262144. Returns from StartAsync immediately — Kestrel must not wait
/// on wisdom generation.
///
/// Also the one place that states, on the record, which DSP engine this process
/// actually loaded. Both hosts (Zeus.Server and the standalone station engine)
/// register this as a hosted service exactly once, so the version line is
/// emitted once per process at DSP start.
/// </summary>
public sealed class WisdomBootstrapService : IHostedService
{
    private readonly WdspWisdomInitializer _initializer;
    private readonly ILogger _log;
    private readonly Func<WdspEngineVersion> _resolveEngineVersion;

    public WisdomBootstrapService(
        WdspWisdomInitializer initializer,
        ILogger<WisdomBootstrapService>? logger = null)
        : this(initializer, logger, WdspDspEngine.ResolveEngineVersion)
    {
    }

    internal WisdomBootstrapService(
        WdspWisdomInitializer initializer,
        ILogger? logger,
        Func<WdspEngineVersion> resolveEngineVersion)
    {
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _log = logger ?? NullLogger.Instance;
        _resolveEngineVersion = resolveEngineVersion ?? throw new ArgumentNullException(nameof(resolveEngineVersion));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogEngineVersion(_log, _resolveEngineVersion);
        _ = _initializer.EnsureInitializedAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Puts the loaded engine's identity on the wire. One greppable prefix
    /// (<c>wdsp.engine.version</c>) with the same key set in every case, so a
    /// field report can be filtered on the prefix alone and still carry both the
    /// raw integer WDSP returned and its human form.
    ///
    /// A version below the required one is a genuine engine mismatch and is
    /// logged at Error, but deliberately does NOT stop the process or block the
    /// operator: Zeus drives real transmitters, and refusing to start is a worse
    /// outcome than running loudly degraded. The condition is additionally
    /// published in diagnostics (<c>wdspVersionMismatch</c>) so the UI can
    /// surface it.
    /// </summary>
    internal static void LogEngineVersion(ILogger log, Func<WdspEngineVersion> resolve)
    {
        WdspEngineVersion version;
        try
        {
            version = resolve();
        }
        catch (Exception ex)
        {
            // Engine identity is diagnostic, never load-bearing. Failing to read
            // it must not take the host down at start.
            log.LogWarning(ex, "wdsp.engine.version probe failed; engine identity unknown");
            return;
        }

        switch (version.State)
        {
            case WdspEngineVersionState.Ok:
                log.LogInformation(
                    "wdsp.engine.version raw={Raw} version={Version} required={Required} status={Status}",
                    version.Raw, version.Display, WdspEngineVersion.RequiredDisplay, version.StatusToken);
                break;

            case WdspEngineVersionState.Mismatch:
                log.LogError(
                    "wdsp.engine.version raw={Raw} version={Version} required={Required} status={Status} "
                    + "— the loaded libwdsp is OLDER than the engine Zeus targets. Every WDSP-2.0-dependent "
                    + "behaviour is unreliable until the matching library is installed.",
                    version.Raw, version.Display, WdspEngineVersion.RequiredDisplay, version.StatusToken);
                break;

            case WdspEngineVersionState.SymbolMissing:
                log.LogWarning(
                    "wdsp.engine.version raw={Raw} version={Version} required={Required} status={Status} "
                    + "— the loaded libwdsp does not export GetWDSPVersion, so its version cannot be confirmed.",
                    version.Raw, version.Display, WdspEngineVersion.RequiredDisplay, version.StatusToken);
                break;

            default:
                // No library at all is already reported by the native-loadable
                // diagnostics; state it here too so the version line is never
                // simply absent, but at Information — this is not a version fault.
                log.LogInformation(
                    "wdsp.engine.version raw={Raw} version={Version} required={Required} status={Status} "
                    + "— libwdsp is not loadable in this process.",
                    version.Raw, version.Display, WdspEngineVersion.RequiredDisplay, version.StatusToken);
                break;
        }
    }
}
