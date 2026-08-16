// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server.Tdoa;

public sealed class TdoaSolver
{
    private const double LightSpeedMetersPerSecond = 299_792_458.0;
    private const int MaxSearchExpansions = 4;
    private readonly SemaphoreSlim _solveGate = new(1, 1);
    private int _activeAsyncSolves;

    internal bool IsSolveInProgress => Volatile.Read(ref _activeAsyncSolves) != 0;

    public async Task<TdoaSolveResponse> SolveAsync(TdoaSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _solveGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new TdoaBusyException("A TDoA solve is already in progress; retry after it completes.");
        try
        {
            Interlocked.Increment(ref _activeAsyncSolves);
            // Keep the request thread responsive while the one admitted CPU-bound solve runs.
            return await Task.Run(() => Solve(request, cancellationToken), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeAsyncSolves);
            _solveGate.Release();
        }
    }

    public TdoaSolveResponse Solve(TdoaSolveRequest request, CancellationToken cancellationToken = default)
    {
        var stations = TdoaCaptureValidator.Validate(request, cancellationToken);
        ValidateGeometry(stations);
        var analyses = new List<PairAnalysis>();
        for (int i = 0; i < stations.Count; i++)
            for (int j = i + 1; j < stations.Count; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                analyses.Add(TdoaPairAnalyzer.Analyze(stations[i], stations[j], cancellationToken));
            }
        if (analyses.Count(p => p.Result.Usable) < 3)
            throw new TdoaValidationException("Fewer than three station pairs have usable coherent timing evidence.");

        IReadOnlyList<PairAnalysis> solvePairs = analyses.Where(pair => pair.Result.Usable).ToArray();
        SearchResult search = SearchAdaptive(stations, solvePairs, cancellationToken);
        string? rejectedStation = null;
        if (stations.Count >= 5)
        {
            double fullResidual = NormalizedResidual(search.Best, solvePairs);
            (string Station, double Residual, SearchResult Search, PairAnalysis[] Pairs)? strongest = null;
            foreach (ValidatedTdoaCapture station in stations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PairAnalysis[] subset = solvePairs.Where(pair => pair.A.Id != station.Id && pair.B.Id != station.Id)
                    .ToArray();
                if (subset.Length < 3) continue;
                SearchResult candidateSearch = SearchAdaptive(stations.Where(value => value.Id != station.Id).ToArray(),
                    subset, cancellationToken);
                double residual = NormalizedResidual(candidateSearch.Best, subset);
                if (strongest is null || residual < strongest.Value.Residual)
                    strongest = (station.Id, residual, candidateSearch, subset);
            }
            if (strongest is { } robust
                && fullResidual > 1.25
                && robust.Residual < fullResidual * 0.65
                && robust.Residual < 3)
            {
                rejectedStation = robust.Station;
                solvePairs = robust.Pairs;
                search = robust.Search;
            }
        }

        SearchBounds bounds = search.Bounds;
        List<Candidate> candidates = search.Candidates;
        List<Candidate> refined = search.Refined;
        Candidate best = search.Best;
        double latStep = search.LatitudeStep;
        double lonStep = search.LongitudeStep;

        var ellipse = EstimateUncertainty(best, solvePairs, out double geometryCondition);
        double? closureRmsNs = ComputeClosureRms(stations, analyses);
        var warnings = new List<string>
        {
            "Groundwave-only estimate: ionospheric/skywave paths and unmodelled receiver delays can create convincing false modes.",
            "The uncertainty ellipse is a heuristic geometry/timing scale, not a calibrated confidence interval."
        };
        if (search.Expansions > 0)
            warnings.Add($"Geographic search expanded outward {search.Expansions} time(s) because a supported mode reached the initial receiver bounds.");
        if (search.Clipped)
            warnings.Add("Best mode remains at the maximum bounded search edge; reported quality is strongly suppressed because the transmitter may lie outside the reported heatmap.");
        if (rejectedStation is not null)
            warnings.Add($"Station '{rejectedStation}' was rejected by leave-one-station-out consensus because its clock/path evidence was inconsistent with the remaining network.");
        if (geometryCondition > 100) warnings.Add("Station geometry is ill-conditioned; the heuristic uncertainty region is elongated and optimistic outside the search area.");
        double closureReferenceNs = analyses.Where(p => p.Result.Usable).Average(p => p.Result.UncertaintyNanoseconds);
        if (closureRmsNs is { } closure && closure > closureReferenceNs * 2)
            warnings.Add("Station-cycle delay closure exceeds the declared timing uncertainty; clock or path bias is likely.");
        if (closureRmsNs is null)
            warnings.Add("No complete usable station triplet is available; station-cycle delay closure cannot be evaluated.");
        if (refined.Count > 1 && Math.Exp(-0.5 * (refined[1].Score - best.Score)) > 0.35)
            warnings.Add("Multiple geographic modes have material likelihood.");
        warnings.AddRange(analyses.SelectMany(a => a.Result.Warnings.Select(w => $"{a.A.Id}/{a.B.Id}: {w}")));

        double residualNs = best.ResidualNanoseconds;
        double pairQuality = solvePairs.Average(p => p.Result.QualityScore);
        double medianUncertainty = solvePairs.Select(p => p.Result.UncertaintyNanoseconds).Order()
            .ElementAt(solvePairs.Count / 2);
        double quality = Math.Clamp(pairQuality * Math.Exp(-0.5 * Math.Pow(residualNs / Math.Max(medianUncertainty * 2, 1), 2))
            / Math.Sqrt(Math.Max(1, geometryCondition / 4)), 0, 1);
        if (closureRmsNs is { } closureValue)
            quality /= Math.Sqrt(1 + Math.Pow(closureValue / Math.Max(closureReferenceNs * 2, 1), 2));
        double? leaveOneOutMeters = ComputeLeaveOneOutStability(stations, solvePairs, best, cancellationToken);
        if (leaveOneOutMeters is { } displacement)
        {
            double stabilityScaleMeters = Math.Max(1_000, ellipse.SemiMajorKm * 2_000);
            quality /= Math.Sqrt(1 + Math.Pow(displacement / stabilityScaleMeters, 2));
            if (displacement > stabilityScaleMeters * 2)
                warnings.Add("Leave-one-station-out positions are unstable; reported quality has been reduced.");
        }
        if (rejectedStation is not null) quality *= 0.75;
        if (search.Clipped) quality *= 0.05;
        quality = Math.Clamp(quality, 0, 1);
        double radius = Math.Sqrt(ellipse.SemiMajorKm * ellipse.SemiMinorKm);
        var estimate = new TdoaEstimate(best.Latitude, best.Longitude, ellipse, radius);
        var modes = refined.Select(c => new TdoaMode(c.Latitude, c.Longitude,
            Math.Exp(-0.5 * Math.Min(100, c.Score - best.Score)), c.ResidualNanoseconds)).ToArray();
        var heatmap = candidates.Take(120).Select(c => new TdoaHeatmapPoint(c.Latitude, c.Longitude,
            Math.Exp(-0.5 * Math.Min(100, c.Score - best.Score)))).ToArray();
        var diagnostics = new TdoaDiagnostics(closureRmsNs, residualNs, geometryCondition,
            solvePairs.Count, analyses.Count,
            "GNSS/TAI sample-domain timestamps with locked sample clocks required; measured station rates are windowed-sinc normalized to a common grid and host/network arrival timestamps are rejected. Geographic uncertainty is heuristic, not a calibrated confidence level.");
        TdoaPairResult[] outputPairs = analyses.Select(pair => rejectedStation is not null
                && (pair.A.Id == rejectedStation || pair.B.Id == rejectedStation)
            ? pair.Result with
            {
                Usable = false,
                Warnings = pair.Result.Warnings.Append(
                    $"Excluded because station '{rejectedStation}' failed leave-one-station-out consensus.").ToArray(),
            }
            : pair.Result).ToArray();
        return new TdoaSolveResponse(estimate, quality, warnings.Distinct().ToArray(),
            outputPairs, modes, heatmap, diagnostics);
    }

