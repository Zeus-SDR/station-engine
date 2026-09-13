// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
using System.Net.Sockets;
#if ZEUS_PRODUCT_HOST
using Zeus.Product.Hosting.GodsEye;
namespace Zeus.Product.Hosting.Aprs;
#else
using Zeus.Server.GodsEye;
namespace Zeus.Server.Aprs;
#endif

public sealed class AprsTrackingService(AprsSettingsStore settings, IAprsConnector connector,
    GodsEyeViewerRegistry viewers, GodsEyeFeatureGate feature,
    ILogger<AprsTrackingService> log, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly AprsTrackCache _cache = new();
    private readonly object _gate = new();
    private string _state = "disabled", _reason = "APRS tracking is off.";
    private DateTimeOffset? _lastPacket;
    private long _packets, _accepted;
    public AprsSnapshot Snapshot(string? selectedId = null, string? cursor = null)
    {
        var config = settings.Get(); var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var changes = config.Enabled ? _cache.Changes(now, TimeSpan.FromMinutes(config.MaxAgeMinutes), selectedId, cursor) : null;
            return new(config, config.Enabled ? _state : "disabled", config.Enabled ? _reason : "APRS tracking is off.",
                AprsConnector.Host, now, _lastPacket, _packets, _accepted, changes?.Tracks ?? [], false,
                changes?.Cursor, changes?.IsDelta ?? false, changes?.RemovedIds ?? []);
        }
    }
    private void Status(string state, string reason) { lock (_gate) { _state = state; _reason = reason; } }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TrackAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                Status("offline", "APRS tracking is temporarily unavailable; retrying.");
                log.LogWarning(exception, "aprs.stream unexpected tracking failure");
                try { await Task.Delay(TimeSpan.FromSeconds(5), _clock, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
    private async Task TrackAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = settings.Get();
            var featureEnabled = feature.Enabled;
            if (!config.Enabled || !featureEnabled || !viewers.HasViewers)
            {
                Status(!config.Enabled ? "disabled" : "offline",
                    !config.Enabled ? "APRS tracking is off." : !featureEnabled ? "Zeus HUD is disabled." : "Waiting for an active HUD viewer.");
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, stoppingToken).ConfigureAwait(false); continue;
            }
            using var connectionToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var guard = WatchConfigurationAsync(config, connectionToken, stoppingToken);
            try
            {
                Status("connecting", "Connecting to the APRS-IS live stream…");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), _clock);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connectionToken.Token, deadline.Token);
                await using var connection = await connector.ConnectAsync(timeout.Token).ConfigureAwait(false);
                var greeting = await connection.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (greeting == null || !greeting.StartsWith('#')) throw new IOException("APRS-IS greeting missing.");
                await connection.LoginAsync(Login(config), timeout.Token).ConfigureAwait(false);
                var loggedIn = false;
                while (!connectionToken.IsCancellationRequested)
                {
                    using var idleDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(loggedIn ? 90 : 15), _clock);
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(connectionToken.Token, idleDeadline.Token);
                    var line = await connection.ReadAsync(idle.Token).ConfigureAwait(false);
                    if (line == null) throw new IOException("APRS-IS closed the stream.");
                    if (line.StartsWith("# logresp ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!line.StartsWith($"# logresp {config.Callsign} ", StringComparison.OrdinalIgnoreCase)
                            || !line.Contains("unverified", StringComparison.OrdinalIgnoreCase)) throw new IOException("Receive-only login was not acknowledged.");
                        loggedIn = true; failures = 0;
                        Status("live", "Live APRS-IS stream · receive only. Positions appear as stations report.");
                    }
                    if (!loggedIn || line.Length == 0 || line[0] == '#') continue;
                    var now = _clock.GetUtcNow();
                    lock (_gate) { _lastPacket = now; _packets++; }
                    var position = AprsParser.Parse(line, now);
                    if (position == null) continue;
                    if (_cache.Accept(position, now, TimeSpan.FromMinutes(config.MaxAgeMinutes)))
                        lock (_gate) _accepted++;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                if (!connectionToken.IsCancellationRequested)
                {
                    failures++;
                    Status("offline", "APRS-IS disconnected; reconnecting. Showing last reported positions with their original age.");
                    log.LogWarning("aprs.stream disconnected: {Reason}", ex.Message);
                }
            }
            finally
            {
                connectionToken.Cancel();
                await guard.ConfigureAwait(false);
            }
            if (stoppingToken.IsCancellationRequested) break;
            if (failures > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * failures)), _clock, stoppingToken).ConfigureAwait(false);
        }
    }
    private async Task WatchConfigurationAsync(AprsSettings config, CancellationTokenSource connection, CancellationToken stop)
    {
        try
        {
            while (!connection.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, connection.Token).ConfigureAwait(false);
                if (settings.Get() != config || !viewers.HasViewers || !feature.Enabled || stop.IsCancellationRequested)
                { _cache.Clear(); connection.Cancel(); }
            }
        }
        catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
        catch
        {
            connection.Cancel();
            throw;
        }
    }
    public static string Login(AprsSettings settings) =>
        $"user {settings.Callsign} pass -1 vers ZeusSDR 1.0 filter t/poi";
}
