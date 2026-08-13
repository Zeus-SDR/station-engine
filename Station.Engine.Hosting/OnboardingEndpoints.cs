// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server;

/// <summary>
/// Maps the first-run / onboarding-wizard progress routes. Shared by the
/// product host and the standalone station engine (the MapWorkspaceLayout
/// pattern) so the SPA's wizard state behaves identically in both topologies:
/// in Zeus Link attach these routes target the engine, and the desktop /
/// full host maps the same mapper. Step and goal ids are opaque strings —
/// the wizard vocabulary lives in the frontend registry only.
///
/// These routes are pure UI-progress persistence: nothing here touches the
/// radio, TX state, or any DSP surface.
/// </summary>
public static class OnboardingEndpoints
{
    /// <summary>Request body for the full-state upsert. Wire-shape mirrors
    /// <see cref="OnboardingSnapshot"/> minus the server-computed fields.</summary>
    public sealed record OnboardingSetRequest(
        List<string>? CompletedSteps,
        List<string>? CompletedGoals,
        string? ActiveGoal,
        string? LastCompletedStep,
        DateTime? DismissedUtc);

    public static IEndpointRouteBuilder MapOnboardingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // Snapshot incl. firstRun (row absent = never recorded = auto-open).
        endpoints.MapGet("/api/onboarding", (OnboardingStateStore store) =>
            Results.Ok(store.Get()));

        // Idempotent full-document upsert; last-writer-wins across clients.
        endpoints.MapPut("/api/onboarding",
            (OnboardingSetRequest req, OnboardingStateStore store) =>
            {
                var saved = store.Set(
                    req.CompletedSteps,
                    req.CompletedGoals,
                    req.ActiveGoal,
                    req.LastCompletedStep,
                    req.DismissedUtc);
                return Results.Ok(saved);
            });

        // Start-over / support reset: restores first-run semantics.
        endpoints.MapPost("/api/onboarding/reset", (OnboardingStateStore store) =>
        {
            store.Reset();
            log.LogInformation("api.onboarding.reset");
            return Results.Ok(store.Get());
        });

        return endpoints;
    }
}
