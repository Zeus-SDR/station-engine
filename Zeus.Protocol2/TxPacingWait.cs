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
using Microsoft.Win32.SafeHandles;

namespace Zeus.Protocol2;

/// <summary>
/// One-millisecond wait used by the Protocol-2 TX-IQ sender to hold the
/// modeled radio FIFO at its target. On Windows this is a high-resolution
/// waitable timer so the wait stays ~1 ms even when the process-wide
/// <c>timeBeginPeriod(1)</c> request is withdrawn (Windows 11 occludes or
/// minimizes a window-owning process — issue #3401). Other platforms keep
/// <see cref="Thread.Sleep(int)"/>, which is already ~1 ms there.
/// </summary>
internal interface ITxPacingWait : IDisposable
{
    void WaitOneMillisecond();
    bool IsHighResolution { get; }
}

internal sealed partial class TxPacingWait : ITxPacingWait
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;
    // Relative due time: 1 ms in 100-ns units, negative = relative.
    private const long OneMillisecondDueTime = -10_000;

    private SafeWaitHandle? _timer;

    internal static ITxPacingWait Create() => new TxPacingWait();

    public bool IsHighResolution { get; }

    private TxPacingWait()
    {
        if (!OperatingSystem.IsWindows())
        {
            IsHighResolution = false;
            return;
        }

        try
        {
            IntPtr raw = CreateWaitableTimerExW(
                IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (raw != IntPtr.Zero)
            {
                var handle = new SafeWaitHandle(raw, ownsHandle: true);
                if (!handle.IsInvalid)
                {
                    _timer = handle;
                    IsHighResolution = true;
                    return;
                }
                handle.Dispose();
            }
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

        IsHighResolution = false;
    }

    public void WaitOneMillisecond()
    {
        SafeWaitHandle? timer = _timer;
        if (OperatingSystem.IsWindows() && timer is { IsInvalid: false, IsClosed: false })
        {
            try
            {
                long dueTime = OneMillisecondDueTime;
                if (SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false)
                    && WaitForSingleObject(timer, Infinite) == WaitObject0)
                    return;
            }
            catch
            {
                // Fall through to Thread.Sleep. Never throw out of the pacer.
            }
        }

        Thread.Sleep(1);
    }

    public void Dispose()
    {
        SafeWaitHandle? timer = Interlocked.Exchange(ref _timer, null);
        timer?.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        SafeWaitHandle hTimer,
        ref long lpDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeWaitHandle hHandle, uint dwMilliseconds);
}
