// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using Zeus.Server.Cat;

namespace Zeus.Server;

internal interface INanoVnaTransport : IAsyncDisposable
{
    string PortName { get; }
    Task OpenAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ExecuteAsync(
        string command, TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface INanoVnaTransportFactory
{
    INanoVnaTransport Create(string portName);
}

internal sealed class SystemNanoVnaTransportFactory : INanoVnaTransportFactory
{
    public INanoVnaTransport Create(string portName) => new SystemNanoVnaTransport(portName);
}

/// <summary>
/// NanoVNA shell transport compatible with the command flow used by
/// NanoVNA-Saver: 115200 8N1, CR-terminated commands, and a <c>ch&gt;</c>
/// prompt. It intentionally opens only the operator-selected port.
/// </summary>
internal sealed class SystemNanoVnaTransport : INanoVnaTransport
{
    private readonly SerialPort _port;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SystemNanoVnaTransport(string portName)
    {
        _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 50,
            WriteTimeout = 2000,
            Encoding = Encoding.ASCII,
        };
    }

    public string PortName => _port.PortName;

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _port.Open();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> ExecuteAsync(
        string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Execute(command, timeout, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<string> Execute(
        string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("NanoVNA serial port is not open.");
        DrainInput();
        _port.Write(command + "\r");

        var lines = new List<string>();
        var line = new StringBuilder();
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int next;
            try { next = _port.ReadByte(); }
            catch (TimeoutException) { continue; }
            if (next < 0) continue;
            char ch = (char)next;
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                AddLine(lines, line, command);
                continue;
            }
            line.Append(ch);
            if (line.Length >= 3 && line.ToString().EndsWith("ch>", StringComparison.Ordinal))
            {
                line.Length -= 3;
                AddLine(lines, line, command);
                return lines;
            }
        }
        throw new TimeoutException($"NanoVNA did not finish '{command}' within {timeout.TotalSeconds:F0} seconds.");
    }

    private static void AddLine(List<string> lines, StringBuilder line, string command)
    {
        string value = line.ToString().Trim();
        line.Clear();
        if (value.Length == 0 || string.Equals(value, command, StringComparison.Ordinal)) return;
        lines.Add(value);
    }

    private void DrainInput()
    {
        for (int i = 0; i < 16 && _port.BytesToRead > 0; i++)
        {
            _ = _port.ReadExisting();
            Thread.Sleep(10);
        }
    }

    public ValueTask DisposeAsync()
    {
        try { if (_port.IsOpen) _port.Close(); }
        finally
        {
            _port.Dispose();
            _gate.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed record NanoVnaConnectionStatus(
    bool Connected,
    string? PortName,
    string? DeviceName,
    string? Error);

/// <summary>
/// Vector S11 adapter for NanoVNA-family devices. Newer firmware uses the
/// scan-mask commands introduced in NanoVNA-Saver; older firmware falls back
/// to sweep/frequencies/data 0. The NanoVNA's active onboard calibration is
/// retained, and Zeus may optionally layer its own OSL capture over the data.
/// </summary>
public sealed partial class NanoVnaSweepHardware : IVnaSweepHardware, IDisposable
{
    private const int NativePoints = 101;
    private const int MaximumSegmentedPoints = 1001;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
    private readonly ILogger<NanoVnaSweepHardware> _log;
    private readonly INanoVnaTransportFactory _transportFactory;
    private readonly Func<IReadOnlyList<string>> _portEnumerator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Session? _session;
    private string? _lastError;

    private sealed record Session(
        INanoVnaTransport Transport,
        string Version,
        bool ScanMask,
        string DeviceName);

    public NanoVnaSweepHardware(ILogger<NanoVnaSweepHardware> log)
        : this(log, new SystemNanoVnaTransportFactory(), SerialPortEnumeration.AvailablePorts)
    {
    }

    internal NanoVnaSweepHardware(
        ILogger<NanoVnaSweepHardware> log,
        INanoVnaTransportFactory transportFactory,
        Func<IReadOnlyList<string>> portEnumerator)
    {
        _log = log;
        _transportFactory = transportFactory;
        _portEnumerator = portEnumerator;
    }

    public IReadOnlyList<string> AvailablePorts() => _portEnumerator();

    internal NanoVnaConnectionStatus ConnectionStatus
    {
        get
        {
            lock (_sync)
            {
                return _session is null
                    ? new(false, null, null, _lastError)
                    : new(true, _session.Transport.PortName, _session.DeviceName, null);
            }
        }
    }

    public VnaCapabilityDto Capability
    {
        get
        {
            NanoVnaConnectionStatus status = ConnectionStatus;
            Session? session;
            lock (_sync) session = _session;
            int maximumPoints = session?.ScanMask == true ? MaximumSegmentedPoints : NativePoints;
            return status.Connected
                ? new(true, true, status.DeviceName ?? "NanoVNA", "nanovna",
                    "NanoVNA connected. Vector S11 provides SWR, return loss, phase, and R+jX without keying the radio.",
                    RequiresExternalBridge: true, RequiresCalibration: false, MaximumPoints: maximumPoints)
                : new(false, true, "NanoVNA", "nanovna",
                    status.Error ?? "Select a NanoVNA serial device and connect it.",
                    RequiresExternalBridge: true, RequiresCalibration: false, MaximumPoints: maximumPoints);
        }
    }

    public async Task ConnectAsync(string portName, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        INanoVnaTransport? candidate = null;
        try
        {
            Session? prior;
            lock (_sync) prior = _session;
            if (prior is not null && PortEquals(prior.Transport.PortName, portName)) return;

            candidate = _transportFactory.Create(portName);
            await candidate.OpenAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> versionLines = await candidate.ExecuteAsync(
                "version", CommandTimeout, cancellationToken).ConfigureAwait(false);
            string version = versionLines.FirstOrDefault(line => VersionRegex().IsMatch(line))
                ?? throw new InvalidOperationException("The selected port did not identify itself as a NanoVNA.");
            IReadOnlyList<string> help = await candidate.ExecuteAsync(
                "help", CommandTimeout, cancellationToken).ConfigureAwait(false);
            bool nanoVnaCommands = help.Any(line =>
                line.Contains("scan", StringComparison.OrdinalIgnoreCase)
                || line.Contains("sweep", StringComparison.OrdinalIgnoreCase))
                && help.Any(line => line.Contains("data", StringComparison.OrdinalIgnoreCase));
            if (!nanoVnaCommands)
                throw new InvalidOperationException("The selected port does not expose the NanoVNA sweep and data commands.");
            bool scanMask = help.Any(line => line.Contains("scan", StringComparison.OrdinalIgnoreCase))
                && ParsedVersion(version) >= new Version(0, 7, 1);
            string deviceName = $"NanoVNA {VersionRegex().Match(version).Value} ({portName})";
            var next = new Session(candidate, version, scanMask, deviceName);
            candidate = null;
            lock (_sync)
            {
                _session = next;
                _lastError = null;
            }
            if (prior is not null) await prior.Transport.DisposeAsync().ConfigureAwait(false);
            _log.LogInformation("vna.nanovna connected port={Port} version={Version} scanMask={ScanMask}",
                portName, version, scanMask);
        }
        catch (OperationCanceledException)
        {
            if (candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            if (candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
            string detail = SerialPortEnumeration.Describe(ex);
            lock (_sync) _lastError = detail;
            throw new InvalidOperationException($"Could not connect to NanoVNA on {portName}: {detail}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Session? session;
            lock (_sync)
            {
                session = _session;
                _session = null;
                _lastError = null;
            }
            if (session is not null) await session.Transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VnaCaptureResult> CaptureAsync(
        long startHz, long endHz, int points, int driveLevel, bool fixedRxGainHigh,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        _ = driveLevel;
        _ = fixedRxGainHigh;
        if (startHz <= 0 || endHz <= startHz)
            throw new ArgumentException("Sweep end must be above sweep start.");
        if (points is < 3 or > MaximumSegmentedPoints)
            throw new ArgumentOutOfRangeException(nameof(points), $"NanoVNA sweep points must be 3..{MaximumSegmentedPoints}.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Session session;
            lock (_sync) session = _session
                ?? throw new InvalidOperationException("Select a NanoVNA serial device and connect it.");
            if (!session.ScanMask && points > NativePoints)
                throw new ArgumentOutOfRangeException(nameof(points),
                    $"This NanoVNA firmware supports at most {NativePoints} points per sweep.");

            var samples = new List<VnaComplexSample>(points);
            int segments = session.ScanMask ? (int)Math.Ceiling(points / (double)NativePoints) : 1;
            int baseCount = points / segments;
            int remainder = points % segments;
            int offset = 0;
            for (int segment = 0; segment < segments; segment++)
            {
                int count = baseCount + (segment < remainder ? 1 : 0);
                long segmentStart = FrequencyAt(startHz, endHz, points, offset);
                long segmentEnd = FrequencyAt(startHz, endHz, points, offset + count - 1);
                IReadOnlyList<long> frequencies;
                IReadOnlyList<ComplexPair> s11;
                if (session.ScanMask)
                {
                    frequencies = ParseFrequencies(await session.Transport.ExecuteAsync(
                        $"scan {segmentStart} {segmentEnd} {count} 0b001", CommandTimeout, cancellationToken)
                        .ConfigureAwait(false));
                    s11 = ParseS11(await session.Transport.ExecuteAsync(
                        $"scan {segmentStart} {segmentEnd} {count} 0b110", CommandTimeout, cancellationToken)
                        .ConfigureAwait(false), fourColumns: true);
                }
                else
                {
                    await session.Transport.ExecuteAsync(
                        $"sweep {segmentStart} {segmentEnd} {count}", CommandTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    frequencies = ParseFrequencies(await session.Transport.ExecuteAsync(
                        "frequencies", CommandTimeout, cancellationToken).ConfigureAwait(false));
                    s11 = ParseS11(await session.Transport.ExecuteAsync(
                        "data 0", CommandTimeout, cancellationToken).ConfigureAwait(false), fourColumns: false);
                }
                if (frequencies.Count != count || s11.Count != count)
                    throw new InvalidOperationException(
                        $"NanoVNA returned {frequencies.Count} frequencies and {s11.Count} S11 values; expected {count}.");
                for (int i = 0; i < count; i++)
                {
                    samples.Add(new VnaComplexSample(frequencies[i], s11[i].Real, s11[i].Imaginary));
                    progress?.Report(samples.Count);
                }
                offset += count;
            }
            return new VnaCaptureResult(samples, ReflectionCalibrated: true, Vector: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal readonly record struct ComplexPair(double Real, double Imaginary);

    private static long FrequencyAt(long startHz, long endHz, int points, int index) =>
        startHz + (long)Math.Round((endHz - startHz) * index / (double)(points - 1));

    internal static IReadOnlyList<long> ParseFrequencies(IEnumerable<string> lines) =>
        lines.Select(line => long.Parse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();

    internal static IReadOnlyList<ComplexPair> ParseS11(IEnumerable<string> lines, bool fourColumns)
    {
        var result = new List<ComplexPair>();
        foreach (string line in lines)
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int required = fourColumns ? 4 : 2;
            if (fields.Length < required)
                throw new FormatException($"NanoVNA returned malformed S11 data: '{line}'.");
            double real = double.Parse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture);
            double imaginary = double.Parse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!double.IsFinite(real) || !double.IsFinite(imaginary))
                throw new FormatException($"NanoVNA returned non-finite S11 data: '{line}'.");
            result.Add(new ComplexPair(real, imaginary));
        }
        return result;
    }

    private static Version ParsedVersion(string value)
    {
        Match match = VersionRegex().Match(value);
        return Version.TryParse(match.Value, out Version? version) ? version : new Version(0, 0);
    }

    private static bool PortEquals(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionRegex();

    public void Dispose()
    {
        Session? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }
        if (session is not null) session.Transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _gate.Dispose();
    }
}
