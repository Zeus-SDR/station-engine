// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// SPE Expert 1.5K Taurus amplifier support. This file is GPL-3.0-or-later
// (see Station.Engine.Hosting/SpeTaurus/SOURCE.md); the rest of the engine is
// GPL-2.0-or-later, whose "or later" option permits the combination. The
// resulting engine binary is distributed as GPL-3.0-or-later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Zeus.Server.SpeTaurus;

internal sealed class ExpertAmpServerTunePreflight(
    SpeTaurusService taurus,
    IInstalledFeatureState features,
    IHttpClientFactory httpClientFactory,
    ILogger<ExpertAmpServerTunePreflight> log) : IAmplifierTunePreflight
{
    internal const string PluginId = "org.openhpsdr.speexperttaurus";
    internal const string HttpClientName = "SpeTaurusExpertAmpServer";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(75);

    public async Task<AmplifierTunePreflightResult> PrepareAsync(
        CancellationToken cancellationToken)
    {
        var config = taurus.Config;
        if (!features.IsActive(PluginId))
            return AmplifierTunePreflightResult.Success();
        if (config.ExpertServerUrl.Length == 0)
        {
            if (!config.Enabled)
                return Fail(
                    "The Taurus feature is active, but neither direct amplifier communication nor an Expert Amp Server URL is configured.");
            var direct = await taurus.TuneAsync(cancellationToken).ConfigureAwait(false);
            if (direct.Error is not null) return Fail(direct.Error);
            if (!features.IsActive(PluginId) || !ReferenceEquals(config, taurus.Config))
                return Fail("The Taurus feature or direct amplifier configuration changed while TUNE was arming; RF was not keyed.");
            return Ready(config, "direct amplifier");
        }

        using var featureIo = taurus.LinkFeatureIo(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(featureIo.Token);
        timeout.CancelAfter(config.TuneArmTimeoutMs);
        var token = timeout.Token;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            var status = await GetDataAsync<ExpertStatus>(
                client,
                $"{config.ExpertServerUrl}/api/v1/status",
                token).ConfigureAwait(false);

            // Identity is confirmed separately from the checksum-valid LCD
            // immediately below so older servers that omit modelName remain usable.
            var unsafeReason = UnsafeStatusReason(status, displayIdentityConfirmed: true);
            if (unsafeReason is not null) return Fail(unsafeReason);

            var before = await GetDisplayFrameAsync(client, config.ExpertServerUrl, token)
                .ConfigureAwait(false);
            var identityConfirmed = IsExpectedTaurus(status) || before.TaurusIdentity;
            if (!identityConfirmed)
                return Fail(
                    "Expert Amp Server did not provide checksum-valid SPE Expert 1.5K Taurus identity evidence.");
            if (before.Tune)
                return await ConfirmReadyAsync(client, config, identityConfirmed, token)
                    .ConfigureAwait(false);

            using var configLease = await taurus.TryAcquireConfigLeaseAsync(config, token)
                .ConfigureAwait(false);
            if (configLease is null
                || !features.IsActive(PluginId)
                || !ReferenceEquals(config, taurus.Config))
                return Fail(
                    "The Taurus feature or Expert Amp Server configuration changed while TUNE was arming; RF was not keyed.");

            // The display identity read above is a separate network round trip.
            // Re-read protocol-native state while the configuration lease is held
            // so a transition into OPERATE cannot race the TUNE button write.
            var preWriteStatus = await GetDataAsync<ExpertStatus>(
                client,
                $"{config.ExpertServerUrl}/api/v1/status",
                token).ConfigureAwait(false);
            var preWriteUnsafeReason = UnsafeStatusReason(
                preWriteStatus,
                identityConfirmed);
            if (preWriteUnsafeReason is not null) return Fail(preWriteUnsafeReason);

            using var response = await client.PostAsJsonAsync(
                $"{config.ExpertServerUrl}/api/v1/actions/button",
                new { name = "tune" },
                token).ConfigureAwait(false);
            var action = await ReadEnvelopeAsync<ExpertButtonResult>(response, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !action.Success || action.Data?.Sent != true)
                return Fail(action.Error ?? action.Message
                    ?? $"Expert Amp Server rejected TUNE ({(int)response.StatusCode}).");

            while (true)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                var display = await GetDisplayFrameAsync(client, config.ExpertServerUrl, token)
                    .ConfigureAwait(false);
                if (display.Tune)
                    return await ConfirmReadyAsync(client, config, identityConfirmed, token)
                        .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            featureIo.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Fail(
                "The Taurus feature changed while TUNE was arming; RF was not keyed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(
                "Taurus did not confirm its yellow TUNE indicator before the arm timeout; RF was not keyed.");
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "spe-taurus.expert-server tune preflight failed");
            return Fail($"Expert Amp Server is unavailable: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            log.LogWarning(ex, "spe-taurus.expert-server returned invalid tune evidence");
            return Fail(ex.Message);
        }
    }

    private static AmplifierTunePreflightResult Fail(string error) =>
        AmplifierTunePreflightResult.Fail($"Taurus TUN preflight failed: {error}");

    private async Task<AmplifierTunePreflightResult> ConfirmReadyAsync(
        HttpClient client,
        SpeTaurusConfig expectedConfig,
        bool displayIdentityConfirmed,
        CancellationToken cancellationToken)
    {
        // This is deliberately the final remote read before the coordinator
        // may enter Zeus's normal TUN state machine. Unknown TX is unsafe, and
        // an operator config/plugin change supersedes this preflight.
        var status = await GetDataAsync<ExpertStatus>(
            client,
            $"{expectedConfig.ExpertServerUrl}/api/v1/status",
            cancellationToken).ConfigureAwait(false);
        var unsafeReason = UnsafeStatusReason(status, displayIdentityConfirmed);
        if (unsafeReason is not null) return Fail(unsafeReason);
        if (!features.IsActive(PluginId) || !ReferenceEquals(expectedConfig, taurus.Config))
            return Fail("The Taurus feature or Expert Amp Server configuration changed while TUNE was arming; RF was not keyed.");
        return Ready(expectedConfig, "Expert Amp Server");
    }

    private AmplifierTunePreflightResult Ready(SpeTaurusConfig expectedConfig, string source) =>
        AmplifierTunePreflightResult.Success(
            stillReady: () => features.IsActive(PluginId)
                && ReferenceEquals(expectedConfig, taurus.Config),
            readinessError: $"Taurus TUN preflight failed: The Taurus feature or {source} configuration changed before RF was keyed.");

    private static string? UnsafeStatusReason(
        ExpertStatus status,
        bool displayIdentityConfirmed)
    {
        if (!status.RecentContact)
            return "Expert Amp Server has no recent contact with the Taurus.";
        if (!HasAuthoritativeStatus(status))
            return "Expert Amp Server did not provide fresh protocol-native Taurus status evidence.";
        if (!displayIdentityConfirmed && !IsExpectedTaurus(status))
            return "Expert Amp Server did not identify an SPE Expert 1.5K Taurus.";
        if (status.Tx is not false)
            return status.Tx == true
                ? "Taurus tuning is blocked because the amplifier reports TX."
                : "Taurus tuning is blocked because the amplifier TX state is unknown.";
        var operatingState = string.IsNullOrWhiteSpace(status.OperatingState)
            ? status.Mode
            : status.OperatingState;
        return string.Equals(operatingState, "standby", StringComparison.OrdinalIgnoreCase)
            ? null
            : "Taurus tuning is allowed only in STANDBY. The amplifier currently reports "
                + $"{(string.IsNullOrWhiteSpace(operatingState) ? "an unknown state" : operatingState)}.";
    }

    private static async Task<ExpertDisplayEvidence> GetDisplayFrameAsync(
        HttpClient client,
        string serverUrl,
        CancellationToken cancellationToken)
    {
        var frame = await GetDataAsync<ExpertDisplayFrame>(
            client,
            $"{serverUrl}/api/v1/display/frame",
            cancellationToken).ConfigureAwait(false);
        var flags = frame.LcdFlags;
        if (flags?.ChecksumPresent != true || flags.ChecksumValid != true || flags.Leds is null)
            throw new InvalidDataException(
                "Expert Amp Server did not provide checksum-valid Taurus display evidence; RF was not keyed.");
        return new(
            flags.Leds.Tune,
            frame.ScreenText?.Contains("EXPERT 1.5K TAURUS", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool HasAuthoritativeStatus(ExpertStatus status) =>
        ExpertAmpServerEvidence.HasFreshProtocolStatus(
            status.Source,
            status.Confidence,
            status.Provenance,
            status.LastContactAt);

    private static bool IsExpectedTaurus(ExpertStatus status) =>
        status.ModelName?.Contains("TAURUS", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<T> GetDataAsync<T>(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var envelope = await ReadEnvelopeAsync<T>(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || !envelope.Success || envelope.Data is null)
            throw new InvalidDataException(envelope.Error ?? envelope.Message
                ?? $"Expert Amp Server returned {(int)response.StatusCode}.");
        return envelope.Data;
    }

    private static async Task<ExpertEnvelope<T>> ReadEnvelopeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ExpertEnvelope<T>>(
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Expert Amp Server returned an empty response.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException("Expert Amp Server returned malformed JSON.", ex);
        }
    }

    private sealed record ExpertEnvelope<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("data")] T? Data);

    private sealed record ExpertStatus(
        [property: JsonPropertyName("modelName")] string? ModelName,
        [property: JsonPropertyName("operatingState")] string? OperatingState,
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("tx")] bool? Tx,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("confidence")] string? Confidence,
        [property: JsonPropertyName("provenance")] string? Provenance,
        [property: JsonPropertyName("recentContact")] bool RecentContact,
        [property: JsonPropertyName("lastContactAt")] string? LastContactAt);

    private sealed record ExpertButtonResult(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("sent")] bool Sent);

    private sealed record ExpertDisplayFrame(
        [property: JsonPropertyName("screenText")] string? ScreenText,
        [property: JsonPropertyName("lcdFlags")] ExpertLcdFlags? LcdFlags);

    private sealed record ExpertLcdFlags(
        [property: JsonPropertyName("checksumPresent")] bool ChecksumPresent,
        [property: JsonPropertyName("checksumValid")] bool ChecksumValid,
        [property: JsonPropertyName("leds")] ExpertLeds? Leds);

    private sealed record ExpertLeds(
        [property: JsonPropertyName("tune")] bool Tune);

    private readonly record struct ExpertDisplayEvidence(bool Tune, bool TaurusIdentity);
}
