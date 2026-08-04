// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA),
//                    Christian Suarez (N9WAR), and contributors.

using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LiteDB;

namespace Zeus.Server;

public sealed record HfAutoRuntimeConfig(
    bool Enabled,
    string BindAddress,
    int Port,
    string ExpectedSenderAddress = "127.0.0.1");

public sealed record HfAutoTelemetry(
    bool? Valid,
    string? FirmwareVersion,
    string? Mode,
    int? ModeCode,
    long? DetectedFrequencyHz,
    int? Capacitor,
    int? Inductor,
    int? Antenna,
    string? SelectedMotor,
    double? DisplayedPowerWatts,
    bool? PeakPower,
    int? PowerRange,
    double? Swr,
    bool? HasRf,
    bool? Stored,
    bool? UnsolicitedMessages,
    bool? TuneComplete,
    bool? GoodAutoModePower,
    bool? InvalidFrequency,
    bool? InvalidInductor,
    bool? InvalidCapacitor,
    string? RawBytes);

public sealed record HfAutoStatus(
    HfAutoRuntimeConfig Config,
    bool ListenerActive,
    bool Connected,
    DateTime? LastReceivedUtc,
    string? LastRemoteEndpoint,
    long PacketsReceived,
    string? Error,
    HfAutoTelemetry? Telemetry);

