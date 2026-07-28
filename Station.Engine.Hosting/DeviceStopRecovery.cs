// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

namespace Zeus.Server;

internal enum DeviceRecoveryResult
{
    Recovered,
    GaveUp,
    Cancelled,
}

internal static class DeviceStopRecovery
{
    internal static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    ];

    internal static async Task<DeviceRecoveryResult> RunAsync(
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<int, CancellationToken, Task<bool>> attempt,
        CancellationToken cancellationToken,
        TimeSpan[]? backoff = null)
    {
        var schedule = backoff ?? Backoff;
        try
        {
            for (int index = 0; index < schedule.Length; index++)
            {
                await delay(schedule[index], cancellationToken).ConfigureAwait(false);
                if (await attempt(index + 1, cancellationToken).ConfigureAwait(false))
                    return DeviceRecoveryResult.Recovered;
            }

            return DeviceRecoveryResult.GaveUp;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DeviceRecoveryResult.Cancelled;
        }
    }

    internal static bool ShouldSchedule(
        bool shutdown,
        bool intentionalStop,
        int stoppedGeneration,
        int currentGeneration) =>
        !shutdown && !intentionalStop && stoppedGeneration == currentGeneration;
}
