// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// Held remote-TX lease: the product host keeps one loopback GET open for as
// long as a local keying device (a Bluetooth PTT button) may transmit. The
// engine streams a heartbeat line; the moment the request ends — clean
// shutdown, product crash, killed process, broken socket — the lease is
// revoked and the station converges to safe idle without tails. That closes
// the gap a plain registered lease leaves open: a lease registered by a
// process that then dies would otherwise stay live, and a key it held would
// last until the TX timeout.

namespace Zeus.Server;

internal static class RemoteTxLeaseHold
{
    internal static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WriteDeadline = TimeSpan.FromSeconds(1);
    private static readonly byte[] HeartbeatLine = "{\"held\":true}\n"u8.ToArray();

    /// <summary>
    /// Runs the hold for <paramref name="leaseId"/> until the request ends.
    /// <paramref name="register"/> admits the lease (false = refused, 429);
    /// <paramref name="release"/> is ALWAYS invoked once the hold was admitted,
    /// however the request ended.
    /// </summary>
    internal static async Task RunAsync(
        HttpContext context,
        string leaseId,
        Func<string, bool> register,
        Func<string, bool> release,
        ILogger log,
        TimeSpan? heartbeatPeriod = null)
    {
        if (!register(leaseId))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var period = heartbeatPeriod ?? HeartbeatPeriod;
        log.LogInformation("tx.lease.hold attached");
        try
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/x-ndjson";
            context.Response.Headers.CacheControl = "no-store";
            var aborted = context.RequestAborted;
            while (!aborted.IsCancellationRequested)
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(aborted))
                {
                    deadline.CancelAfter(WriteDeadline);
                    await context.Response.Body.WriteAsync(HeartbeatLine, deadline.Token).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                await Task.Delay(period, aborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            var released = release(leaseId);
            log.LogInformation("tx.lease.hold detached released={Released}", released);
        }
    }
}
