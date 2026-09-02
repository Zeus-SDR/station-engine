// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using Zeus.Contracts;

namespace Zeus.Server;

public interface IVnaSweepHardware
{
    VnaCapabilityDto Capability { get; }
    Task<VnaCaptureResult> CaptureAsync(
        long startHz,
        long endHz,
        int points,
        int driveLevel,
        bool fixedRxGainHigh,
        IProgress<int>? progress,
        CancellationToken cancellationToken);
}

public sealed class VnaService
{
    private const int MinimumPoints = 3;
    private readonly IVnaSweepHardware _hardware;
    private readonly VnaHardwareRouter _router;
    private readonly VnaSweepStore _store;
    private readonly RadioService _radio;
    private readonly ILogger<VnaService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private VnaStatusDto _status = new(false, null, 0, 0, null, null, null);

    public VnaService(
        IVnaSweepHardware hardware,
        VnaHardwareRouter router,
        VnaSweepStore store,
        RadioService radio,
        ILogger<VnaService> log)
    {
        _hardware = hardware;
        _router = router;
        _store = store;
        _radio = radio;
        _log = log;
    }

    public VnaCapabilityDto Capability() => _hardware.Capability;
    public VnaSourceStatusDto SourceStatus() => _router.SourceStatus();
    public Task<VnaSourceStatusDto> SelectSourceAsync(
        VnaSourceSelectionRequest request, CancellationToken cancellationToken)
    {
        if (Status().Running)
            throw new InvalidOperationException("Cancel the active sweep before changing measurement source.");
        return _router.SelectAsync(request.Source, request.DeviceId, cancellationToken);
    }
    public VnaStatusDto Status() { lock (_sync) return _status; }
    public IReadOnlyList<VnaSweepDto> Sweeps() => _store.GetSweeps();
    public IReadOnlyList<VnaCalibrationDto> Calibrations() => _store.GetCalibrations();
    public bool DeleteSweep(string id) => _store.DeleteSweep(id);
    public bool DeleteCalibration(string id) => _store.DeleteCalibration(id);

    public void Cancel()
    {
        lock (_sync) _activeCancellation?.Cancel();
    }

    public async Task<VnaSweepDto> SweepAsync(VnaSweepRequest request, CancellationToken cancellationToken)
    {
        Validate(request.StartHz, request.EndHz, request.Points, request.Antenna, request.Band);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Begin("sweep", request.Points, request.Antenna, request.Band, linked);
            var progress = new Progress<int>(count => UpdateProgress(count));
            VnaCaptureResult capture = await _hardware.CaptureAsync(
                request.StartHz, request.EndHz, request.Points,
                Math.Clamp(request.DriveLevel, 0, 255), request.FixedRxGainHigh,
                progress, linked.Token).ConfigureAwait(false);
            IReadOnlyList<VnaComplexSample> raw = capture.Samples;
            if (raw.Count != request.Points)
                throw new InvalidOperationException($"Analyzer returned {raw.Count} VNA points; expected {request.Points}.");

            var (points, calibrated) = BuildPoints(raw, request.Kind, request.CalibrationId,
                capture.ReflectionCalibrated, capture.Vector);
            string label = string.IsNullOrWhiteSpace(request.Label)
                ? $"{request.Band} {request.Antenna} {DateTimeOffset.Now:g}"
                : request.Label.Trim();
            string board = _hardware.Capability.Board;
            string radioKey = RadioKey();
            var result = new VnaSweepDto(
                Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, radioKey, board,
                request.Antenna.Trim(), request.Band.Trim(), label,
                raw[0].FrequencyHz, raw[^1].FrequencyHz, raw.Count, request.Kind,
                request.CalibrationId, calibrated, VnaMath.Metrics(points), points);
            _store.Save(result);
            Complete();
            return result;
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
        finally
        {
            lock (_sync) _activeCancellation = null;
            _gate.Release();
        }
    }

    public async Task<VnaCalibrationDto> CaptureCalibrationAsync(
        VnaCalibrationCaptureRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request.StartHz, request.EndHz, request.Points, request.Antenna, request.Band);
        if (!_hardware.Capability.NativeVector)
            throw new InvalidOperationException(
                "Open/short/load calibration requires a radio with phase-coherent vector capture.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Begin($"calibration-{request.Standard.ToString().ToLowerInvariant()}", request.Points,
                request.Antenna, request.Band, linked);
            VnaCaptureResult capture = await _hardware.CaptureAsync(
                request.StartHz, request.EndHz, request.Points,
                Math.Clamp(request.DriveLevel, 0, 255), request.FixedRxGainHigh,
                new Progress<int>(UpdateProgress), linked.Token).ConfigureAwait(false);
            IReadOnlyList<VnaComplexSample> raw = capture.Samples;
            if (raw.Count != request.Points)
                throw new InvalidOperationException($"Analyzer returned {raw.Count} calibration points; expected {request.Points}.");

