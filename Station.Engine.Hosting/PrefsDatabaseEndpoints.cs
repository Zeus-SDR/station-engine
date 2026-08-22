// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Single user-facing database backup routes, shared by the product host
/// (ZeusEndpoints) and the standalone station engine
/// (StationEngineEndpoints). The live stores retain separate process-owned
/// files, but export/import presents them as one versioned .zeusdb container.
/// </summary>
public static class PrefsDatabaseEndpoints
{
    // UnifiedDatabaseBackup accepts at most 120 MiB of backup payload. Leave
    // one MiB for multipart framing so Kestrel does not reject a Zeus-produced
    // backup before this endpoint can return its own operator-facing error.
    internal const long UploadRequestBodyLimitBytes = 121L * 1024 * 1024;

    public static IEndpointRouteBuilder MapPrefsDatabaseEndpoints(
        this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var downloadsDirectory =
            app.ServiceProvider.GetService<OperatorDownloadsDirectory>()
            ?? new OperatorDownloadsDirectory();

        // Compatibility inventory: Zeus now exposes one canonical database to
        // the operator. Keep the route shape while older frontends roll forward.
        app.MapGet("/api/prefs/databases", () =>
            Results.Ok(CanonicalDatabaseDto()));

        app.MapPost("/api/prefs/active-database", (SetActiveDatabaseRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.RelativePath))
                return Results.BadRequest(new { error = "relativePath required" });
            try
            {
                if (!string.Equals(
                    req.RelativePath,
                    PrefsDbPath.LegacyFileName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new
                    {
                        error = "Zeus uses one database; database switching is no longer available.",
                    });
                }
                return Results.Ok(new { restartRequired = false });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/prefs/databases", (CreateDatabaseRequest req) =>
        {
            try
            {
                return Results.BadRequest(new
                {
                    error = "Zeus uses one database; creating additional databases is no longer available.",
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/prefs/databases/import", (HttpContext ctx, ImportDatabaseRequest req) =>
        {
            if (LocalRequestGuard.RejectIfNotLocalAllowedBrowserOrigin(
                    ctx, "import the Zeus settings database") is { } rejected)
                return rejected;
            if (LocalRequestGuard.RejectIfNotLiteralLocalHost(
                    ctx, "import the Zeus settings database") is { } hostRejected)
                return hostRejected;
            try
            {
                using var source = File.OpenRead(req.SourcePath);
                UnifiedDatabaseBackup.StageImport(source);
                return Results.Ok(new { restartRequired = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // File-picker import: the webview can't hand the server a filesystem
        // path, so the chosen backup is uploaded as multipart. The route is
        // still explicitly restricted to a local allowed app before the
        // multipart body is read.
        app.MapPost("/api/prefs/databases/upload", async (HttpContext ctx) =>
        {
            if (LocalRequestGuard.RejectIfNotLocalAllowedBrowserOrigin(
                    ctx, "import the Zeus settings database") is { } rejected)
                return rejected;
            if (LocalRequestGuard.RejectIfNotLiteralLocalHost(
                    ctx, "import the Zeus settings database") is { } hostRejected)
                return hostRejected;
            try
            {
                var req = ctx.Request;
                if (!req.HasFormContentType)
                    return Results.BadRequest(new { error = "Expected a multipart file upload." });
                var form = await req.ReadFormAsync();
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "No file uploaded." });
                if (file.Length > UnifiedDatabaseBackup.MaximumBackupBytes)
                    return Results.BadRequest(new { error = "That Zeus database backup is too large." });

                await using var stream = file.OpenReadStream();
                UnifiedDatabaseBackup.StageImport(stream);
                return Results.Ok(new { restartRequired = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
                UploadRequestBodyLimitBytes));

        // Download the one operator-facing Zeus backup. This remains the live
        // remote-LAN appliance path: the SPA falls back to it when a local
        // server-side save is forbidden or the engine predates that route.
        // The versioned container captures both authoritative settings stores
        // and deliberately excludes PureSignal's ps_settings collection.
        // The ".zeusdb" extension (#64) avoids collisions with ham-radio
        // logbook apps that register ".db" in the Windows shell.
        //
        // Legacy imports remain supported as raw LiteDB, but new exports are
        // ZIP containers with a manifest and per-entry integrity hashes.
        //
        // Historical context: Windows ham-radio logbook apps (N1MM+, Log4OM,
        // HRD, ...) register
        // ".db" as their file type in the shell, so a double-clicked export
        // used to open the last-viewed logbook instead of anything Zeus. The
        // ".zeusdb" extension keeps a Zeus-specific identity so no other app
        // silently claims it.
        app.MapGet("/api/prefs/databases/export", (HttpContext ctx, string? relativePath) =>
        {
            if (LocalRequestGuard.RejectIfNotLocalAllowedBrowserOrigin(
                    ctx, "export the Zeus settings database") is { } rejected)
                return rejected;
            if (LocalRequestGuard.RejectIfNotLiteralLocalHost(
                    ctx, "export the Zeus settings database") is { } hostRejected)
                return hostRejected;
            try
            {
                // relativePath is intentionally ignored as a compatibility
                // alias for older frontends. There is only one export target.
                var bytes = UnifiedDatabaseBackup.BuildContainer();
                var downloadName = $"zeus-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zeusdb";
                return Results.File(bytes, "application/x-zeus-database", downloadName);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/prefs/databases/export-file", (HttpContext ctx) =>
            ExportFile(ctx, downloadsDirectory));

        return app;
    }

    internal static IResult ExportFile(
        HttpContext ctx,
        OperatorDownloadsDirectory downloadsDirectory)
    {
        if (LocalRequestGuard.RejectIfNotLocalAllowedBrowserOrigin(
                ctx, "export the Zeus settings database") is { } rejected)
            return rejected;
        if (LocalRequestGuard.RejectIfNotLiteralLocalHost(
                ctx, "export the Zeus settings database") is { } hostRejected)
            return hostRejected;
        try
        {
            var bytes = UnifiedDatabaseBackup.BuildContainer();
            var saved = downloadsDirectory.SaveBackup(bytes);
            return Results.Ok(new
            {
                path = saved.Path,
                fileName = saved.FileName,
                directory = saved.Directory,
                sizeBytes = saved.SizeBytes,
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static PrefsDatabasesDto CanonicalDatabaseDto()
    {
        var path = PrefsDbPath.Get();
        var info = new FileInfo(path);
        var database = new PrefsDatabaseInfo(
            Name: "Zeus",
            RelativePath: PrefsDbPath.LegacyFileName,
            SizeBytes: info.Exists ? info.Length : 0,
            ModifiedUtcMs: info.Exists
                ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds()
                : 0,
            Active: true);
        return new PrefsDatabasesDto(PrefsDbPath.LegacyFileName, [database]);
    }
}
