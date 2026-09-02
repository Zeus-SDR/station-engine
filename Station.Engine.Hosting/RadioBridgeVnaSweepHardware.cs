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
    private const double AnalyzerTargetWatts = 5.0;
    private static readonly TimeSpan FrequencySettle = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(2);
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly TxMetersService _meters;
    private readonly PaSettingsStore _paSettings;
    private readonly Channel<(ushort Fwd, ushort Ref)> _samples = Channel.CreateBounded<(ushort, ushort)>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true, SingleWriter = false });

    public RadioBridgeVnaSweepHardware(
        RadioService radio,
        TxService tx,
        TxMetersService meters,
        PaSettingsStore paSettings)
    {
        _radio = radio;
        _tx = tx;
        _meters = meters;
        _paSettings = paSettings;
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

        _ = driveLevel;
        StateDto before = _radio.Snapshot();
        int paMaxWatts = _paSettings.GetGlobal(
            _radio.ConnectedBoardKind, _radio.EffectiveOrionMkIIVariant).PaMaxPowerWatts;
        int requestedTune = TunePercentForTargetWatts(AnalyzerTargetWatts, paMaxWatts);
        if (!_radio.SetTuneDriveIfCurrent(requestedTune, before.TunePct))
            throw new InvalidOperationException("The radio changed state before analyzer drive could be applied.");

        long expectedVfo = before.VfoHz;
        try
        {
            DrainSamples();
            long[] frequencies = Enumerable.Range(0, points)
                .Select(i => startHz + (long)Math.Round(
                    (endHz - startHz) * i / (double)(points - 1)))
                .ToArray();
            IReadOnlyList<VnaComplexSample> result = await CaptureContinuouslyKeyedAsync(
                frequencies,
                frequencyHz =>
                {
                    if (_radio.Snapshot().VfoHz != expectedVfo)
                        throw new InvalidOperationException("The radio frequency changed during the antenna sweep.");
                    DrainSamples();
                    _radio.SetVfo(frequencyHz, fromExternal: true);
                    expectedVfo = frequencyHz;
                },
                () =>
                {
                    bool success = _tx.TrySetTun(true, MoxSource.Analyzer, out string? error);
                    return (success, error);
                },
                () => _tx.TrySetTun(false, MoxSource.Analyzer, out _),
                async (frequencyHz, token) =>
                {
                    if (!_tx.IsTunOn)
                        throw new InvalidOperationException("The radio unkeyed during the antenna sweep.");
                    // Do not let valid bridge samples from the previous keyed
                    // frequency satisfy this point after an NCO transition.
                    DrainSamples();
                    var calibration = RadioCalibrations.For(
                        _radio.ConnectedBoardKind, _radio.EffectiveOrionMkIIVariant);
                    bool sixMeters = BandUtils.FreqToBand(frequencyHz) == "6m";
                    // G2 sends a burst of hi-priority status packets at the
                    // single TX edge. Wait for four consecutive RF-valid pairs
                    // there, and after every keyed frequency step, rather than
                    // averaging stale bridge ADCs into a false 0 W result.
                    var (fwd, rev) = await ReadSettledAverageAsync(
                        _samples.Reader, calibration, sixMeters, frequencyHz,
                        SampleTimeout, token).ConfigureAwait(false);
                    var (_, _, swr) = TxMetersService.ComputeMeters(fwd, rev,
                        calibration, sixMeters);
                    double gammaMagnitude = Math.Clamp(
                        (swr - 1.0) / (swr + 1.0), 0.0, 0.999999);
                    return new VnaComplexSample(frequencyHz, gammaMagnitude, 0);
                },
                FrequencySettle,
                progress,
                cancellationToken).ConfigureAwait(false);
            return new VnaCaptureResult(result, ReflectionCalibrated: true, Vector: false);
        }
        finally
        {
            if (_radio.Snapshot().VfoHz == expectedVfo)
                _radio.SetVfo(before.VfoHz, fromExternal: true);
            _radio.SetTuneDriveIfCurrent(before.TunePct, requestedTune);
            DrainSamples();
        }
    }

    internal static int TunePercentForTargetWatts(double targetWatts, int paMaxWatts)
    {
        int effectiveMaxWatts = paMaxWatts > 0 ? paMaxWatts : 100;
        double percent = Math.Max(0, targetWatts) * 100.0 / effectiveMaxWatts;
        return Math.Clamp((int)Math.Round(percent, MidpointRounding.AwayFromZero), 1, 100);
    }

    internal static async Task<IReadOnlyList<VnaComplexSample>> CaptureContinuouslyKeyedAsync(
        IReadOnlyList<long> frequencies,
        Action<long> setFrequency,
        Func<(bool Success, string? Error)> keyTune,
        Action unkeyTune,
        Func<long, CancellationToken, Task<VnaComplexSample>> capturePoint,
        TimeSpan frequencySettle,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var result = new List<VnaComplexSample>(frequencies.Count);
        bool keyed = false;
        try
        {
            foreach (long frequencyHz in frequencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                setFrequency(frequencyHz);
                if (!keyed)
                {
                    var (success, error) = keyTune();
                    if (!success)
                        throw new InvalidOperationException(
                            error ?? "The radio rejected the analyzer TUNE request.");
                    keyed = true;
                }
                await Task.Delay(frequencySettle, cancellationToken).ConfigureAwait(false);
                result.Add(await capturePoint(frequencyHz, cancellationToken).ConfigureAwait(false));
                progress?.Report(result.Count);
            }
            return result;
        }
        finally
        {
            if (keyed) unkeyTune();
        }
    }

    internal static async Task<(double Fwd, double Ref)> ReadSettledAverageAsync(
        ChannelReader<(ushort Fwd, ushort Ref)> reader,
        RadioCalibration calibration,
        bool sixMeters,
        long frequencyHz,
        TimeSpan acquisitionTimeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(acquisitionTimeout);
        double highestWatts = 0;
        double fwd = 0;
        double reverse = 0;
        int accepted = 0;
        try
        {
            while (accepted < 4)
            {
                var pair = await reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
                var (watts, _, _) = TxMetersService.ComputeMeters(
                    pair.Fwd, pair.Ref, calibration, sixMeters);
                highestWatts = Math.Max(highestWatts, watts);
                if (watts <= 2.0)
                {
                    accepted = 0;
                    fwd = 0;
                    reverse = 0;
                    continue;
                }
                fwd += pair.Fwd;
                reverse += pair.Ref;
                accepted++;
            }
            return (fwd / accepted, reverse / accepted);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Forward power did not rise above 2 W at {frequencyHz / 1_000_000.0:F3} MHz " +
                $"within {acquisitionTimeout.TotalSeconds:F1} seconds (highest observed {highestWatts:F1} W). " +
                "Check the selected antenna, TUNE power, and radio PA enable.");
        }
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
