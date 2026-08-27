// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Grants Zeus its Windows Firewall rule automatically at engine startup, at most
/// once per engine executable.
///
/// Why this lives in the ENGINE and not the product host: StationEngine is the
/// process that opens the HPSDR UDP sockets, so it is the executable the rule has
/// to name. Before this, the only automatic grant was
/// <c>WindowsFirewallAutoApplyService</c> in Zeus.Server.Hosting, which is
/// referenced solely by Zeus.Host — the retired Photino dev bench. Operators
/// running Zeus Link therefore never got a rule from anything at all, and the
/// symptom is silent: discovery finds nothing and the panadapter stays flat,
/// because Protocol 1 receives on an ephemeral UDP port that Windows drops
/// inbound by default.
///
/// Why it is careful about prompting: Zeus Link provisions the engine into
/// <c>&lt;cache&gt;/&lt;version&gt;/&lt;target&gt;/StationEngine.exe</c>. That path changes on every
/// engine update, so a naive "apply on every start" would raise a UAC prompt on
/// every launch for anyone who ever declined one. The ordering below is built so
/// that the common case raises <b>no prompt at all</b>:
///
///   1. Read-only probe. Rule already covers this exe -> done, silently.
///   2. Already recorded an attempt for this exact path -> done, never re-ask.
///   3. Non-elevated add. Succeeds when the engine is already running elevated.
///   4. The elevated helper task, if an installer registered one -> no prompt.
///   5. Only then, one interactive elevation prompt — and the answer is persisted
///      either way, so it is asked exactly once.
///
/// Step 4 is the piece that makes engine updates silent; see
/// docs/designs/windows-firewall-zero-prompt.md for the task registration that
/// the Zeus Link installer needs to perform.
/// </summary>
public sealed class WindowsFirewallStartupGrant : BackgroundService
{
    // Registered by the Zeus Link / Zeus installer, elevated, at install time.
    // Running it needs no elevation of our own and raises no UAC prompt.
    internal const string HelperTaskName = @"\ZeusSDR\GrantNetworkAccess";

    private readonly IWindowsFirewallService _firewall;
    private readonly WindowsFirewallGrantStore _store;
    private readonly IWindowsFirewallElevationHelper _helper;
    private readonly ILogger<WindowsFirewallStartupGrant> _log;
    private readonly Func<bool> _isInteractive;

    public WindowsFirewallStartupGrant(
        IWindowsFirewallService firewall,
        WindowsFirewallGrantStore store,
        IWindowsFirewallElevationHelper helper,
        ILogger<WindowsFirewallStartupGrant> log)
        : this(firewall, store, helper, log, () => Environment.UserInteractive)
    {
    }

    internal WindowsFirewallStartupGrant(
        IWindowsFirewallService firewall,
        WindowsFirewallGrantStore store,
        IWindowsFirewallElevationHelper helper,
        ILogger<WindowsFirewallStartupGrant> log,
        Func<bool> isInteractive)
    {
        _firewall = firewall;
        _store = store;
        _helper = helper;
        _log = log;
        _isInteractive = isInteractive;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never block startup on this. The radio path comes up regardless; the
        // grant is a convenience that either lands or gets surfaced in Settings.
        await Task.Yield();

        try
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during startup. Nothing to record.
        }
        catch (Exception ex)
        {
            // A failure here must never take the engine down.
            _log.LogWarning(ex, "windows.firewall.startup_grant failed");
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var status = await _firewall.GetStatusAsync(ct).ConfigureAwait(false);
        if (!status.Supported)
            return;

        var path = status.ProgramPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _log.LogDebug("windows.firewall.startup_grant skipped: executable path unresolved");
            return;
        }

        // Housekeeping: Zeus Link leaves one dead engine path behind per update.
        // Explicitly protects the running executable — pruning its record here
        // would erase the operator's answer and re-open a settled question.
        try
        {
            _store.PruneMissing(keepPath: path);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "windows.firewall.grant prune failed");
        }

        // (1) Already covered. The overwhelmingly common case after first run.
        if (status.RulePresent == true && status.RuleMatchesProgram == true)
        {
            _store.Record(path, WindowsFirewallGrantOutcome.Granted);
            _log.LogInformation("windows.firewall.startup_grant already allowed path={Path}", path);
            return;
        }

        // (2) We have already had this conversation about this exact binary.
        var prior = _store.Find(path);
        if (prior is not null)
        {
            _log.LogInformation(
                "windows.firewall.startup_grant skipped path={Path} prior={Prior} — not re-asking",
                path,
                prior);
            return;
        }

        // (3) Non-elevated add, explicitly forbidding escalation so this step can
        // never be the thing that pops a dialog. Succeeds outright when the engine
        // is already running elevated.
        var silent = await _firewall
            .ApplyAllowRuleAsync(allowElevation: false, ct)
            .ConfigureAwait(false);
        if (silent.Applied)
        {
            _store.Record(path, WindowsFirewallGrantOutcome.Granted);
            _log.LogInformation("windows.firewall.startup_grant applied path={Path} elevated=false", path);
            return;
        }

        // (4) Elevated helper task. Present only when an installer registered it,
        // and the whole point of it: silent across engine updates.
        if (await _helper.TryInvokeAsync(path, ct).ConfigureAwait(false))
        {
            var after = await _firewall.GetStatusAsync(ct).ConfigureAwait(false);
            if (after.RulePresent == true && after.RuleMatchesProgram == true)
            {
                _store.Record(path, WindowsFirewallGrantOutcome.Granted);
                _log.LogInformation("windows.firewall.startup_grant applied path={Path} via=helper-task", path);
                return;
            }

            _log.LogWarning("windows.firewall.startup_grant helper task ran but no rule appeared path={Path}", path);
        }

        // (5) Last resort: one interactive prompt, ever.
        //
        // A non-interactive session (service, SSH, CI) cannot show a UAC dialog —
        // ShellExec would fail or, worse, hang on an invisible consent window. Do
        // not record an outcome in that case: this machine has not actually
        // refused anything, and a later interactive run should still get its one
        // chance.
        if (!_isInteractive())
        {
            _log.LogInformation(
                "windows.firewall.startup_grant needs elevation but the session is non-interactive path={Path}",
                path);
            return;
        }

        var elevated = await _firewall
            .ApplyAllowRuleAsync(allowElevation: true, ct)
            .ConfigureAwait(false);
        if (elevated.Applied)
        {
            _store.Record(path, WindowsFirewallGrantOutcome.Granted);
            _log.LogInformation("windows.firewall.startup_grant applied path={Path} elevated=true", path);
            return;
        }

        _store.Record(
            path,
            elevated.ElevationCanceled
                ? WindowsFirewallGrantOutcome.Declined
                : WindowsFirewallGrantOutcome.Failed);

        _log.LogInformation(
            "windows.firewall.startup_grant not applied path={Path} canceled={Canceled} — will not ask again",
            path,
            elevated.ElevationCanceled);
    }
}
