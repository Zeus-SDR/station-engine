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
/// Prefs-database (profile) management routes, shared by the product host
/// (ZeusEndpoints) and the standalone station engine
/// (StationEngineEndpoints). The handlers were extracted verbatim from the
/// product host so both surfaces behave identically; the only intentional
/// enhancement is the export route, which now serves a merged snapshot (see
/// PrefsProfileExport) so an exported profile carries the CURRENT engine-side
/// settings instead of the stale pre-split copies.
/// </summary>
public static class PrefsDatabaseEndpoints
{
    public static IEndpointRouteBuilder MapPrefsDatabaseEndpoints(
        this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Prefs-database (profile) selector. All Zeus prefs/settings/layouts live
        // in a single LiteDB resolved by PrefsDbPath.Get() at startup; the active
        // choice is a pointer file (NOT inside any DB). Switching applies on the
        // next launch, so /api/prefs/active-database flags restartRequired and the
        // frontend follows up with POST /api/app/restart.
        app.MapGet("/api/prefs/databases", () =>
            Results.Ok(new PrefsDatabasesDto(PrefsDbPath.ActiveRelativePath(), PrefsDbPath.ListProfiles())));

        app.MapPost("/api/prefs/active-database", (SetActiveDatabaseRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.RelativePath))
                return Results.BadRequest(new { error = "relativePath required" });
            try
            {
                PrefsDbPath.SetActive(req.RelativePath);
                return Results.Ok(new { restartRequired = true });
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
                PrefsDbPath.CreateProfile(req.Name);
                return Results.Ok(new PrefsDatabasesDto(PrefsDbPath.ActiveRelativePath(), PrefsDbPath.ListProfiles()));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/prefs/databases/import", (ImportDatabaseRequest req) =>
        {
            try
            {
                PrefsDbPath.ImportProfile(req.SourcePath, req.Name);
                return Results.Ok(new PrefsDatabasesDto(PrefsDbPath.ActiveRelativePath(), PrefsDbPath.ListProfiles()));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // File-picker import: the webview can't hand the server a filesystem
        // path, so the chosen .db is uploaded as multipart and written into the
        // profiles dir here. Antiforgery is disabled — this is a loopback /
        // LAN-token API, not a cookie-auth form post.
        app.MapPost("/api/prefs/databases/upload", async (HttpRequest req) =>
        {
            try
            {
                if (!req.HasFormContentType)
                    return Results.BadRequest(new { error = "Expected a multipart file upload." });
                var form = await req.ReadFormAsync();
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "No file uploaded." });

                var nameField = form.TryGetValue("name", out var n) ? n.ToString() : null;
                // Strip any directory portion the browser might send. Modern
                // browsers submit the bare file name, but a full Windows path
                // (C:\dir\profile.db) must not become the profile name — on
                // Linux/macOS Path.GetFileName treats '\' as a normal
                // character and the whole mangled string would survive.
                var uploadedName = file.FileName;
                var lastSeparator = uploadedName.LastIndexOfAny(['/', '\\']);
                if (lastSeparator >= 0)
                    uploadedName = uploadedName[(lastSeparator + 1)..];
                var profileName = string.IsNullOrWhiteSpace(nameField)
                    ? Path.GetFileNameWithoutExtension(uploadedName)
                    : nameField;

                await using var stream = file.OpenReadStream();
                PrefsDbPath.ImportProfileFromStream(stream, profileName);
                return Results.Ok(new PrefsDatabasesDto(PrefsDbPath.ActiveRelativePath(), PrefsDbPath.ListProfiles()));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).DisableAntiforgery();

        // Download an existing profile so the operator can back it up or move
        // it to another machine (the download is named *.zeusdb — see below).
        // The payload is a MERGED snapshot: the profile's product prefs file
        // with every mergeable engine-owned collection replaced by the current
        // contents of the profile's engine database (PrefsProfileExport —
        // PureSignal's ps_settings is deliberately excluded), so the backup
        // captures the engine settings that have lived in station-engine.db
        // since the persistence split.
        //
        // The download filename is rewritten from ".db" to ".zeusdb" (#64):
        // Windows ham-radio logbook apps (N1MM+, Log4OM, HRD, ...) register
        // ".db" as their file type in the shell, so a double-clicked export
        // used to open the last-viewed logbook instead of anything Zeus. The
        // ".zeusdb" extension keeps a Zeus-specific identity so no other app
        // silently claims it. The bytes are raw LiteDB either way and Import
        // validates content, not the file extension.
        app.MapGet("/api/prefs/databases/export", (string? relativePath) =>
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return Results.BadRequest(new { error = "relativePath required" });
            try
            {
                var productPath = PrefsDbPath.ResolveProfileFullPath(relativePath);
                var enginePath = PrefsDbPath.EnginePathForProductPath(productPath);
                var bytes = PrefsProfileExport.BuildMergedSnapshot(productPath, enginePath);
                var downloadName = Path.ChangeExtension(Path.GetFileName(productPath), ".zeusdb");
                return Results.File(bytes, "application/octet-stream", downloadName);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { error = "Database not found." });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}
