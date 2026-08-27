// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

public enum VnaMeasurementKind
{
    Raw = 0,
    Transmission = 1,
    Reflection = 2,
}

public enum VnaCalibrationStandard
{
    Thru = 0,
    Open = 1,
    Short = 2,
    Load = 3,
}

public sealed record VnaCapabilityDto(
    bool Available,
    bool NativeVector,
    string Board,
    string Source,
    string Reason,
    bool RequiresExternalBridge,
    bool RequiresCalibration,
    int MaximumPoints);

public sealed record VnaSweepRequest(
    string Antenna,
    string Band,
    long StartHz,
    long EndHz,
    int Points = 501,
    VnaMeasurementKind Kind = VnaMeasurementKind.Reflection,
    string? CalibrationId = null,
    string? Label = null,
    int DriveLevel = 8,
    bool FixedRxGainHigh = false);

public sealed record VnaCalibrationCaptureRequest(
    string Name,
    string Antenna,
    string Band,
    long StartHz,
    long EndHz,
    int Points,
    VnaCalibrationStandard Standard,
    string? CalibrationId = null,
    int DriveLevel = 8,
    bool FixedRxGainHigh = false);

public sealed record VnaComplexSample(long FrequencyHz, double Real, double Imaginary);

public sealed record VnaCaptureResult(
    IReadOnlyList<VnaComplexSample> Samples,
    bool ReflectionCalibrated,
    bool Vector);

public sealed record VnaPointDto(
    long FrequencyHz,
    double RawReal,
    double RawImaginary,
    double MagnitudeDb,
    double PhaseDeg,
    double? Swr,
    double? ReturnLossDb,
    double? ResistanceOhms,
    double? ReactanceOhms);

public sealed record VnaSweepMetricsDto(
    long ResonantFrequencyHz,
    double? MinimumSwr,
    double? MaximumReturnLossDb,
    double? ResistanceAtResonanceOhms,
    double? ReactanceAtResonanceOhms,
    long? Bandwidth15Hz,
    long? Bandwidth20Hz,
    long? Bandwidth30Hz,
    double? EstimatedQ);

public sealed record VnaSweepDto(
    string Id,
    DateTimeOffset CapturedUtc,
    string RadioKey,
    string Board,
    string Antenna,
    string Band,
    string Label,
    long StartHz,
    long EndHz,
    int PointCount,
    VnaMeasurementKind Kind,
    string? CalibrationId,
    bool Calibrated,
    VnaSweepMetricsDto Metrics,
    IReadOnlyList<VnaPointDto> Points);

public sealed record VnaCalibrationDto(
    string Id,
    string Name,
    DateTimeOffset UpdatedUtc,
    string RadioKey,
    string Antenna,
    string Band,
    long StartHz,
    long EndHz,
    int PointCount,
    bool HasThru,
    bool HasOpen,
    bool HasShort,
    bool HasLoad,
    bool ReflectionReady,
    bool TransmissionReady);

public sealed record VnaStatusDto(
    bool Running,
    string? Phase,
    int PointsReceived,
    int PointsExpected,
    string? ActiveAntenna,
    string? ActiveBand,
    string? Error);
