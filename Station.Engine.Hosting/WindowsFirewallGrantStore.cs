// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Remembers, per engine executable path, that we already tried to grant Zeus its
/// Windows Firewall rule — and how that went.
///
/// This exists so the operator is asked <b>at most once</b>. Without it the
/// startup grant would re-raise a UAC prompt on every single launch for anyone
/// who declined it, or for anyone on a machine where the grant cannot succeed
/// (Group Policy, a locked-down firewall, a stopped mpssvc).
///
/// Keyed by program path on purpose. Zeus Link provisions the engine into
/// <c>&lt;cache&gt;/&lt;version&gt;/&lt;target&gt;/StationEngine.exe</c>, so the path changes on
/// every engine update and the old rule stops covering the running binary. A new
/// path is a genuinely new question; the same path never is.
/// </summary>
public sealed class WindowsFirewallGrantStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<WindowsFirewallGrantEntry> _docs;
    private readonly ILogger<WindowsFirewallGrantStore> _log;
    private readonly object _sync = new();

    public WindowsFirewallGrantStore(
        ILogger<WindowsFirewallGrantStore> log,
        string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _docs = _db.GetCollection<WindowsFirewallGrantEntry>("windows_firewall_grant");
    }

    /// <summary>
    /// The recorded outcome for <paramref name="programPath"/>, or null if we have
    /// never attempted a grant for that executable.
    /// </summary>
    public WindowsFirewallGrantOutcome? Find(string programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath)) return null;
        lock (_sync)
        {
            var key = Normalize(programPath);
            var entry = _docs.FindAll().FirstOrDefault(e => Normalize(e.ProgramPath) == key);
            return entry is null ? null : entry.Outcome;
        }
    }

    public void Record(string programPath, WindowsFirewallGrantOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(programPath)) return;
        lock (_sync)
        {
            var key = Normalize(programPath);
            var entry = _docs.FindAll().FirstOrDefault(e => Normalize(e.ProgramPath) == key);
            if (entry is null)
            {
                entry = new WindowsFirewallGrantEntry { ProgramPath = programPath };
                entry.Outcome = outcome;
                entry.UpdatedUtc = DateTime.UtcNow;
                _docs.Insert(entry);
            }
            else
            {
                entry.Outcome = outcome;
                entry.UpdatedUtc = DateTime.UtcNow;
                _docs.Update(entry);
            }

            _log.LogInformation(
                "windows.firewall.grant.recorded path={ProgramPath} outcome={Outcome}",
                programPath,
                outcome);
        }
    }

    /// <summary>
    /// Drop records for executables that no longer exist on disk. Zeus Link leaves
    /// one dead path behind per engine update; without this the collection grows
    /// without bound.
    /// </summary>
    /// <param name="keepPath">
    /// Never prune this path, even if <see cref="File.Exists"/> says otherwise.
    /// The caller passes the running executable: pruning the record for the very
    /// binary whose verdict we are about to read would silently resurrect a
    /// question the operator already answered, turning "ask once" back into "ask
    /// on every launch". File.Exists can also answer false for reasons that have
    /// nothing to do with the file being gone — a transient sharing violation, a
    /// network path, an ACL — so this is a guard, not an optimisation.
    /// </param>
    public int PruneMissing(string? keepPath = null)
    {
        lock (_sync)
        {
            var keep = keepPath is null ? null : Normalize(keepPath);
            var dead = _docs.FindAll()
                .Where(e => !string.IsNullOrWhiteSpace(e.ProgramPath)
                            && (keep is null || Normalize(e.ProgramPath) != keep)
                            && !File.Exists(e.ProgramPath))
                .ToList();
            foreach (var e in dead)
                _docs.Delete(e.Id);
            return dead.Count;
        }
    }

    private static string Normalize(string path) =>
        path.Trim().TrimEnd('\\').ToLowerInvariant();

    public void Dispose() => _dbLease.Dispose();
}

public enum WindowsFirewallGrantOutcome
{
    /// <summary>The rule is in place for this executable.</summary>
    Granted = 0,

    /// <summary>The operator dismissed the elevation prompt. Never ask again.</summary>
    Declined = 1,

    /// <summary>
    /// The grant was attempted and failed for a reason retrying will not fix
    /// (policy, a disabled firewall service). Never ask again; Settings still offers
    /// the manual action.
    /// </summary>
    Failed = 2,
}

public sealed class WindowsFirewallGrantEntry
{
    public int Id { get; set; }
    public string ProgramPath { get; set; } = string.Empty;
    public WindowsFirewallGrantOutcome Outcome { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
