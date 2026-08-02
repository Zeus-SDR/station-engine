// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;

namespace Zeus.Plugins.Host.Registry;

public interface IPluginPackageDownloader
{
    Task<DownloadedPluginPackage> DownloadAndVerifyToTempFileAsync(
        string url,
        string? expectedSha256,
        CancellationToken ct);
}

public sealed class DownloadedPluginPackage : IDisposable
{
    internal DownloadedPluginPackage(string path, bool deleteOnDispose = true)
    {
        Path = path;
        _deleteOnDispose = deleteOnDispose;
    }

    private readonly bool _deleteOnDispose;

    public string Path { get; }

    public void Dispose()
    {
        if (!_deleteOnDispose) return;
        try { File.Delete(Path); } catch { /* ignore */ }
    }
}

public sealed class PluginPackageDownloader : IPluginPackageDownloader
{
    private readonly HttpClient _http;

    public PluginPackageDownloader(HttpClient http) => _http = http;

    public async Task<DownloadedPluginPackage> DownloadAndVerifyToTempFileAsync(
        string url,
        string? expectedSha256,
        CancellationToken ct)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new PluginInstallException($"refusing non-HTTPS download URL: {url}");

        var tempZip = System.IO.Path.GetTempFileName();
        try
        {
            await DownloadAsync(url, tempZip, ct).ConfigureAwait(false);
            if (expectedSha256 is { Length: > 0 })
                VerifySha256(tempZip, expectedSha256);
            return new DownloadedPluginPackage(tempZip);
        }
        catch
        {
            try { File.Delete(tempZip); } catch { /* ignore */ }
            throw;
        }
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        // A malformed registry URL must surface as a readable install error,
        // not an unhandled 500 from the Uri constructor.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl))
            throw new PluginInstallException($"plugin download failed: registry returned an invalid download URL");
        var displayUrl = parsedUrl.GetLeftPart(UriPartial.Path);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PluginInstallException(
                $"plugin download failed: {ex.Message} ({displayUrl})", ex);
        }
        // HttpClient's internal request timeout surfaces as TaskCanceledException
        // (not HttpRequestException); if the caller's own token didn't fire, treat
        // it as an install-time download failure so the endpoint returns 400 with
        // a readable message instead of an unhandled 500.
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginInstallException(
                $"plugin download timed out fetching {displayUrl}", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                throw new PluginInstallException(
                    $"plugin download failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from {displayUrl}");
            }

            try
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new PluginInstallException(
                    $"plugin download failed mid-transfer: {ex.Message} ({displayUrl})", ex);
            }
            catch (IOException ex)
            {
                throw new PluginInstallException(
                    $"plugin download failed mid-transfer: {ex.Message} ({displayUrl})", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Destination not writable (permissions/read-only plugins dir):
                // same operator-readable shape as the transfer failures above.
                throw new PluginInstallException(
                    $"plugin download failed writing to disk: {ex.Message}", ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new PluginInstallException(
                    $"plugin download timed out mid-transfer fetching {displayUrl}", ex);
            }
        }
    }

    private static void VerifySha256(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        var actual = Convert.ToHexString(bytes);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new PluginInstallException(
                $"sha256 mismatch — expected {expected.ToLowerInvariant()}, got {actual.ToLowerInvariant()}");
    }
}