    private static Candidate Evaluate(double latitude, double longitude, IReadOnlyList<PairAnalysis> pairs)
    {
        double score = 0, residualSquares = 0, weightSum = 0;
        foreach (var pair in pairs.Where(p => p.Result.Usable))
        {
            double distanceA = TdoaGeodesy.SurfaceDistanceMeters(latitude, longitude, pair.A.LatitudeDeg, pair.A.LongitudeDeg);
            double distanceB = TdoaGeodesy.SurfaceDistanceMeters(latitude, longitude, pair.B.LatitudeDeg, pair.B.LongitudeDeg);
            double predictedNs = (distanceB - distanceA) / LightSpeedMetersPerSecond * 1e9;
            double likelihood = pair.LikelihoodAt(predictedNs);
            double nearestPeakNs = pair.Peaks.Count == 0
                ? pair.Result.DelayNanoseconds
                : pair.Peaks.MinBy(peak => Math.Abs(predictedNs - peak.DelayNanoseconds))!.DelayNanoseconds;
            double residual = predictedNs - nearestPeakNs;
            double weight = Math.Max(0.05, pair.Result.QualityScore);
            // The geographic objective is driven only by the full pair-delay likelihood.
            // Residuals are diagnostics against the nearest retained compatible delay mode.
            double negativeLogLikelihood = -2 * Math.Log(Math.Max(likelihood, 1e-12));
            score += weight * RobustLoss(negativeLogLikelihood);
            residualSquares += weight * residual * residual;
            weightSum += weight;
        }
        return new Candidate(latitude, TdoaGeodesy.WrapLongitude(longitude), score,
            Math.Sqrt(residualSquares / Math.Max(weightSum, 1e-12)));
    }

