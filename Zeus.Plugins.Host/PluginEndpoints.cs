// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host.Registry;

namespace Zeus.Plugins.Host;

/// <summary>
/// REST endpoints for the plugin system. Mounts under <c>/api/plugins</c>.
/// Plugin-owned endpoints (from <see cref="IBackendPlugin"/>) land under
/// <c>/api/plugins/{id}/...</c> and are mapped during activation by
/// <see cref="MapAll"/> — call once at app start.
/// </summary>
public static class PluginEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app, PluginManager manager)
    {
        app.MapGet("/api/plugins", () =>
        {
            var items = manager.Catalog.Select(ToDto).ToArray();
            return Results.Ok(new PluginListResponse
            {
                SdkAbi = AbiVersion.Current,
                SdkVersion = AbiVersion.SdkVersion,
                Plugins = items,
            });
        });

        app.MapGet("/api/plugins/{id}", (string id) =>
        {
            var p = manager.FindInstalled(id);
            return p is null ? Results.NotFound() : Results.Ok(ToDto(p));
        });

        app.MapGet("/api/plugins/registry", async (
            IRegistryClient registry, CancellationToken ct) =>
        {
            try
            {
                var catalog = await registry.FetchAsync(ct);
                catalog = PluginIdMigrations.FilterSupersededEntries(catalog);
                return Results.Ok(new RegistryResponse { SourceUrl = registry.SourceUrl, Catalog = catalog });
            }
            catch (RegistryFetchException ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    title: "registry-fetch-failed",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/plugins/install", async (
            InstallRequest req,
            PluginInstaller installer,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            try
            {
                if (string.Equals(req.Source, "registry", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(req.Id))
                {
                    var accessGate = services.GetService<IPluginInstallAccessGate>()
                        ?? new AllowAllPluginInstallAccessGate();
                    var access = await accessGate.CheckInstallAsync(req.Id, ct).ConfigureAwait(false);
                    if (!access.Allowed)
                    {
                        return Results.Json(
                            new { error = access.Reason ?? "Plugin subscription required" },
                            statusCode: StatusCodes.Status402PaymentRequired);
                    }
                }

                InstalledPlugin installed = req.Source switch
                {
                    "zip-url"  => await installer.InstallFromZipUrlAsync(req.Url ?? "", req.Sha256, ct),
                    "url"      => await installer.InstallFromZipUrlAsync(req.Url ?? "", req.Sha256, ct),
                    "file"     => await installer.InstallFromZipFileAsync(req.FilePath ?? "", ct),
                    "registry" => await installer.InstallFromRegistryAsync(req.Id ?? "", req.Version ?? "", ct),
                    _          => throw new PluginInstallException($"unknown source '{req.Source}'"),
                };
                return Results.Ok(ToDto(installed.Activated));
            }
            catch (PluginInstallException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (RegistryFetchException ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    title: "registry-fetch-failed",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/plugins/install/zip", async (
            HttpRequest req,
            PluginInstaller installer,
            CancellationToken ct) =>
        {
            string? tempZip = null;
            try
            {
                if (!req.HasFormContentType)
                    return Results.BadRequest(new { error = "Expected a multipart plugin zip upload." });

                var form = await req.ReadFormAsync(ct).ConfigureAwait(false);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "No plugin zip uploaded." });

                if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "Plugin package must be a .zip file." });

                tempZip = Path.Combine(Path.GetTempPath(), $"zeus-plugin-{Guid.NewGuid():N}.zip");
                await using (var dst = File.Create(tempZip))
                {
                    await file.CopyToAsync(dst, ct).ConfigureAwait(false);
                }

                var installed = await installer.InstallFromZipFileAsync(tempZip, ct).ConfigureAwait(false);
                return Results.Ok(ToDto(installed.Activated));
            }
            catch (PluginInstallException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                if (tempZip is not null)
                {
                    try { File.Delete(tempZip); } catch { /* ignore */ }
                }
            }
        }).DisableAntiforgery();

        app.MapDelete("/api/plugins/{id}", async (
            string id, PluginInstaller installer, CancellationToken ct) =>
        {
            try
            {
                await installer.UninstallAsync(id, ct);
                return Results.NoContent();
            }
            catch (PluginInstallException ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    title: "plugin-uninstall-deferred",
                    statusCode: StatusCodes.Status202Accepted);
            }
        });

        // Static UI module files. Plugins ship ES modules under
        // <PluginRoot>/<id>/ui/<file>.js; the frontend dynamic-imports
        // them via this route to register panels with the workspace.
        app.MapGet("/api/plugins/{id}/ui/{*path}", (string id, string path, HttpContext http) =>
        {
            var p = manager.FindInstalled(id);
            if (p is null) return Results.NotFound();

            // Dev iteration aid: re-installs swap the file on disk; without
            // no-cache headers the browser holds the previous module forever
            // since the URL is stable. Production hosting can fingerprint
            // these later if needed.
            http.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            http.Response.Headers["Pragma"] = "no-cache";

            var pluginDir = p.PluginDir;
            var uiDir = Path.GetFullPath(Path.Combine(pluginDir, "ui"));
            var fullPath = Path.GetFullPath(Path.Combine(uiDir, path));

            // Guard against `../` traversal.
            if (!fullPath.StartsWith(uiDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && fullPath != uiDir)
                return Results.NotFound();

            if (!File.Exists(fullPath)) return Results.NotFound();

            var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".js"   => "application/javascript",
                ".mjs"  => "application/javascript",
                ".css"  => "text/css",
                ".json" => "application/json",
                ".map"  => "application/json",
                _        => "application/octet-stream",
            };
            return Results.File(fullPath, contentType);
        });

        // Per-plugin endpoints from IBackendPlugin flow through ONE mutable
        // data source registered in the route table at startup, so a plugin
        // installed / uninstalled / reinstalled MID-SESSION goes live (or
        // dark) on the next request — no restart. Mapping routes directly
        // onto `app` after the server has started silently does nothing
        // (the matcher never re-reads them), which is why store installs
        // used to 404 until restart.
        var backendRoutes = new PluginBackendEndpointDataSource();
        app.DataSources.Add(backendRoutes);
        manager.PluginActivated += p => backendRoutes.SetPluginEndpoints(
            p.Loaded.Manifest.Id, BuildBackendEndpoints(app.ServiceProvider, p));
        manager.PluginDeactivated += p => backendRoutes.RemovePluginEndpoints(
            p.Loaded.Manifest.Id);
        // Seed plugins already active (startup activation runs before MapAll).
        // Subscribing first closes the gap for an activation racing this loop;
        // SetPluginEndpoints is idempotent per id so a double-publish is fine.
        foreach (var p in manager.Active)
        {
            backendRoutes.SetPluginEndpoints(
                p.Loaded.Manifest.Id, BuildBackendEndpoints(app.ServiceProvider, p));
        }
    }

    /// <summary>
    /// Build (without registering) one activated plugin's
    /// <c>/api/plugins/{id}/...</c> endpoints, for publication via
    /// <see cref="PluginBackendEndpointDataSource"/>. Returns an empty list
    /// for non-backend plugins and for plugins whose MapEndpoints throws —
    /// a bad plugin endpoint mapping must not take down the host.
    /// </summary>
    internal static IReadOnlyList<Microsoft.AspNetCore.Http.Endpoint> BuildBackendEndpoints(
        IServiceProvider services, ActivatedPlugin p)
    {
        if (p.Loaded.Plugin is not IBackendPlugin backend)
            return Array.Empty<Microsoft.AspNetCore.Http.Endpoint>();
        try
        {
            var builder = new DetachedEndpointRouteBuilder(services);
            var group = builder.MapGroup($"/api/plugins/{p.Loaded.Manifest.Id}");
            backend.MapEndpoints(group);
            // .Endpoints forces endpoint construction — handler-signature
            // errors surface here, inside the try, not at request time.
            return builder.DataSources.SelectMany(ds => ds.Endpoints).ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[plugins] {p.Loaded.Manifest.Id}: MapEndpoints threw: {ex.Message}");
            return Array.Empty<Microsoft.AspNetCore.Http.Endpoint>();
        }
    }

    /// <summary>
    /// True when a plugin id was minted by the VST3 / AU directory scanners
    /// (the <c>com.openhpsdr.zeus.{vst,rxvst,au,rxau}.*</c> namespaces). These
    /// are operator-scanned audio plugins that live ONLY in the Audio Suite
    /// rack — they are not Zeus plugin-repo plugins, so the Settings ▸ Plugins
    /// list filters them out. Native Zeus audio plugins (e.g. the
    /// <c>com.openhpsdr.zeus.samples.*</c> chain) are NOT scanned and stay in
    /// the list. Reuses the scanners' own id-prefix predicates so the
    /// classification has a single source of truth.
    /// </summary>
    public static bool IsScannedAudioPlugin(string id) =>
        VstDirectoryScanService.IsTxPluginId(id) ||
        VstDirectoryScanService.IsRxPluginId(id) ||
        AuComponentScanService.IsTxPluginId(id) ||
        AuComponentScanService.IsRxPluginId(id);

    internal static PluginDto ToDto(ActivatedPlugin p) =>
        ToDto(p.Loaded.Manifest, p.Context.GrantedCapabilities);

    internal static PluginDto ToDto(PluginCatalogEntry p) =>
        ToDto(p.Manifest, p.Manifest.ParseCapabilities());

    private static PluginDto ToDto(PluginManifest manifest, PluginCapabilities granted) => new()
    {
        Id = manifest.Id,
        Scanned = IsScannedAudioPlugin(manifest.Id),
        Name = manifest.Name,
        Version = manifest.Version,
        Author = manifest.Author,
        Description = manifest.Description,
        Homepage = manifest.Homepage,
        License = manifest.License,
        Capabilities = granted.ToString().Split(", "),
        Ui = manifest.Ui is null ? null : new PluginUiDto
        {
            Modules = manifest.Ui.Modules,
            Panels = manifest.Ui.Panels.Select(panel => new PluginPanelDto
            {
                Id = panel.Id,
                Title = panel.Title,
                Icon = panel.Icon,
                Slot = panel.Slot,
                Category = panel.Category,
            }).ToArray(),
        },
        Audio = manifest.Audio is { } a ? new PluginAudioDto
        {
            Vst3Path = a.Vst3Path,
            Slot = a.Slot,
            Channels = a.Channels,
            SampleRate = a.SampleRate,
        } : null,
    };
}

