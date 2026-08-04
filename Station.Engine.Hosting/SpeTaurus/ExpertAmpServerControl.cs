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

internal readonly record struct AutomaticTuneOperateRestoreResult(
    bool Restored,
    SpeTaurusStatus? Status,
    string? Error);

internal readonly record struct AutomaticTuneStandbyResult(
    bool Verified,
    SpeTaurusStatus? Status,
    string? Error);

internal sealed record SpeRemoteActionResult(
    bool Success,
    bool Sent,
    bool Confirmed,
    string State,
    string Message,
    SpeTaurusStatus? Status);

internal sealed record SpeDisplayTextSpan(
    int Row,
    int StartColumn,
    int EndColumn,
    string Text);

internal sealed record SpeDisplayText(
    IReadOnlyList<string> Rows,
    IReadOnlyList<SpeDisplayTextSpan> HighlightedSpans,
    string SelectedText,
    ulong Sequence,
    DateTimeOffset UpdatedAt,
    string Source,
    string ModelName,
    string ScreenText);

/// <summary>
/// Selects the direct SPE transport or the Expert Amp Server owned by the G2.
/// Remote commands are single-shot front-panel button actions, so every write
/// is preceded by fresh protocol-native safety evidence and is never retried.
/// </summary>
internal sealed class ExpertAmpServerControl : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan AutomaticTuneOperateConfirmationTimeout =
        TimeSpan.FromSeconds(3);
    private static readonly string[] Bands =
    [
        "160m", "80m", "60m", "40m", "30m", "20m",
        "17m", "15m", "12m", "10m", "6m", "4m",
    ];

    private readonly SpeTaurusService _taurus;
    private readonly IInstalledFeatureState _features;
    private readonly IInstalledFeatureChangeSource? _featureChanges;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _featureGate = new();
    private readonly object _identityGate = new();
    private readonly object _remotePowerGate = new();
    private CancellationTokenSource _featureCancellation = new();
    private SpeTaurusConfig? _confirmedIdentityConfig;
    private SpeTaurusConfig? _ambiguousRemotePowerConfig;
    private string? _ambiguousRemotePowerReason;
    private bool _featureActive;
    private bool _disposed;

    public ExpertAmpServerControl(
        SpeTaurusService taurus,
        IInstalledFeatureState features,
        IHttpClientFactory httpClientFactory,
        ILogger<ExpertAmpServerControl> log)
    {
        _taurus = taurus;
        _features = features;
        _httpClientFactory = httpClientFactory;
        _log = log;
        _featureChanges = features as IInstalledFeatureChangeSource;
        _featureActive = FeatureActive;
        if (_featureChanges is not null)
            _featureChanges.Changed += OnFeatureStateChanged;
        if (!_featureActive)
            _featureCancellation.Cancel();
    }

    internal async Task<SpeTaurusStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var config = _taurus.Config;
        ClearRemotePowerAmbiguityForConfigChange(config);
        if (config.ExpertServerUrl.Length == 0)
            return _taurus.Status();
        if (!FeatureActive)
        {
            ForgetTaurusIdentity();
            return RemoteUnavailable(config, "feature-inactive", "feature-inactive");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(config.ConnectTimeoutMs);
            var remote = await GetStatusAsync(config, timeout.Token).ConfigureAwait(false);
            return ToPanelStatus(config, remote);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ForgetTaurusIdentity(config);
            return RemoteUnavailable(
                config,
                "faulted",
                "Timed out reading live status from Expert Amp Server.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            ForgetTaurusIdentity(config);
            _log.LogDebug(ex, "spe-taurus.expert-server status failed");
            return RemoteUnavailable(
                config,
                "faulted",
                $"Expert Amp Server is unavailable: {ex.Message}");
        }
    }

    internal Task<SpeTaurusStatus> SetOperateAsync(
        bool operate,
        CancellationToken cancellationToken) =>
        ExecuteAsync(RemoteCommand.Operate, operate, cancellationToken);

    internal async Task<(SpeTaurusStatus Status, bool WasOperate)>
        EnterStandbyForAutomaticTuneAsync(CancellationToken cancellationToken)
    {
        bool? wasOperate = null;
        var status = await ExecuteAsync(
                RemoteCommand.Operate,
                false,
                cancellationToken,
                operate => wasOperate = operate)
            .ConfigureAwait(false);
        return (status, wasOperate == true);
    }

    /// <summary>
    /// Establishes the final STANDBY/RX safety boundary after automatic tuning.
    /// The Expert Amp Server button is a toggle, so this transaction observes
    /// fresh protocol-native state, writes at most once, and never retries an
    /// ambiguous command. The post-write confirmation window starts only after
    /// the server accepts the button frame.
    /// </summary>
    internal async Task<AutomaticTuneStandbyResult>
        EnsureStandbyAfterAutomaticTuneAsync(
            SpeTaurusConfig expectedConfig,
            CancellationToken cancellationToken)
    {
        var confirmationTimeoutMs = Math.Max(
            AutomaticTuneOperateConfirmationTimeout.TotalMilliseconds,
            expectedConfig.ResponseTimeoutMs);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        operation.CancelAfter(TimeSpan.FromMilliseconds(
            expectedConfig.ConnectTimeoutMs + confirmationTimeoutMs));
        var token = operation.Token;

        await _commands.WaitAsync(token).ConfigureAwait(false);
        IDisposable? configLease = null;
        var writeAttempted = false;
        try
        {
            try
            {
                configLease = await _taurus.TryAcquireConfigLeaseAsync(
                        expectedConfig,
                        token)
                    .ConfigureAwait(false);
                if (configLease is null)
                {
                    return new(
                        false,
                        null,
                        "The Taurus configuration changed before final STANDBY verification.");
                }

                var before = await GetRawStatusAsync(expectedConfig, token)
                    .ConfigureAwait(false);
                var unsafeReason = UnsafeControlReason(
                    before,
                    RemoteCommand.Operate,
                    false);
                if (unsafeReason is not null)
                {
                    return new(
                        false,
                        ToPanelStatus(expectedConfig, before, unsafeReason),
                        unsafeReason);
                }

                if (!IsOperate(before))
                {
                    if (IsVerifiedStandby(before))
                        return new(true, ToPanelStatus(expectedConfig, before), null);
                    return new(
                        false,
                        ToPanelStatus(expectedConfig, before),
                        "Final Taurus STANDBY identity or RX state could not be verified.");
                }

                writeAttempted = true;
                await PostButtonRawAsync(expectedConfig, RemoteCommand.Operate, token)
                    .ConfigureAwait(false);

                // The preflight read and HTTP POST use the broader connection
                // budget. Guarantee a fresh confirmation floor after the one
                // non-idempotent STANDBY toggle has actually been accepted.
                operation.CancelAfter(TimeSpan.FromMilliseconds(
                    confirmationTimeoutMs));
                var deadline = DateTimeOffset.UtcNow.AddMilliseconds(
                    confirmationTimeoutMs);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                    var after = await GetRawStatusAsync(expectedConfig, token)
                        .ConfigureAwait(false);
                    if (IsVerifiedStandby(after))
                        return new(true, ToPanelStatus(expectedConfig, after), null);
                }

                return new(
                    false,
                    null,
                    "The Taurus did not confirm final STANDBY/RX after automatic tuning; the toggle was not repeated.");
            }
            catch (OperationCanceledException) when (writeAttempted)
            {
                var reason = cancellationToken.IsCancellationRequested
                    ? "Final Taurus STANDBY verification was canceled after the command was sent."
                    : "Final Taurus STANDBY verification timed out after the command was sent; the toggle was not repeated.";
                return new(false, null, reason);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(
                    false,
                    null,
                    "Final Taurus STANDBY verification timed out before a command was sent.");
            }
            catch (Exception ex) when (
                ex is HttpRequestException or InvalidDataException or TimeoutException)
            {
                var phase = writeAttempted
                    ? "after the command was sent; the toggle was not repeated"
                    : "before a command was sent";
                _log.LogWarning(
                    ex,
                    "spe-taurus automatic tune final STANDBY verification failed");
                return new(
                    false,
                    null,
                    $"Final Taurus STANDBY verification failed {phase}: {ex.Message}");
            }
        }
        finally
        {
            configLease?.Dispose();
            _commands.Release();
        }
    }

    /// <summary>
    /// Restores OP after a completed automatic tune as one control transaction.
    /// The original configuration lease remains held through confirmation and
    /// any compensating STANDBY, so a settings save cannot redirect cleanup to
    /// another server. Feature deactivation or cancellation after the OP write
    /// fails closed through the raw, lease-pinned STANDBY path.
    /// </summary>
    internal async Task<AutomaticTuneOperateRestoreResult>
        RestoreOperateAfterAutomaticTuneAsync(
            SpeTaurusConfig expectedConfig,
            CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        IDisposable? configLease = null;
        var writeAttempted = false;
        string? failure = null;
        try
        {
            try
            {
                configLease = await _taurus.TryAcquireConfigLeaseAsync(
                        expectedConfig,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (configLease is null)
                    return new(false, null, "The Taurus configuration changed before OPERATE restoration.");
                if (!FeatureActive)
                    return new(false, null, "The Taurus feature was deactivated before OPERATE restoration.");

                using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                var confirmationTimeoutMs = Math.Max(
                    AutomaticTuneOperateConfirmationTimeout.TotalMilliseconds,
                    expectedConfig.ResponseTimeoutMs);
                operation.CancelAfter(TimeSpan.FromMilliseconds(
                    expectedConfig.ConnectTimeoutMs + confirmationTimeoutMs));
                var token = operation.Token;
                var before = await GetRawStatusAsync(expectedConfig, token).ConfigureAwait(false);
                var unsafeReason = UnsafeControlReason(before, RemoteCommand.Operate, true);
                if (unsafeReason is not null)
                    return new(false, ToPanelStatus(expectedConfig, before, unsafeReason), unsafeReason);
                if (cancellationToken.IsCancellationRequested || !FeatureActive)
                    return new(false, ToPanelStatus(expectedConfig, before), null);
                if (IsOperate(before))
                    return new(true, ToPanelStatus(expectedConfig, before), null);

                writeAttempted = true;
                await PostButtonRawAsync(expectedConfig, RemoteCommand.Operate, token)
                    .ConfigureAwait(false);

                // The preflight status read and HTTP POST use the broader
                // connection budget. Start the promised confirmation floor
                // only after Expert Amp Server has accepted the OP frame.
                operation.CancelAfter(TimeSpan.FromMilliseconds(
                    confirmationTimeoutMs));

                var deadline = DateTimeOffset.UtcNow.AddMilliseconds(
                    confirmationTimeoutMs);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                    var after = await GetRawStatusAsync(expectedConfig, token).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        failure = "OPERATE restoration was canceled after the command was sent.";
                        break;
                    }
                    if (!FeatureActive)
                    {
                        failure = "The Taurus feature was deactivated after OPERATE was requested.";
                        break;
                    }
                    var afterReason = UnsafeControlReason(after, RemoteCommand.Operate, true);
                    if (afterReason is null && IsOperate(after))
                    {
                        // Linearization point: once fresh OP/RX is confirmed while
                        // the feature and original config epoch are still valid,
                        // later cancellation belongs to the next operator action.
                        return new(true, ToPanelStatus(expectedConfig, after), null);
                    }
                }

                failure ??= "The Taurus did not confirm OPERATE after automatic tuning.";
            }
            catch (OperationCanceledException) when (writeAttempted)
            {
                failure = cancellationToken.IsCancellationRequested
                    ? "OPERATE restoration was canceled after the command was sent."
                    : "OPERATE restoration timed out after the command was sent.";
            }
            catch (Exception ex) when (
                writeAttempted
                && ex is HttpRequestException or InvalidDataException or TimeoutException)
            {
                failure = $"OPERATE restoration became ambiguous: {ex.Message}";
                _log.LogWarning(ex, "spe-taurus automatic tune OPERATE confirmation failed");
            }

            if (!writeAttempted)
                return new(false, null, failure ?? "Taurus OPERATE restoration did not begin.");

            var compensationError = await CompensateStandbyRawAsync(expectedConfig)
                .ConfigureAwait(false);
            if (compensationError is not null)
            {
                failure = failure is null
                    ? compensationError
                    : $"{failure} {compensationError}";
            }
            return new(false, null, failure);
        }
        finally
        {
            configLease?.Dispose();
            _commands.Release();
        }
    }

    internal Task<SpeTaurusStatus> CycleAsync(
        SpeCommand command,
        CancellationToken cancellationToken) => command switch
        {
            SpeCommand.PowerLevel => ExecuteAsync(RemoteCommand.Power, null, cancellationToken),
            SpeCommand.Input => ExecuteAsync(RemoteCommand.Input, null, cancellationToken),
            SpeCommand.Antenna => ExecuteAsync(RemoteCommand.Antenna, null, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    internal Task<SpeTaurusStatus> TuneAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(RemoteCommand.Tune, null, cancellationToken);

    /// <summary>
    /// Sends the Expert Amp Server's dedicated hardware wake action once. Wake
    /// is deliberately unavailable to Zeus's direct serial transports: the
    /// server owns the G2 cable and its DTR/RTS timing. A successful HTTP write
    /// is not treated as power-on confirmation; fresh protocol-native Taurus
    /// status must arrive within the bounded confirmation window.
    /// </summary>
    internal async Task<SpeRemoteActionResult> WakeAsync(
        CancellationToken cancellationToken)
    {
        var initialConfig = _taurus.Config;
        var unavailable = RemotePowerUnavailable(initialConfig, "wake");
        if (unavailable is not null) return unavailable;

        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var featureIo = LinkFeatureIo(cancellationToken);
        CancellationTokenSource? operation = null;
        var writeAttempted = false;
        try
        {
            var config = _taurus.Config;
            unavailable = RemotePowerUnavailable(config, "wake");
            if (unavailable is not null) return unavailable;

            operation = CancellationTokenSource.CreateLinkedTokenSource(featureIo.Token);
            operation.CancelAfter(TimeSpan.FromMilliseconds(
                config.ConnectTimeoutMs + config.RemotePowerOnTimeoutMs));
            var token = operation.Token;

            using var configLease = await _taurus.TryAcquireConfigLeaseAsync(config, token)
                .ConfigureAwait(false);
            if (configLease is null || !CanUse(config) || !config.RemotePowerEnabled)
                return Blocked("wake", "The Taurus configuration changed before wake.");

            // Wake is permitted only when the server positively reports no
            // recent amplifier contact. Any live identity, including TX or an
            // unexpected model, must prevent a control-line pulse.
            var current = await GetRawStatusAsync(config, token).ConfigureAwait(false);
            if (current.RecentContact)
            {
                if (!HasAuthoritativeStatus(current))
                    return Blocked(
                        "wake",
                        "Wake is blocked because current amplifier contact is not authoritative protocol status.",
                        ToPanelStatus(config, current));
                if (!IsExpectedTaurus(current))
                    return Blocked(
                        "wake",
                        "Wake is blocked because the connected amplifier is not an SPE Expert 1.5K Taurus.",
                        ToPanelStatus(config, current));
                return new(
                    true,
                    false,
                    true,
                    "already-on",
                    "The Taurus is already on and reporting fresh protocol status.",
                    ToPanelStatus(config, current));
            }

            var client = _httpClientFactory.CreateClient(
                ExpertAmpServerTunePreflight.HttpClientName);
            writeAttempted = true;
            using var response = await client.PostAsync(
                    $"{config.ExpertServerUrl}/api/v1/actions/wake",
                    content: null,
                    token)
                .ConfigureAwait(false);
            var action = await ReadEnvelopeAsync<ExpertButtonResult>(response, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || !action.Success
                || action.Data?.Sent != true
                || !string.Equals(action.Data.Name, "wake", StringComparison.Ordinal))
            {
                throw new InvalidDataException(action.Error ?? action.Message
                    ?? $"Expert Amp Server rejected wake ({(int)response.StatusCode}).");
            }

            // Begin a fresh, bounded confirmation interval only after the one
            // wake write has been accepted. Never repeat an ambiguous wake.
            var confirmationMs = config.RemotePowerOnTimeoutMs;
            operation.CancelAfter(TimeSpan.FromMilliseconds(confirmationMs));
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(confirmationMs);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                try
                {
                    var after = await GetStatusAsync(config, token).ConfigureAwait(false);
                    if (!IsFreshTaurusRx(after)) continue;
                    return new(
                        true,
                        true,
                        true,
                        "powered-on",
                        "The Taurus confirmed power-on with fresh protocol status.",
                        ToPanelStatus(config, after));
                }
                catch (Exception ex) when (
                    ex is HttpRequestException or InvalidDataException)
                {
                    // The serial session may be restarting after wake. Continue
                    // within this one bounded observation window only.
                }
            }

            return AmbiguousRemotePower(
                initialConfig,
                "wake-unconfirmed",
                "Wake was sent exactly once, but fresh Taurus status was not confirmed; it was not repeated.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return writeAttempted
                ? AmbiguousRemotePower(
                    initialConfig,
                    "wake-unconfirmed",
                    "Wake was sent exactly once, but confirmation timed out; it was not repeated.")
                : Blocked("wake", "Wake timed out before Expert Amp Server accepted it.");
        }
        catch (OperationCanceledException)
        {
            if (!writeAttempted) throw;
            return AmbiguousRemotePower(
                initialConfig,
                "wake-unconfirmed",
                "Wake was sent exactly once, but cancellation left its outcome unknown; it was not repeated.");
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidDataException or TimeoutException)
        {
            _log.LogWarning(ex, "spe-taurus.expert-server wake failed");
            return writeAttempted
                ? AmbiguousRemotePower(
                    initialConfig,
                    "wake-unconfirmed",
                    $"Wake outcome is ambiguous; it was not repeated. {ex.Message}")
                : Blocked("wake", ex.Message);
        }
        finally
        {
            operation?.Dispose();
            _commands.Release();
        }
    }

    /// <summary>
    /// Safely sends the documented OFF button exactly once. The amplifier must
    /// be positively identified, in RX, alarm-free, and in verified STANDBY.
    /// If it starts in OPERATE, this method sends one OPERATE toggle and waits
    /// for STANDBY before it can authorize the OFF write.
    /// </summary>
    internal async Task<SpeRemoteActionResult> PowerOffAsync(
        CancellationToken cancellationToken)
    {
        var initialConfig = _taurus.Config;
        var unavailable = RemotePowerUnavailable(initialConfig, "power-off");
        if (unavailable is not null) return unavailable;

        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var featureIo = LinkFeatureIo(cancellationToken);
        CancellationTokenSource? operation = null;
        var offWriteAttempted = false;
        var standbyWriteAttempted = false;
        try
        {
            var config = _taurus.Config;
            unavailable = RemotePowerUnavailable(config, "power-off");
            if (unavailable is not null) return unavailable;
            operation = CancellationTokenSource.CreateLinkedTokenSource(featureIo.Token);
            operation.CancelAfter(TimeSpan.FromMilliseconds(
                config.ConnectTimeoutMs + Math.Max(1500, config.ResponseTimeoutMs)));
            var token = operation.Token;
            using var configLease = await _taurus.TryAcquireConfigLeaseAsync(config, token)
                .ConfigureAwait(false);
            if (configLease is null || !CanUse(config) || !config.RemotePowerEnabled)
                return Blocked("power-off", "The Taurus configuration changed before power-off.");

            var status = await GetRawStatusAsync(config, token).ConfigureAwait(false);
            var unsafeReason = PowerSafetyReason(status);
            if (unsafeReason is not null)
                return Blocked("power-off", unsafeReason, ToPanelStatus(config, status, unsafeReason));

            if (IsOperate(status))
            {
                standbyWriteAttempted = true;
                await PostButtonRawAsync(config, RemoteCommand.Operate, token)
                    .ConfigureAwait(false);
                var standbyDeadline = DateTimeOffset.UtcNow.AddMilliseconds(
                    Math.Max(750, config.ResponseTimeoutMs));
                ExpertStatus? verifiedStandby = null;
                while (DateTimeOffset.UtcNow < standbyDeadline)
                {
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                    var sample = await GetRawStatusAsync(config, token).ConfigureAwait(false);
                    if (IsVerifiedStandby(sample) && AlarmText(sample).Length == 0)
                    {
                        verifiedStandby = sample;
                        break;
                    }
                }
                if (verifiedStandby is null)
                {
                    return AmbiguousRemotePower(
                        initialConfig,
                        "standby-unconfirmed",
                        "STANDBY was requested exactly once, but could not be verified; OFF was not sent.",
                        powerCommandSent: false);
                }
                status = verifiedStandby;
            }

            // Re-authorize OFF from the freshest protocol-native STANDBY/RX
            // sample while the configuration lease is still held.
            status = await GetRawStatusAsync(config, token).ConfigureAwait(false);
            unsafeReason = PowerSafetyReason(status);
            if (unsafeReason is not null || !IsVerifiedStandby(status))
            {
                var reason = unsafeReason
                    ?? "Power-off is blocked because final STANDBY/RX could not be verified.";
                return Blocked("power-off", reason, ToPanelStatus(config, status, reason));
            }

            offWriteAttempted = true;
            await PostButtonRawAsync(config, RemoteCommand.Off, token).ConfigureAwait(false);
            try
            {
                // A cable that keeps REMOTE ON asserted can prevent shutdown.
                // Observe briefly after the accepted OFF write so the panel can
                // explain the documented R / POWER HELD BY REMOTE condition.
                using var observation = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(750));
                while (!observation.IsCancellationRequested)
                {
                    var afterOff = await GetRawStatusAsync(config, observation.Token)
                        .ConfigureAwait(false);
                    if (!afterOff.RecentContact) break;
                    var warning = FirstNonBlank(
                        afterOff.WarningsText?.FirstOrDefault(),
                        afterOff.Warnings?.FirstOrDefault(),
                        afterOff.WarningCode);
                    if (string.Equals(afterOff.WarningCode, "R", StringComparison.OrdinalIgnoreCase)
                        || warning.Contains("REMOTE", StringComparison.OrdinalIgnoreCase))
                    {
                        return new(
                            true,
                            true,
                            false,
                            "power-held-by-remote",
                            "OFF was sent exactly once, but the Taurus reports POWER HELD BY REMOTE. Release the REMOTE ON assertion before trying again.",
                            ToPanelStatus(config, afterOff));
                    }
                    await Task.Delay(PollInterval, observation.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (
                ex is OperationCanceledException or HttpRequestException or InvalidDataException)
            {
                // Loss of serial contact is expected after OFF. It is not
                // proof of power state, and never causes a second write.
            }
            var sentMessage =
                "The documented OFF command was sent exactly once from verified STANDBY/RX. Final power state cannot be protocol-confirmed after contact stops.";
            LatchRemotePowerAmbiguity(initialConfig, sentMessage);
            return new(
                true,
                true,
                false,
                "power-off-sent",
                sentMessage,
                ToPanelStatus(config, status));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (offWriteAttempted)
                return AmbiguousRemotePower(
                    initialConfig,
                    "power-off-unconfirmed",
                    "OFF was sent exactly once, but its outcome is unknown; it was not repeated.");
            if (standbyWriteAttempted)
                return AmbiguousRemotePower(
                    initialConfig,
                    "standby-unconfirmed",
                    "STANDBY was sent exactly once, but could not be verified; OFF was not sent.",
                    powerCommandSent: false);
            return Blocked("power-off", "Power-off timed out before any command was sent.");
        }
        catch (OperationCanceledException)
        {
            if (!offWriteAttempted && !standbyWriteAttempted) throw;
            return offWriteAttempted
                ? AmbiguousRemotePower(
                    initialConfig,
                    "power-off-unconfirmed",
                    "OFF was sent exactly once, but cancellation left its outcome unknown; it was not repeated.")
                : AmbiguousRemotePower(
                    initialConfig,
                    "standby-unconfirmed",
                    "STANDBY was sent exactly once, but cancellation prevented verification; OFF was not sent.",
                    powerCommandSent: false);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidDataException or TimeoutException)
        {
            _log.LogWarning(ex, "spe-taurus.expert-server power-off failed");
            if (offWriteAttempted)
                return AmbiguousRemotePower(
                    initialConfig,
                    "power-off-unconfirmed",
                    $"OFF outcome is ambiguous; it was not repeated. {ex.Message}");
            if (standbyWriteAttempted)
                return AmbiguousRemotePower(
                    initialConfig,
                    "standby-unconfirmed",
                    $"STANDBY outcome is ambiguous; OFF was not sent. {ex.Message}",
                    powerCommandSent: false);
            return Blocked("power-off", ex.Message);
        }
        finally
        {
            operation?.Dispose();
            _commands.Release();
        }
    }

    internal async Task<SpeDisplayText> DisplayAsync(
        CancellationToken cancellationToken)
    {
        var config = _taurus.Config;
        if (!FeatureActive)
            throw new InvalidDataException("The Taurus feature is inactive.");
        if (config.ExpertServerUrl.Length == 0)
            throw new InvalidDataException(
                "The amplifier display is available only through Expert Amp Server.");

        using var featureIo = LinkFeatureIo(cancellationToken);
        using var configIo = _taurus.LinkConfigIo(config, featureIo.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(configIo.Token);
        timeout.CancelAfter(config.ConnectTimeoutMs);
        var display = await GetDisplayTextAsync(config, timeout.Token).ConfigureAwait(false);
        return ValidateDisplay(display);
    }

    internal Task<SpeRemoteActionResult> CycleDisplayPageAsync(
        CancellationToken cancellationToken) =>
        ExecuteAdvancedAsync(RemoteCommand.Display, cancellationToken);

    internal Task<SpeRemoteActionResult> CycleCatPageAsync(
        CancellationToken cancellationToken) =>
        ExecuteAdvancedAsync(RemoteCommand.Cat, cancellationToken);

    private async Task<SpeRemoteActionResult> ExecuteAdvancedAsync(
        RemoteCommand command,
        CancellationToken cancellationToken)
    {
        var initialConfig = _taurus.Config;
        if (initialConfig.ExpertServerUrl.Length == 0)
            return Blocked(ButtonName(command),
                "This control is available only through Expert Amp Server.");
        if (!FeatureActive)
            return Blocked(ButtonName(command), "The Taurus feature is inactive.");

        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var featureIo = LinkFeatureIo(cancellationToken);
        CancellationTokenSource? operation = null;
        var writeAttempted = false;
        ExpertStatus? before = null;
        try
        {
            var config = _taurus.Config;
            if (!CanUse(config))
                return Blocked(ButtonName(command), "The Taurus feature or server configuration changed.");
            operation = CancellationTokenSource.CreateLinkedTokenSource(featureIo.Token);
            operation.CancelAfter(TimeSpan.FromMilliseconds(
                config.ConnectTimeoutMs + Math.Max(750, config.ResponseTimeoutMs)));
            var token = operation.Token;

            before = await GetStatusAsync(config, token).ConfigureAwait(false);
            var unsafeReason = UnsafeControlReason(before, command, null);
            if (unsafeReason is not null)
                return Blocked(ButtonName(command), unsafeReason,
                    ToPanelStatus(config, before, unsafeReason));
            if (command == RemoteCommand.Cat
                && string.IsNullOrWhiteSpace(before.CatInterface))
            {
                return Blocked(
                    "cat",
                    "CAT page control is blocked because the current CAT interface is unknown.",
                    ToPanelStatus(config, before));
            }

            using var configLease = await _taurus.TryAcquireConfigLeaseAsync(config, token)
                .ConfigureAwait(false);
            if (configLease is null || !CanUse(config))
                return Blocked(ButtonName(command), "The Taurus configuration changed before the command.");

            // Re-read the authoritative safety sample immediately before the
            // one documented button write.
            before = await GetRawStatusAsync(config, token).ConfigureAwait(false);
            unsafeReason = UnsafeControlReason(before, command, null);
            if (unsafeReason is not null)
                return Blocked(ButtonName(command), unsafeReason,
                    ToPanelStatus(config, before, unsafeReason));
            if (command == RemoteCommand.Cat
                && string.IsNullOrWhiteSpace(before.CatInterface))
                return Blocked(
                    "cat",
                    "CAT page control is blocked because the current CAT interface is unknown.",
                    ToPanelStatus(config, before));

            // Capture the display baseline under the same configuration lease
            // and immediately before the one DISPLAY write. A merely newer
            // periodic frame is insufficient; confirmation also needs changed
            // LCD content.
            var displayBefore = command == RemoteCommand.Display
                ? ValidateDisplay(await GetDisplayTextAsync(config, token).ConfigureAwait(false))
                : null;

            writeAttempted = true;
            await PostButtonRawAsync(config, command, token).ConfigureAwait(false);
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(
                Math.Max(750, config.ResponseTimeoutMs));
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                if (command == RemoteCommand.Display)
                {
                    var displayAfter = ValidateDisplay(
                        await GetDisplayTextAsync(config, token).ConfigureAwait(false));
                    if (displayBefore is not null
                        && displayAfter.Sequence > displayBefore.Sequence
                        && DisplayContentChanged(displayBefore, displayAfter))
                    {
                        return new(
                            true,
                            true,
                            true,
                            "display-page-changed",
                            "The Taurus confirmed a new display page.",
                            ToPanelStatus(config, before));
                    }
                    continue;
                }

                var after = await GetStatusAsync(config, token).ConfigureAwait(false);
                if (IsFreshTaurusRx(after)
                    && Changed(before.CatInterface, after.CatInterface))
                {
                    return new(
                        true,
                        true,
                        true,
                        "cat-page-changed",
                        $"The Taurus confirmed CAT {after.CatInterface}.",
                        ToPanelStatus(config, after));
                }
            }

            return Ambiguous(
                $"{ButtonName(command)}-unconfirmed",
                $"{ButtonName(command).ToUpperInvariant()} was sent exactly once, but the new state was not confirmed; it was not repeated.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return writeAttempted
                ? Ambiguous(
                    $"{ButtonName(command)}-unconfirmed",
                    $"{ButtonName(command).ToUpperInvariant()} was sent exactly once, but confirmation timed out; it was not repeated.")
                : Blocked(ButtonName(command), "The command timed out before it was sent.");
        }
        catch (OperationCanceledException)
        {
            if (!writeAttempted) throw;
            return Ambiguous(
                $"{ButtonName(command)}-unconfirmed",
                $"{ButtonName(command).ToUpperInvariant()} was sent exactly once, but cancellation left its outcome unknown; it was not repeated.");
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidDataException or TimeoutException)
        {
            _log.LogWarning(ex, "spe-taurus.expert-server {Command} failed", ButtonName(command));
            return writeAttempted
                ? Ambiguous(
                    $"{ButtonName(command)}-unconfirmed",
                    $"{ButtonName(command).ToUpperInvariant()} outcome is ambiguous; it was not repeated. {ex.Message}")
                : Blocked(ButtonName(command), ex.Message);
        }
        finally
        {
            operation?.Dispose();
            _commands.Release();
        }
    }

    internal async Task WaitForTuneCompletionAsync(
        SpeTaurusConfig expectedConfig,
        Func<bool> carrierStillOn,
        CancellationToken cancellationToken)
    {
        if (expectedConfig.ExpertServerUrl.Length == 0)
            throw new InvalidDataException(
                "Expert Amp Server display monitoring is not configured.");

        using var featureIo = LinkFeatureIo(cancellationToken);
        using var configIo = _taurus.LinkConfigIo(expectedConfig, featureIo.Token);
        var monitorToken = configIo.Token;
        var client = _httpClientFactory.CreateClient(
            ExpertAmpServerTunePreflight.HttpClientName);
        while (true)
        {
            if (!carrierStillOn())
                throw new InvalidDataException(
                    "The Zeus tuning carrier stopped before the Taurus finished tuning.");
            if (!CanUse(expectedConfig))
                throw new InvalidDataException(
                    "The Taurus feature or Expert Amp Server configuration changed while tuning.");

            using var sampleTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                monitorToken);
            sampleTimeout.CancelAfter(expectedConfig.ConnectTimeoutMs);
            var sampleToken = sampleTimeout.Token;
            var status = await GetStatusAsync(expectedConfig, sampleToken)
                .ConfigureAwait(false);
            if (!status.RecentContact || !HasAuthoritativeStatus(status))
                throw new InvalidDataException(
                    "Expert Amp Server lost fresh protocol-native Taurus status while tuning.");
            if (!IsExpectedTaurus(status))
                throw new InvalidDataException(
                    "Expert Amp Server lost confirmed SPE Expert 1.5K Taurus identity while tuning.");
            if (!string.Equals(
                    OperatingState(status),
                    "standby",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The amplifier left STANDBY while tuning; Zeus stopped the carrier.");
            if (status.Tx is null)
                throw new InvalidDataException(
                    "The Taurus transmit state became unknown while tuning; Zeus stopped the carrier.");
            var alarm = AlarmText(status);
            if (alarm.Length > 0)
                throw new InvalidDataException(
                    $"The Taurus reported an alarm while tuning ({alarm}); Zeus stopped the carrier.");

            var display = await GetDisplayEvidenceAsync(
                client,
                expectedConfig.ExpertServerUrl,
                sampleToken).ConfigureAwait(false);
            if (!display.Tune) return;
            await Task.Delay(PollInterval, monitorToken).ConfigureAwait(false);
        }
    }

    private async Task<SpeTaurusStatus> ExecuteAsync(
        RemoteCommand command,
        bool? requestedOperate,
        CancellationToken cancellationToken,
        Action<bool>? operateStateObserver = null)
    {
        var initialConfig = _taurus.Config;
        if (initialConfig.ExpertServerUrl.Length == 0)
        {
            return command switch
            {
                RemoteCommand.Operate => await _taurus.SetOperateAsync(
                    requestedOperate == true,
                    cancellationToken).ConfigureAwait(false),
                RemoteCommand.Power => await _taurus.CycleAsync(
                    SpeCommand.PowerLevel,
                    cancellationToken).ConfigureAwait(false),
                RemoteCommand.Input => await _taurus.CycleAsync(
                    SpeCommand.Input,
                    cancellationToken).ConfigureAwait(false),
                RemoteCommand.Antenna => await _taurus.CycleAsync(
                    SpeCommand.Antenna,
                    cancellationToken).ConfigureAwait(false),
                _ => await _taurus.TuneAsync(cancellationToken).ConfigureAwait(false),
            };
        }

        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var featureIo = LinkFeatureIo(cancellationToken);
        CancellationTokenSource? operationTimeout = null;
        var writeAttempted = false;
        ExpertStatus? before = null;
        try
        {
            var config = _taurus.Config;
            if (!CanUse(config))
                return RemoteUnavailable(config, "feature-inactive", "amplifier-disabled");

            operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(featureIo.Token);
            operationTimeout.CancelAfter(
                config.ConnectTimeoutMs
                + (command == RemoteCommand.Tune
                    ? config.TuneArmTimeoutMs
                    : Math.Max(500, config.ResponseTimeoutMs)));
            var token = operationTimeout.Token;
            var client = _httpClientFactory.CreateClient(
                ExpertAmpServerTunePreflight.HttpClientName);
            before = await GetStatusAsync(config, token).ConfigureAwait(false);
            var unsafeReason = UnsafeControlReason(before, command, requestedOperate);
            if (unsafeReason is not null)
                return ToPanelStatus(config, before, unsafeReason);

            // Automatic tuning uses this observation to restore the operator's
            // prior mode. Capture it inside the serialized command path so a
            // separate status read cannot race the STANDBY transition.
            if (command == RemoteCommand.Operate)
                operateStateObserver?.Invoke(IsOperate(before));

            if (command == RemoteCommand.Operate
                && IsOperate(before) == requestedOperate)
                return ToPanelStatus(config, before);

            ExpertDisplayEvidence? displayBefore = null;
            if (command == RemoteCommand.Tune)
            {
                displayBefore = await GetDisplayEvidenceAsync(
                    client,
                    config.ExpertServerUrl,
                    token).ConfigureAwait(false);
                if (displayBefore.Value.Tune)
                    return ToPanelStatus(config, before);
            }

            // Hold the service configuration gate from the final identity
            // check through confirmation. A settings save cannot race this
            // one non-idempotent write to the captured server URL.
            using var configLease = await _taurus.TryAcquireConfigLeaseAsync(config, token)
                .ConfigureAwait(false);
            if (configLease is null || !CanUse(config))
                return ToPanelStatus(config, before, "amplifier-disabled");

            if (command == RemoteCommand.Tune)
            {
                // The display evidence above is a separate network round trip.
                // Re-read protocol-native state while the configuration lease is
                // held so a transition into OPERATE cannot race the TUNE write.
                before = await GetStatusAsync(config, token).ConfigureAwait(false);
                var preWriteUnsafeReason = UnsafeControlReason(
                    before,
                    RemoteCommand.Tune,
                    null);
                if (preWriteUnsafeReason is not null)
                    return ToPanelStatus(config, before, preWriteUnsafeReason);
            }

            writeAttempted = true;
            using var response = await client.PostAsJsonAsync(
                $"{config.ExpertServerUrl}/api/v1/actions/button",
                new { name = ButtonName(command) },
                token).ConfigureAwait(false);
            var action = await ReadEnvelopeAsync<ExpertButtonResult>(
                response,
                token).ConfigureAwait(false);
            var expectedActionName = ButtonName(command);
            if (!response.IsSuccessStatusCode
                || !action.Success
                || action.Data?.Sent != true
                || !string.Equals(
                    action.Data.Name,
                    expectedActionName,
                    StringComparison.Ordinal))
                throw new InvalidDataException(action.Error ?? action.Message
                    ?? $"Expert Amp Server returned an invalid result for {expectedActionName} ({(int)response.StatusCode}).");

            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(
                command == RemoteCommand.Tune
                    ? config.TuneArmTimeoutMs
                    : Math.Max(500, config.ResponseTimeoutMs));
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                if (command == RemoteCommand.Tune)
                {
                    var display = await GetDisplayEvidenceAsync(
                        client,
                        config.ExpertServerUrl,
                        token).ConfigureAwait(false);
                    if (!display.Tune) continue;
                    var afterTune = await GetStatusAsync(config, token)
                        .ConfigureAwait(false);
                    var finalReason = UnsafeControlReason(
                        afterTune,
                        RemoteCommand.Tune,
                        null);
                    return finalReason is null
                        ? ToPanelStatus(config, afterTune)
                        : ToPanelStatus(config, afterTune, finalReason);
                }

                var after = await GetStatusAsync(config, token).ConfigureAwait(false);
                if (Confirmed(command, requestedOperate, before, after))
                    return ToPanelStatus(config, after);
            }

            throw new TimeoutException(
                $"Expert Amp Server sent {ButtonName(command)}, but the Taurus did not confirm the new state.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!writeAttempted) throw;
            return before is null
                ? RemoteUnavailable(initialConfig, "ambiguous-command", "Command cancellation left its outcome unknown.")
                : ToPanelStatus(initialConfig, before, "Command cancellation left its outcome unknown; the command was not repeated.");
        }
        catch (OperationCanceledException) when (featureIo.IsCancellationRequested)
        {
            var error = writeAttempted
                ? "Feature deactivation left the command outcome unknown; the command was not repeated."
                : "amplifier-disabled";
            return before is null
                ? RemoteUnavailable(initialConfig, writeAttempted ? "ambiguous-command" : "feature-inactive", error)
                : ToPanelStatus(initialConfig, before, error);
        }
        catch (OperationCanceledException) when (operationTimeout?.IsCancellationRequested == true)
        {
            var error = writeAttempted
                ? "Command outcome is ambiguous; the bounded Expert Amp Server operation timed out and the command was not repeated."
                : "Timed out before the Expert Amp Server accepted the command.";
            return before is null
                ? RemoteUnavailable(initialConfig, writeAttempted ? "ambiguous-command" : "faulted", error)
                : ToPanelStatus(initialConfig, before, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TimeoutException)
        {
            _log.LogWarning(ex, "spe-taurus.expert-server {Command} failed", ButtonName(command));
            var error = writeAttempted
                ? $"Command outcome is ambiguous; it was not repeated. {ex.Message}"
                : ex.Message;
            return before is null
                ? RemoteUnavailable(initialConfig, writeAttempted ? "ambiguous-command" : "faulted", error)
                : ToPanelStatus(initialConfig, before, error);
        }
        finally
        {
            operationTimeout?.Dispose();
            _commands.Release();
        }
    }

    private async Task<ExpertStatus> GetStatusAsync(
        SpeTaurusConfig expectedConfig,
        CancellationToken cancellationToken)
    {
        if (!CanUse(expectedConfig))
            throw new InvalidDataException(
                "The Taurus feature or Expert Amp Server configuration changed.");
        var client = _httpClientFactory.CreateClient(
            ExpertAmpServerTunePreflight.HttpClientName);
        ExpertStatus status;
        try
        {
            status = await GetDataAsync<ExpertStatus>(
                client,
                $"{expectedConfig.ExpertServerUrl}/api/v1/status",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or HttpRequestException or InvalidDataException)
        {
            // A transport fault may represent an Expert Amp Server restart or
            // a different device behind the same URL. Never carry identity
            // trust across that connection epoch, regardless of which caller
            // (panel status, command, or active tune monitor) observed it.
            ForgetTaurusIdentity(expectedConfig);
            throw;
        }
        if (!status.RecentContact || !HasAuthoritativeStatus(status))
        {
            ForgetTaurusIdentity(expectedConfig);
            return status;
        }
        if (IsExpectedTaurus(status))
        {
            RememberTaurusIdentity(expectedConfig);
            ClearResolvedRemotePowerAmbiguity(expectedConfig, status);
            return status;
        }
        if (!CanUseDisplayIdentityFallback(status.ModelName))
        {
            ForgetTaurusIdentity(expectedConfig);
            return status;
        }
        if (HasConfirmedTaurusIdentity(expectedConfig))
            return status with { ModelName = "EXPERT 1.5K TAURUS" };
        try
        {
            var display = await GetDisplayEvidenceAsync(
                client,
                expectedConfig.ExpertServerUrl,
                cancellationToken).ConfigureAwait(false);
            if (!display.TaurusIdentity) return status;
            RememberTaurusIdentity(expectedConfig);
            return status with { ModelName = "EXPERT 1.5K TAURUS" };
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException)
        {
            return status;
        }
    }

    private async Task<ExpertStatus> GetRawStatusAsync(
        SpeTaurusConfig expectedConfig,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(
            ExpertAmpServerTunePreflight.HttpClientName);
        var status = await GetDataAsync<ExpertStatus>(
                client,
                $"{expectedConfig.ExpertServerUrl}/api/v1/status",
                cancellationToken)
            .ConfigureAwait(false);
        return status.RecentContact
            && HasAuthoritativeStatus(status)
            && CanUseDisplayIdentityFallback(status.ModelName)
            && HasConfirmedTaurusIdentity(expectedConfig)
                ? status with { ModelName = "EXPERT 1.5K TAURUS" }
                : status;
    }

    private async Task PostButtonRawAsync(
        SpeTaurusConfig expectedConfig,
        RemoteCommand command,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(
            ExpertAmpServerTunePreflight.HttpClientName);
        using var response = await client.PostAsJsonAsync(
                $"{expectedConfig.ExpertServerUrl}/api/v1/actions/button",
                new { name = ButtonName(command) },
                cancellationToken)
            .ConfigureAwait(false);
        var action = await ReadEnvelopeAsync<ExpertButtonResult>(
                response,
                cancellationToken)
            .ConfigureAwait(false);
        var expectedName = ButtonName(command);
        if (!response.IsSuccessStatusCode
            || !action.Success
            || action.Data?.Sent != true
            || !string.Equals(action.Data.Name, expectedName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(action.Error ?? action.Message
                ?? $"Expert Amp Server returned an invalid result for {expectedName} ({(int)response.StatusCode}).");
        }
    }

    private async Task<string?> CompensateStandbyRawAsync(SpeTaurusConfig expectedConfig)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var token = cleanup.Token;
            var before = await GetRawStatusAsync(expectedConfig, token).ConfigureAwait(false);
            var unsafeReason = UnsafeControlReason(before, RemoteCommand.Operate, false);
            if (unsafeReason is not null)
                return $"Compensating Taurus STANDBY was blocked: {unsafeReason}";
            if (!IsOperate(before))
            {
                return IsExpectedTaurus(before)
                    ? null
                    : "Compensating Taurus STANDBY identity could not be verified.";
            }

            await PostButtonRawAsync(expectedConfig, RemoteCommand.Operate, token)
                .ConfigureAwait(false);
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(
                Math.Max(500, expectedConfig.ResponseTimeoutMs));
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                var after = await GetRawStatusAsync(expectedConfig, token).ConfigureAwait(false);
                if (after.RecentContact
                    && HasAuthoritativeStatus(after)
                    && IsExpectedTaurus(after)
                    && after.Tx is false
                    && !IsOperate(after))
                    return null;
            }
            return "Compensating Taurus STANDBY could not be verified.";
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or HttpRequestException or InvalidDataException)
        {
            _log.LogCritical(ex, "spe-taurus automatic tune STANDBY compensation failed");
            return $"Compensating Taurus STANDBY failed: {ex.Message}";
        }
    }

    private static async Task<ExpertDisplayEvidence> GetDisplayEvidenceAsync(
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
                "Expert Amp Server did not provide checksum-valid Taurus display evidence.");
        return new(
            flags.Leds.Tune,
            frame.ScreenText?.Contains("EXPERT 1.5K TAURUS", StringComparison.OrdinalIgnoreCase) == true);
    }

    private async Task<ExpertDisplayText> GetDisplayTextAsync(
        SpeTaurusConfig expectedConfig,
        CancellationToken cancellationToken)
    {
        if (!CanUse(expectedConfig))
            throw new InvalidDataException(
                "The Taurus feature or Expert Amp Server configuration changed.");
        var client = _httpClientFactory.CreateClient(
            ExpertAmpServerTunePreflight.HttpClientName);
        return await GetDataAsync<ExpertDisplayText>(
                client,
                $"{expectedConfig.ExpertServerUrl}/api/v1/display/text",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static SpeDisplayText ValidateDisplay(ExpertDisplayText display)
    {
        if (display.Rows is null || display.Rows.Length != 8
            || display.Rows.Any(row => row is null || row.Length != 40))
            throw new InvalidDataException(
                "Expert Amp Server returned an invalid Taurus display geometry; expected exactly 8 rows of 40 characters.");
        if (display.HighlightedSpans is null || display.HighlightedSpans.Length > 320)
            throw new InvalidDataException("Expert Amp Server returned invalid display highlights.");

        var spans = new List<SpeDisplayTextSpan>(display.HighlightedSpans.Length);
        foreach (var span in display.HighlightedSpans)
        {
            if (span.Row is < 0 or >= 8
                || span.StartColumn < 0
                || span.EndColumn <= span.StartColumn
                || span.EndColumn > 40
                || span.Text is null
                || span.Text.Length != span.EndColumn - span.StartColumn
                || !string.Equals(
                    display.Rows[span.Row].Substring(
                        span.StartColumn,
                        span.EndColumn - span.StartColumn),
                    span.Text,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Expert Amp Server returned an out-of-range or inconsistent display highlight.");
            spans.Add(new(span.Row, span.StartColumn, span.EndColumn, span.Text));
        }

        if (!DateTimeOffset.TryParse(
                display.UpdatedAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var updatedAt))
            throw new InvalidDataException("Expert Amp Server returned an invalid display timestamp.");
        var displayAge = DateTimeOffset.UtcNow - updatedAt.ToUniversalTime();
        if (displayAge < TimeSpan.FromSeconds(-5))
            throw new InvalidDataException(
                "Expert Amp Server returned a future-dated Taurus display frame.");
        var source = (display.Source ?? "").Trim();
        var model = (display.ModelName ?? "").Trim();
        if (source.Length == 0
            || !source.StartsWith("serial", StringComparison.OrdinalIgnoreCase)
            || !model.Contains("TAURUS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Expert Amp Server display is not serial evidence from an SPE Expert Taurus.");
        var selected = display.SelectedText ?? "";
        var screen = display.ScreenText ?? "";
        if (selected.Length > 320 || screen.Length > 1024 || source.Length > 128 || model.Length > 128)
            throw new InvalidDataException("Expert Amp Server returned oversized display text.");

        return new(
            display.Rows,
            spans,
            selected,
            display.Sequence,
            updatedAt,
            source,
            model,
            screen);
    }

    private SpeRemoteActionResult? RemotePowerUnavailable(
        SpeTaurusConfig config,
        string action)
    {
        ClearRemotePowerAmbiguityForConfigChange(config);
        if (!FeatureActive)
            return Blocked(action, "The Taurus feature is inactive.");
        if (!config.RemotePowerEnabled)
            return Blocked(action, "Remote power is disabled. Enable it explicitly in Taurus settings first.");
        if (config.ExpertServerUrl.Length == 0)
            return Blocked(action, "Remote power is available only through Expert Amp Server.");
        lock (_remotePowerGate)
        {
            if (ReferenceEquals(_ambiguousRemotePowerConfig, config))
                return Blocked(
                    "remote-power-ambiguous",
                    _ambiguousRemotePowerReason
                    ?? "A previous remote power command is unresolved. Wait for fresh Taurus status or save the configuration before trying again.");
        }
        return null;
    }

    private static SpeRemoteActionResult Blocked(
        string state,
        string message,
        SpeTaurusStatus? status = null) =>
        new(false, false, false, state, message, status);

    private static SpeRemoteActionResult Ambiguous(string state, string message) =>
        new(false, true, false, state, message, null);

    private SpeRemoteActionResult AmbiguousRemotePower(
        SpeTaurusConfig config,
        string state,
        string message,
        bool powerCommandSent = true)
    {
        lock (_remotePowerGate)
        {
            _ambiguousRemotePowerConfig = config;
            _ambiguousRemotePowerReason =
                $"A previous remote power operation is unresolved: {message}";
        }
        return new(false, powerCommandSent, false, state, message, null);
    }

    private void LatchRemotePowerAmbiguity(SpeTaurusConfig config, string message)
    {
        lock (_remotePowerGate)
        {
            _ambiguousRemotePowerConfig = config;
            _ambiguousRemotePowerReason =
                $"A previous remote power operation is unresolved: {message}";
        }
    }

    private void ClearResolvedRemotePowerAmbiguity(
        SpeTaurusConfig config,
        ExpertStatus status)
    {
        if (!status.RecentContact
            || !HasAuthoritativeStatus(status)
            || !IsExpectedTaurus(status))
            return;
        lock (_remotePowerGate)
        {
            if (!ReferenceEquals(_ambiguousRemotePowerConfig, config)) return;
            _ambiguousRemotePowerConfig = null;
            _ambiguousRemotePowerReason = null;
        }
    }

    private void ClearRemotePowerAmbiguityForConfigChange(SpeTaurusConfig config)
    {
        lock (_remotePowerGate)
        {
            if (_ambiguousRemotePowerConfig is null
                || ReferenceEquals(_ambiguousRemotePowerConfig, config))
                return;
            _ambiguousRemotePowerConfig = null;
            _ambiguousRemotePowerReason = null;
        }
    }

    private void ClearRemotePowerAmbiguity()
    {
        lock (_remotePowerGate)
        {
            _ambiguousRemotePowerConfig = null;
            _ambiguousRemotePowerReason = null;
        }
    }

    private static string? PowerSafetyReason(ExpertStatus status)
    {
        if (!status.RecentContact)
            return "Power-off is blocked because Expert Amp Server has no recent amplifier contact.";
        if (!HasAuthoritativeStatus(status))
            return "Power-off is blocked without fresh protocol-native status evidence.";
        if (!IsExpectedTaurus(status))
            return "Power-off is blocked because the amplifier was not identified as an SPE Expert 1.5K Taurus.";
        if (status.Tx is not false)
            return status.Tx == true
                ? "Power-off is blocked while the Taurus reports TX."
                : "Power-off is blocked because the Taurus TX state is unknown.";
        var alarm = AlarmText(status);
        if (alarm.Length > 0)
            return $"Power-off is blocked by amplifier alarm: {alarm}";
        var operatingState = OperatingState(status);
        if (!string.Equals(operatingState, "operate", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(operatingState, "standby", StringComparison.OrdinalIgnoreCase))
            return "Power-off is blocked because the amplifier operating state is unknown.";
        return null;
    }

    private static bool IsFreshTaurusRx(ExpertStatus status) =>
        status.RecentContact
        && HasAuthoritativeStatus(status)
        && IsExpectedTaurus(status)
        && status.Tx is false;

    private static string? UnsafeControlReason(
        ExpertStatus status,
        RemoteCommand command,
        bool? requestedOperate)
    {
        var isStandbyRequest = command == RemoteCommand.Operate
            && requestedOperate == false;
        if (!status.RecentContact)
            return "Expert Amp Server has no recent contact with the Taurus.";
        if (!HasAuthoritativeStatus(status))
            return "Control is blocked because Expert Amp Server did not provide fresh protocol-native status evidence.";
        // An explicit transition from a fresh, authoritative OPERATE state to
        // STANDBY is a fail-safe action. Permit that one de-escalation even if
        // this display page omits the model name; all other controls still
        // require positively confirmed Taurus identity.
        if (!IsExpectedTaurus(status) && !isStandbyRequest)
            return "Control is blocked because Expert Amp Server did not identify an SPE Expert 1.5K Taurus.";
        if (status.Tx is not false)
            return status.Tx == true
                ? "Control is blocked while the amplifier reports TX."
                : "Control is blocked because the amplifier TX state is unknown.";

        var alarm = AlarmText(status);
        if (alarm.Length > 0 && !isStandbyRequest)
            return $"Control is blocked by amplifier alarm: {alarm}";

        var operatingState = OperatingState(status).ToLowerInvariant();
        if (operatingState is not ("standby" or "operate"))
            return "Control is blocked because the amplifier operating state is unknown.";
        if (command == RemoteCommand.Tune
            && !string.Equals(operatingState, "standby", StringComparison.OrdinalIgnoreCase))
            return "ATU tune is blocked while the amplifier is in OP/OPERATE. Put it in STANDBY first.";
        return null;
    }

    private static bool Confirmed(
        RemoteCommand command,
        bool? requestedOperate,
        ExpertStatus before,
        ExpertStatus after) => command switch
        {
            RemoteCommand.Operate when requestedOperate == true =>
                string.Equals(OperatingState(after), "operate", StringComparison.OrdinalIgnoreCase),
            RemoteCommand.Operate =>
                string.Equals(OperatingState(after), "standby", StringComparison.OrdinalIgnoreCase),
            RemoteCommand.Power => Changed(before.OutputLevel, after.OutputLevel),
            RemoteCommand.Input => Changed(before.Input, after.Input),
            RemoteCommand.Antenna => Changed(before.Antenna, after.Antenna),
            _ => false,
        };

    private static bool Changed(string? before, string? after) =>
        !string.IsNullOrWhiteSpace(before)
        && !string.IsNullOrWhiteSpace(after)
        && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);

    private static bool DisplayContentChanged(
        SpeDisplayText before,
        SpeDisplayText after) =>
        !before.Rows.SequenceEqual(after.Rows, StringComparer.Ordinal)
        || !string.Equals(before.SelectedText, after.SelectedText, StringComparison.Ordinal);

    private SpeTaurusStatus ToPanelStatus(
        SpeTaurusConfig config,
        ExpertStatus remote,
        string? error = null)
    {
        var direct = _taurus.Status();
        var connected = remote.RecentContact;
        DateTimeOffset? contact = ExpertAmpServerEvidence.TryParseContact(
            remote.LastContactAt,
            out var parsed)
                ? parsed
                : null;
        var operatingState = OperatingState(remote).ToLowerInvariant();
        var controlReady = connected
            && error is null
            && HasAuthoritativeStatus(remote)
            && IsExpectedTaurus(remote)
            && remote.Tx is false
            && operatingState is "standby" or "operate";
        var connectionError = error
            ?? (!connected
                ? "Expert Amp Server has no recent contact with the Taurus."
                : !HasAuthoritativeStatus(remote)
                    ? "Expert Amp Server status is not fresh protocol-native amplifier evidence."
                    : !IsExpectedTaurus(remote)
                        ? "Expert Amp Server did not identify an SPE Expert 1.5K Taurus."
                        : null);
        return direct with
        {
            Enabled = FeatureActive,
            Connected = connected,
            ControlReady = controlReady,
            ConnectionState = connected ? "connected" : "server-no-contact",
            Transport = "expert-server",
            Endpoint = config.ExpertServerUrl,
            Amplifier = MapAmplifier(remote),
            Error = connectionError,
            LastSampleUtc = contact,
        };
    }

    private SpeTaurusStatus RemoteUnavailable(
        SpeTaurusConfig config,
        string state,
        string error)
    {
        var direct = _taurus.Status();
        return direct with
        {
            Enabled = FeatureActive,
            Connected = false,
            ControlReady = false,
            ConnectionState = state,
            Transport = "expert-server",
            Endpoint = config.ExpertServerUrl,
            Amplifier = null,
            Error = error,
            LastSampleUtc = null,
        };
    }

    private static SpeAmplifierStatus MapAmplifier(ExpertStatus status)
    {
        var band = FirstNonBlank(status.BandText, status.Band);
        var model = FirstNonBlank(status.ModelName, "SPE Expert (identity unknown)");
        var isTaurus = model.Contains("TAURUS", StringComparison.OrdinalIgnoreCase);
        var alarm = AlarmText(status);
        var warning = FirstNonBlank(
            status.WarningsText?.FirstOrDefault(),
            status.Warnings?.FirstOrDefault());
        return new(
            isTaurus ? "15T" : "",
            model,
            isTaurus,
            IsOperate(status),
            status.Tx == true,
            FirstNonBlank(status.AntennaBank, "x"),
            ParsePort(status.Input),
            band,
            Array.FindIndex(Bands, value => string.Equals(value, band, StringComparison.OrdinalIgnoreCase)),
            ParsePort(status.Antenna),
            FirstNonBlank(status.AtuStatusCode, "unknown"),
            FirstNonBlank(status.RxAntenna, "0r"),
            FirstNonBlank(status.OutputLevel, "unknown"),
            status.PowerWatts,
            status.Swr,
            status.AntennaSwr,
            status.PaSupplyVoltage,
            status.PaCurrent,
            RoundNullable(status.TemperatureC),
            RoundNullable(status.TemperatureLowerC),
            RoundNullable(status.TemperatureCombinerC),
            NormalizeCode(status.WarningCode),
            warning,
            NormalizeCode(status.AlarmCode),
            alarm,
            FirstNonBlank(status.CatInterface));
    }

    private static int ParsePort(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : 0;

    private static int? RoundNullable(double? value) =>
        value is null
            ? null
            : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);

    private static string OperatingState(ExpertStatus status) =>
        FirstNonBlank(status.OperatingState, status.Mode);

    private static bool IsOperate(ExpertStatus status) =>
        string.Equals(OperatingState(status), "operate", StringComparison.OrdinalIgnoreCase);

    private static bool IsVerifiedStandby(ExpertStatus status) =>
        status.RecentContact
        && HasAuthoritativeStatus(status)
        && IsExpectedTaurus(status)
        && status.Tx is false
        && !IsOperate(status)
        && string.Equals(
            OperatingState(status),
            "standby",
            StringComparison.OrdinalIgnoreCase);

    private static bool HasAuthoritativeStatus(ExpertStatus status) =>
        ExpertAmpServerEvidence.HasFreshProtocolStatus(
            status.Source,
            status.Confidence,
            status.Provenance,
            status.LastContactAt);

    private static bool IsExpectedTaurus(ExpertStatus status) =>
        status.ModelName?.Contains("TAURUS", StringComparison.OrdinalIgnoreCase) == true;

    private static bool CanUseDisplayIdentityFallback(string? modelName) =>
        string.IsNullOrWhiteSpace(modelName)
        || modelName.Contains("1.5K-FA", StringComparison.OrdinalIgnoreCase);

    private bool HasConfirmedTaurusIdentity(SpeTaurusConfig config)
    {
        lock (_identityGate)
            return ReferenceEquals(_confirmedIdentityConfig, config);
    }

    private void RememberTaurusIdentity(SpeTaurusConfig config)
    {
        lock (_identityGate)
        {
            if (ReferenceEquals(config, _taurus.Config))
                _confirmedIdentityConfig = config;
        }
    }

    private void ForgetTaurusIdentity(SpeTaurusConfig config)
    {
        lock (_identityGate)
        {
            if (ReferenceEquals(_confirmedIdentityConfig, config)
                || ReferenceEquals(config, _taurus.Config))
                _confirmedIdentityConfig = null;
        }
    }

    private void ForgetTaurusIdentity()
    {
        lock (_identityGate)
            _confirmedIdentityConfig = null;
    }

    private static string AlarmText(ExpertStatus status) => FirstNonBlank(
        status.AlarmsText?.FirstOrDefault(),
        status.ActiveAlarms?.FirstOrDefault(),
        string.Equals(status.AlarmCode, "N", StringComparison.OrdinalIgnoreCase)
            ? null
            : status.AlarmCode);

    private static string NormalizeCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "N" : value.Trim();

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string ButtonName(RemoteCommand command) => command switch
    {
        RemoteCommand.Operate => "operate",
        RemoteCommand.Power => "power",
        RemoteCommand.Input => "input",
        RemoteCommand.Antenna => "antenna",
        RemoteCommand.Off => "off",
        RemoteCommand.Display => "display",
        RemoteCommand.Cat => "cat",
        _ => "tune",
    };

    private bool FeatureActive =>
        !_disposed && _features.IsActive(ExpertAmpServerTunePreflight.PluginId);

    private bool CanUse(SpeTaurusConfig config) =>
        FeatureActive
        && config.ExpertServerUrl.Length > 0
        && ReferenceEquals(config, _taurus.Config);

    private CancellationTokenSource LinkFeatureIo(CancellationToken cancellationToken)
    {
        lock (_featureGate)
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _featureCancellation.Token);
    }

    private void OnFeatureStateChanged()
    {
        CancellationTokenSource? previous = null;
        var active = FeatureActive;
        lock (_featureGate)
        {
            if (active == _featureActive) return;
            _featureActive = active;
            ClearRemotePowerAmbiguity();
            if (active)
            {
                previous = _featureCancellation;
                _featureCancellation = new();
            }
            else
            {
                ForgetTaurusIdentity();
                _featureCancellation.Cancel();
            }
        }
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_featureChanges is not null)
            _featureChanges.Changed -= OnFeatureStateChanged;
        lock (_featureGate)
        {
            ForgetTaurusIdentity();
            ClearRemotePowerAmbiguity();
            _featureCancellation.Cancel();
            _featureCancellation.Dispose();
        }
        _commands.Dispose();
    }

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

    private enum RemoteCommand
    {
        Operate,
        Power,
        Input,
        Antenna,
        Tune,
        Off,
        Display,
        Cat,
    }

    private sealed record ExpertEnvelope<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("data")] T? Data);

    private sealed record ExpertButtonResult(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("sent")] bool Sent);

    private sealed record ExpertStatus(
        [property: JsonPropertyName("modelName")] string? ModelName,
        [property: JsonPropertyName("operatingState")] string? OperatingState,
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("tx")] bool? Tx,
        [property: JsonPropertyName("band")] string? Band,
        [property: JsonPropertyName("bandText")] string? BandText,
        [property: JsonPropertyName("input")] string? Input,
        [property: JsonPropertyName("antenna")] string? Antenna,
        [property: JsonPropertyName("antennaBank")] string? AntennaBank,
        [property: JsonPropertyName("catInterface")] string? CatInterface,
        [property: JsonPropertyName("outputLevel")] string? OutputLevel,
        [property: JsonPropertyName("swr")] double? Swr,
        [property: JsonPropertyName("antennaSwr")] double? AntennaSwr,
        [property: JsonPropertyName("paSupplyVoltage")] double? PaSupplyVoltage,
        [property: JsonPropertyName("paCurrent")] double? PaCurrent,
        [property: JsonPropertyName("temperatureC")] double? TemperatureC,
        [property: JsonPropertyName("temperatureLowerC")] double? TemperatureLowerC,
        [property: JsonPropertyName("temperatureCombinerC")] double? TemperatureCombinerC,
        [property: JsonPropertyName("powerWatts")] double? PowerWatts,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("confidence")] string? Confidence,
        [property: JsonPropertyName("provenance")] string? Provenance,
        [property: JsonPropertyName("recentContact")] bool RecentContact,
        [property: JsonPropertyName("lastContactAt")] string? LastContactAt,
        [property: JsonPropertyName("rxAntenna")] string? RxAntenna,
        [property: JsonPropertyName("warningCode")] string? WarningCode,
        [property: JsonPropertyName("alarmCode")] string? AlarmCode,
        [property: JsonPropertyName("atuStatusCode")] string? AtuStatusCode,
        [property: JsonPropertyName("warningsText")] string[]? WarningsText,
        [property: JsonPropertyName("alarmsText")] string[]? AlarmsText,
        [property: JsonPropertyName("warnings")] string[]? Warnings,
        [property: JsonPropertyName("activeAlarms")] string[]? ActiveAlarms);

    private sealed record ExpertDisplayFrame(
        [property: JsonPropertyName("screenText")] string? ScreenText,
        [property: JsonPropertyName("lcdFlags")] ExpertLcdFlags? LcdFlags);

    private sealed record ExpertDisplayText(
        [property: JsonPropertyName("rows")] string[]? Rows,
        [property: JsonPropertyName("highlightedSpans")] ExpertDisplayTextSpan[]? HighlightedSpans,
        [property: JsonPropertyName("selectedText")] string? SelectedText,
        [property: JsonPropertyName("sequence")] ulong Sequence,
        [property: JsonPropertyName("updatedAt")] string? UpdatedAt,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("modelName")] string? ModelName,
        [property: JsonPropertyName("screenText")] string? ScreenText);

    private sealed record ExpertDisplayTextSpan(
        [property: JsonPropertyName("row")] int Row,
        [property: JsonPropertyName("startColumn")] int StartColumn,
        [property: JsonPropertyName("endColumn")] int EndColumn,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record ExpertLcdFlags(
        [property: JsonPropertyName("checksumPresent")] bool ChecksumPresent,
        [property: JsonPropertyName("checksumValid")] bool ChecksumValid,
        [property: JsonPropertyName("leds")] ExpertLeds? Leds);

    private sealed record ExpertLeds(
        [property: JsonPropertyName("tune")] bool Tune);

    private readonly record struct ExpertDisplayEvidence(bool Tune, bool TaurusIdentity);
}