    private static double RobustLoss(double squaredResidualLike)
    {
        const double huberThreshold = 3;
        double residualLike = Math.Sqrt(Math.Max(0, squaredResidualLike));
        return residualLike <= huberThreshold
            ? squaredResidualLike
            : 2 * huberThreshold * residualLike - huberThreshold * huberThreshold;
    }

    private static double NormalizedResidual(Candidate candidate, IReadOnlyList<PairAnalysis> pairs)
    {
        double sum = 0, weight = 0;
        foreach (PairAnalysis pair in pairs)
        {
            double predicted = PredictedDelay(candidate.Latitude, candidate.Longitude, pair);
            double peak = pair.Peaks.Count == 0
                ? pair.Result.DelayNanoseconds
                : pair.Peaks.MinBy(value => Math.Abs(predicted - value.DelayNanoseconds))!.DelayNanoseconds;
            double normalized = (predicted - peak) / Math.Max(pair.Result.UncertaintyNanoseconds, 1);
            double pairWeight = Math.Max(0.05, pair.Result.QualityScore);
            sum += pairWeight * normalized * normalized;
            weight += pairWeight;
        }
        return Math.Sqrt(sum / Math.Max(weight, 1e-12));
    }

    private static SearchResult SearchAdaptive(IReadOnlyList<ValidatedTdoaCapture> stations,
        IReadOnlyList<PairAnalysis> pairs, CancellationToken token)
    {
        SearchBounds bounds = SearchBounds.Create(stations);
        SearchResult result = default!;
        for (int expansion = 0; expansion <= MaxSearchExpansions; expansion++)
        {
            result = SearchOnce(bounds, pairs, token) with { Expansions = expansion };
            if (!result.Clipped) return result;
            if (expansion < MaxSearchExpansions) bounds = bounds.Expand(2.5);
        }
        return result;
    }

    private static SearchResult SearchOnce(SearchBounds bounds, IReadOnlyList<PairAnalysis> pairs,
        CancellationToken token)
    {
        const int gridSize = 51;
        var candidates = new List<Candidate>(gridSize * gridSize);
        var grid = new Candidate[gridSize, gridSize];
        for (int y = 0; y < gridSize; y++)
        {
            token.ThrowIfCancellationRequested();
            double lat = bounds.MinLatitude + (bounds.MaxLatitude - bounds.MinLatitude) * y / (gridSize - 1);
            for (int x = 0; x < gridSize; x++)
            {
                double lon = bounds.MinLongitude + (bounds.MaxLongitude - bounds.MinLongitude) * x / (gridSize - 1);
                Candidate candidate = Evaluate(lat, TdoaGeodesy.WrapLongitude(lon), pairs);
                grid[y, x] = candidate;
                candidates.Add(candidate);
            }
        }
        candidates.Sort((left, right) => left.Score.CompareTo(right.Score));
        double latStep = (bounds.MaxLatitude - bounds.MinLatitude) / (gridSize - 1);
        double lonStep = (bounds.MaxLongitude - bounds.MinLongitude) / (gridSize - 1);
        var refined = new List<Candidate>();
        foreach (Candidate seed in SelectGeographicModeSeeds(grid, candidates, bounds))
        {
            Candidate candidate = Refine(seed, latStep, lonStep, pairs, token);
            if (refined.All(existing => TdoaGeodesy.SurfaceDistanceMeters(existing.Latitude, existing.Longitude,
                    candidate.Latitude, candidate.Longitude) > Math.Max(1_000, bounds.SpanMeters / 100)))
                refined.Add(candidate);
            if (refined.Count == 5) break;
        }
        refined.Sort((left, right) => left.Score.CompareTo(right.Score));
        Candidate best = refined[0];
        bool clipped = best.Latitude <= bounds.MinLatitude + latStep
            || best.Latitude >= bounds.MaxLatitude - latStep
            || IsNearLongitudeBoundary(best.Longitude, bounds, lonStep);
        return new SearchResult(bounds, candidates, refined, best, latStep, lonStep, clipped, 0);
    }

