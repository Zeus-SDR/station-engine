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

using System.Runtime.InteropServices;

namespace Zeus.Protocol2;

/// <summary>
/// Result of a best-effort process-wide Windows timer-resolution raise.
/// Off Windows every field is the not-Windows default, and
/// <see cref="TimeBeginPeriodResult"/> is 0. That 0 is not a
/// <c>timeBeginPeriod</c> status: <see cref="ToLogFragment"/> prints
/// <c>not-windows</c> and does not include the field, so it cannot be read
/// as success. On Windows <see cref="TimeBeginPeriodResult"/> is the
/// <c>timeBeginPeriod</c> return (<c>MMSYSERR_NOERROR</c> is 0) or
/// <see cref="uint.MaxValue"/> when that call was not made, so a missing
/// call cannot be recorded or logged as success.
/// </summary>
public readonly record struct WindowsTimerResolutionResult(
    bool IsWindows,
    bool ThrottlingOptOutApplied,
    int ThrottlingOptOutError,
    uint TimeBeginPeriodResult)
{
    /// <summary>
    /// Compact log fragment: <c>timer.resolution timeBeginPeriod=0 throttlingOptOut=applied</c>,
    /// <c>timer.resolution timeBeginPeriod=0 throttlingOptOut=unsupported err=87</c>,
    /// or <c>timer.resolution not-windows</c>.
    /// </summary>
    public string ToLogFragment()
    {
        if (!IsWindows)
            return "timer.resolution not-windows";
        string optOut = ThrottlingOptOutApplied
            ? "throttlingOptOut=applied"
            : $"throttlingOptOut=unsupported err={ThrottlingOptOutError}";
        return $"timer.resolution timeBeginPeriod={TimeBeginPeriodResult} {optOut}";
    }
}

/// <summary>
/// Raises the process-wide Windows timer resolution for TX pacing and opts
/// the process out of Windows 11's occlusion/minimize throttle.
/// </summary>
/// <remarks>
/// Windows' default system timer is ~15.6 ms. <c>timeBeginPeriod(1)</c> raises
/// it for the process lifetime (issue #633 / PR 632). Starting with Windows 11,
/// that request is withdrawn when a window-owning process is fully occluded,
/// minimized, or otherwise invisible or inaudible — which is the Zeus Link
/// engine with an ASIO hidden window (issue #3401). Microsoft's documented
/// opt-out is <c>SetProcessInformation(ProcessPowerThrottling)</c> with
/// <c>ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION</c> and
/// <c>StateMask = 0</c>. Windows 10 rejects the flag (ERROR_INVALID_PARAMETER);
/// that is expected and ignored. Never throws.
/// </remarks>
public static partial class WindowsTimerResolution
{
    private const int ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    /// <summary>
    /// Process-power-throttling state that opts this process out of the
    /// Windows 11 timer-resolution throttle: version 1,
    /// <c>ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION</c>
    /// (0x4), <c>StateMask = 0</c>. Pure; <see cref="Raise"/> is the only caller.
    /// </summary>
    internal static (uint Version, uint ControlMask, uint StateMask) IgnoreTimerResolutionOptOut() =>
        (ProcessPowerThrottlingCurrentVersion, ProcessPowerThrottlingIgnoreTimerResolution, 0);

    public static WindowsTimerResolutionResult Raise()
    {
        if (!OperatingSystem.IsWindows())
            return new WindowsTimerResolutionResult(false, false, 0, 0);

        bool optOutApplied = false;
        int optOutError = 0;
        // 0 is MMSYSERR_NOERROR. Start at the not-called sentinel so deleting
        // the timeBeginPeriod call cannot record success.
        uint timeBeginPeriodResult = uint.MaxValue;

        try
        {
            (uint version, uint controlMask, uint stateMask) = IgnoreTimerResolutionOptOut();
            var state = new ProcessPowerThrottlingState
            {
                Version = version,
                ControlMask = controlMask,
                StateMask = stateMask,
            };
            uint size = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
            if (SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, size))
            {
                optOutApplied = true;
            }
            else
            {
                optOutError = Marshal.GetLastPInvokeError();
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
            optOutError = Marshal.GetLastPInvokeError();
        }
        catch (Exception)
        {
        }

        try
        {
            timeBeginPeriodResult = TimeBeginPeriod(1);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (Exception)
        {
        }

        return new WindowsTimerResolutionResult(true, optOutApplied, optOutError, timeBeginPeriodResult);
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint uMilliseconds);
}
