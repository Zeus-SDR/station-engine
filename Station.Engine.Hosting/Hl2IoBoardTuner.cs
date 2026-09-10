// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

/// <summary>Ownership checks and radio transitions must be atomic in the adapter.
/// TryStart must reject an existing transmission; StopOwned must never release
/// another source's transmission. These synchronous operations must not call back
/// into the tuner while holding a radio lock.</summary>
internal interface IHl2IoBoardTunerRadio
{
    bool IsTransmitting { get; }
    bool IsOwner { get; }
    bool TryStart(out string? error);
    void StopOwned();
}

/// <summary>HL2 IO-board register-7 handshake. Only an explicit start authorizes
/// an EE status to request RF. The deadline also runs independently of polling.
/// The caller supplies a cancellation-aware register writer, scoped to the
/// current connection, and calls Disconnect when that connection is lost.</summary>
internal sealed class Hl2IoBoardTuner : IDisposable
{
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(15);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly IHl2IoBoardTunerRadio _radio;
    private readonly Func<byte, CancellationToken, Task> _writeCommand;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _cycleCancellation;
    private CancellationTokenRegistration _cancellationRegistration;
    private ITimer? _deadline;
    private long _generation;
    private long? _pendingResetGeneration;
    private bool _active;
    private bool _commandAccepted;
    private bool _rfStarted;
    private bool _rfEnded;
    private bool _disposed;
    private string _state = "idle";
    private string? _error;

    public Hl2IoBoardTuner(IHl2IoBoardTunerRadio radio,
        Func<byte, CancellationToken, Task> writeCommand, TimeProvider? timeProvider = null)
    {
        _radio = radio;
        _writeCommand = writeCommand;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string State { get { lock (_sync) return _state; } }
    public string? Error { get { lock (_sync) return _error; } }
    public bool IsActive { get { lock (_sync) return _active; } }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        CancellationToken cycleToken;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_active) throw new InvalidOperationException("An IO-board tune is already active.");
            if (_radio.IsTransmitting) throw new InvalidOperationException("Unkey before starting an IO-board tune.");
            generation = ++_generation;
            _pendingResetGeneration = null;
            _active = true;
            _commandAccepted = false;
            _rfStarted = false;
            _rfEnded = false;
            _state = "waiting";
            _error = null;
            _cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cycleToken = _cycleCancellation.Token;
            _deadline = _time.CreateTimer(_ => EndCycle(generation, "timed-out", "IO-board tune exceeded 15 seconds."),
                null, MaximumDuration, Timeout.InfiniteTimeSpan);
            _cancellationRegistration = cycleToken.Register(() => EndCycle(generation, "cancelled", null));
        }
        try
        {
            await WriteAsync(1, cycleToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (_active && generation == _generation) _commandAccepted = true;
            }
        }
        catch (OperationCanceledException)
        {
            EndCycle(generation, "cancelled", null);
            throw;
        }
        catch (Exception ex)
        {
            EndCycle(generation, "failed", ex.Message);
            throw;
        }
    }

    public async Task AdvanceAsync(byte tunerStatus, CancellationToken cancellationToken = default)
    {
        bool reset = false;
        lock (_sync)
        {
            if (!_active || !_commandAccepted) reset = _pendingResetGeneration.HasValue;
            else if (cancellationToken.IsCancellationRequested)
            {
                FinishLocked("cancelled", null);
            }
            else if ((_rfStarted && !_rfEnded && !_radio.IsOwner) || (!_rfStarted && _radio.IsTransmitting))
            {
                FinishLocked("cancelled", "Transmit ownership changed during IO-board tuning.");
                reset = true;
            }
            else if (tunerStatus >= 0xF0)
            {
                FinishLocked("failed", $"IO-board tuner fault 0x{tunerStatus:X2}.");
                reset = true;
            }
            else if (tunerStatus == 0xEE)
            {
                if (!_rfStarted)
                {
                    try
                    {
                        if (_radio.TryStart(out var error))
                        {
                            _rfStarted = true;
                            _state = "tuning";
                        }
                        else
                        {
                            FinishLocked("failed", error ?? "Transmit request was rejected.");
                            reset = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        // An adapter may throw after partially starting RF.
                        _rfStarted = true;
                        FinishLocked("failed", ex.Message);
                        reset = true;
                    }
                }
            }
            else if (tunerStatus == 0)
            {
                FinishLocked("complete", null);
            }
            else if (_rfStarted && !_rfEnded)
            {
                // AH4 publishes 4 before RF and 7 while checking the final
                // result. Leaving EE ends RF, but success/failure follows later.
                _radio.StopOwned();
                _rfEnded = true;
                _state = "finishing";
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (reset) await WriteResetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        bool reset;
        lock (_sync)
        {
            reset = _active;
            if (_active) FinishLocked("cancelled", null);
        }
        // Unkey precedes transport cancellation or a slow I2C write.
        cancellationToken.ThrowIfCancellationRequested();
        if (reset) await WriteResetAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            if (_active) FinishLocked("disconnected", "IO-board connection was lost.");
            _pendingResetGeneration = null;
        }
    }

    private void EndCycle(long generation, string state, string? error)
    {
        lock (_sync)
        {
            if (_active && generation == _generation) FinishLocked(state, error);
        }
    }

    private void FinishLocked(string state, string? error)
    {
        _active = false;
        _pendingResetGeneration = _generation;
        _state = state;
        _error = error;
        _commandAccepted = false;
        _deadline?.Dispose();
        _deadline = null;
        _cancellationRegistration.Unregister();
        var cancellation = _cycleCancellation;
        _cycleCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (_rfStarted && !_rfEnded)
        {
            _rfStarted = false;
            try { _radio.StopOwned(); }
            catch (Exception ex) { _state = "failed"; _error = ex.Message; }
        }
    }

    private async Task WriteResetAsync(CancellationToken cancellationToken)
    {
        // Serialize with a cancelled start write, but never let an older reset
        // overwrite the request of a newly started cycle.
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long generation;
            lock (_sync)
            {
                if (_active || _disposed || _pendingResetGeneration is not { } pending) return;
                generation = pending;
            }
            await _writeCommand(0, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (_pendingResetGeneration == generation) _pendingResetGeneration = null;
            }
        }
        finally { _writes.Release(); }
    }

    private async Task WriteAsync(byte command, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _writeCommand(command, cancellationToken).ConfigureAwait(false); }
        finally { _writes.Release(); }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_active) FinishLocked("cancelled", null);
            _pendingResetGeneration = null;
        }
    }
}