    private static double? ComputeLeaveOneOutStability(IReadOnlyList<ValidatedTdoaCapture> stations,
        IReadOnlyList<PairAnalysis> pairs, Candidate reference, CancellationToken token)
    {
        string[] activeIds = pairs.SelectMany(pair => new[] { pair.A.Id, pair.B.Id })
            .Distinct(StringComparer.Ordinal).ToArray();
        if (activeIds.Length < 5) return null;
        var displacements = new List<double>();
        foreach (string id in activeIds)
        {
            PairAnalysis[] subset = pairs.Where(pair => pair.A.Id != id && pair.B.Id != id).ToArray();
            ValidatedTdoaCapture[] subsetStations = stations.Where(station => station.Id != id
                && activeIds.Contains(station.Id, StringComparer.Ordinal)).ToArray();
            if (subsetStations.Length < 3 || subset.Length < 3) continue;
            SearchResult solution = SearchAdaptive(subsetStations, subset, token);
            displacements.Add(TdoaGeodesy.SurfaceDistanceMeters(reference.Latitude, reference.Longitude,
                solution.Best.Latitude, solution.Best.Longitude));
        }
        if (displacements.Count == 0) return null;
        displacements.Sort();
        return displacements[displacements.Count / 2];
    }

    private static IReadOnlyList<Candidate> SelectGeographicModeSeeds(Candidate[,] grid,
        IReadOnlyList<Candidate> sortedCandidates, SearchBounds bounds)
    {
        int height = grid.GetLength(0), width = grid.GetLength(1);
        var localMinima = new List<Candidate>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Candidate candidate = grid[y, x];
                bool local = true;
                for (int dy = -1; dy <= 1 && local; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int otherY = y + dy, otherX = x + dx;
                        if (otherY >= 0 && otherY < height && otherX >= 0 && otherX < width
                            && grid[otherY, otherX].Score < candidate.Score)
                        {
                            local = false;
                            break;
                        }
                    }
                if (local) localMinima.Add(candidate);
            }

        localMinima.Sort((left, right) => left.Score.CompareTo(right.Score));
        double minimumSeparationMeters = Math.Max(1_000, bounds.SpanMeters / 20);
        var seeds = new List<Candidate>();
        void AddSeparated(IEnumerable<Candidate> source)
        {
            foreach (Candidate candidate in source)
            {
                if (seeds.All(existing => TdoaGeodesy.SurfaceDistanceMeters(existing.Latitude, existing.Longitude,
                        candidate.Latitude, candidate.Longitude) >= minimumSeparationMeters))
                    seeds.Add(candidate);
                if (seeds.Count == 12) break;
            }
        }

