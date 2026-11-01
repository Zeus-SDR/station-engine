// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Dsp.Wdsp;

public sealed partial class WdspDspEngine : ITxWaveformSource
{
    private TxWaveformHandler? _waveformTap;
    private long _waveformDemandUntil;
    private readonly object _waveformSubscribersGate = new();
    private TxWaveformHandler[] _waveformSubscribers = [];

    public void SetTxWaveformTap(TxWaveformHandler? handler, long demandUntilTickMs)
    {
        Volatile.Write(ref _waveformTap, handler);
        Volatile.Write(ref _waveformDemandUntil, demandUntilTickMs);
    }

    public IDisposable SubscribeTxWaveform(TxWaveformHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_waveformSubscribersGate)
            Volatile.Write(ref _waveformSubscribers, [.. _waveformSubscribers, handler]);
        return new WaveformSubscription(this, handler);
    }

    private void RemoveTxWaveformSubscriber(TxWaveformHandler handler)
    {
        lock (_waveformSubscribersGate)
        {
            int index = Array.FindIndex(_waveformSubscribers,
                candidate => ReferenceEquals(candidate, handler));
            if (index < 0) return;
            var replacement = new TxWaveformHandler[_waveformSubscribers.Length - 1];
            Array.Copy(_waveformSubscribers, 0, replacement, 0, index);
            Array.Copy(_waveformSubscribers, index + 1, replacement, index,
                _waveformSubscribers.Length - index - 1);
            Volatile.Write(ref _waveformSubscribers, replacement);
        }
    }

    private sealed class WaveformSubscription(WdspDspEngine owner, TxWaveformHandler handler) : IDisposable
    {
        private WdspDspEngine? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RemoveTxWaveformSubscriber(handler);
    }
}