public sealed class HfAutoConfigStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<HfAutoConfigEntry> _entries;
    private readonly object _sync = new();

    public HfAutoConfigStore(string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _entries = _dbLease.Database.GetCollection<HfAutoConfigEntry>("hf_auto_config");
    }

    public HfAutoRuntimeConfig? Get()
    {
        lock (_sync)
        {
            var entry = _entries.FindAll().FirstOrDefault();
            return entry is null
                ? null
                : new HfAutoRuntimeConfig(
                    entry.Enabled,
                    entry.BindAddress,
                    entry.Port,
                    entry.ExpectedSenderAddress);
        }
    }

    public void Set(HfAutoRuntimeConfig config)
    {
        lock (_sync)
        {
            var entry = _entries.FindAll().FirstOrDefault() ?? new HfAutoConfigEntry();
            entry.Enabled = config.Enabled;
            entry.BindAddress = config.BindAddress;
            entry.Port = config.Port;
            entry.ExpectedSenderAddress = config.ExpectedSenderAddress;
            entry.UpdatedUtc = DateTime.UtcNow;
            _entries.Upsert(entry);
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class HfAutoConfigEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 50001;
    public string ExpectedSenderAddress { get; set; } = "127.0.0.1";
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Receives the HF-AUTO Controller's optional UDP JSON output. The companion
/// application remains responsible for the proprietary serial transport;
/// Zeus consumes only the explicitly exposed interoperability stream.
/// </summary>
public sealed class HfAutoService : BackgroundService
{
    private static readonly TimeSpan ConnectedWindow = TimeSpan.FromSeconds(5);
    private readonly ILogger<HfAutoService> _log;
    private readonly HfAutoConfigStore _store;
    private readonly object _sync = new();
    private readonly object _configurationSync = new();

    private HfAutoRuntimeConfig _config;
    private CancellationTokenSource? _listenerCancellation;
    private int _configurationVersion;
    private bool _listenerActive;
    private DateTime? _lastReceivedUtc;
    private string? _lastRemoteEndpoint;
    private long _packetsReceived;
    private string? _error;
    private HfAutoTelemetry? _telemetry;

    public HfAutoService(ILogger<HfAutoService> log, HfAutoConfigStore store)
    {
        _log = log;
        _store = store;
        _config = store.Get() ?? new HfAutoRuntimeConfig(false, "127.0.0.1", 50001, "127.0.0.1");
    }

    public HfAutoStatus GetStatus()
    {
        lock (_sync)
        {
            var connected = _listenerActive
                && _lastReceivedUtc is DateTime received
                && DateTime.UtcNow - received <= ConnectedWindow;
            return new HfAutoStatus(
                _config,
                _listenerActive,
                connected,
                _lastReceivedUtc,
                _lastRemoteEndpoint,
                _packetsReceived,
                _error,
                _telemetry);
        }
    }

    public HfAutoStatus SetConfig(HfAutoRuntimeConfig requested)
    {
        lock (_configurationSync)
        {
            var normalized = NormalizeConfig(requested);
            _store.Set(normalized);

            CancellationTokenSource? listener;
            lock (_sync)
            {
                _config = normalized;
                _configurationVersion++;
                listener = _listenerCancellation;
                if (!normalized.Enabled)
                {
                    _listenerActive = false;
                    _error = null;
                }
            }

            try
            {
                listener?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The receive loop completed between capture and cancellation.
            }
            _log.LogInformation(
                "hfauto.config enabled={Enabled} bind={Bind} port={Port}",
                normalized.Enabled,
                normalized.BindAddress,
                normalized.Port);
            return GetStatus();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            HfAutoRuntimeConfig config;
            int version;
            lock (_sync)
            {
                config = _config;
                version = _configurationVersion;
            }

            if (!config.Enabled)
            {
                await Task.Delay(500, stoppingToken);
                continue;
            }

            using var rebind = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, rebind.Token);
            lock (_sync)
            {
                _listenerCancellation = rebind;
                if (version != _configurationVersion)
                    rebind.Cancel();
            }

            try
            {
                var endpoint = new IPEndPoint(ResolveBindAddress(config.BindAddress), config.Port);
                var expectedSender = ResolveExpectedSender(config.ExpectedSenderAddress);
                using var udp = new UdpClient(endpoint);
                SetListenerState(active: true, error: null);
                _log.LogInformation("hfauto.udp.listening bind={Bind} port={Port}", config.BindAddress, config.Port);

                while (!linked.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(linked.Token);
                    if (!AddressesEqual(result.RemoteEndPoint.Address, expectedSender))
                    {
                        _log.LogDebug(
                            "hfauto.udp.sender-rejected remote={Remote} expected={Expected}",
                            result.RemoteEndPoint.Address,
                            expectedSender);
                        continue;
                    }
                    ReceiveDatagram(result.Buffer, result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // Configuration changed or the host is stopping.
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                SetListenerState(active: false, error: ex.Message);
                _log.LogWarning(ex, "hfauto.udp.listener failed");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_listenerCancellation, rebind))
                        _listenerCancellation = null;
                    _listenerActive = false;
                }
            }

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(750, stoppingToken);
        }
    }

    private void ReceiveDatagram(byte[] bytes, IPEndPoint remote)
    {
        if (bytes.Length is 0 or > 65_536)
            return;

        try
        {
            var telemetry = ParseTelemetry(bytes);
            lock (_sync)
            {
                _telemetry = telemetry;
                _lastReceivedUtc = DateTime.UtcNow;
                _lastRemoteEndpoint = remote.ToString();
                _packetsReceived++;
                _error = null;
            }
        }
        catch (JsonException ex)
        {
            lock (_sync)
                _error = "The last HF-AUTO UDP datagram was not valid JSON.";
            _log.LogDebug(ex, "hfauto.udp.invalid-json remote={Remote}", remote);
        }
    }

    private void SetListenerState(bool active, string? error)
    {
        lock (_sync)
        {
            _listenerActive = active;
            _error = error;
        }
    }

    private static HfAutoRuntimeConfig NormalizeConfig(HfAutoRuntimeConfig config)
    {
        if (config.Port is <= 0 or >= 65_536)
            throw new ArgumentException("Port must be between 1 and 65535.");

        var bind = string.IsNullOrWhiteSpace(config.BindAddress)
            ? "127.0.0.1"
            : config.BindAddress.Trim();
        _ = ResolveBindAddress(bind);
        var expectedSender = string.IsNullOrWhiteSpace(config.ExpectedSenderAddress)
            ? throw new ArgumentException("Expected sender address is required.")
            : config.ExpectedSenderAddress.Trim();
        _ = ResolveExpectedSender(expectedSender);
        return config with { BindAddress = bind, ExpectedSenderAddress = expectedSender };
    }

    private static IPAddress ResolveBindAddress(string bindAddress)
    {
        if (bindAddress is "0.0.0.0" or "*") return IPAddress.Any;
        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (IPAddress.TryParse(bindAddress, out var parsed)) return parsed;
        throw new ArgumentException("Bind address must be an IP address, localhost, or *.");
    }

    private static IPAddress ResolveExpectedSender(string senderAddress)
    {
        if (string.Equals(senderAddress, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (!IPAddress.TryParse(senderAddress, out var parsed)
            || parsed.Equals(IPAddress.Any)
            || parsed.Equals(IPAddress.IPv6Any))
            throw new ArgumentException("Expected sender must be one specific IP address or localhost.");
        return parsed;
    }

    internal static bool IsExpectedSender(IPAddress remoteAddress, string expectedSenderAddress) =>
        AddressesEqual(remoteAddress, ResolveExpectedSender(expectedSenderAddress));

    private static bool AddressesEqual(IPAddress left, IPAddress right) =>
        left.MapToIPv6().Equals(right.MapToIPv6());

    internal static HfAutoTelemetry ParseTelemetry(byte[] bytes)
    {
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("HF-AUTO telemetry must be a JSON object.");

        var modeCode = ReadInt(root, "mode", "modeCode");
        var modeText = NonNumericText(ReadString(root, "modeName", "modeText", "mode"));
        var mode = modeCode switch
        {
            1 => "AUTO",
            _ => modeText?.ToUpperInvariant(),
        };

        var motorCode = ReadInt(root, "stepperMotorSelection", "selectedMotor", "motor");
        var motorText = NonNumericText(ReadString(
            root,
            "stepperMotorName",
            "selectedMotorName",
            "stepperMotorSelection",
            "selectedMotor"));
        var motor = motorCode switch
        {
            2 => "IND",
            _ => motorText?.ToUpperInvariant(),
        };

        return new HfAutoTelemetry(
            ReadBool(root, "valid", "isValid"),
            ReadString(root, "firmwareVersion", "fwVersion", "firmware", "fw"),
            mode,
            modeCode,
            ReadLong(root, "detectedFrequency", "detectedFrequencyHz", "frequency", "frequencyHz"),
            ReadInt(root, "displayedValueC", "capacitor", "c"),
            ReadInt(root, "displayedValueL", "inductor", "l"),
            ReadInt(root, "antennaSelected", "antenna", "antennaNumber"),
            motor,
            ReadDouble(root, "displayedPower", "displayedPowerWatts", "power", "powerWatts"),
            ReadBool(root, "isPeakPower", "peakPower"),
            ReadInt(root, "powerRange", "powerRangeWatts"),
            ReadDouble(root, "displayedSwr", "swr"),
            ReadBool(root, "hasRf", "rfPresent"),
            ReadBool(root, "stored", "isStored"),
            ReadBool(root, "unsolicitedMessages", "hasUnsolicitedMessages"),
            ReadBool(root, "tuneComplete", "isTuneComplete"),
            ReadBool(root, "goodAutoModePower"),
            ReadBool(root, "invalidFreq", "invalidFrequency"),
            ReadBool(root, "invalidL", "invalidInductor"),
            ReadBool(root, "invalidC", "invalidCapacitor"),
            ReadString(root, "rawBytes", "raw"));
    }

    private static JsonElement? Find(JsonElement root, params string[] names)
    {
        var wanted = names.Select(NormalizeName).ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (wanted.Contains(NormalizeName(property.Name)))
                return property.Value;
        }
        return null;
    }

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? NonNumericText(string? value) =>
        value is not null && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? value
            : null;

    private static string? ReadString(JsonElement root, params string[] names)
    {
        var value = Find(root, names);
        if (value is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }) return null;
        return value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString()
            : value.Value.GetRawText();
    }

    private static int? ReadInt(JsonElement root, params string[] names)
    {
        var value = Find(root, names);
        if (value is null) return null;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var number)) return number;
        return value.Value.ValueKind == JsonValueKind.String
            && int.TryParse(value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static long? ReadLong(JsonElement root, params string[] names)
    {
        var value = Find(root, names);
        if (value is null) return null;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var number)) return number;
        return value.Value.ValueKind == JsonValueKind.String
            && long.TryParse(value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static double? ReadDouble(JsonElement root, params string[] names)
    {
        var value = Find(root, names);
        if (value is null) return null;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var number)) return number;
        return value.Value.ValueKind == JsonValueKind.String
            && double.TryParse(value.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static bool? ReadBool(JsonElement root, params string[] names)
    {
        var value = Find(root, names);
        if (value is null) return null;
        if (value.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.Value.GetBoolean();
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var number))
            return number switch { 0 => false, 1 => true, _ => null };
        return value.Value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.Value.GetString(), out var parsed) ? parsed : null;
    }
}