        AddSeparated(localMinima);
        if (seeds.Count < 12) AddSeparated(sortedCandidates);
        return seeds;
    }

    private static Candidate Refine(Candidate seed, double latStep, double lonStep,
        IReadOnlyList<PairAnalysis> pairs, CancellationToken token)
    {
        Candidate best = seed;
        double dLat = latStep, dLon = lonStep;
        for (int iteration = 0; iteration < 18; iteration++)
        {
            token.ThrowIfCancellationRequested();
            Candidate next = best;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    double lat = Math.Clamp(best.Latitude + y * dLat, -89.999999, 89.999999);
                    Candidate candidate = Evaluate(lat, best.Longitude + x * dLon, pairs);
                    if (candidate.Score < next.Score) next = candidate;
                }
            best = next;
            dLat *= 0.55;
            dLon *= 0.55;
        }
        return best;
    }

    private static TdoaUncertaintyEllipse EstimateUncertainty(Candidate best, IReadOnlyList<PairAnalysis> pairs,
        out double condition)
    {
        const double stepMeters = 50;
        double latStep = stepMeters / 111_320.0;
        double lonStep = stepMeters / (111_320.0 * Math.Max(0.05, Math.Cos(TdoaGeodesy.DegreesToRadians(best.Latitude))));
        double fEe = 0, fEn = 0, fNn = 0;
        foreach (var pair in pairs.Where(p => p.Result.Usable))
        {
            double eastPlus = PredictedDelay(best.Latitude, best.Longitude + lonStep, pair);
            double eastMinus = PredictedDelay(best.Latitude, best.Longitude - lonStep, pair);
            double northPlus = PredictedDelay(best.Latitude + latStep, best.Longitude, pair);
            double northMinus = PredictedDelay(best.Latitude - latStep, best.Longitude, pair);
            double jE = (eastPlus - eastMinus) / (2 * stepMeters);
            double jN = (northPlus - northMinus) / (2 * stepMeters);
            double weight = Math.Max(0.05, pair.Result.QualityScore) / Math.Pow(Math.Max(pair.Result.UncertaintyNanoseconds, 1), 2);
            fEe += weight * jE * jE;
            fEn += weight * jE * jN;
            fNn += weight * jN * jN;
        }
        double trace = fEe + fNn;
        double discriminant = Math.Sqrt(Math.Max(0, (fEe - fNn) * (fEe - fNn) + 4 * fEn * fEn));
        double lambdaMax = Math.Max((trace + discriminant) / 2, 1e-18);
        double lambdaMin = Math.Max((trace - discriminant) / 2, 1e-18);
        condition = lambdaMax / lambdaMin;
        // Unit information-matrix scale only. This deliberately does not apply a chi-square
        // quantile: the pair likelihoods are heuristic and do not justify coverage claims.
        double semiMajorMeters = Math.Sqrt(1 / lambdaMin);
        double semiMinorMeters = Math.Sqrt(1 / lambdaMax);
        semiMajorMeters = Math.Clamp(semiMajorMeters, 10, 20_000_000);
        semiMinorMeters = Math.Clamp(semiMinorMeters, 10, semiMajorMeters);
        // Minor information-axis angle; covariance major axis is perpendicular.
        double infoAngleEastOfNorth = 0.5 * Math.Atan2(2 * fEn, fNn - fEe) * 180 / Math.PI;
        double majorBearing = (infoAngleEastOfNorth + 90 + 360) % 180;
        return new TdoaUncertaintyEllipse(semiMajorMeters / 1000, semiMinorMeters / 1000, majorBearing);
    }

    private static double PredictedDelay(double latitude, double longitude, PairAnalysis pair) =>
        (TdoaGeodesy.SurfaceDistanceMeters(latitude, longitude, pair.B.LatitudeDeg, pair.B.LongitudeDeg)
         - TdoaGeodesy.SurfaceDistanceMeters(latitude, longitude, pair.A.LatitudeDeg, pair.A.LongitudeDeg))
        / LightSpeedMetersPerSecond * 1e9;

    internal static double? ComputeClosureRms(IReadOnlyList<ValidatedTdoaCapture> stations,
        IReadOnlyList<PairAnalysis> pairs)
    {
        var lookup = pairs.Where(p => p.Result.Usable).ToDictionary(
            p => (p.A.Id, p.B.Id), p => p, EqualityComparer<(string, string)>.Default);
        double weightedSquares = 0, weightSum = 0;
        for (int i = 0; i < stations.Count; i++)
            for (int j = i + 1; j < stations.Count; j++)
                for (int k = j + 1; k < stations.Count; k++)
                {
                    if (!lookup.TryGetValue((stations[i].Id, stations[j].Id), out var ij)
                        || !lookup.TryGetValue((stations[j].Id, stations[k].Id), out var jk)
                        || !lookup.TryGetValue((stations[i].Id, stations[k].Id), out var ik)) continue;
                    double closure = ij.Result.DelayNanoseconds + jk.Result.DelayNanoseconds - ik.Result.DelayNanoseconds;
                    double variance = Math.Pow(ij.Result.UncertaintyNanoseconds, 2)
                        + Math.Pow(jk.Result.UncertaintyNanoseconds, 2)
                        + Math.Pow(ik.Result.UncertaintyNanoseconds, 2);
                    double weight = Math.Min(ij.Result.QualityScore, Math.Min(jk.Result.QualityScore, ik.Result.QualityScore))
                        / Math.Max(variance, 1);
                    weightedSquares += weight * closure * closure;
                    weightSum += weight;
                }
        return weightSum > 0 ? Math.Sqrt(weightedSquares / weightSum) : null;
    }

    private static void ValidateGeometry(IReadOnlyList<ValidatedTdoaCapture> stations)
    {
        double maxBaseline = 0;
        for (int i = 0; i < stations.Count; i++)
            for (int j = i + 1; j < stations.Count; j++)
            {
                double baseline = TdoaGeodesy.Distance3dMeters(stations[i], stations[j]);
                if (baseline < 10) throw new TdoaValidationException("Station coordinates must describe distinct sites at least 10 metres apart.");
                maxBaseline = Math.Max(maxBaseline, baseline);
            }
        if (maxBaseline < 100)
            throw new TdoaValidationException("Station geometry spans less than 100 metres and cannot support a meaningful HF TDoA solution.");
    }

    private static bool IsNearLongitudeBoundary(double longitude, SearchBounds bounds, double step)
    {
        double unwrapped = longitude;
        while (unwrapped < bounds.MinLongitude) unwrapped += 360;
        while (unwrapped > bounds.MaxLongitude) unwrapped -= 360;
        return unwrapped <= bounds.MinLongitude + step || unwrapped >= bounds.MaxLongitude - step;
    }

    private sealed record Candidate(double Latitude, double Longitude, double Score, double ResidualNanoseconds);

    private sealed record SearchResult(SearchBounds Bounds, List<Candidate> Candidates,
        List<Candidate> Refined, Candidate Best, double LatitudeStep, double LongitudeStep,
        bool Clipped, int Expansions);

    private sealed record SearchBounds(double MinLatitude, double MaxLatitude, double MinLongitude,
        double MaxLongitude, double SpanMeters)
    {
        public static SearchBounds Create(IReadOnlyList<ValidatedTdoaCapture> stations)
        {
            double meanLon = stations[0].LongitudeDeg;
            var lons = stations.Select(s =>
            {
                double lon = s.LongitudeDeg;
                while (lon - meanLon > 180) lon -= 360;
                while (lon - meanLon < -180) lon += 360;
                return lon;
            }).ToArray();
            double minLat = stations.Min(s => s.LatitudeDeg), maxLat = stations.Max(s => s.LatitudeDeg);
            double minLon = lons.Min(), maxLon = lons.Max();
            double latSpan = Math.Max(0.05, maxLat - minLat);
            double lonSpan = Math.Max(0.05, maxLon - minLon);
            double latPad = Math.Max(0.15, latSpan * 1.5);
            double lonPad = Math.Max(0.15, lonSpan * 1.5);
            double spanMeters = Math.Max(latSpan * 111_320,
                lonSpan * 111_320 * Math.Max(0.1, Math.Cos(TdoaGeodesy.DegreesToRadians((minLat + maxLat) / 2))));
            return new SearchBounds(Math.Max(-89.9, minLat - latPad), Math.Min(89.9, maxLat + latPad),
                minLon - lonPad, maxLon + lonPad, spanMeters);
        }

        public SearchBounds Expand(double factor)
        {
            double centerLat = (MinLatitude + MaxLatitude) / 2;
            double centerLon = (MinLongitude + MaxLongitude) / 2;
            double halfLat = Math.Min(89.9, (MaxLatitude - MinLatitude) / 2 * factor);
            double halfLon = Math.Min(179.9, (MaxLongitude - MinLongitude) / 2 * factor);
            double minLat = Math.Max(-89.9, centerLat - halfLat);
            double maxLat = Math.Min(89.9, centerLat + halfLat);
            return new SearchBounds(minLat, maxLat, centerLon - halfLon, centerLon + halfLon,
                Math.Min(40_000_000, SpanMeters * factor));
        }
    }
}
