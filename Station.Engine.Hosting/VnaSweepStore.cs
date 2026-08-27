// SPDX-License-Identifier: GPL-2.0-or-later

using LiteDB;

namespace Zeus.Server;

public sealed class VnaSweepStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<VnaSweepEntry> _sweeps;
    private readonly ILiteCollection<VnaCalibrationEntry> _calibrations;
    private readonly object _sync = new();

    public VnaSweepStore(ILogger<VnaSweepStore> log, string? dbPathOverride = null)
    {
        string dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        string? directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _sweeps = _dbLease.Database.GetCollection<VnaSweepEntry>("vna_sweeps");
        _calibrations = _dbLease.Database.GetCollection<VnaCalibrationEntry>("vna_calibrations");
        _sweeps.EnsureIndex(x => x.CapturedUtc);
        _sweeps.EnsureIndex(x => x.RadioKey);
        _sweeps.EnsureIndex(x => x.Band);
        _calibrations.EnsureIndex(x => x.RadioKey);
        log.LogInformation("VNA sweep store initialized at {Path}", dbPath);
    }

    public IReadOnlyList<VnaSweepDto> GetSweeps(string? radioKey = null)
    {
        lock (_sync)
        {
            IEnumerable<VnaSweepEntry> rows = string.IsNullOrWhiteSpace(radioKey)
                ? _sweeps.FindAll()
                : _sweeps.Find(x => x.RadioKey == radioKey);
            return rows.OrderBy(x => x.CapturedUtc).Select(ToDto).ToArray();
        }
    }

    public VnaSweepDto? GetSweep(string id)
    {
        lock (_sync) return _sweeps.FindById(id) is { } row ? ToDto(row) : null;
    }

    public void Save(VnaSweepDto sweep)
    {
        lock (_sync) _sweeps.Upsert(ToEntry(sweep));
    }

    public bool DeleteSweep(string id)
    {
        lock (_sync) return _sweeps.Delete(id);
    }

    public IReadOnlyList<VnaCalibrationDto> GetCalibrations(string? radioKey = null)
    {
        lock (_sync)
        {
            IEnumerable<VnaCalibrationEntry> rows = string.IsNullOrWhiteSpace(radioKey)
                ? _calibrations.FindAll()
                : _calibrations.Find(x => x.RadioKey == radioKey);
            return rows.OrderBy(x => x.UpdatedUtc).Select(ToDto).ToArray();
        }
    }

    internal VnaCalibrationEntry? GetCalibrationEntry(string id)
    {
        lock (_sync) return _calibrations.FindById(id);
    }

    internal VnaCalibrationDto SaveCalibrationStandard(
        string id,
        string name,
        string radioKey,
        string antenna,
        string band,
        long startHz,
        long endHz,
        IReadOnlyList<VnaComplexSample> points,
        VnaCalibrationStandard standard)
    {
        lock (_sync)
        {
            var row = _calibrations.FindById(id) ?? new VnaCalibrationEntry { Id = id };
            row.Name = name;
            row.RadioKey = radioKey;
            row.Antenna = antenna;
            row.Band = band;
            row.StartHz = startHz;
            row.EndHz = endHz;
            row.PointCount = points.Count;
            row.UpdatedUtc = DateTimeOffset.UtcNow;
            var stored = points.Select(VnaStoredComplex.From).ToList();
            switch (standard)
            {
                case VnaCalibrationStandard.Thru: row.Thru = stored; break;
                case VnaCalibrationStandard.Open: row.Open = stored; break;
                case VnaCalibrationStandard.Short: row.Short = stored; break;
                case VnaCalibrationStandard.Load: row.Load = stored; break;
            }
            _calibrations.Upsert(row);
            return ToDto(row);
        }
    }

    public bool DeleteCalibration(string id)
    {
        lock (_sync) return _calibrations.Delete(id);
    }

    private static VnaSweepDto ToDto(VnaSweepEntry row) => new(
        row.Id, row.CapturedUtc, row.RadioKey, row.Board, row.Antenna, row.Band,
        row.Label, row.StartHz, row.EndHz, row.PointCount, row.Kind,
        row.CalibrationId, row.Calibrated, row.Metrics,
        row.Points.Select(x => x.ToDto()).ToArray());

    private static VnaSweepEntry ToEntry(VnaSweepDto sweep) => new()
    {
        Id = sweep.Id,
        CapturedUtc = sweep.CapturedUtc,
        RadioKey = sweep.RadioKey,
        Board = sweep.Board,
        Antenna = sweep.Antenna,
        Band = sweep.Band,
        Label = sweep.Label,
        StartHz = sweep.StartHz,
        EndHz = sweep.EndHz,
        PointCount = sweep.PointCount,
        Kind = sweep.Kind,
        CalibrationId = sweep.CalibrationId,
        Calibrated = sweep.Calibrated,
        Metrics = sweep.Metrics,
        Points = sweep.Points.Select(VnaPointEntry.From).ToList(),
    };

    private static VnaCalibrationDto ToDto(VnaCalibrationEntry row) => new(
        row.Id, row.Name, row.UpdatedUtc, row.RadioKey, row.Antenna, row.Band,
        row.StartHz, row.EndHz, row.PointCount,
        row.Thru.Count > 0, row.Open.Count > 0, row.Short.Count > 0, row.Load.Count > 0,
        row.Open.Count == row.PointCount && row.Short.Count == row.PointCount && row.Load.Count == row.PointCount,
        row.Thru.Count == row.PointCount);

    public void Dispose() => _dbLease.Dispose();
}

