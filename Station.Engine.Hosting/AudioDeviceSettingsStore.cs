// SPDX-License-Identifier: GPL-2.0-or-later

using LiteDB;

namespace Zeus.Server;

public enum AudioHostApi
{
    System = 0,
    Asio = 1,
}

public sealed record AudioDeviceSettings(
    string? InputDeviceId,
    string? OutputDeviceId,
    int? InputChannel,
    AudioHostApi Backend = AudioHostApi.System,
    string? AsioDriverId = null,
    int AsioInputChannel = 0,
    int AsioOutputChannel = 0);

public sealed class AudioDeviceSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<AudioDeviceSettingsEntry> _docs;
    private readonly ILogger<AudioDeviceSettingsStore> _log;
    private readonly object _sync = new();

    public AudioDeviceSettingsStore(
        ILogger<AudioDeviceSettingsStore> log,
        string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _docs = _db.GetCollection<AudioDeviceSettingsEntry>("audio_device_settings");

        _log.LogInformation("AudioDeviceSettingsStore initialized at {Path}", dbPath);
    }

    public AudioDeviceSettings Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            return e is null
                ? new AudioDeviceSettings(
                    InputDeviceId: null,
                    OutputDeviceId: null,
                    InputChannel: null)
                : new AudioDeviceSettings(
                    InputDeviceId: Normalize(e.InputDeviceId),
                    OutputDeviceId: Normalize(e.OutputDeviceId),
                    InputChannel: e.InputChannel is >= 0 ? e.InputChannel : null,
                    Backend: Enum.IsDefined(e.Backend) ? e.Backend : AudioHostApi.System,
                    AsioDriverId: Normalize(e.AsioDriverId),
                    AsioInputChannel: Math.Max(0, e.AsioInputChannel),
                    AsioOutputChannel: Math.Max(0, e.AsioOutputChannel));
        }
    }

    public void SetInputDeviceId(string? inputDeviceId)
    {
        lock (_sync)
        {
            var e = GetOrCreateEntry();
            e.InputDeviceId = Normalize(inputDeviceId);
            SaveEntry(e);
        }
    }

    public void SetOutputDeviceId(string? outputDeviceId)
    {
        lock (_sync)
        {
            var e = GetOrCreateEntry();
            e.OutputDeviceId = Normalize(outputDeviceId);
            SaveEntry(e);
        }
    }

    public void SetInputChannel(int? inputChannel)
    {
        if (inputChannel is < 0)
            throw new ArgumentOutOfRangeException(nameof(inputChannel));

        lock (_sync)
        {
            var e = GetOrCreateEntry();
            e.InputChannel = inputChannel;
            SaveEntry(e);
        }
    }

    public void Set(string? inputDeviceId, string? outputDeviceId)
    {
        lock (_sync)
        {
            var e = GetOrCreateEntry();
            e.InputDeviceId = Normalize(inputDeviceId);
            e.OutputDeviceId = Normalize(outputDeviceId);
            SaveEntry(e);
        }
    }

    /// <summary>Replaces the complete host-audio route in one LiteDB write.</summary>
    public void Set(AudioDeviceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.InputChannel is < 0)
            throw new ArgumentOutOfRangeException(nameof(settings));
        if (settings.AsioInputChannel < 0 || settings.AsioOutputChannel < 0)
            throw new ArgumentOutOfRangeException(nameof(settings));

        lock (_sync)
        {
            var e = GetOrCreateEntry();
            e.InputDeviceId = Normalize(settings.InputDeviceId);
            e.OutputDeviceId = Normalize(settings.OutputDeviceId);
            e.InputChannel = settings.InputChannel;
            e.Backend = settings.Backend;
            e.AsioDriverId = Normalize(settings.AsioDriverId);
            e.AsioInputChannel = settings.AsioInputChannel;
            e.AsioOutputChannel = settings.AsioOutputChannel;
            SaveEntry(e);
        }
    }

    public void Dispose() => _dbLease.Dispose();

    private AudioDeviceSettingsEntry GetOrCreateEntry() =>
        _docs.FindAll().FirstOrDefault() ?? new AudioDeviceSettingsEntry();

    private void SaveEntry(AudioDeviceSettingsEntry e)
    {
        e.UpdatedUtc = DateTime.UtcNow;
        if (e.Id == 0) _docs.Insert(e);
        else _docs.Update(e);
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

public sealed class AudioDeviceSettingsEntry
{
    public int Id { get; set; }
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public int? InputChannel { get; set; }
    public AudioHostApi Backend { get; set; }
    public string? AsioDriverId { get; set; }
    public int AsioInputChannel { get; set; }
    public int AsioOutputChannel { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
