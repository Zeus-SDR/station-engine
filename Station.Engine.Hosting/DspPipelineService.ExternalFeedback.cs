// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

public partial class DspPipelineService
{
    private readonly ExternalFeedbackPort? _externalFeedbackPort;
    private int _externalFeedbackActive;
    internal Action? AfterExternalPsEnableForTest { get; set; }

    /// <summary>True while Zeus PureSignal corrects a product-operated transmitter.</summary>
    internal bool ExternalFeedbackActive => Volatile.Read(ref _externalFeedbackActive) != 0 &&
        !_radio.IsConnected && _radio.Snapshot().Status == ConnectionStatus.Disconnected;

    // A product receiver or feedback relay operates this station and the engine
    // has no radio of its own: an arm goes to the external feedback path.
    private bool IsExternalFeedbackRadioSessionCore =>
        _externalFeedbackPort?.HasExternalSession == true && !_radio.IsConnected &&
        _radio.Snapshot().Status == ConnectionStatus.Disconnected;

    /// <summary>
    /// PureSignal arm decisions currently belong to a product-operated radio
    /// (the external path would take an arm, or holds one), not to an HPSDR board.
    /// </summary>
    internal bool IsExternalFeedbackRadioSession => IsExternalFeedbackRadioSessionCore || ExternalFeedbackActive;

    // The external path feeds calcc at 48 kHz, where common two-tone pairs
    // repeat on whole samples; HPSDR boards keep the operator's frequencies.
    private double EffectiveTwoToneFreq2(double freq1Hz, double freq2Hz) =>
        IsExternalFeedbackRadioSession ? ExternalFeedbackTwoTone.SpreadUpperTone(freq1Hz, freq2Hz) : freq2Hz;

    /// <summary>The product-operated radio reports it is transmitting under an external arm.</summary>
    internal bool ExternalFeedbackTransmitting =>
        ExternalFeedbackActive && _externalFeedbackPort?.IsCorrectingTransmit == true;

    internal int ExternalFeedbackAttenuationDb => _externalFeedbackPort?.FeedbackAttenuationDb ?? 0;

    /// <summary>
    /// The external path's feedback attenuation is digital, inside the port.
    /// It uses the same state field as the HPSDR step attenuator; there is no
    /// connected HPSDR board, so nothing is written to a radio.
    /// </summary>
    private bool TrySetExternalFeedbackAttenuationDb(int db)
    {
        var port = _externalFeedbackPort;
        if (port is null || !IsExternalFeedbackRadioSession) return false;
        int clamped = Math.Clamp(db, 0, ExternalFeedbackPort.MaximumAttenuationDb);
        port.SetFeedbackAttenuationDb(clamped);
        _radio.SetPsTxAttenuationDbStateOnly(clamped);
        return true;
    }

    /// <summary>
    /// Queues an explicit two-tone key or release to the product-operated radio
    /// and returns at once with its eventual answer. Never waits for the radio,
    /// so TxService may call it while holding its transition lock. A release
    /// with no external radio attached has nothing to undo. <paramref name="armId"/>
    /// names the external arm the request went to (0 for none); that arm's end
    /// is reported through <see cref="ExternalFeedbackArmEnded"/>.
    /// </summary>
    internal virtual Task<ExternalTwoToneOutcome>? BeginExternalFeedbackTwoTone(bool enabled, out string? error,
        out long armId)
    {
        if (_externalFeedbackPort is not null) return _externalFeedbackPort.BeginTwoTone(enabled, out error, out armId);
        armId = 0;
        error = enabled ? "No external feedback radio is attached." : null;
        return enabled ? null : Task.FromResult(ExternalTwoToneOutcome.Ended);
    }

    /// <summary>
    /// Every external arm that ends, for any reason, with its arm ID and detach
    /// reason. Raised on a pool thread, never under the engine lock. No
    /// subscription without a port.
    /// </summary>
    internal event Action<long, string>? ExternalFeedbackArmEnded
    {
        add { if (_externalFeedbackPort is not null) _externalFeedbackPort.ArmEnded += value; }
        remove { if (_externalFeedbackPort is not null) _externalFeedbackPort.ArmEnded -= value; }
    }

    /// <summary>Auto-attenuate writes only the external runtime attenuation.</summary>
    internal void SetExternalFeedbackAttenuationFromServo(int db) => TrySetExternalFeedbackAttenuationDb(db);

    // Called under _engineLock by every primary-engine publish. An arm taken
    // on the outgoing engine is released and fails closed before the swap.
    private void ReleaseExternalFeedbackOnEngineReplaceLocked(IDspEngine replacement)
    {
        var outgoing = _engine;
        if (outgoing is not null && !ReferenceEquals(outgoing, replacement))
            _externalFeedbackPort?.ReleaseIfArmedOn(outgoing, "engine-replaced");
    }