internal sealed class VnaSweepEntry
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public DateTimeOffset CapturedUtc { get; set; }
    public string RadioKey { get; set; } = string.Empty;
    public string Board { get; set; } = string.Empty;
    public string Antenna { get; set; } = string.Empty;
    public string Band { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public long StartHz { get; set; }
    public long EndHz { get; set; }
    public int PointCount { get; set; }
    public VnaMeasurementKind Kind { get; set; }
    public string? CalibrationId { get; set; }
    public bool Calibrated { get; set; }
    public VnaSweepMetricsDto Metrics { get; set; } = new(0, null, null, null, null, null, null, null, null);
    public List<VnaPointEntry> Points { get; set; } = [];
}

internal sealed class VnaPointEntry
{
    public long FrequencyHz { get; set; }
    public double RawReal { get; set; }
    public double RawImaginary { get; set; }
    public double MagnitudeDb { get; set; }
    public double PhaseDeg { get; set; }
    public double? Swr { get; set; }
    public double? ReturnLossDb { get; set; }
    public double? ResistanceOhms { get; set; }
    public double? ReactanceOhms { get; set; }

    public VnaPointDto ToDto() => new(FrequencyHz, RawReal, RawImaginary, MagnitudeDb,
        PhaseDeg, Swr, ReturnLossDb, ResistanceOhms, ReactanceOhms);

    public static VnaPointEntry From(VnaPointDto p) => new()
    {
        FrequencyHz = p.FrequencyHz, RawReal = p.RawReal, RawImaginary = p.RawImaginary,
        MagnitudeDb = p.MagnitudeDb, PhaseDeg = p.PhaseDeg, Swr = p.Swr,
        ReturnLossDb = p.ReturnLossDb, ResistanceOhms = p.ResistanceOhms,
        ReactanceOhms = p.ReactanceOhms,
    };
}

internal sealed class VnaCalibrationEntry
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; set; }
    public string RadioKey { get; set; } = string.Empty;
    public string Antenna { get; set; } = string.Empty;
    public string Band { get; set; } = string.Empty;
    public long StartHz { get; set; }
    public long EndHz { get; set; }
    public int PointCount { get; set; }
    public List<VnaStoredComplex> Thru { get; set; } = [];
    public List<VnaStoredComplex> Open { get; set; } = [];
    public List<VnaStoredComplex> Short { get; set; } = [];
    public List<VnaStoredComplex> Load { get; set; } = [];
}

internal sealed class VnaStoredComplex
{
    public long FrequencyHz { get; set; }
    public double Real { get; set; }
    public double Imaginary { get; set; }
    public static VnaStoredComplex From(VnaComplexSample sample) =>
        new() { FrequencyHz = sample.FrequencyHz, Real = sample.Real, Imaginary = sample.Imaginary };
}
