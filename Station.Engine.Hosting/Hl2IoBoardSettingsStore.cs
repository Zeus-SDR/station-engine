// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;

namespace Zeus.Server;

public sealed record Hl2IoBoardBand(string Band, byte RxAntenna = 0, byte TxAntenna = 0);

public sealed class Hl2IoBoardSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _lease;
    private readonly ILiteCollection<Hl2IoBoardBandEntry> _bands;
    private readonly object _sync = new();
    public event Action? Changed;

    public Hl2IoBoardSettingsStore(string? dbPathOverride = null)
    {
        var path = dbPathOverride ?? PrefsDbPath.EngineGet();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _lease = Zeus.Data.SharedLiteDatabase.Acquire(path);
        _bands = _lease.Database.GetCollection<Hl2IoBoardBandEntry>("hl2_io_bands");
    }

    public Hl2IoBoardBand GetBand(string band)
    {
        lock (_sync)
        {
            var entry = _bands.FindById(band);
            return new(band, (byte)Math.Clamp((int)(entry?.RxAntenna ?? 0), 0, 15), (byte)Math.Clamp((int)(entry?.TxAntenna ?? 0), 0, 15));
        }
    }

    public IReadOnlyList<Hl2IoBoardBand> GetAll() => BandUtils.HfBands.Select(GetBand).ToArray();

    public void SetBand(Hl2IoBoardBand band)
    {
        if (!BandUtils.HfBands.Contains(band.Band) || band.RxAntenna > 15 || band.TxAntenna > 15)
            throw new ArgumentException("Choose a valid band and antenna numbers from 0 to 15.", nameof(band));
        lock (_sync)
            _bands.Upsert(new Hl2IoBoardBandEntry { Id = band.Band, RxAntenna = band.RxAntenna, TxAntenna = band.TxAntenna });
        Changed?.Invoke();
    }

    public void Dispose() => _lease.Dispose();
}

public sealed class Hl2IoBoardBandEntry
{
    [BsonId] public string Id { get; set; } = "";
    public byte RxAntenna { get; set; }
    public byte TxAntenna { get; set; }
}
