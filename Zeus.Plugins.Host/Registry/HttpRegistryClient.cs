// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts.Registry;

namespace Zeus.Plugins.Host.Registry;

/// <summary>
/// HTTPS-fetched registry client. Default source is the
/// public Zeus SDR plugin-management registry; operators can override via
/// <c>ZEUS_PLUGIN_REGISTRY_URL</c> or <see cref="RegistryClientOptions.SourceUrl"/>.
/// </summary>
public sealed class HttpRegistryClient : IRegistryClient
{
    public const string SourceUrlEnvVar = "ZEUS_PLUGIN_REGISTRY_URL";

    public const string DefaultUrl =
        "https://downloads.zeussdr.com/plugins/registry.json";

    private readonly HttpClient _http;
    private readonly ILogger<HttpRegistryClient>? _log;
    private readonly RegistryClientOptions _options;

    private RegistryCatalog? _cached;
    private DateTimeOffset _cachedAt;

    public HttpRegistryClient(
        HttpClient http,
        RegistryClientOptions? options = null,
        ILogger<HttpRegistryClient>? log = null)
    {
        _http = http;
        _log = log;
        _options = options ?? new RegistryClientOptions();
    }

    public string SourceUrl => _options.SourceUrl;

    public async Task<RegistryCatalog> FetchAsync(CancellationToken ct)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < _options.CacheTtl)
        {
            return _cached;
        }

        if (!_options.SourceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !_options.SourceUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) &&
            !_options.SourceUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            throw new RegistryFetchException(
                $"refusing to fetch registry over insecure transport: {_options.SourceUrl}");
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _options.SourceUrl);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var catalog = await resp.Content
                .ReadFromJsonAsync<RegistryCatalog>(cancellationToken: ct)
                .ConfigureAwait(false);

            if (catalog is null)
                throw new RegistryFetchException("registry response deserialised to null");

            _cached = catalog;
            _cachedAt = DateTimeOffset.UtcNow;
            _log?.LogInformation(
                "Fetched plugin registry from {Url}: {Count} entries",
                _options.SourceUrl, catalog.Plugins.Count);

            return catalog;
        }
        catch (HttpRequestException ex)
        {
            throw new RegistryFetchException(
                $"failed to fetch registry from {_options.SourceUrl}: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new RegistryFetchException(
                $"registry at {_options.SourceUrl} is not valid JSON: {ex.Message}", ex);
        }
        // HttpClient's internal request timeout surfaces as TaskCanceledException
        // (not HttpRequestException); if the caller's own token didn't fire, treat
        // it as a registry-fetch failure so the endpoint returns 502 instead of 500.
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RegistryFetchException(
                $"timed out fetching registry from {_options.SourceUrl}", ex);
        }
    }

    /// <summary>Force a refresh on the next FetchAsync call.</summary>
    public void InvalidateCache() => _cached = null;

    public static string NormalizeSourceUrl(string? sourceUrl)
    {
        var source = string.IsNullOrWhiteSpace(sourceUrl) ? DefaultUrl : sourceUrl.Trim();
        return IsLegacyZeusPluginRegistryUrl(source) ? DefaultUrl : source;
    }

    private static bool IsLegacyZeusPluginRegistryUrl(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host == "downloads.openhpsdrzeus.com"
            && segments.Length >= 2
            && string.Equals(segments[^2], "plugins", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[^1], "registry.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (host == "raw.githubusercontent.com"
            && segments.Length >= 4
            && string.Equals(segments[1], "openhpsdr-zeus-plugins", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[^1], "registry.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return host == "github.com"
            && segments.Length >= 5
            && string.Equals(segments[1], "openhpsdr-zeus-plugins", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[^1], "registry.json", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record RegistryClientOptions
{
    private readonly string _sourceUrl = HttpRegistryClient.NormalizeSourceUrl(
        Environment.GetEnvironmentVariable(HttpRegistryClient.SourceUrlEnvVar));

    public string SourceUrl
    {
        get => _sourceUrl;
        init => _sourceUrl = HttpRegistryClient.NormalizeSourceUrl(value);
    }

    /// <summary>How long the in-memory catalog cache is reused. Default 5 minutes.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);
}
