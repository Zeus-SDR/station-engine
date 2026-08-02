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

/// <summary>
/// Selects the direct SPE transport or the Expert Amp Server owned by the G2.
/// Remote commands are single-shot front-panel button actions, so every write
/// is preceded by fresh protocol-native safety evidence and is never retried.
/// </summary>
internal sealed class ExpertAmpServerControl : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(75);
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
    private CancellationTokenSource _featureCancellation = new();
    private SpeTaurusConfig? _confirmedIdentityConfig;
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
        CancellationToken cancellationToken)
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
            if (!response.IsSuccessStatusCode || !action.Success || action.Data?.Sent != true)
                throw new InvalidDataException(action.Error ?? action.Message
                    ?? $"Expert Amp Server rejected {ButtonName(command)} ({(int)response.StatusCode}).");

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

    private SpeTaurusStatus ToPanelStatus(
        SpeTaurusConfig config,
        ExpertStatus remote,
        string? error = null)
    {
        var direct = _taurus.Status();
        var connected = remote.RecentContact;
        DateTimeOffset? contact = DateTimeOffset.TryParse(remote.LastContactAt, out var parsed)
            ? parsed
            : connected ? DateTimeOffset.UtcNow : null;
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
            alarm);
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

    private static bool HasAuthoritativeStatus(ExpertStatus status) =>
        string.Equals(status.Source, "serial", StringComparison.OrdinalIgnoreCase)
        && string.Equals(status.Confidence, "protocol-native", StringComparison.OrdinalIgnoreCase)
        && string.Equals(status.Provenance, "status-poll", StringComparison.OrdinalIgnoreCase);

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

    private sealed record ExpertLcdFlags(
        [property: JsonPropertyName("checksumPresent")] bool ChecksumPresent,
        [property: JsonPropertyName("checksumValid")] bool ChecksumValid,
        [property: JsonPropertyName("leds")] ExpertLeds? Leds);

    private sealed record ExpertLeds(
        [property: JsonPropertyName("tune")] bool Tune);

    private readonly record struct ExpertDisplayEvidence(bool Tune, bool TaurusIdentity);
}
