// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;

namespace Zeus.Server;

/// <summary>
/// Runs the firewall grant through a pre-registered elevated Scheduled Task, so
/// no UAC prompt appears.
///
/// This is the only mechanism Windows offers for letting an unprivileged process
/// perform a privileged action repeatedly without consent dialogs: a task
/// registered once, at install time, by something that was already elevated, with
/// "run with highest privileges" set. Starting such a task needs no rights beyond
/// being the user it was registered for.
///
/// It matters because Zeus Link reinstalls the engine to a new version-scoped
/// path on every update, which invalidates the path-pinned firewall rule. Without
/// the task, every engine update would cost the operator one UAC prompt. With it,
/// updates are silent.
///
/// Absent task = not an error. <see cref="TryInvokeAsync"/> returns false and the
/// caller falls back to asking once, interactively.
/// </summary>
public interface IWindowsFirewallElevationHelper
{
    Task<bool> TryInvokeAsync(string programPath, CancellationToken ct);
}

public sealed class ScheduledTaskFirewallElevationHelper : IWindowsFirewallElevationHelper
{
    private readonly ILogger<ScheduledTaskFirewallElevationHelper> _log;

    public ScheduledTaskFirewallElevationHelper(ILogger<ScheduledTaskFirewallElevationHelper> log)
    {
        _log = log;
    }

    public async Task<bool> TryInvokeAsync(string programPath, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        // The task reads the path it should authorise from an environment variable
        // the task definition forwards, so one registration serves every future
        // engine version without re-registration.
        if (!await QueryTaskExistsAsync(ct).ConfigureAwait(false))
        {
            _log.LogDebug(
                "windows.firewall.helper task {Task} is not registered; falling back",
                WindowsFirewallStartupGrant.HelperTaskName);
            return false;
        }

        var exit = await RunSchtasksAsync(
            ["/Run", "/TN", WindowsFirewallStartupGrant.HelperTaskName],
            ct).ConfigureAwait(false);

        if (exit != 0)
        {
            _log.LogWarning("windows.firewall.helper task run failed exit={Exit}", exit);
            return false;
        }

        // schtasks /Run returns as soon as the task is queued, not when it finishes.
        // Poll briefly so the caller's follow-up probe sees the result rather than
        // racing it.
        for (var i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            if (!await QueryTaskRunningAsync(ct).ConfigureAwait(false))
                break;
        }

        _log.LogInformation(
            "windows.firewall.helper task ran for path={Path}",
            programPath);
        return true;
    }

    private async Task<bool> QueryTaskExistsAsync(CancellationToken ct) =>
        await RunSchtasksAsync(["/Query", "/TN", WindowsFirewallStartupGrant.HelperTaskName], ct)
            .ConfigureAwait(false) == 0;

    private async Task<bool> QueryTaskRunningAsync(CancellationToken ct)
    {
        var (exit, output) = await RunSchtasksCapturedAsync(
            ["/Query", "/TN", WindowsFirewallStartupGrant.HelperTaskName, "/FO", "LIST"],
            ct).ConfigureAwait(false);
        if (exit != 0) return false;
        return output.Contains("Running", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> RunSchtasksAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var (exit, _) = await RunSchtasksCapturedAsync(args, ct).ConfigureAwait(false);
        return exit;
    }

    private async Task<(int ExitCode, string Output)> RunSchtasksCapturedAsync(
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(ResolveSchtasks())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return (-1, "");

            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, stdout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "windows.firewall.helper schtasks invocation failed");
            return (-1, "");
        }
    }

    private static string ResolveSchtasks()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidate = Path.Combine(system, "schtasks.exe");
        return File.Exists(candidate) ? candidate : "schtasks.exe";
    }
}

/// <summary>No helper available — every non-Windows host, and tests.</summary>
public sealed class NullFirewallElevationHelper : IWindowsFirewallElevationHelper
{
    public static readonly NullFirewallElevationHelper Instance = new();

    public Task<bool> TryInvokeAsync(string programPath, CancellationToken ct) =>
        Task.FromResult(false);
}
