// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Zeus.Server.SpeTaurus;

internal sealed record ExpertAmpServerDiscoveryResult(
    bool Found,
    string? Url,
    string? ModelName,
    string? Source,
    IReadOnlyList<string> Probed);

internal sealed class ExpertAmpServerDiscovery(
    IHttpClientFactory httpClientFactory,
    ILogger<ExpertAmpServerDiscovery> log)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(900);

    internal async Task<ExpertAmpServerDiscoveryResult> DiscoverAsync(
        SpeTaurusConfig config,
        string? radioEndpoint,
        CancellationToken cancellationToken)
    {
        var candidates = CandidateUrls(config, radioEndpoint);
        var probes = candidates.Select((candidate, index) =>
            ProbeAsync(candidate.Url, candidate.Source, index, cancellationToken)).ToArray();
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        var found = results
            .Where(result => result is not null)
            .OrderBy(result => result!.Order)
            .FirstOrDefault();
        return found is null
            ? new(false, null, null, null, candidates.Select(candidate => candidate.Url).ToArray())
            : new(true, found.Url, found.ModelName, found.Source,
                candidates.Select(candidate => candidate.Url).ToArray());
    }

    internal static IReadOnlyList<(string Url, string Source)> CandidateUrls(
        SpeTaurusConfig config,
        string? radioEndpoint)
    {
        var candidates = new List<(string Url, string Source)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string url, string source)
        {
            var normalized = url.TrimEnd('/');
            if (seen.Add(normalized)) candidates.Add((normalized, source));
        }

        if (config.ExpertServerUrl.Length > 0)
            Add(config.ExpertServerUrl, "configured");
        if (RadioService.TryParseEndpoint(radioEndpoint ?? "", out var radio))
            Add(new UriBuilder("http", radio.Address.ToString(), 8088).Uri.GetLeftPart(UriPartial.Authority),
                "connected-radio");
        if (SpeTaurusService.IsValidHost(config.BridgeHost))
            Add(new UriBuilder("http", config.BridgeHost, 8088).Uri.GetLeftPart(UriPartial.Authority),
                "configured-g2-host");

        Add("http://g2-radio.local:8088", "g2-mdns");
        Add("http://saturn.local:8088", "saturn-mdns");
        Add("http://saturn-radxa-cm5-8inch.local:8088", "saturn-mdns");
        Add("http://127.0.0.1:8088", "same-host");
        return candidates;
    }

    private async Task<ProbeResult?> ProbeAsync(
        string url,
        string source,
        int order,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var client = httpClientFactory.CreateClient(ExpertAmpServerTunePreflight.HttpClientName);
            using var response = await client.GetAsync($"{url}/api/v1/status", timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var envelope = await response.Content.ReadFromJsonAsync<DiscoveryEnvelope>(
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var status = envelope?.Data;
            var model = status?.ModelName?.Trim();
            if (envelope?.Success != true
                || status?.RecentContact != true
                || !string.Equals(status.Source, "serial", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(status.Confidence, "protocol-native", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(status.Provenance, "status-poll", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!ExpertAmpServerEvidence.MentionsTaurus(model))
            {
                if (!CanUseDisplayIdentityFallback(model)) return null;
                using var displayResponse = await client.GetAsync(
                    $"{url}/api/v1/display/frame",
                    timeout.Token).ConfigureAwait(false);
                if (!displayResponse.IsSuccessStatusCode) return null;
                var displayEnvelope = await displayResponse.Content
                    .ReadFromJsonAsync<DiscoveryDisplayEnvelope>(
                        cancellationToken: timeout.Token).ConfigureAwait(false);
                var display = displayEnvelope?.Data;
                if (displayEnvelope?.Success != true
                    || display?.LcdFlags?.ChecksumPresent != true
                    || display.LcdFlags.ChecksumValid != true
                    || !ExpertAmpServerEvidence.HasTaurusDisplayBanner(display.ScreenText))
                    return null;
                model = ExpertAmpServerEvidence.TaurusDisplayBanner;
            }
            return new(order, url, model, source);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            log.LogDebug(ex, "spe-taurus.discovery probe failed url={Url}", url);
            return null;
        }
    }

    private sealed record DiscoveryEnvelope(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] DiscoveryStatus? Data);

    private sealed record DiscoveryStatus(
        [property: JsonPropertyName("modelName")] string? ModelName,
        [property: JsonPropertyName("recentContact")] bool RecentContact,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("confidence")] string? Confidence,
        [property: JsonPropertyName("provenance")] string? Provenance);

    private sealed record DiscoveryDisplayEnvelope(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] DiscoveryDisplay? Data);

    private sealed record DiscoveryDisplay(
        [property: JsonPropertyName("screenText")] string? ScreenText,
        [property: JsonPropertyName("lcdFlags")] DiscoveryLcdFlags? LcdFlags);

    private sealed record DiscoveryLcdFlags(
        [property: JsonPropertyName("checksumPresent")] bool ChecksumPresent,
        [property: JsonPropertyName("checksumValid")] bool ChecksumValid);

    private static bool CanUseDisplayIdentityFallback(string? modelName) =>
        ExpertAmpServerEvidence.CanUseDisplayIdentityFallback(modelName);

    private sealed record ProbeResult(int Order, string Url, string ModelName, string Source);
}
