// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

internal sealed record StationMemoryFolderRequest(string? Name);

/// <summary>Persists empty folder names alongside the station memory library.</summary>
internal sealed class StationMemoryFolderStore : IDisposable
{
    internal const string CollectionName = StationMemoryStore.FolderCollectionName;
    private readonly StationMemoryGate.Entry _gate;
    private readonly Zeus.Data.SharedLiteDatabase.Lease _lease;
    private readonly LiteDatabase _db;

    internal StationMemoryFolderStore(string path)
    {
        _gate = StationMemoryGate.For(path);
        _lease = Zeus.Data.SharedLiteDatabase.Acquire(path);
        _db = _lease.Database;
    }

    internal string[] List()
    {
        lock (_gate.Sync)
            return _db.GetCollection<Folder>(CollectionName).FindAll().Select(x => x.Name)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal string Create(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > StationMemorySchema.MaxGroupLength || name.Any(char.IsControl))
            throw new ArgumentException("Enter a folder name of up to 80 characters.");
        lock (_gate.Sync)
        {
            var folders = _db.GetCollection<Folder>(CollectionName);
            if (folders.FindAll().Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                || _db.GetCollection<StationMemoryDocument>(StationMemoryStore.CollectionName).FindAll()
                    .Any(x => string.Equals(x.Group, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A folder with this name already exists.");
            folders.Insert(new Folder { Name = name });
            _gate.Version++;
        }
        return name;
    }

    internal void Delete(string name)
    {
        lock (_gate.Sync)
        {
            if (_db.GetCollection<StationMemoryDocument>(StationMemoryStore.CollectionName).Exists(x => x.Group == name))
                throw new InvalidOperationException("Move the memories out of this folder first.");
            if (_db.GetCollection<Folder>(CollectionName).Delete(name)) _gate.Version++;
        }
    }

    public void Dispose() => _lease.Dispose();

    public sealed class Folder
    {
        [BsonId] public string Name { get; set; } = string.Empty;
    }
}
