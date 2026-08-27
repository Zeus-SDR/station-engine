// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Zeus.Server;

public interface IWindowsFirewallService
{
    WindowsFirewallStatus GetStatus();

    /// <summary>
    /// Same as <see cref="GetStatus"/> but additionally probes for the rule so the
    /// caller can tell "no rule" from "rule present" from "rule points somewhere
    /// else". The probe is a read-only <c>netsh show rule</c>, so it needs no
    /// elevation and raises no UAC prompt.
    /// </summary>
    Task<WindowsFirewallStatus> GetStatusAsync(CancellationToken ct = default);

    Task<WindowsFirewallApplyResult> ApplyAllowRuleAsync(CancellationToken ct = default);

    /// <summary>
    /// As <see cref="ApplyAllowRuleAsync(CancellationToken)"/>, but when
    /// <paramref name="allowElevation"/> is false the non-elevated
    /// <c>netsh add</c> is the last thing tried — no <c>runas</c>, so no UAC
    /// prompt can appear. The startup grant uses this to exhaust every silent
    /// option before it considers interrupting the operator.
    /// </summary>
    Task<WindowsFirewallApplyResult> ApplyAllowRuleAsync(
        bool allowElevation,
        CancellationToken ct = default);
}

public sealed record WindowsFirewallStatus(
    bool Supported,
    bool CanApply,
    string RuleName,
    string? ProgramPath,
    string Message,
    // null = not probed (non-Windows, or the synchronous GetStatus overload).
    bool? RulePresent = null,
    // Only meaningful when RulePresent is true. False means a rule with our name
    // exists but allows a different executable -- the usual aftermath of
    // reinstalling into a different folder, and it allows nothing.
    bool? RuleMatchesProgram = null);

public sealed record WindowsFirewallApplyResult(
    bool Supported,
    bool Applied,
    bool ElevationAttempted,
    bool ElevationCanceled,
    string RuleName,
    string? ProgramPath,
    string Message);

public sealed class WindowsFirewallService : IWindowsFirewallService
{
    public const string RuleName = "ZeusSDR (HPSDR receive)";

    // The firewall rule shipped under the pre-rebrand product name. We delete it
    // whenever we (re)apply the rule so an upgraded-over install doesn't leave an
    // orphaned duplicate in Windows Defender Firewall. Never re-added.
    public const string LegacyRuleName = "OpenHPSDR Zeus (HPSDR receive)";

    private readonly ILogger<WindowsFirewallService> _log;
    private readonly IWindowsFirewallCommandRunner _runner;
    private readonly Func<bool> _isWindows;
    private readonly Func<string?> _processPath;

    public WindowsFirewallService(ILogger<WindowsFirewallService> log)
        : this(
            log,
            new ProcessWindowsFirewallCommandRunner(),
            OperatingSystem.IsWindows,
            () => Environment.ProcessPath)
    {
    }

    internal WindowsFirewallService(
        ILogger<WindowsFirewallService> log,
        IWindowsFirewallCommandRunner runner,
        Func<bool> isWindows,
        Func<string?> processPath)
    {
        _log = log;
        _runner = runner;
        _isWindows = isWindows;
        _processPath = processPath;
    }

    public WindowsFirewallStatus GetStatus()
    {
        if (!_isWindows())
        {
            return new(
                Supported: false,
                CanApply: false,
                RuleName,
                ProgramPath: null,
                Message: "Windows Firewall rule management is only available on Windows.");
        }

        var programPath = ResolveProgramPath();
        if (string.IsNullOrWhiteSpace(programPath))
        {
            return new(
                Supported: true,
                CanApply: false,
                RuleName,
                ProgramPath: null,
                Message: "Could not resolve the Zeus executable path.");
        }

        return new(
            Supported: true,
            CanApply: true,
            RuleName,
            ProgramPath: programPath,
            Message: "Ready to add the Zeus inbound allow rule.");
    }

    public async Task<WindowsFirewallStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var status = GetStatus();
        if (!status.Supported || string.IsNullOrWhiteSpace(status.ProgramPath))
            return status;

        FirewallRuleProbe probe;
        try
        {
            probe = await _runner.ProbeAsync(RuleName, status.ProgramPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe failure must not degrade into "no rule" -- that would nag an
            // operator who is already protected. Leave the flags null (unknown).
            _log.LogDebug(ex, "windows.firewall.probe failed path={ProgramPath}", status.ProgramPath);
            return status;
        }

        var message = status.Message;
        if (probe.Exists && probe.MatchesProgram)
        {
            message = "Zeus is allowed through Windows Firewall.";
        }
        else if (probe.Exists)
        {
            // Rule present but pointing elsewhere. Silent and total: it allows
            // nothing, and the operator has no way to notice without being told.
            message = "A firewall rule with this name exists but allows a different "
                    + "copy of Zeus. Apply the rule to repoint it at this one.";
        }
        else
        {
            message = "Zeus is not allowed through Windows Firewall. Without this rule "
                    + "the radio will most likely not be found.";
        }

        return status with
        {
            Message = message,
            RulePresent = probe.Exists,
            RuleMatchesProgram = probe.Exists ? probe.MatchesProgram : null,
        };
    }

