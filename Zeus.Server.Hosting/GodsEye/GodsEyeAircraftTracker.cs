// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
using System.Net;
using System.Text.Json;

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.GodsEye;
#else
namespace Zeus.Server.GodsEye;
#endif

public sealed record GodsEyeAircraftReport(string State, GodsEyeItemDto? Item, string? Reason, int RetryAfterSeconds = 5, DateTimeOffset? FetchedUtc = null);

/// <summary>Quota-bounded, on-demand position reports for the aircraft being inspected.</summary>
public sealed class GodsEyeAircraftTracker(IHttpClientFactory clients, TimeProvider? timeProvider = null) : IDisposable
{
    public const string HttpClientName = "GodsEyeAircraftTracker";
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset Expires, GodsEyeAircraftReport Report)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _retryUtc;
    private DateTimeOffset _nextRequestUtc;
    public static bool IsValidIcao(string value) => value.Length == 6 && value.All(Uri.IsHexDigit);

    public async Task<GodsEyeAircraftReport> GetAsync(string icao, CancellationToken cancellationToken)
    {
        if (!IsValidIcao(icao)) throw new ArgumentException("Aircraft address must be six hexadecimal characters.", nameof(icao));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (_cache.TryGetValue(icao, out var cached) && cached.Expires > now) return cached.Report with { FetchedUtc = now };
            if (_retryUtc > now) return new(GodsEyeFreshness.RateLimited, null, "Aircraft provider is cooling down.",
                Math.Max(5, (int)Math.Ceiling((_retryUtc - now).TotalSeconds)));
            if (_nextRequestUtc > now) return new(GodsEyeFreshness.RateLimited, null, "Tracking update queued; retry scheduled.");
            _nextRequestUtc = now.AddSeconds(1);
            GodsEyeAircraftReport report;
            try
            {
                using var client = clients.CreateClient(HttpClientName);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await client.GetAsync($"https://api.adsb.lol/v2/icao/{icao.ToLowerInvariant()}", timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - now) ?? TimeSpan.FromSeconds(60);
                    var seconds = (int)Math.Clamp(Math.Ceiling(delay.TotalSeconds), 5, 900);
                    _retryUtc = now.AddSeconds(seconds);
                    report = new(GodsEyeFreshness.RateLimited, null, "Aircraft provider rate limit; retry scheduled.", seconds);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    var item = GodsEyeParsers.ParseMilitaryFlights(payload).FirstOrDefault(item => string.Equals(item.Id, icao, StringComparison.OrdinalIgnoreCase));
                    var age = item is null ? double.PositiveInfinity : (_clock.GetUtcNow() - item.TimestampUtc).TotalSeconds;
                    report = item is null ? new(GodsEyeFreshness.Unavailable, null, "No current position in ADSB.lol coverage.")
                        : age < -5 ? new(GodsEyeFreshness.Unavailable, null, "Provider position has an invalid future timestamp.")
                        : new(age <= 30 ? GodsEyeFreshness.Live : GodsEyeFreshness.Stale, item,
                            age <= 30 ? null : "Last reported position is over 30 seconds old.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { report = new(GodsEyeFreshness.Unavailable, null, "Aircraft update timed out."); }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or ArgumentOutOfRangeException)
            { report = new(GodsEyeFreshness.Unavailable, null, "Aircraft provider is temporarily unavailable."); }
            if (_cache.Count >= 64)
            {
                var oldest = _cache.MinBy(entry => entry.Value.Expires).Key;
                _cache.Remove(oldest);
            }
            _cache[icao] = (_clock.GetUtcNow().AddSeconds(report.RetryAfterSeconds), report);
            return report with { FetchedUtc = _clock.GetUtcNow() };
        }
        finally { _gate.Release(); }
    }
    public void Dispose() => _gate.Dispose();
}
