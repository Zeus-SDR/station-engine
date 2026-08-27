// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

/// <summary>
/// Maps the Windows Firewall helper routes on the standalone station engine.
/// These mirror the product-host mappings in Zeus.Server.Hosting
/// (ZeusEndpoints.cs) route-for-route and payload-for-payload: in the Zeus
/// Link attach topology the SPA's /api/* calls land on the trimmed engine,
/// so the Settings "Apply firewall rule" control 404'd until the engine
/// served the same surface.
/// </summary>
public static class WindowsFirewallEndpoints
{
    public static IEndpointRouteBuilder MapWindowsFirewallEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Windows Firewall helper for source builds and non-elevated installs.
        // It adds the same inbound program allow rule the installer attempts,
        // scoped to the running Zeus executable. POST is local-only so a LAN
        // browser cannot trigger a UAC prompt on the host machine.
        endpoints.MapGet("/api/system/windows-firewall",
            async (HttpContext ctx, IWindowsFirewallService firewall, CancellationToken ct) =>
                Results.Ok(FirewallStatusDto(
                    await firewall.GetStatusAsync(ct),
                    LocalRequestGuard.IsLocalRequest(ctx))));

        endpoints.MapPost("/api/system/windows-firewall/allow",
            async (HttpContext ctx, IWindowsFirewallService firewall, CancellationToken ct) =>
            {
                if (!LocalRequestGuard.IsLocalRequest(ctx))
                {
                    return Results.Json(
                        new
                        {
                            error = "Open Settings on the Windows machine running Zeus to change Windows Firewall.",
                        },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var result = await firewall.ApplyAllowRuleAsync(ct);
                if (result.Applied)
                    return Results.Ok(result);

                var statusCode = !result.Supported
                    ? StatusCodes.Status400BadRequest
                    : result.ElevationCanceled
                        ? StatusCodes.Status409Conflict
                        : StatusCodes.Status500InternalServerError;
                return Results.Json(new { error = result.Message, result }, statusCode: statusCode);
            });

        return endpoints;
    }

    private static object FirewallStatusDto(WindowsFirewallStatus status, bool localRequest) => new
    {
        status.Supported,
        CanApply = status.CanApply && localRequest,
        LocalRequest = localRequest,
        status.RuleName,
        status.ProgramPath,
        status.RulePresent,
        status.RuleMatchesProgram,
        Message = localRequest
            ? status.Message
            : "Open Settings on the Windows machine running Zeus to change Windows Firewall.",
    };
}
