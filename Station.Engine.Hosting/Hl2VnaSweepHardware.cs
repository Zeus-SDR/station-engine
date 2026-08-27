// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Runs the FPGA-scanned VNA mode on the operator's currently connected HL2.
/// Normal radio state is never rewritten: Protocol1Client overlays the VNA
/// register values atomically and ClearVna restores the ordinary snapshots.
/// </summary>
public sealed class Hl2VnaSweepHardware : IVnaSweepHardware, IDisposable
{
    private const int MaximumPoints = 4096;
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(20);
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _dsp;
    private readonly ILogger<Hl2VnaSweepHardware> _log;
    private readonly object _sync = new();
    private Collector? _collector;

    public Hl2VnaSweepHardware(
        RadioService radio,
        TxService tx,
        DspPipelineService dsp,
        ILogger<Hl2VnaSweepHardware> log)
    {
        _radio = radio;
        _tx = tx;
        _dsp = dsp;
        _log = log;
        _dsp.RxIqAvailable += OnRxIq;
    }

    public VnaCapabilityDto Capability
    {
        get
        {
            HpsdrBoardKind board = _radio.ConnectedBoardKind;
            if (!_radio.IsConnected)
                return Unavailable(board, "Connect a Hermes-Lite 2 to run a native vector sweep.");
            if (board != HpsdrBoardKind.HermesLite2 || _radio.ActiveClient is null)
                return Unavailable(board,
                    "The connected radio does not expose a phase-coherent VNA stream. Saved sweeps remain available.");
            return new VnaCapabilityDto(
                true, true, board.ToString(), "hl2-fpga-vna",
                "Ready to sweep with the connected Hermes-Lite 2.",
                RequiresExternalBridge: true, RequiresCalibration: true, MaximumPoints);
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
        VnaCapabilityDto capability = Capability;
        if (!capability.Available) throw new InvalidOperationException(capability.Reason);
        if (_tx.IsMoxOn || _tx.IsTunOn)
            throw new InvalidOperationException("Unkey MOX and TUNE before starting an antenna sweep.");
        if (startHz is < 0 or > uint.MaxValue || endHz is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(startHz), "HL2 VNA frequencies must fit the 32-bit NCO.");
        if (points is < 3 or > MaximumPoints)
            throw new ArgumentOutOfRangeException(nameof(points), $"HL2 VNA point count must be 3..{MaximumPoints}.");
        if (!_tx.ValidateAnalyzerSweep(startHz, endHz, points, out string? validationError))
            throw new InvalidOperationException(validationError);

        long span = endHz - startHz;
        long step = Math.Max(1, (long)Math.Round(span / (double)(points - 1)));
        if (step > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(endHz), "HL2 VNA step is too large.");
        var client = _radio.ActiveClient
            ?? throw new InvalidOperationException("The Protocol-1 radio disconnected before the sweep started.");
        var collector = new Collector(startHz, step, points, progress);
        lock (_sync)
        {
            if (_collector is not null) throw new InvalidOperationException("A VNA capture is already active.");
            _collector = collector;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CaptureTimeout);
        try
        {
            client.ConfigureVna((uint)startHz, (uint)step, (ushort)points,
                fixedRxGainHigh, (byte)Math.Clamp(driveLevel, 0, 255));
            var samples = await collector.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return new VnaCaptureResult(samples, ReflectionCalibrated: false, Vector: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for the HL2 VNA separator and {points} sweep points.");
        }
        finally
        {
            // Clear first so no additional VNA-keyed frame can be produced
            // after this method releases the collector or returns to the UI.
            try { client.ClearVna(); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to clear HL2 VNA state after capture"); }
            lock (_sync)
            {
                if (ReferenceEquals(_collector, collector)) _collector = null;
            }
        }
    }

    private void OnRxIq(int receiverIndex, int sampleRateHz, ReadOnlyMemory<double> samples)
    {
        if (receiverIndex != 0) return;
        Collector? collector;
        lock (_sync) collector = _collector;
        collector?.Accept(samples.Span);
    }

    private static VnaCapabilityDto Unavailable(HpsdrBoardKind board, string reason) =>
        new(false, false, board.ToString(), "unavailable", reason,
            RequiresExternalBridge: true, RequiresCalibration: true, MaximumPoints);

    public void Dispose()
    {
        _dsp.RxIqAvailable -= OnRxIq;
        lock (_sync)
        {
            _collector?.Completion.TrySetCanceled();
            _collector = null;
        }
    }

    private sealed class Collector
    {
        private const double SeparatorEpsilon = 1e-15;
        private readonly long _startHz;
        private readonly long _stepHz;
        private readonly int _expected;
        private readonly IProgress<int>? _progress;
        private readonly List<VnaComplexSample> _points;
        private readonly object _sync = new();
        private bool _separatorSeen;

        public Collector(long startHz, long stepHz, int expected, IProgress<int>? progress)
        {
            _startHz = startHz;
            _stepHz = stepHz;
            _expected = expected;
            _progress = progress;
            _points = new List<VnaComplexSample>(expected);
        }

        public TaskCompletionSource<IReadOnlyList<VnaComplexSample>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Accept(ReadOnlySpan<double> interleaved)
        {
            lock (_sync)
            {
                if (Completion.Task.IsCompleted) return;
                for (int i = 0; i + 1 < interleaved.Length; i += 2)
                {
                    double real = interleaved[i];
                    double imaginary = interleaved[i + 1];
                    if (!_separatorSeen)
                    {
                        if (Math.Abs(real) <= SeparatorEpsilon && Math.Abs(imaginary) <= SeparatorEpsilon)
                            _separatorSeen = true;
                        continue;
                    }

                    int index = _points.Count;
                    _points.Add(new VnaComplexSample(_startHz + index * _stepHz, real, imaginary));
                    _progress?.Report(_points.Count);
                    if (_points.Count == _expected)
                    {
                        Completion.TrySetResult(_points.ToArray());
                        return;
                    }
                }
            }
        }
    }
}