    private IDspEngine? RebuildDisconnectedEngineForExternalFeedback(int txOutputRateHz)
    {
        if (_radio.IsConnected || _radio.IsProtocol3Active) return null;
        // Whatever arm is held is released before its engine can be disposed.
        _externalFeedbackPort?.Release("engine-rebuild");
        // A fresh engine otherwise opens TXA at the neutral 0.4072 seed. This
        // replacement is the only path that has the operator's peak and no
        // later per-board apply, so the open itself must use it.
        double operatorPeak = _radio.Snapshot().PsHwPeak;
        var replacement = CreateDisconnectedEngine(out int channelId, txOutputRateHz, operatorPeak);
        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            PublishPrimaryEngineLocked(replacement, channelId);
            ResetSecondaryRxChannels();
            Volatile.Write(ref _sampleRateHz, SyntheticSampleRateHz);
        }
        TeardownEngine(old, oldChannel);
        _psResyncRequired = true;
        _appliedTxMonitorEnabled = false;
        RaiseEngineChanged(replacement);
        return replacement;
    }

    private async Task<bool> RunExternalFeedbackArmTransitionAsync(bool enable, CancellationToken stop)
    {
        var port = _externalFeedbackPort;
        if (!enable)
        {
            if (port is null)
            {
                _radio.FailClosedPureSignalRuntime();
                return false;
            }
            // Release the arm actually held, whichever engine it was taken on.
            port.Release("operator-disarm");
            _radio.ClearExternalFeedbackRuntime();
            var current = CurrentEngine;
            lock (_engineLock)
                if (ReferenceEquals(current, _engine))
                    current?.SetPsEnabled(false);
            DrainPsFeedback();
            _ = RebuildDisconnectedEngineForExternalFeedback(OfflinePreviewTxOutputRateHz);
            Interlocked.Exchange(ref _externalFeedbackActive, 0);
            return true;
        }

        var state = _radio.Snapshot();
        // A request queued behind a fail-close must not re-arm: only the
        // operator's still-current arm proceeds.
        if (!state.PsEnabled) return false;
        var receiver = _externalReceiverPort;
        if (port is null || receiver is null || !receiver.TryGetContext(0, out var context) ||
            context.Mode is not (RxMode.USB or RxMode.LSB) || !port.CanAttemptCorrection)
        {
            port?.Release("arm-unavailable");
            Interlocked.Exchange(ref _externalFeedbackActive, 0);
            _radio.FailClosedPureSignalRuntime();
            return false;
        }
        var mode = context.Mode;

        // A resync or a repeated state push re-requests the arm already held.
        // It is a no-op: never rebuild under a live arm, and never re-enable,
        // because SetPsEnabled(true) restarts the calibration.
        if (ExternalFeedbackActive && port.IsArmedFor(CurrentEngine, mode)) return true;

        port.Release("rearm");
        Interlocked.Exchange(ref _externalFeedbackActive, 0);
        var engine = RebuildDisconnectedEngineForExternalFeedback(ExternalFeedbackSampleRate.TxHz);
        if (engine is null || engine.TxOutputSamples <= 0)
        {
            _ = RebuildDisconnectedEngineForExternalFeedback(OfflinePreviewTxOutputRateHz);
            _radio.FailClosedPureSignalRuntime();
            return false;
        }
        engine.SetTxMode(mode);
        engine.SetPsHwPeak(state.PsHwPeak);
        engine.SetPsAdvanced(state.PsMoxDelaySec, state.PsLoopDelaySec, state.PsAmpDelayNs, state.PsHwPeak);
        engine.SetPsControl(state.PsAuto, state.PsSingle);
        _radio.SetPsTxAttenuationDbStateOnly(port.FeedbackAttenuationDb);
        bool armed;
        try { armed = await port.ArmAsync(engine, mode, stop).ConfigureAwait(false); }
        catch
        {
            port.Release("arm-failed");
            _ = RebuildDisconnectedEngineForExternalFeedback(OfflinePreviewTxOutputRateHz);
            _radio.FailClosedPureSignalRuntime();
            throw;
        }
        if (!armed || !_radio.Snapshot().PsEnabled || !port.IsArmedFor(engine, mode))
        {
            port.Release("arm-refused");
            _ = RebuildDisconnectedEngineForExternalFeedback(OfflinePreviewTxOutputRateHz);
            _radio.FailClosedPureSignalRuntime();
            return false;
        }
        bool engineReplaced;
        Exception? enableFailure = null;
        lock (_engineLock)
        {
            engineReplaced = !ReferenceEquals(engine, _engine);
            if (!engineReplaced)
            {
                try
                {
                    engine.SetPsEnabled(true);
                    port.OnPsEnabled(engine);
                    AfterExternalPsEnableForTest?.Invoke();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { enableFailure = exception; }
            }
        }
        if (engineReplaced || enableFailure is not null)
        {
            port.Release(engineReplaced ? "engine-replaced" : "engine-enable-failed");
            _ = RebuildDisconnectedEngineForExternalFeedback(OfflinePreviewTxOutputRateHz);
            _radio.FailClosedPureSignalRuntime();
            if (enableFailure is not null)
                throw new InvalidOperationException(
                    "External PureSignal ended because the engine could not be enabled.", enableFailure);
            return false;
        }
        Interlocked.Exchange(ref _externalFeedbackActive, 1);
        return true;
    }
}
