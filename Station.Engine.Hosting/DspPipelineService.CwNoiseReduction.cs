// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

public partial class DspPipelineService
{
    private bool _cwNoiseReductionDisposed;

    // Protocol 3 receives and processes audio in the sidecar, outside the local CW tap.
    private bool CwNoiseReductionBypassForReceiver(StateDto state, int receiver) =>
        !Volatile.Read(ref _cwNoiseReductionDisposed) &&
        !_radio.IsProtocol3Active &&
        CwNoiseReductionBypassRequested(state, receiver, _hub.CwDecodeRequestedOn(receiver));

    internal static bool CwNoiseReductionBypassRequested(StateDto state, int receiver, bool requested)
    {
        if (!requested || receiver < 0 || receiver >= MaxReceivers) return false;
        if (receiver == 0) return state.Mode is RxMode.CWU or RxMode.CWL;
        var row = state.Receivers?.FirstOrDefault(rx => rx.Index == receiver);
        return row is { Enabled: true, Name: null, Mode: RxMode.CWU or RxMode.CWL };
    }

    // Called under _engineLock: demand may have changed while the replacement
    // channel was configured. Reconcile before publishing it to audio readers.
    private void PublishPrimaryEngineLocked(IDspEngine engine, int channel)
    {
        engine.SetRxCwNoiseReductionBypass(channel,
            CwNoiseReductionBypassForReceiver(_radio.Snapshot(), 0));
        Volatile.Write(ref _engine, engine);
        Volatile.Write(ref _channelId, channel);
    }

    private void OnCwNoiseReductionDemandChanged()
    {
        lock (_engineLock)
        {
            var engine = _engine;
            if (engine is null || _cwNoiseReductionDisposed) return;
            ApplyCwNoiseReductionDemandLocked(engine);
        }
    }

    // Native close/open and guest rebuilding must not hold the operator control
    // lock. Reconcile after releasing _guestLock, which also avoids lock inversion.
    private void ReconcileCwNoiseReductionAfterRerate(IDspEngine engine)
    {
        lock (_engineLock)
        {
            if (!ReferenceEquals(engine, _engine) || _cwNoiseReductionDisposed) return;
            ApplyCwNoiseReductionDemandLocked(engine);
        }
    }

    private void ApplyCwNoiseReductionDemandLocked(IDspEngine engine)
    {
        var state = _radio.Snapshot();
        if (_channelId >= 0)
            engine.SetRxCwNoiseReductionBypass(_channelId,
                CwNoiseReductionBypassForReceiver(state, 0));
        for (int receiver = 1; receiver < MaxReceivers; receiver++)
        {
            int channel = _secondaryRx[receiver].ChannelId;
            if (channel >= 0)
                engine.SetRxCwNoiseReductionBypass(channel,
                    CwNoiseReductionBypassForReceiver(state, receiver));
        }
    }

    private void ClearCwNoiseReductionBypass()
    {
        lock (_engineLock)
        {
            _cwNoiseReductionDisposed = true;
            var engine = _engine;
            if (engine is null) return;
            if (_channelId >= 0) engine.SetRxCwNoiseReductionBypass(_channelId, false);
            for (int receiver = 1; receiver < MaxReceivers; receiver++)
            {
                int channel = _secondaryRx[receiver].ChannelId;
                if (channel >= 0) engine.SetRxCwNoiseReductionBypass(channel, false);
            }
        }
    }
}
