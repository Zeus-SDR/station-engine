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

using Microsoft.Extensions.Logging;

namespace Zeus.Server.SpeTaurus;

public sealed record SpeTaurusConfig(
    bool Enabled = false,
    string Transport = "local",
    string PortName = "",
    int BaudRate = 115200,
    string BridgeHost = "",
    int BridgePort = 9001,
    bool AutoReconnect = true,
    int ActivePollingMs = 100,
    int IdlePollingMs = 1000,
    int ResponseTimeoutMs = 1200,
    int ConnectTimeoutMs = 3000,
    string D2xxSerial = "");

internal sealed record SpeTaurusStatus(
    bool Enabled,
    bool Connected,
    string ConnectionState,
    string Transport,
    string Endpoint,
    SpeAmplifierStatus? Amplifier,
    int RatedPowerWatts,
    string? Error,
    DateTimeOffset? LastSampleUtc,
    long ValidFrames,
    long InvalidFrames,
    IReadOnlyList<string> AvailablePorts,
    IReadOnlyList<string> AvailableD2xxDevices,
    SpeD2xxDiagnostic D2xx);

internal sealed class SpeTaurusService : IAsyncDisposable
{
    private const int RatedPowerWatts = 1500;
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(3);

    private readonly ILogger _log;
    private readonly Func<string, ISpeTransport> _transportFactory;
    private readonly Func<SpeD2xxScan> _d2xxScan;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly SpeChangePulse _changed = new();
    private readonly object _stateGate = new();

    private volatile SpeTaurusConfig _config;
    private ISpeTransport? _transport;
    private string _connectionState = "disabled";
    private string? _error;
    private SpeAmplifierStatus? _amplifier;
    private DateTimeOffset? _lastSampleUtc;
    private long _validFrames;
    private long _invalidFrames;
    private IReadOnlyList<string> _availablePorts = [];
    private SpeD2xxScan _d2xx = SpeD2xxScan.NotProbed;
    private bool _disposed;

    internal SpeTaurusService(
        ILogger<SpeTaurusService> log,
        Func<string, ISpeTransport>? transportFactory = null,
        SpeTaurusConfig? initialConfig = null,
        Func<SpeD2xxScan>? d2xxScan = null)
    {
        _log = log;
        var startupConfig = (initialConfig ?? new SpeTaurusConfig()) with
        {
            Enabled = false,
            D2xxSerial = "",
        };
        try { _config = Sanitize(startupConfig); }
        catch (ArgumentException ex)
        {
            // Corrupt stored data must not prevent station-engine startup.
            // Clear the unsafe selector and require an explicit operator save.
            _config = Sanitize(new SpeTaurusConfig());
            _error = ex.Message;
        }
        _transportFactory = transportFactory ?? (kind => kind switch
        {
            "d2xx" => new SpeD2xxTransport(),
            "tcp" => new SpeTcpTransport(),
            _ => new SpeSerialTransport(),
        });
        _d2xxScan = d2xxScan ?? (() => SpeD2xxDiscovery.Scan());
    }

    internal SpeTaurusConfig Config => _config;

    internal SpeTaurusStatus Status()
    {
        var config = _config;
        lock (_stateGate)
        {
            return new(
                config.Enabled,
                _transport?.IsOpen == true,
                _connectionState,
                config.Transport,
                Endpoint(config),
                _amplifier,
                RatedPowerWatts,
                _error,
                _lastSampleUtc,
                _validFrames,
                _invalidFrames,
                _availablePorts,
                _d2xx.Devices.Select(device => device.Serial).ToArray(),
                _d2xx.Diagnostic);
        }
    }

