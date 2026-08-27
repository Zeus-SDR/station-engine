// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading.Channels;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Universal scalar analyzer path. It uses the connected radio's own
/// forward/reverse bridge under bounded low-power TUNE, so every calibrated
/// P1/P2 transmitter can report SWR, return loss, resonance and bandwidth.
/// Bridge power has no phase, therefore this path never claims R+jX.
/// </summary>
public sealed class RadioBridgeVnaSweepHardware : IVnaSweepHardware, IDisposable
{
    private const int MaximumPoints = 201;
    private const int MaximumTunePercent = 25;
    private static readonly TimeSpan RelaySettle = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(2);
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly TxMetersService _meters;
    private readonly Channel<(ushort Fwd, ushort Ref)> _samples = Channel.CreateBounded<(ushort, ushort)>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true, SingleWriter = false });

    public RadioBridgeVnaSweepHardware(RadioService radio, TxService tx, TxMetersService meters)
    {
        _radio = radio;
        _tx = tx;
        _meters = meters;
        _meters.RawPowerTelemetryUpdated += OnRawPower;
    }

    public VnaCapabilityDto Capability
    {
        get
        {
            HpsdrBoardKind board = _radio.ConnectedBoardKind;
            if (!_radio.IsConnected)
                return Unavailable(board, "Connect a supported radio to run an SWR sweep.");
            if (_radio.IsProtocol3Active)
                return Unavailable(board,
                    "This Protocol 3 bridge scale is not yet validated for analyzer measurements. Saved sweeps remain available.");
            return new VnaCapabilityDto(true, false, board.ToString(), "radio-forward-reverse-bridge",
                "Ready for a scalar SWR sweep with the connected radio. Complex impedance requires vector hardware.",
                RequiresExternalBridge: false, RequiresCalibration: false, MaximumPoints);
        }
    }

    public async Task<VnaCaptureResult> CaptureAsync(
        long startHz,
        long endHz,
        int points,
        int driveLevel,
        bool fixedRxGainHigh,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        _ = fixedRxGainHigh;
        VnaCapabilityDto capability = Capability;
        if (!capability.Available) throw new InvalidOperationException(capability.Reason);
        if (_tx.IsMoxOn || _tx.IsTunOn)
            throw new InvalidOperationException("Unkey MOX and TUNE before starting an antenna sweep.");
        if (points is < 3 or > MaximumPoints)
            throw new ArgumentOutOfRangeException(nameof(points), $"Scalar sweep points must be 3..{MaximumPoints}.");
        if (!_tx.ValidateAnalyzerSweep(startHz, endHz, points, out string? validationError))
            throw new InvalidOperationException(validationError);

        StateDto before = _radio.Snapshot();
        int requestedTune = Math.Clamp(driveLevel, 1, MaximumTunePercent);
        if (!_radio.SetTuneDriveIfCurrent(requestedTune, before.TunePct))
            throw new InvalidOperationException("The radio changed state before analyzer drive could be applied.");

        var result = new List<VnaComplexSample>(points);
        bool keyed = false;
        long expectedVfo = before.VfoHz;
        try
        {
            DrainSamples();
            for (int i = 0; i < points; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long frequencyHz = startHz + (long)Math.Round((endHz - startHz) * i / (double)(points - 1));
                if (_radio.Snapshot().VfoHz != expectedVfo)
                    throw new InvalidOperationException("The radio frequency changed during the antenna sweep.");
                DrainSamples();
                _radio.SetVfo(frequencyHz, fromExternal: true);
                expectedVfo = frequencyHz;
                if (!_tx.TrySetTun(true, MoxSource.Analyzer, out string? keyError))
                    throw new InvalidOperationException(keyError ?? "The radio rejected the analyzer TUNE request.");
                keyed = true;
                await Task.Delay(RelaySettle, cancellationToken).ConfigureAwait(false);

                // Average four unsmoothed bridge pairs after each retune. This
                // avoids TxMetersService's display EMA mixing adjacent points.
                double fwd = 0;
                double rev = 0;
                for (int sample = 0; sample < 4; sample++)
                {
                    var pair = await ReadSampleAsync(cancellationToken).ConfigureAwait(false);
                    fwd += pair.Fwd;
                    rev += pair.Ref;
                }
                var calibration = RadioCalibrations.For(
                    _radio.ConnectedBoardKind, _radio.EffectiveOrionMkIIVariant);
                bool sixMeters = BandUtils.FreqToBand(frequencyHz) == "6m";
                var (fwdWatts, _, swr) = TxMetersService.ComputeMeters(fwd / 4.0, rev / 4.0,
                    calibration, sixMeters);
                if (fwdWatts <= 2.0)
                    throw new InvalidOperationException(
                        $"Forward power was only {fwdWatts:F1} W at {frequencyHz / 1_000_000.0:F3} MHz; " +
                        "the radio bridge cannot measure SWR reliably below 2 W.");
                double gammaMagnitude = Math.Clamp((swr - 1.0) / (swr + 1.0), 0.0, 0.999999);
                result.Add(new VnaComplexSample(frequencyHz, gammaMagnitude, 0));
                progress?.Report(result.Count);
                _tx.TrySetTun(false, MoxSource.Analyzer, out _);
                keyed = false;
            }
            return new VnaCaptureResult(result, ReflectionCalibrated: true, Vector: false);
        }
        finally
        {
            if (keyed) _tx.TrySetTun(false, MoxSource.Analyzer, out _);
            if (_radio.Snapshot().VfoHz == expectedVfo)
                _radio.SetVfo(before.VfoHz, fromExternal: true);
            _radio.SetTuneDriveIfCurrent(before.TunePct, requestedTune);
            DrainSamples();
        }
    }

    private async ValueTask<(ushort Fwd, ushort Ref)> ReadSampleAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SampleTimeout);
        try { return await _samples.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The connected radio stopped reporting forward/reverse power."); }
    }

    private void DrainSamples()
    {
        while (_samples.Reader.TryRead(out _)) { }
    }

    private void OnRawPower(ushort fwd, ushort reverse) => _samples.Writer.TryWrite((fwd, reverse));

    private static VnaCapabilityDto Unavailable(HpsdrBoardKind board, string reason) =>
        new(false, false, board.ToString(), "unavailable", reason,
            RequiresExternalBridge: false, RequiresCalibration: false, MaximumPoints);

    public void Dispose()
    {
        _meters.RawPowerTelemetryUpdated -= OnRawPower;
        _samples.Writer.TryComplete();
    }
}