public sealed record PluginListResponse
{
    public int SdkAbi { get; init; }
    public string SdkVersion { get; init; } = "";
    public IReadOnlyList<PluginDto> Plugins { get; init; } = Array.Empty<PluginDto>();
}

public sealed record PluginDto
{
    public string Id { get; init; } = "";

    /// <summary>
    /// True for operator-scanned VST3 / Audio Unit plugins (the
    /// <c>com.openhpsdr.zeus.{vst,rxvst,au,rxau}.*</c> namespaces). These belong
    /// to the Audio Suite rack only; the Settings ▸ Plugins list hides them so
    /// it shows just Zeus plugin-repo plugins. Defaults false.
    /// </summary>
    public bool Scanned { get; init; }

    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Homepage { get; init; }
    public string License { get; init; } = "";
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public PluginUiDto? Ui { get; init; }
    public PluginAudioDto? Audio { get; init; }
}

public sealed record PluginUiDto
{
    public IReadOnlyList<string> Modules { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PluginPanelDto> Panels { get; init; } = Array.Empty<PluginPanelDto>();
}

public sealed record PluginPanelDto
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Slot { get; init; } = "";
    public string Category { get; init; } = "plugins";
}

public sealed record PluginAudioDto
{
    public string? Vst3Path { get; init; }
    public string Slot { get; init; } = "";
    public int Channels { get; init; }
    public int SampleRate { get; init; }
}

public sealed record RegistryResponse
{
    public string SourceUrl { get; init; } = "";
    public Zeus.Plugins.Contracts.Registry.RegistryCatalog Catalog { get; init; }
        = new Zeus.Plugins.Contracts.Registry.RegistryCatalog();
}

public sealed record InstallRequest
{
    /// <summary>One of: "zip-url", "url", "file", "registry".</summary>
    public string Source { get; init; } = "zip-url";

    /// <summary>HTTPS download URL to a plugin zip. Used when Source = "zip-url" or "url".</summary>
    public string? Url { get; init; }

    /// <summary>Absolute path to a .zip on disk. Used when Source = "file".</summary>
    public string? FilePath { get; init; }

    /// <summary>Optional hex SHA-256 of the zip, verified before extraction.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Plugin id. Used when Source = "registry".</summary>
    public string? Id { get; init; }

    /// <summary>Plugin version. Used when Source = "registry".</summary>
    public string? Version { get; init; }
}