    public Task<WindowsFirewallApplyResult> ApplyAllowRuleAsync(CancellationToken ct = default) =>
        ApplyAllowRuleAsync(allowElevation: true, ct);

    public async Task<WindowsFirewallApplyResult> ApplyAllowRuleAsync(
        bool allowElevation,
        CancellationToken ct = default)
    {
        var status = GetStatus();
        if (!status.Supported || !status.CanApply || string.IsNullOrWhiteSpace(status.ProgramPath))
        {
            return new(
                Supported: status.Supported,
                Applied: false,
                ElevationAttempted: false,
                ElevationCanceled: false,
                RuleName,
                ProgramPath: status.ProgramPath,
                Message: status.Message);
        }

        // Querying an existing rule does not require administrator rights, so probe
        // first: if the inbound allow rule already exists for this exact program path
        // there is nothing to change, and re-adding it would needlessly trigger the
        // Windows UAC elevation prompt on every launch. Short-circuit before touching
        // netsh add / runas.
        var probe = await _runner.ProbeAsync(RuleName, status.ProgramPath, ct);
        if (probe.Exists && probe.MatchesProgram)
        {
            _log.LogInformation(
                "windows.firewall.rule already present path={ProgramPath}; skipping apply", status.ProgramPath);
            return new(
                Supported: true,
                Applied: true,
                ElevationAttempted: false,
                ElevationCanceled: false,
                RuleName,
                ProgramPath: status.ProgramPath,
                Message: "Windows Firewall rule already present.");
        }

        var direct = await _runner.ApplyAsync(RuleName, status.ProgramPath, elevated: false, ct);
        if (direct.ExitCode == 0)
        {
            _log.LogInformation("windows.firewall.rule applied path={ProgramPath} elevated=false", status.ProgramPath);
            return new(
                Supported: true,
                Applied: true,
                ElevationAttempted: false,
                ElevationCanceled: false,
                RuleName,
                ProgramPath: status.ProgramPath,
                Message: "Windows Firewall rule applied.");
        }

        if (!allowElevation)
        {
            // Caller asked for silent-only. Report the failure without prompting so
            // it can try a non-interactive path (the elevated helper task) first.
            _log.LogInformation(
                "windows.firewall.rule direct apply failed exit={ExitCode}; elevation not permitted by caller",
                direct.ExitCode);
            return new(
                Supported: true,
                Applied: false,
                ElevationAttempted: false,
                ElevationCanceled: false,
                RuleName,
                ProgramPath: status.ProgramPath,
                Message: "Windows Firewall rule needs administrator approval.");
        }

        _log.LogInformation(
            "windows.firewall.rule direct apply failed exit={ExitCode}; requesting elevation",
            direct.ExitCode);

        var elevated = await _runner.ApplyAsync(RuleName, status.ProgramPath, elevated: true, ct);
        if (elevated.ExitCode == 0)
        {
            _log.LogInformation("windows.firewall.rule applied path={ProgramPath} elevated=true", status.ProgramPath);
            return new(
                Supported: true,
                Applied: true,
                ElevationAttempted: true,
                ElevationCanceled: false,
                RuleName,
                ProgramPath: status.ProgramPath,
                Message: "Windows Firewall rule applied.");
        }

        if (elevated.Canceled)
        {
            return new(
                Supported: true,
                Applied: false,
                ElevationAttempted: true,
                ElevationCanceled: true,
                RuleName,
                ProgramPath: status.ProgramPath,
                Message: "Windows administrator approval was cancelled.");
        }

        _log.LogWarning(
            "windows.firewall.rule elevated apply failed exit={ExitCode} output={Output}",
            elevated.ExitCode,
            elevated.Output);
        return new(
            Supported: true,
            Applied: false,
            ElevationAttempted: true,
            ElevationCanceled: false,
            RuleName,
            ProgramPath: status.ProgramPath,
            Message: "Windows did not accept the firewall rule. Try running Zeus as administrator, then apply it again.");
    }

