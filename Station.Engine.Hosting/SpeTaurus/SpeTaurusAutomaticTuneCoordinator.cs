// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// SPE Expert 1.5K Taurus amplifier support. This file is GPL-3.0-or-later
// (see Station.Engine.Hosting/SpeTaurus/SOURCE.md); the rest of the engine is
// GPL-2.0-or-later, whose "or later" option permits the combination. The
// resulting engine binary is distributed as GPL-3.0-or-later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.SpeTaurus;

/// <summary>
/// Owns the complete panel-initiated Taurus tuning cycle. The amplifier is
/// placed in STANDBY before its tuner is armed, and Zeus owns the RF carrier
/// only until the checksum-valid Taurus TUNE indication clears.
/// </summary>
internal sealed class SpeTaurusAutomaticTuneCoordinator(
    SpeTaurusService taurus,
    ExpertAmpServerControl amplifier,
    TuneCarrierCommandCoordinator carrier,
    TxService tx,
    ILogger<SpeTaurusAutomaticTuneCoordinator> log)
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OperateRestoreSettle = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _operation = new(1, 1);

    internal async Task<SpeTaurusStatus> TuneAsync(CancellationToken cancellationToken)
    {
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return await WithErrorAsync(
                "A Taurus automatic tuning cycle is already in progress.",
                cancellationToken).ConfigureAwait(false);

        var carrierStarted = false;
        var tuneCompleted = false;
        var restoreOperate = false;
        var restoreCommitted = false;
        SpeTaurusStatus? committedStatus = null;
        SpeTaurusConfig? expectedConfig = null;
        string? failure = null;
        try
        {
            expectedConfig = taurus.Config;
            if (expectedConfig.ExpertServerUrl.Length == 0)
                return await WithErrorAsync(
                    "Automatic carrier control requires the configured Expert Amp Server connection.",
                    cancellationToken).ConfigureAwait(false);
            if (tx.IsTunOn)
                return await WithErrorAsync(
                    "Zeus TUN is already active. Turn it off before starting Taurus automatic tuning.",
                    cancellationToken).ConfigureAwait(false);

            // STANDBY is a mandatory safety boundary. This is deliberately
            // idempotent when the amplifier is already in STANDBY.
            var standbyTransition = await amplifier
                .EnterStandbyForAutomaticTuneAsync(cancellationToken)
                .ConfigureAwait(false);
            var standby = standbyTransition.Status;
            restoreOperate = standbyTransition.WasOperate;
            log.LogInformation(
                "spe-taurus automatic tune captured starting mode: {StartingMode}",
                restoreOperate ? "OPERATE" : "STANDBY");
            if (standby.Error is not null)
                return standby;
            if (standby.Amplifier is not { IsTaurus: true, Operate: false, Transmitting: false })
                return standby with
                {
                    Error = "Taurus automatic tuning stopped because STANDBY could not be verified."
                };
            if (!ReferenceEquals(expectedConfig, taurus.Config))
                return standby with
                {
                    Error = "Taurus configuration changed before automatic tuning began."
                };

            // The normal Zeus TUN path owns amplifier preflight. For an active
            // Taurus feature that preflight sends exactly one TUNE keystroke,
            // waits for the yellow indication, revalidates STANDBY, and only
            // then permits TxService to key RF.
            var start = await carrier.SetAsync(true, tx, cancellationToken)
                .ConfigureAwait(false);
            if (!start.Success)
                return standby with
                {
                    Error = start.Error ?? "Zeus could not start the tuning carrier."
                };
            carrierStarted = true;

            using var completion = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            completion.CancelAfter(CompletionTimeout);
            await amplifier.WaitForTuneCompletionAsync(
                    expectedConfig,
                    () => tx.IsTunOn,
                    completion.Token)
                .ConfigureAwait(false);
            tuneCompleted = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failure = "Taurus automatic tuning timed out; Zeus stopped the tuning carrier.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            failure = $"Taurus automatic tuning stopped: {ex.Message}";
            log.LogWarning(ex, "spe-taurus automatic tune monitoring failed");
        }
        finally
        {
            try
            {
                if (carrierStarted)
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(CleanupTimeout);
                        var stop = await carrier.SetAsync(false, tx, cleanup.Token)
                            .ConfigureAwait(false);
                        if (!stop.Success)
                        {
                            failure = AppendFailure(
                                failure,
                                $"Zeus could not stop TUN: {stop.Error}");
                            log.LogError(
                                "spe-taurus automatic tune carrier cleanup failed: {Error}",
                                stop.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = AppendFailure(
                            failure,
                            $"Zeus TUN cleanup failed: {ex.Message}");
                        log.LogError(ex, "spe-taurus automatic tune carrier cleanup threw");
                    }

                    // SetAsync(false) is the normal serialized path. If it
                    // could not complete, bypass the coordinator as a
                    // last-resort fail-safe so an owned full-duty carrier is
                    // never left on.
                    try
                    {
                        if (tx.IsTunOn && !tx.TrySetTun(false, out var fallbackError))
                        {
                            failure = AppendFailure(
                                failure,
                                $"Emergency Zeus TUN shutdown failed: {fallbackError}");
                            log.LogCritical(
                                "spe-taurus emergency tune carrier shutdown failed: {Error}",
                                fallbackError);
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = AppendFailure(
                            failure,
                            $"Emergency Zeus TUN shutdown threw: {ex.Message}");
                        log.LogCritical(
                            ex,
                            "spe-taurus emergency tune carrier shutdown threw");
                    }

                    try
                    {
                        // This dedicated cleanup transaction is internally
                        // bounded and grants a fresh confirmation window after
                        // its one possible non-idempotent STANDBY toggle.
                        var safe = await amplifier.EnsureStandbyAfterAutomaticTuneAsync(
                                expectedConfig!,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!safe.Verified)
                            failure = AppendFailure(
                                failure,
                                safe.Error
                                ?? "Final Taurus STANDBY state could not be verified.");
                    }
                    catch (Exception ex)
                    {
                        failure = AppendFailure(
                            failure,
                            $"Final Taurus STANDBY verification failed: {ex.Message}");
                        log.LogError(ex, "spe-taurus automatic tune standby cleanup threw");
                    }

                    if (tuneCompleted
                        && restoreOperate
                        && failure is null
                        && !cancellationToken.IsCancellationRequested)
                    {
                        // The Expert Amp Server confirms only that it wrote the
                        // front-panel keystroke frame. The Taurus does not expose
                        // that command ACK through the HTTP bridge, and real
                        // hardware can ignore OPERATE while its ATU state machine
                        // is still returning home. Leave a full status-poll window
                        // after RF and TUNE have cleared before requesting OP.
                        log.LogInformation(
                            "spe-taurus automatic tune waiting {SettleMilliseconds} ms before OPERATE restoration",
                            OperateRestoreSettle.TotalMilliseconds);
                        await Task.Delay(OperateRestoreSettle, cancellationToken)
                            .ConfigureAwait(false);

                        if (!ReferenceEquals(expectedConfig, taurus.Config))
                        {
                            failure = AppendFailure(
                                failure,
                                "Taurus OPERATE was not restored because the configuration changed.");
                        }
                        else
                        {
                            string? restorationFailure = null;
                            var transmitIdle = tx.TryRunWithTransmitIdle(
                                () =>
                                {
                                    if (cancellationToken.IsCancellationRequested)
                                        return;

                                    try
                                    {
                                        var restored = amplifier
                                            .RestoreOperateAfterAutomaticTuneAsync(
                                                expectedConfig,
                                                cancellationToken)
                                            .GetAwaiter().GetResult();
                                        if (restored.Restored)
                                        {
                                            restoreCommitted = true;
                                            committedStatus = restored.Status;
                                            log.LogInformation(
                                                "spe-taurus automatic tune restored OPERATE after confirmed OP/RX");
                                            return;
                                        }

                                        restorationFailure = restored.Error;
                                    }
                                    catch (OperationCanceledException)
                                        when (cancellationToken.IsCancellationRequested)
                                    {
                                        // No OP commit occurred. The control
                                        // transaction compensates any write that
                                        // reached the amplifier before cancellation.
                                    }
                                    catch (Exception ex)
                                    {
                                        restorationFailure =
                                            $"Taurus OPERATE restoration failed: {ex.Message}";
                                        log.LogError(
                                            ex,
                                            "spe-taurus automatic tune operate restoration threw");
                                    }
                                },
                                out var transmitIdleError);

                            if (!transmitIdle)
                            {
                                failure = AppendFailure(
                                    failure,
                                    $"Taurus OPERATE was not restored: {transmitIdleError}");
                            }
                            else if (restorationFailure is not null)
                            {
                                failure = AppendFailure(failure, restorationFailure);
                            }
                        }
                    }
                }
            }
            finally
            {
                _operation.Release();
            }
        }

        if (cancellationToken.IsCancellationRequested && !restoreCommitted)
            cancellationToken.ThrowIfCancellationRequested();
        if (failure is null && committedStatus is not null)
            return committedStatus;
        return failure is null
            ? await amplifier.StatusAsync(cancellationToken).ConfigureAwait(false)
            : await WithErrorAsync(failure, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SpeTaurusStatus> WithErrorAsync(
        string error,
        CancellationToken cancellationToken)
    {
        var status = await amplifier.StatusAsync(cancellationToken).ConfigureAwait(false);
        return status with { Error = error };
    }

    private static string AppendFailure(string? current, string next) =>
        current is null ? next : $"{current} {next}";
}