    internal async Task<SpeTaurusConfig> SetConfigAsync(
        SpeTaurusConfig? requested,
        CancellationToken cancellationToken)
    {
        var next = Sanitize(requested ?? new SpeTaurusConfig());
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changedTransport = !SameEndpoint(_config, next);
            _config = next;
            if (changedTransport || !next.Enabled)
                await CloseLockedAsync(next.Enabled ? "configuration-changed" : "disabled", null)
                    .ConfigureAwait(false);
            lock (_stateGate)
            {
                if (next.Enabled && _connectionState == "disabled") _connectionState = "idle";
            }
        }
        finally
        {
            _io.Release();
            _changed.Pulse();
        }
        return _config;
    }

    internal SpeTaurusStatus RefreshDevices()
    {
        var ports = SpeSerialPorts.List();
        var d2xx = _d2xxScan();
        lock (_stateGate)
        {
            _availablePorts = ports;
            _d2xx = d2xx;
        }
        return Status();
    }

    internal Task<SpeTaurusStatus> SetOperateAsync(bool operate, CancellationToken cancellationToken) =>
        ExecuteCommandAsync(
            SpeCommand.Operate,
            allowAlarmedStandby: !operate,
            requireOperate: false,
            before => before.Operate == operate,
            after => after.Operate == operate,
            cancellationToken);

    internal Task<SpeTaurusStatus> CycleAsync(
        SpeCommand command,
        CancellationToken cancellationToken)
    {
        if (command is not (SpeCommand.PowerLevel or SpeCommand.Antenna or SpeCommand.Input))
            throw new ArgumentOutOfRangeException(nameof(command));
        return ExecuteCommandAsync(
            command,
            allowAlarmedStandby: false,
            requireOperate: false,
            _ => false,
            command switch
            {
                SpeCommand.PowerLevel => after => _commandBefore?.PowerLevel != after.PowerLevel,
                SpeCommand.Antenna => after => _commandBefore?.TxAntenna != after.TxAntenna,
                _ => after => _commandBefore?.Input != after.Input,
            },
            cancellationToken);
    }

    internal Task<SpeTaurusStatus> TuneAsync(CancellationToken cancellationToken) =>
        ExecuteCommandAsync(
            SpeCommand.Tune,
            allowAlarmedStandby: false,
            requireOperate: true,
            _ => false,
            after => !HasAlarm(after),
            cancellationToken);

    // Accessed only while _io is held; used by cycle validators so they
    // compare the one fresh preflight sample with the one post-command sample.
    private SpeAmplifierStatus? _commandBefore;

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var signal = _changed.Capture();
                var config = _config;
                if (!config.Enabled)
                {
                    await CloseForGateAsync("disabled", stoppingToken).ConfigureAwait(false);
                    await _changed.WaitAsync(signal, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                if (!HasEndpoint(config, out var endpointError))
                {
                    await CloseForGateAsync("idle", stoppingToken).ConfigureAwait(false);
                    lock (_stateGate) _error = endpointError;
                    await _changed.WaitAsync(signal, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var ok = await PollCycleAsync(config, stoppingToken).ConfigureAwait(false);
                if (!ok)
                {
                    if (!config.AutoReconnect)
                        await _changed.WaitAsync(signal, stoppingToken).ConfigureAwait(false);
                    else
                        await _changed.WaitOrDelayAsync(signal, ReconnectBackoff, stoppingToken)
                            .ConfigureAwait(false);
                    continue;
                }

                var active = false;
                lock (_stateGate) active = _amplifier?.Transmitting == true;
                var delay = TimeSpan.FromMilliseconds(active ? config.ActivePollingMs : config.IdlePollingMs);
                await _changed.WaitOrDelayAsync(signal, delay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _disposed)
        {
        }
        finally
        {
            await _io.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await CloseLockedAsync("stopped", null).ConfigureAwait(false); }
            finally { _io.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _changed.Pulse();
        await _io.WaitAsync().ConfigureAwait(false);
        try { await CloseLockedAsync("disposed", null).ConfigureAwait(false); }
        finally
        {
            _io.Release();
            _io.Dispose();
        }
    }

    private async Task<bool> PollCycleAsync(SpeTaurusConfig config, CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanUse(config))
            {
                await CloseLockedAsync("disabled", null).ConfigureAwait(false);
                return false;
            }
            if (!await EnsureOpenLockedAsync(config, cancellationToken).ConfigureAwait(false)) return false;
            var sample = await RequestStatusLockedAsync(config, cancellationToken).ConfigureAwait(false);
            ApplySample(sample);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "spe-taurus.poll failed");
            await CloseLockedAsync("faulted", ex.Message).ConfigureAwait(false);
            return false;
        }
        finally { _io.Release(); }
    }

    private async Task<SpeTaurusStatus> ExecuteCommandAsync(
        SpeCommand command,
        bool allowAlarmedStandby,
        bool requireOperate,
        Func<SpeAmplifierStatus, bool> alreadySatisfied,
        Func<SpeAmplifierStatus, bool> confirmed,
        CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        var writeAttempted = false;
        try
        {
            var config = _config;
            if (!CanUse(config))
                return Reject("amplifier-disabled");
            if (!await EnsureOpenLockedAsync(config, cancellationToken).ConfigureAwait(false))
                return Status();

            var before = await RequestStatusLockedAsync(config, cancellationToken).ConfigureAwait(false);
            ApplySample(before);
            _commandBefore = before;
            if (before.Transmitting) return Reject("Control is blocked while the amplifier reports TX.");
            if (HasAlarm(before) && !allowAlarmedStandby)
                return Reject($"Control is blocked by amplifier alarm {before.AlarmCode}: {before.Alarm}");
            if (requireOperate && !before.Operate)
                return Reject("ATU tune is blocked until the amplifier is in OPERATE.");
            if (alreadySatisfied(before)) return Status();

            // Recheck the active configuration immediately before the one
            // non-idempotent write so a concurrent disable closes the path.
            if (!CanUse(config))
                return Reject("amplifier-disabled");

            var transport = _transport ?? throw new IOException("Transport is not connected.");
            writeAttempted = true;
            await transport.WriteAsync(SpeProtocol.EncodeCommand(command), cancellationToken)
                .ConfigureAwait(false);
            var responseStatus = await WaitForCommandResponseLockedAsync(command, config, cancellationToken)
                .ConfigureAwait(false);
            var after = responseStatus
                ?? await RequestStatusLockedAsync(config, cancellationToken).ConfigureAwait(false);
            ApplySample(after);
            if (!confirmed(after))
                throw new IOException("The command response was valid, but its effect was not confirmed.");
            lock (_stateGate) _error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (writeAttempted)
                await CloseLockedAsync("ambiguous-command", "Command cancellation left its outcome unknown; the transport was closed.")
                    .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var message = writeAttempted
                ? $"Command outcome is ambiguous; the transport was closed. {ex.Message}"
                : ex.Message;
            await CloseLockedAsync(writeAttempted ? "ambiguous-command" : "faulted", message)
                .ConfigureAwait(false);
        }
        finally
        {
            _commandBefore = null;
            _io.Release();
        }
        return Status();
    }

    private async Task<bool> EnsureOpenLockedAsync(
        SpeTaurusConfig config,
        CancellationToken cancellationToken)
    {
        if (_transport?.IsOpen == true) return true;
        await CloseLockedAsync("connecting", null).ConfigureAwait(false);
        if (!CanUse(config)) return false;
        try
        {
            var transport = _transportFactory(config.Transport);
            await transport.OpenAsync(config, cancellationToken).ConfigureAwait(false);
            if (!CanUse(config))
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                return false;
            }
            _transport = transport;
            lock (_stateGate)
            {
                _connectionState = "connected";
                _error = null;
                if (config.Transport == "local") _availablePorts = SpeSerialPorts.List();
            }
            return true;
        }
        catch (Exception ex)
        {
            await CloseLockedAsync("faulted", ex.Message).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<SpeAmplifierStatus> RequestStatusLockedAsync(
        SpeTaurusConfig config,
        CancellationToken cancellationToken)
    {
        if (!CanUse(config)) throw new InvalidOperationException("amplifier-disabled");
        var transport = _transport ?? throw new IOException("Transport is not connected.");
        await transport.WriteAsync(SpeProtocol.EncodeCommand(SpeCommand.Status), cancellationToken)
            .ConfigureAwait(false);
        return await ReadStatusLockedAsync(config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SpeAmplifierStatus?> WaitForCommandResponseLockedAsync(
        SpeCommand command,
        SpeTaurusConfig config,
        CancellationToken cancellationToken)
    {
        var frame = await ReadFrameLockedAsync(config, cancellationToken, candidate =>
            candidate.IsStatus || (candidate.Data.Length == 1 && candidate.Data[0] == (byte)command))
            .ConfigureAwait(false);
        if (!frame.IsStatus) return null;
        var status = SpeProtocol.TryParseStatus(frame.Data);
        if (status is null)
        {
            lock (_stateGate) _invalidFrames++;
            throw new InvalidDataException("Amplifier returned a malformed status response.");
        }
        lock (_stateGate) _validFrames++;
        return status;
    }

    private async Task<SpeAmplifierStatus> ReadStatusLockedAsync(
        SpeTaurusConfig config,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReadFrameLockedAsync(config, cancellationToken, candidate => candidate.IsStatus)
                .ConfigureAwait(false);
            var status = SpeProtocol.TryParseStatus(frame.Data);
            if (status is not null)
            {
                lock (_stateGate) _validFrames++;
                return status;
            }
            lock (_stateGate) _invalidFrames++;
        }
    }

    private async Task<SpeFrame> ReadFrameLockedAsync(
        SpeTaurusConfig config,
        CancellationToken cancellationToken,
        Func<SpeFrame, bool> accept)
    {
        var transport = _transport ?? throw new IOException("Transport is not connected.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(config.ResponseTimeoutMs);
        var parser = new SpeFrameParser();
        var previousRejected = 0L;
        var buffer = new byte[512];
        try
        {
            while (true)
            {
                var read = await transport.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read <= 0) throw new EndOfStreamException("Amplifier transport closed.");
                var frames = parser.Push(buffer.AsSpan(0, read));
                var rejected = parser.RejectedFrames;
                if (rejected != previousRejected)
                {
                    lock (_stateGate) _invalidFrames += rejected - previousRejected;
                    previousRejected = rejected;
                }
                foreach (var frame in frames)
                    if (accept(frame)) return frame;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for a complete checksummed amplifier response.");
        }
    }

    private void ApplySample(SpeAmplifierStatus sample)
    {
        lock (_stateGate)
        {
            _amplifier = sample;
            _lastSampleUtc = DateTimeOffset.UtcNow;
            _connectionState = "connected";
            _error = null;
        }
    }

    private SpeTaurusStatus Reject(string message)
    {
        lock (_stateGate) _error = message;
        return Status();
    }

    private async Task CloseForGateAsync(string reason, CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CloseLockedAsync(reason, reason is "disabled" ? null : reason).ConfigureAwait(false); }
        finally { _io.Release(); }
    }

    private async Task CloseLockedAsync(string state, string? error)
    {
        var transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            try { await transport.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "spe-taurus.close failed"); }
            try { await transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "spe-taurus.dispose failed"); }
        }
        lock (_stateGate)
        {
            _connectionState = state;
            _error = error;
            _amplifier = null;
            _lastSampleUtc = null;
        }
    }

    private bool CanUse(SpeTaurusConfig config) =>
        !_disposed && config.Enabled && ReferenceEquals(config, _config);

    private static bool HasAlarm(SpeAmplifierStatus status) =>
        !string.Equals(status.AlarmCode, "N", StringComparison.OrdinalIgnoreCase);

    private static bool SameEndpoint(SpeTaurusConfig left, SpeTaurusConfig right) =>
        left.Transport == right.Transport
        && left.PortName == right.PortName
        && left.BaudRate == right.BaudRate
        && left.BridgeHost == right.BridgeHost
        && left.BridgePort == right.BridgePort
        && left.D2xxSerial == right.D2xxSerial
        && left.ResponseTimeoutMs == right.ResponseTimeoutMs
        && left.ConnectTimeoutMs == right.ConnectTimeoutMs;

    private static bool HasEndpoint(SpeTaurusConfig config, out string? error)
    {
        error = config.Transport switch
        {
            "local" when config.PortName.Length == 0 => "Select a USB or RS-232 serial port.",
            "d2xx" when config.D2xxSerial.Length == 0 => "Select the Taurus FTDI device by its exact serial number.",
            "tcp" when !IsValidHost(config.BridgeHost) => "Enter the G2 or serial-bridge host name.",
            _ => null,
        };
        return error is null;
    }

    private static string Endpoint(SpeTaurusConfig config) => config.Transport switch
    {
        "d2xx" => config.D2xxSerial,
        "tcp" => config.BridgeHost.Length == 0 ? "" : $"{config.BridgeHost}:{config.BridgePort}",
        _ => config.PortName.Length == 0 ? "" : $"{config.PortName} @ {config.BaudRate}",
    };

    internal static string SanitizeD2xxSerial(string? value)
    {
        var serial = value ?? "";
        if (!string.Equals(serial, serial.Trim(), StringComparison.Ordinal)
            || serial.Length > 64
            || serial.Any(character => char.IsControl(character) || character > 0x7f))
            throw new ArgumentException(
                "The FTDI serial must contain at most 64 printable ASCII characters with no surrounding whitespace; it is never transformed.",
                nameof(value));
        return serial;
    }

    internal static bool IsValidHost(string? value)
    {
        var host = (value ?? "").Trim();
        return host.Length is > 0 and <= 253
            && !host.Any(char.IsWhiteSpace)
            && Uri.CheckHostName(host) is not UriHostNameType.Unknown;
    }

    internal static SpeTaurusConfig Sanitize(SpeTaurusConfig config)
    {
        var transport = (config.Transport ?? "local").Trim().ToLowerInvariant();
        if (transport is not ("local" or "d2xx" or "tcp")) transport = "local";
        var baud = config.BaudRate is 9600 or 14400 or 19200 or 28800 or 38400 or 57600 or 115200
            ? config.BaudRate
            : 115200;
        return config with
        {
            Transport = transport,
            PortName = (config.PortName ?? "").Trim()[..Math.Min((config.PortName ?? "").Trim().Length, 256)],
            BaudRate = baud,
            BridgeHost = (config.BridgeHost ?? "").Trim()[..Math.Min((config.BridgeHost ?? "").Trim().Length, 253)],
            BridgePort = Math.Clamp(config.BridgePort, 1, 65535),
            ActivePollingMs = Math.Clamp(config.ActivePollingMs, 100, 2000),
            IdlePollingMs = Math.Clamp(config.IdlePollingMs, 250, 10000),
            ResponseTimeoutMs = Math.Clamp(config.ResponseTimeoutMs, 200, 5000),
            ConnectTimeoutMs = Math.Clamp(config.ConnectTimeoutMs, 250, 30000),
            D2xxSerial = SanitizeD2xxSerial(config.D2xxSerial),
        };
    }

}

internal sealed class SpeChangePulse
{
    private readonly object _gate = new();
    private TaskCompletionSource _source = NewSource();

    internal Task Capture()
    {
        lock (_gate) return _source.Task;
    }

    internal void Pulse()
    {
        TaskCompletionSource current;
        lock (_gate)
        {
            current = _source;
            _source = NewSource();
        }
        current.TrySetResult();
    }

    internal async Task WaitAsync(Task signal, CancellationToken cancellationToken) =>
        await signal.WaitAsync(cancellationToken).ConfigureAwait(false);

    internal async Task WaitOrDelayAsync(
        Task signal,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await Task.WhenAny(signal, Task.Delay(delay, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
