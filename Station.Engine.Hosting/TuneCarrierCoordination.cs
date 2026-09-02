// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

internal readonly record struct AmplifierTunePreflightResult(
    bool Ready,
    string? Error = null,
    Func<bool>? StillReady = null)
{
    internal static AmplifierTunePreflightResult Success(
        Func<bool>? stillReady = null,
        string? readinessError = null) => new(true, readinessError, stillReady);
    internal static AmplifierTunePreflightResult Fail(string error) => new(false, error);
}

/// <summary>
/// Optional amplifier-specific work that must complete before Zeus is allowed
/// to generate a tune carrier. Implementations must be inert unless explicitly
/// enabled by the operator and must honor cancellation promptly.
/// </summary>
internal interface IAmplifierTunePreflight
{
    Task<AmplifierTunePreflightResult> PrepareAsync(CancellationToken cancellationToken);
}

internal readonly record struct TuneCarrierCommandResult(
    bool Success,
    bool TunOn,
    bool ExternalFailure,
    string? Error = null);

/// <summary>
/// Serializes the HTTP TUN command surface around asynchronous amplifier
/// preflight. An OFF request cancels a pending ON before it can key RF.
/// TxService remains the sole authority for the audited radio transition.
/// </summary>
internal sealed class TuneCarrierCommandCoordinator(
    IEnumerable<IAmplifierTunePreflight> amplifierPreflights)
{
    private readonly IReadOnlyList<IAmplifierTunePreflight> _amplifierPreflights =
        amplifierPreflights.ToArray();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _pendingOn;
    private long _generation;

    internal async Task<TuneCarrierCommandResult> SetAsync(
        bool on,
        TxService tx,
        CancellationToken cancellationToken,
        string? remoteTxLeaseId = null)
    {
        CancellationTokenSource? onCancellation = null;
        long generation;
        lock (_stateGate)
        {
            generation = ++_generation;
            _pendingOn?.Cancel();
            if (on)
            {
                onCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _pendingOn = onCancellation;
            }
        }

        var commandToken = onCancellation?.Token ?? cancellationToken;
        try
        {
            await _transition.WaitAsync(commandToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (on && !cancellationToken.IsCancellationRequested)
        {
            return new(false, tx.IsTunOn, false, "Tune request was superseded before RF was keyed.");
        }

        try
        {
            if (!on)
                return ApplyTxState(false, tx, remoteTxLeaseId);

            // Repeated ON is idempotent. In particular, never turn an already
            // armed amplifier tuner back off by sending its momentary button a
            // second time.
            if (tx.IsTunOn)
                return new(true, true, false);

            var completed = new List<AmplifierTunePreflightResult>(_amplifierPreflights.Count);
            foreach (var preflight in _amplifierPreflights)
            {
                var result = await preflight.PrepareAsync(commandToken).ConfigureAwait(false);
                if (!result.Ready)
                    return new(false, tx.IsTunOn, true,
                        result.Error ?? "The amplifier did not become ready for tuning.");
                completed.Add(result);
            }

            lock (_stateGate)
            {
                if (generation != _generation || commandToken.IsCancellationRequested)
                    return new(false, tx.IsTunOn, false,
                        "Tune request was superseded before RF was keyed.");

                // Revalidate asynchronous amplifier evidence in the same
                // final critical section as RF keying.
                foreach (var result in completed)
                {
                    try
                    {
                        if (result.StillReady?.Invoke() == false)
                            return new(false, tx.IsTunOn, true,
                                result.Error ?? "Amplifier readiness changed before RF was keyed.");
                    }
                    catch (Exception ex)
                    {
                        return new(false, tx.IsTunOn, true,
                            $"Amplifier readiness could not be revalidated: {ex.Message}");
                    }
                }

                return ApplyTxState(true, tx, remoteTxLeaseId);
            }
        }
        catch (OperationCanceledException) when (on && !cancellationToken.IsCancellationRequested)
        {
            return new(false, tx.IsTunOn, false, "Tune request was superseded before RF was keyed.");
        }
        finally
        {
            _transition.Release();
            if (onCancellation is not null)
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_pendingOn, onCancellation)) _pendingOn = null;
                }
                onCancellation.Dispose();
            }
        }
    }

    private static TuneCarrierCommandResult ApplyTxState(
        bool on,
        TxService tx,
        string? remoteTxLeaseId)
    {
        var success = remoteTxLeaseId is null
            ? tx.TrySetTun(on, out var error)
            : tx.TrySetRemoteTun(on, remoteTxLeaseId, out error);
        if (!success)
            return new(false, tx.IsTunOn, false, error);
        return new(true, tx.IsTunOn, false);
    }
}
