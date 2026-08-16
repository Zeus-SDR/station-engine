// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server.Tdoa;

public static class TdoaLimits
{
    public const int MinStations = 3;
    public const int MaxStations = 6;
    public const int MinComplexSamplesPerStation = 4_096;
    public const int MaxComplexSamplesPerStation = 131_072;
    public const int MaxTotalComplexSamples = 393_216;
    public const long MaxHttpBodyBytes = 6L * 1024 * 1024;
}

public sealed record TdoaSolveRequest(
    double CenterFrequencyHz,
    string? PropagationModel,
    IReadOnlyList<TdoaStationCaptureRequest>? Stations);

public sealed record TdoaStationCaptureRequest(
    string? Id,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeMeters,
    string? ReferenceTimeTaiNanoseconds,
    double SampleRateHz,
    double GroupDelayNanoseconds,
    double ClockUncertaintyNanoseconds,
    bool ClockLocked,
    string? IqBase64);

public sealed record TdoaSolveResponse(
    TdoaEstimate Estimate,
    double QualityScore,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TdoaPairResult> Pairs,
    IReadOnlyList<TdoaMode> Modes,
    IReadOnlyList<TdoaHeatmapPoint> Heatmap,
    TdoaDiagnostics Diagnostics);

public sealed record TdoaEstimate(
    double LatitudeDeg,
    double LongitudeDeg,
    TdoaUncertaintyEllipse UncertaintyEllipse,
    double UncertaintyRadiusKm);

public sealed record TdoaUncertaintyEllipse(
    double SemiMajorKm,
    double SemiMinorKm,
    double BearingDeg);

public sealed record TdoaPairResult(
    string StationAId,
    string StationBId,
    double DelayNanoseconds,
    double LagSamples,
    double DifferentialCfoHz,
    double PeakToSidelobeRatio,
    double Coherence,
    double UncertaintyNanoseconds,
    double QualityScore,
    bool Usable,
    IReadOnlyList<string> Warnings);

public sealed record TdoaMode(
    double LatitudeDeg,
    double LongitudeDeg,
    double RelativeLikelihood,
    double ResidualNanoseconds);

public sealed record TdoaHeatmapPoint(double LatitudeDeg, double LongitudeDeg, double Score);

public sealed record TdoaDiagnostics(
    double? ClosureRmsNanoseconds,
    double ResidualRmsNanoseconds,
    double GeometryCondition,
    int UsablePairCount,
    int TotalPairCount,
    string TimingModel);

public sealed class TdoaValidationException(string message) : Exception(message);

public sealed class TdoaBusyException(string message) : Exception(message);
