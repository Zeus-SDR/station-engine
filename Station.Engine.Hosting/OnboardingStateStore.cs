// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists the operator's first-run / onboarding progress (the goal-based
/// setup wizard in the SPA). Single-row collection; the ABSENCE of the row is
/// the "first run" signal, mirroring <see cref="OperatorAckStore"/> — nothing
/// is written until the wizard actually records progress, so a fresh install
/// (or a reset) auto-opens the wizard.
///
/// Step and goal ids are stored as OPAQUE strings: the wizard's vocabulary
/// lives entirely in the frontend registry and evolves freely without a
/// backend or contract change. Server-side persistence (rather than
/// localStorage) is deliberate — progress survives browser-storage wipes and
/// follows the operator between hosts on the same machine, the same rationale
/// as the operator identity and Ft8 settings stores.
/// Thread-safe; registered as a singleton.
/// </summary>
public sealed class OnboardingStateStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<OnboardingStateEntry> _state;
    private readonly ILogger<OnboardingStateStore> _log;
    private readonly object _sync = new();

    public OnboardingStateStore(ILogger<OnboardingStateStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _state = _db.GetCollection<OnboardingStateEntry>("operator_onboarding");

        _log.LogInformation("OnboardingStateStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// Current snapshot. <c>FirstRun</c> is true only while no row exists —
    /// i.e. the wizard has never recorded anything on this prefs database.
    /// </summary>
    public OnboardingSnapshot Get()
    {
        lock (_sync)
        {
            var entry = _state.FindAll().FirstOrDefault();
            if (entry is null)
                return new OnboardingSnapshot(
                    FirstRun: true,
                    CompletedSteps: Array.Empty<string>(),
                    CompletedGoals: Array.Empty<string>(),
                    ActiveGoal: null,
                    LastCompletedStep: null,
                    DismissedUtc: null,
                    FirstCompletedUtc: null);

            return new OnboardingSnapshot(
                FirstRun: false,
                CompletedSteps: entry.CompletedSteps?.ToArray() ?? Array.Empty<string>(),
                CompletedGoals: entry.CompletedGoals?.ToArray() ?? Array.Empty<string>(),
                ActiveGoal: entry.ActiveGoal,
                LastCompletedStep: entry.LastCompletedStep,
                DismissedUtc: entry.DismissedUtc,
                FirstCompletedUtc: entry.FirstCompletedUtc);
        }
    }

    /// <summary>
    /// Idempotent full-state upsert (last-writer-wins across clients — the
    /// wizard PUTs its complete progress document). Step/goal lists are
    /// de-duplicated and normalised defensively; a null list clears.
    /// </summary>
    public OnboardingSnapshot Set(
        IReadOnlyList<string>? completedSteps,
        IReadOnlyList<string>? completedGoals,
        string? activeGoal,
        string? lastCompletedStep,
        DateTime? dismissedUtc)
    {
        static List<string> Clean(IReadOnlyList<string>? ids) =>
            (ids ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

        lock (_sync)
        {
            var entry = _state.FindAll().FirstOrDefault() ?? new OnboardingStateEntry();
            entry.CompletedSteps = Clean(completedSteps);
            entry.CompletedGoals = Clean(completedGoals);
            entry.ActiveGoal = string.IsNullOrWhiteSpace(activeGoal) ? null : activeGoal.Trim();
            entry.LastCompletedStep =
                string.IsNullOrWhiteSpace(lastCompletedStep) ? null : lastCompletedStep.Trim();
            entry.DismissedUtc = dismissedUtc;
            // First goal completion is stamped once and never rewound — it is
            // an audit fact ("this operator finished a path"), not UI state.
            if (entry.FirstCompletedUtc is null && entry.CompletedGoals.Count > 0)
                entry.FirstCompletedUtc = DateTime.UtcNow;
            entry.UpdatedUtc = DateTime.UtcNow;

            _state.Upsert(entry);

            _log.LogInformation(
                "onboarding.state saved steps={Steps} goals={Goals} active={Active}",
                entry.CompletedSteps.Count, entry.CompletedGoals.Count, entry.ActiveGoal ?? "-");
        }

        return Get();
    }

    /// <summary>
    /// Deletes the row entirely, restoring first-run semantics ("start over" /
    /// support reset). The next <see cref="Get"/> reports <c>FirstRun=true</c>.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _state.DeleteAll();
        }

        _log.LogInformation("onboarding.state reset — first-run semantics restored");
    }

    public void Dispose() => _dbLease.Dispose();
}

/// <summary>Immutable snapshot returned by the store (and serialized by the API).</summary>
public sealed record OnboardingSnapshot(
    bool FirstRun,
    IReadOnlyList<string> CompletedSteps,
    IReadOnlyList<string> CompletedGoals,
    string? ActiveGoal,
    string? LastCompletedStep,
    DateTime? DismissedUtc,
    DateTime? FirstCompletedUtc);

public sealed class OnboardingStateEntry
{
    public int Id { get; set; }
    public List<string> CompletedSteps { get; set; } = new();
    public List<string> CompletedGoals { get; set; } = new();
    public string? ActiveGoal { get; set; }
    public string? LastCompletedStep { get; set; }
    public DateTime? DismissedUtc { get; set; }
    public DateTime? FirstCompletedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
