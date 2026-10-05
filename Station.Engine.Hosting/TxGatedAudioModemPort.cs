// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

/// <summary>
/// TX-only view of the one shared audio modem. Receive <see cref="SyncMode"/>,
/// <see cref="ProcessRx"/>, and <see cref="FlushRx"/> stay on the inner port.
/// <see cref="Active"/> is true only when that modem is engaged and the TX
/// receiver's mode is FreeDV, so an SSB transmitter does not encode while
/// another receiver holds the modem.
/// </summary>
/// <remarks>
/// Realtime: no allocation, no blocking, no logging, and no exceptions.
/// The gate callback must be the same. <see cref="FlushTx"/> is the lease
/// reset after <see cref="FinishTx"/> (it clears the tail and the partial TX
/// block). It is forwarded while the gate is open, and once more after the
/// gate closes if this wrapper already forwarded <see cref="ProcessTx"/> or
/// <see cref="FinishTx"/>. An SSB unkey that never opened a FreeDV over does
/// not emit it while another receiver holds the modem.
/// <see cref="ProcessTx"/> that finds the gate closed clears the block and
/// returns. The caller checks <see cref="Active"/> first; the modem can drop
/// before the call, and the lease only clears the mic if this method reaches
/// it. That clear does not call the inner port and does not arm the reset.
/// </remarks>
public sealed class TxGatedAudioModemPort : IAudioModemPort
{
    private readonly IAudioModemPort _inner;
    private readonly Func<bool> _txReceiverIsFreeDv;
    // 1 after this wrapper has forwarded ProcessTx or FinishTx and the
    // matching FlushTx has not yet reached the inner port. The unkey thread
    // and the mic thread both call in; the flag is the only shared state.
    private int _txResetPending;

    public TxGatedAudioModemPort(IAudioModemPort inner, Func<bool> txReceiverIsFreeDv)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _txReceiverIsFreeDv = txReceiverIsFreeDv ?? throw new ArgumentNullException(nameof(txReceiverIsFreeDv));
    }

    internal IAudioModemPort Inner => _inner;

    public bool Available => _inner.Available;

    public bool Active => _inner.Active && _txReceiverIsFreeDv();

    public void SyncMode(byte rxModeByte) { }

    public void ProcessRx(Span<float> block48k) { }

    public void FlushRx() { }

    public void ProcessTx(Span<float> block48k)
    {
        if (!Active)
        {
            block48k.Clear();
            return;
        }

        Volatile.Write(ref _txResetPending, 1);
        _inner.ProcessTx(block48k);
    }

    public void FlushTx()
    {
        // Active unkey resets the over immediately. A mode change after
        // ProcessTx or FinishTx still owes that same reset: FinishTx leaves
        // the lease draining, and only FlushTx clears the tail and the
        // partial TX block. A closed gate with no forwarded TX is an SSB
        // unkey and must not start an over on the shared modem.
        if (Active)
        {
            Volatile.Write(ref _txResetPending, 0);
            _inner.FlushTx();
            return;
        }

        if (Interlocked.Exchange(ref _txResetPending, 0) == 1)
            _inner.FlushTx();
    }

    public int PendingTxSamples => Active ? _inner.PendingTxSamples : 0;

    public int FinishTx()
    {
        if (!Active) return 0;
        Volatile.Write(ref _txResetPending, 1);
        return _inner.FinishTx();
    }
}
