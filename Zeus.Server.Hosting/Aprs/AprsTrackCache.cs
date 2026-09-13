// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
using System.Globalization;
#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.Aprs;
#else
namespace Zeus.Server.Aprs;
#endif

public sealed record AprsTrack(AprsPosition Position, IReadOnlyList<AprsPosition> Trail);
public sealed record AprsSnapshot(AprsSettings Settings, string State, string Reason, string Server,
    DateTimeOffset ServerTimeUtc, DateTimeOffset? LastPacketUtc, long PacketsReceived, long PositionsAccepted,
    // Truncated is retained for existing clients; worldwide snapshots never apply a station cutoff.
    IReadOnlyList<AprsTrack> Tracks, bool Truncated, string? Cursor = null, bool IsDelta = false,
    IReadOnlyList<string>? RemovedIds = null);
public sealed record AprsTrackChanges(AprsTrack[] Tracks, string Cursor, bool IsDelta, string[] RemovedIds);

public sealed class AprsTrackCache
{
    private readonly Dictionary<string, AprsTrack> _tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
    private readonly Queue<(long Version, string Id)> _removals = new();
    private readonly object _gate = new();
    private string _generation = Guid.NewGuid().ToString("N");
    private long _version, _oldestCursor;
    private DateTimeOffset _lastPruned;
    public void Clear()
    {
        lock (_gate)
        {
            _tracks.Clear(); _versions.Clear(); _removals.Clear(); _lastPruned = default;
            _version = _oldestCursor = 0; _generation = Guid.NewGuid().ToString("N");
        }
    }
    public bool Accept(AprsPosition position, DateTimeOffset now, TimeSpan retention)
    {
        lock (_gate)
        {
            // Expiry bounds retention even while no client is requesting snapshots.
            if (now - _lastPruned >= TimeSpan.FromMinutes(1)) Prune(now, retention);
            var timestamp = position.ReportedUtc ?? position.ReceivedUtc;
            if (timestamp < now - retention || timestamp > now.AddMinutes(2)) return false;
            if (_tracks.TryGetValue(position.Id, out var previous))
            {
                if ((position.Raw == previous.Position.Raw && position.ReceivedUtc - previous.Position.ReceivedUtc < TimeSpan.FromSeconds(30)) || timestamp < (previous.Position.ReportedUtc ?? previous.Position.ReceivedUtc)
                    || position.ReceivedUtc <= previous.Position.ReceivedUtc) return false;
            }
            if (position.Killed) return Remove(position.Id);
            var trail = (previous?.Trail ?? []).Append(position).Where(p => (p.ReportedUtc ?? p.ReceivedUtc) >= now - retention).TakeLast(32).ToArray();
            _tracks[position.Id] = new(position, trail);
            _versions[position.Id] = ++_version;
            return true;
        }
    }
    private bool Remove(string id)
    {
        if (!_tracks.Remove(id)) return false;
        _versions.Remove(id);
        _removals.Enqueue((++_version, id));
        // Bound only replay metadata. A client older than this journal receives a complete snapshot.
        while (_removals.Count > 4096) _oldestCursor = _removals.Dequeue().Version;
        return true;
    }
    private void Prune(DateTimeOffset now, TimeSpan retention)
    {
        foreach (var key in _tracks.Where(pair => (pair.Value.Position.ReportedUtc ?? pair.Value.Position.ReceivedUtc) < now - retention).Select(pair => pair.Key).ToArray())
            Remove(key);
        _lastPruned = now;
    }
    public AprsTrack[] Snapshot(DateTimeOffset now, TimeSpan retention, string? selectedId = null)
        => Changes(now, retention, selectedId).Tracks;

    public AprsTrackChanges Changes(DateTimeOffset now, TimeSpan retention, string? selectedId = null, string? cursor = null)
    {
        lock (_gate)
        {
            Prune(now, retention);
            var prefix = _generation + ".";
            long since = 0;
            var delta = cursor is { Length: <= 64 } && cursor.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(cursor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out since)
                && since >= _oldestCursor && since <= _version;
            IEnumerable<AprsTrack> tracks = delta
                ? _tracks.Values.Where(track => _versions[track.Position.Id] > since || track.Position.Id == selectedId)
                : _tracks.Values.OrderByDescending(track => track.Position.ReceivedUtc);
            var visible = tracks.Select(track => track.Position.Id == selectedId
                ? track with { Trail = track.Trail.Where(p => (p.ReportedUtc ?? p.ReceivedUtc) >= now - retention).ToArray() }
                : track with { Position = track.Position with { Raw = "" }, Trail = [] }).ToArray();
            return new(visible, prefix + _version.ToString(CultureInfo.InvariantCulture), delta,
                delta ? _removals.Where(entry => entry.Version > since).Select(entry => entry.Id).Distinct(StringComparer.Ordinal).ToArray() : []);
        }
    }
}