    private string? ResolveProgramPath()
    {
        var path = _processPath()?.Trim();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}

internal interface IWindowsFirewallCommandRunner
{
    Task<FirewallRuleProbe> ProbeAsync(
        string ruleName,
        string programPath,
        CancellationToken ct);

    Task<FirewallCommandResult> ApplyAsync(
        string ruleName,
        string programPath,
        bool elevated,
        CancellationToken ct);
}

internal sealed record FirewallCommandResult(int ExitCode, string Output, bool Canceled = false);

// Result of a non-elevated query for an existing inbound allow rule. MatchesProgram
// is only meaningful when Exists is true.
internal sealed record FirewallRuleProbe(bool Exists, bool MatchesProgram);

internal sealed class ProcessWindowsFirewallCommandRunner : IWindowsFirewallCommandRunner
{
    private const int ErrorCancelled = 1223;

    public async Task<FirewallRuleProbe> ProbeAsync(
        string ruleName,
        string programPath,
        CancellationToken ct)
    {
        // "show rule" is a read-only query and does not require elevation. netsh
        // returns a non-zero exit code ("No rules match the specified criteria")
        // when the rule is absent.
        var result = await RunNetshAsync(
            [
                "advfirewall",
                "firewall",
                "show",
                "rule",
                "name=" + ruleName,
                "verbose",
            ],
            ct);

        if (result.ExitCode != 0)
            return new(Exists: false, MatchesProgram: false);

        var matchesProgram = result.Output.Contains(programPath, StringComparison.OrdinalIgnoreCase);
        return new(Exists: true, MatchesProgram: matchesProgram);
    }

    public async Task<FirewallCommandResult> ApplyAsync(
        string ruleName,
        string programPath,
        bool elevated,
        CancellationToken ct)
    {
        return elevated
            ? await RunElevatedAsync(ruleName, programPath, ct)
            : await RunDirectAsync(ruleName, programPath, ct);
    }

    private static async Task<FirewallCommandResult> RunDirectAsync(
        string ruleName,
        string programPath,
        CancellationToken ct)
    {
        // Remove any pre-rebrand rule first so upgraders don't accumulate a
        // stale "OpenHPSDR Zeus (HPSDR receive)" duplicate. Idempotent.
        await RunNetshAsync(
            [
                "advfirewall",
                "firewall",
                "delete",
                "rule",
                "name=" + WindowsFirewallService.LegacyRuleName,
            ],
            ct);

        var delete = await RunNetshAsync(
            [
                "advfirewall",
                "firewall",
                "delete",
                "rule",
                "name=" + ruleName,
            ],
            ct);

        var add = await RunNetshAsync(
            [
                "advfirewall",
                "firewall",
                "add",
                "rule",
                "name=" + ruleName,
                "dir=in",
                "action=allow",
                "program=" + programPath,
                "enable=yes",
            ],
            ct);

        var output = string.Join(
            Environment.NewLine,
            new[] { delete.Output, add.Output }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return add with { Output = output };
    }

    private static async Task<FirewallCommandResult> RunElevatedAsync(
        string ruleName,
        string programPath,
        CancellationToken ct)
    {
        var script = BuildElevatedPowerShellScript(ruleName, programPath);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo(ResolveTool("powershell.exe"))
        {
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new(-1, "Failed to start elevated PowerShell.");

            await process.WaitForExitAsync(ct);
            return new(process.ExitCode, "");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new(ErrorCancelled, ex.Message, Canceled: true);
        }
    }

    internal static string BuildElevatedPowerShellScript(string ruleName, string programPath)
    {
        var netsh = ResolveTool("netsh.exe");
        return string.Join(
            Environment.NewLine,
            [
                "$ErrorActionPreference = 'SilentlyContinue'",
                "$netsh = " + PowerShellLiteral(netsh),
                "$rule = " + PowerShellLiteral(ruleName),
                "$legacyRule = " + PowerShellLiteral(WindowsFirewallService.LegacyRuleName),
                "$program = " + PowerShellLiteral(programPath),
                "& $netsh advfirewall firewall delete rule (\"name=\" + $legacyRule) | Out-Null",
                "& $netsh advfirewall firewall delete rule (\"name=\" + $rule) | Out-Null",
                "$ErrorActionPreference = 'Stop'",
                "& $netsh advfirewall firewall add rule (\"name=\" + $rule) dir=in action=allow (\"program=\" + $program) enable=yes",
                "exit $LASTEXITCODE",
            ]);
    }

    private static async Task<FirewallCommandResult> RunNetshAsync(
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ResolveTool("netsh.exe"))
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        return new(process.ExitCode, output.ToString().Trim());
    }

    private static string ResolveTool(string fileName)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(system))
        {
            var candidate = Path.Combine(system, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return fileName;
    }

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