            string id = string.IsNullOrWhiteSpace(request.CalibrationId)
                ? Guid.NewGuid().ToString("N")
                : request.CalibrationId;
            var result = _store.SaveCalibrationStandard(
                id, request.Name.Trim(), RadioKey(), request.Antenna.Trim(), request.Band.Trim(),
                raw[0].FrequencyHz, raw[^1].FrequencyHz, raw, request.Standard);
            Complete();
            return result;
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
        finally
        {
            lock (_sync) _activeCancellation = null;
            _gate.Release();
        }
    }

    private (IReadOnlyList<VnaPointDto> Points, bool Calibrated) BuildPoints(
        IReadOnlyList<VnaComplexSample> raw,
        VnaMeasurementKind kind,
        string? calibrationId,
        bool hardwareReflectionCalibrated,
        bool hardwareVector)
    {
        VnaCalibrationEntry? calibration = string.IsNullOrWhiteSpace(calibrationId)
            ? null
            : _store.GetCalibrationEntry(calibrationId);
        if (calibration is not null)
        {
            if (calibration.RadioKey != RadioKey())
                throw new InvalidOperationException("The selected calibration belongs to a different radio.");
            if (calibration.PointCount != raw.Count
                || calibration.StartHz != raw[0].FrequencyHz
                || calibration.EndHz != raw[^1].FrequencyHz)
                throw new InvalidOperationException("Calibration span and point count must match the sweep exactly.");
        }

        bool oslReady = kind == VnaMeasurementKind.Reflection && calibration is not null
            && calibration.Open.Count == raw.Count && calibration.Short.Count == raw.Count
            && calibration.Load.Count == raw.Count;
        bool reflectionReady = kind == VnaMeasurementKind.Reflection
            && (hardwareReflectionCalibrated || oslReady);
        bool transmissionReady = kind == VnaMeasurementKind.Transmission && calibration is not null
            && calibration.Thru.Count == raw.Count;
        var points = new VnaPointDto[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            Complex measured = new(raw[i].Real, raw[i].Imaginary);
            Complex value = measured;
            if (oslReady)
            {
                value = VnaMath.ApplyOsl(measured,
                    AsComplex(calibration!.Open[i]), AsComplex(calibration.Short[i]),
                    AsComplex(calibration.Load[i]));
            }
            else if (transmissionReady)
            {
                value = VnaMath.ApplyThru(measured, AsComplex(calibration!.Thru[i]));
            }
            points[i] = VnaMath.ToPoint(raw[i], value, reflectionReady,
                includeImpedance: reflectionReady && hardwareVector);
        }
        return (points, reflectionReady || transmissionReady);
    }

    private static Complex AsComplex(VnaStoredComplex value) => new(value.Real, value.Imaginary);

    private void Validate(long startHz, long endHz, int points, string antenna, string band)
    {
        VnaCapabilityDto capability = _hardware.Capability;
        if (!capability.Available) throw new InvalidOperationException(capability.Reason);
        if (startHz <= 0 || endHz <= startHz) throw new ArgumentException("Sweep end must be above sweep start.");
        if (points < MinimumPoints || points > capability.MaximumPoints)
            throw new ArgumentOutOfRangeException(nameof(points), $"Points must be {MinimumPoints}..{capability.MaximumPoints}.");
        if (string.IsNullOrWhiteSpace(antenna)) throw new ArgumentException("Antenna label is required.");
        if (string.IsNullOrWhiteSpace(band)) throw new ArgumentException("Band is required.");
    }

    private string RadioKey()
    {
        VnaCapabilityDto capability = _hardware.Capability;
        if (string.Equals(capability.Source, "nanovna", StringComparison.OrdinalIgnoreCase))
            return $"nanovna:{capability.Board}";
        StateDto state = _radio.Snapshot();
        return $"p1:{_radio.ConnectedBoardKind}:{state.Endpoint ?? "unknown"}";
    }

    private void Begin(string phase, int expected, string antenna, string band, CancellationTokenSource cts)
    {
        lock (_sync)
        {
            _activeCancellation = cts;
            _status = new(true, phase, 0, expected, antenna, band, null);
        }
    }

    private void UpdateProgress(int count)
    {
        lock (_sync) _status = _status with { PointsReceived = Math.Clamp(count, 0, _status.PointsExpected) };
    }

    private void Complete()
    {
        lock (_sync) _status = _status with { Running = false, Phase = "complete", Error = null,
            PointsReceived = _status.PointsExpected };
    }

    private void Fail(Exception ex)
    {
        string message = ex is OperationCanceledException ? "Sweep cancelled." : ex.Message;
        lock (_sync) _status = _status with { Running = false, Phase = "failed", Error = message };
        if (ex is not OperationCanceledException) _log.LogWarning(ex, "VNA operation failed");
    }
}
