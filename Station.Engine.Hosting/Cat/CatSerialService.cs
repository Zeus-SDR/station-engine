// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.IO.Ports;
using Microsoft.Extensions.Options;
using Zeus.Contracts;

namespace Zeus.Server.Cat;

/// <summary>
/// Hosts the four serial CAT ports (Thetis CAT1–4). The serial sibling of
/// <see cref="CatServer"/>: where CatServer accepts TCP clients, this opens up
/// to <see cref="CatSerialDefaults.PortCount"/> configured serial devices and
/// runs a <see cref="CatSerialPort"/> on each, all feeding the SAME
/// <see cref="CatCommandHandler"/> command set. One subscription to the radio /
/// TX / RX-meter events fans Auto-Information pushes out to every open port
/// whose client enabled AI — identical to CatServer's broadcast, just over
/// serial.
///
/// <para>Lifecycle mirrors the shipped <c>G2FrontPanelService</c>: a settings
/// change (<see cref="CatSerialConfigStore.Changed"/>) cancels the per-pass
/// wake token, tears the ports down, and re-resolves from disk — no server
/// restart. Each port self-reconnects on a backoff if its device is busy or
/// unplugged, so a port that comes back (com0com / socat re-created) re-opens
/// on its own.</para>
/// </summary>
public sealed class CatSerialService : BackgroundService
{
    private sealed class Slot
    {
        public volatile bool Open;
        public volatile string? Error;
        // Read from the AI-broadcast event threads while RunPortAsync writes it;
        // volatile so a fan-out always sees the current open port (or null).
        public volatile CatSerialPort? Live;
    }

    private readonly ILogger<CatSerialService> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CatOptions _options;
    private readonly CatSerialConfigStore _store;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipeline;

    private readonly Slot[] _slots;

    // Per-pass linked CTS so a settings change (RequestReconnect) breaks the
    // current port set and forces a re-resolve, without stopping the service.
    private volatile CancellationTokenSource? _wakeCts;
    private bool _subscribed;

    // Latest RX1 level (dBm) for the SM poll, stored as long bits + Interlocked
    // so a 32-bit runtime (Pi) can't read a torn double. Mirrors CatServer.
    private long _latestRxDbmBits = BitConverter.DoubleToInt64Bits(-130.0);

    public CatSerialService(
        ILogger<CatSerialService> log,
        ILoggerFactory loggerFactory,
        IOptions<CatOptions> options,
        CatSerialConfigStore store,
        RadioService radio,
        TxService tx,
        DspPipelineService pipeline)
    {
        _log = log;
        _loggerFactory = loggerFactory;
        _options = options.Value;
        _store = store;
        _radio = radio;
        _tx = tx;
        _pipeline = pipeline;
        _slots = new Slot[CatSerialDefaults.PortCount];
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new Slot();
        _store.Changed += OnSettingsChanged;
    }

    public double LatestRxDbm => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestRxDbmBits));

    private void OnSettingsChanged() => RequestReconnect();

    /// <summary>Break the current port set so the run loop re-reads settings and
    /// re-opens. Safe to call from the settings endpoint thread.</summary>
    public void RequestReconnect()
    {
        try { _wakeCts?.Cancel(); }
        catch (ObjectDisposedException) { /* loop already advanced */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe once for the lifetime of the service; AI fan-out checks each
        // open port's client-driven AI state, so a quiet port costs nothing.
        _radio.StateChanged += OnRadioStateChanged;
        _radio.MoxChanged += OnMoxChanged;
        _pipeline.RxMeterUpdated += OnRxMeterUpdated;
        _subscribed = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _wakeCts = wake;
                var ct = wake.Token;

                var configs = _store.Get();
                var active = new List<Task>();
                var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _slots.Length; i++)
                {
                    var cfg = configs[i];
                    if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.PortName))
                    {
                        _slots[i].Error = null;
                        continue;
                    }
                    // A serial device is exclusive-open: two enabled slots on the
                    // same path would have the loser fail-and-retry every 5 s
                    // forever. Open it once; flag the duplicate clearly instead.
                    if (!claimed.Add(cfg.PortName.Trim()))
                    {
                        _slots[i].Open = false;
                        _slots[i].Error = $"Duplicate device {cfg.PortName} (already used by another CAT port)";
                        continue;
                    }
                    active.Add(RunPortAsync(i, cfg, ct));
                }

                if (active.Count == 0)
                {
                    // No ports enabled — idle until a settings change or shutdown.
                    await DelaySafe(Timeout.InfiniteTimeSpan, ct);
                    continue;
                }

                try { await Task.WhenAll(active); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* settings change — re-resolve */ }
                catch (Exception ex) { _log.LogDebug(ex, "cat.serial.runset ended"); }
            }
        }
        finally
        {
            if (_subscribed)
            {
                _radio.StateChanged -= OnRadioStateChanged;
                _radio.MoxChanged -= OnMoxChanged;
                _pipeline.RxMeterUpdated -= OnRxMeterUpdated;
                _subscribed = false;
            }
        }
    }

    // One port's open → read → reconnect loop. Catches its own faults and backs
    // off, so it only returns when the wake/stop token cancels.
    private async Task RunPortAsync(int index, CatSerialPortConfig cfg, CancellationToken ct)
    {
        var slot = _slots[index];
        var portLog = _loggerFactory.CreateLogger<CatSerialPort>();
        while (!ct.IsCancellationRequested)
        {
            CatSerialPort? port = null;
            try
            {
                port = new CatSerialPort(
                    cfg.PortName, cfg.BaudRate, ParseParity(cfg.Parity), cfg.DataBits, ParseStopBits(cfg.StopBits),
                    _radio, _tx, _options, () => LatestRxDbm, portLog);
                port.Open();
                // Per-port "Auto Report" (piHPSDR AutoRprt parity): pre-enable
                // AI1 so devices that never send AI1; still get unsolicited
                // frequency/state pushes. Must be applied after Open() so a
                // pre-enabled state is in effect for the very first fan-out.
                if (cfg.AutoReport) port.EnableAutoInfo();
                slot.Live = port;
                slot.Open = true;
                slot.Error = null;
                _log.LogInformation("cat.serial.open index={Idx} port={Port} baud={Baud} {Data}{Parity}{Stop}{AutoReport}",
                    index + 1, cfg.PortName, cfg.BaudRate, cfg.DataBits, cfg.Parity[..1], StopBitsDigit(cfg.StopBits),
                    cfg.AutoReport ? " auto-report" : string.Empty);
                await port.RunAsync(ct);
            }
            // A requested cancellation (settings change / shutdown) force-closes
            // the port, which unblocks the read as OCE on some platforms and
            // IOException/ObjectDisposedException on others. Either way it's a
            // clean teardown — never a fault to surface in status or log as an error.
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                slot.Error = Describe(ex);
                _log.LogWarning(ex, "cat.serial.error index={Idx} port={Port}; retrying", index + 1, cfg.PortName);
            }
            finally
            {
                slot.Open = false;
                slot.Live = null;
                port?.Dispose();
            }

            // Backoff before reconnect (busy / unplugged). Cancellable so a
            // settings change or shutdown doesn't wait it out.
            if (!ct.IsCancellationRequested)
                await DelaySafe(TimeSpan.FromSeconds(5), ct);
        }
    }

    // --- Auto-Information pushes (open ports whose client enabled AI1/AI2) ---

    private void OnRadioStateChanged(StateDto state)
    {
        if (!AnyOpen) return;
        BroadcastRateLimited("FA", CatProtocol.Response("FA", CatProtocol.FormatFreq(state.VfoHz)));
        Broadcast(CatProtocol.Response("MD", CatProtocol.ModeDigit(state.Mode)));
        BroadcastRateLimited("IF",
            CatProtocol.Response("IF", CatProtocol.BuildIfBody(state.VfoHz, state.Mode, _tx.IsMoxOn, split: false)));
    }

    private void OnMoxChanged(bool moxOn)
    {
        if (!AnyOpen) return;
        var state = _radio.Snapshot();
        Broadcast(CatProtocol.Response("IF", CatProtocol.BuildIfBody(state.VfoHz, state.Mode, moxOn, split: false)));
    }

    private void OnRxMeterUpdated(int channelId, double dbm)
    {
        if (channelId == 0)
            Interlocked.Exchange(ref _latestRxDbmBits, BitConverter.DoubleToInt64Bits(dbm));
    }

    private bool AnyOpen
    {
        get
        {
            foreach (var s in _slots) if (s.Live is not null) return true;
            return false;
        }
    }

    private void Broadcast(string line)
    {
        foreach (var s in _slots)
        {
            var p = s.Live;
            if (p is not null && p.AutoInfoEnabled) p.Send(line);
        }
    }

    private void BroadcastRateLimited(string key, string line)
    {
        foreach (var s in _slots)
        {
            var p = s.Live;
            if (p is not null && p.AutoInfoEnabled) p.SendRateLimited(key, line);
        }
    }

    /// <summary>Per-port status (config from disk + live open/error/activity)
    /// plus the host's enumerable serial devices for the UI's suggestions.</summary>
    public CatSerialStatus Snapshot()
    {
        var configs = _store.Get();
        var ports = new List<CatSerialPortStatus>(CatSerialDefaults.PortCount);
        for (int i = 0; i < CatSerialDefaults.PortCount; i++)
        {
            var c = configs[i];
            var s = _slots[i];
            ports.Add(new CatSerialPortStatus(
                Index: i,
                Enabled: c.Enabled,
                PortName: c.PortName,
                BaudRate: c.BaudRate,
                Parity: c.Parity,
                DataBits: c.DataBits,
                StopBits: c.StopBits,
                AutoReport: c.AutoReport,
                Open: s.Open,
                ClientActivity: s.Live?.Activity ?? 0,
                Error: s.Error));
        }
        return new CatSerialStatus(ports, AvailablePorts());
    }

    /// <summary>Probe-open a port with the given params to verify it can be used,
    /// then close it. Used by the Settings "Test" button.</summary>
    public CatSerialTestResult TestPort(CatSerialTestRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.PortName))
            return new CatSerialTestResult(false, "Port name required");
        try
        {
            using var p = new SerialPort(req.PortName.Trim(), req.BaudRate,
                ParseParity(req.Parity), req.DataBits, ParseStopBits(req.StopBits))
            {
                Handshake = Handshake.None,
                ReadTimeout = 300,
                WriteTimeout = 300,
            };
            p.Open();
            p.Close();
            return new CatSerialTestResult(true, null);
        }
        catch (Exception ex)
        {
            return new CatSerialTestResult(false, Describe(ex));
        }
    }

    private static IReadOnlyList<string> AvailablePorts()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in SerialPort.GetPortNames()) results.Add(p);
        }
        catch
        {
            // GetPortNames can throw on some platforms; suggestions are optional.
        }

        // Linux: the standard TTY enumeration above only sees kernel-assigned
        // /dev/ttyUSB* /dev/ttyACM* nodes. Many stations use persistent udev
        // symlinks — either the by-id/by-path directories udev populates
        // automatically, or user-created aliases like /dev/KPA500. Include both
        // so the dropdown surfaces the same names an operator picked when
        // writing their udev rules.
        if (OperatingSystem.IsLinux())
        {
            AddUdevSerialDir(results, "/dev/serial/by-id");
            AddUdevSerialDir(results, "/dev/serial/by-path");
            AddDevSymlinksToTty(results, "/dev");
        }

        return results
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // /dev/serial/by-id and /dev/serial/by-path are udev-managed and their
    // entire purpose is TTY aliases — add every entry.
    private static void AddUdevSerialDir(HashSet<string> results, string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFileSystemEntries(dir))
                results.Add(f);
        }
        catch { /* permission or races — suggestions are optional */ }
    }

    // Top-level symlinks in /dev/ can point at anything (null, zero, disk
    // partitions). Only keep the ones whose resolved target basename starts
    // with tty or cu — the only names SerialPort will actually open.
    private static void AddDevSymlinksToTty(HashSet<string> results, string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFileSystemEntries(dir))
            {
                try
                {
                    var attrs = File.GetAttributes(f);
                    if ((attrs & FileAttributes.ReparsePoint) == 0) continue;
                    var target = File.ResolveLinkTarget(f, returnFinalTarget: true);
                    if (target is null) continue;
                    var name = Path.GetFileName(target.FullName);
                    if (name.StartsWith("tty", StringComparison.Ordinal)
                        || name.StartsWith("cu", StringComparison.Ordinal))
                    {
                        results.Add(f);
                    }
                }
                catch { /* skip this entry */ }
            }
        }
        catch { /* permission or races — suggestions are optional */ }
    }

    internal static Parity ParseParity(string? s) =>
        Enum.TryParse<Parity>(s, ignoreCase: true, out var p) ? p : Parity.None;

    internal static StopBits ParseStopBits(string? s) =>
        Enum.TryParse<StopBits>(s, ignoreCase: true, out var sb) && sb != StopBits.None ? sb : StopBits.One;

    private static string StopBitsDigit(string? s) => s switch
    {
        "Two" => "2",
        "OnePointFive" => "1.5",
        _ => "1",
    };

    // Friendly, actionable message for the common serial-open failures.
    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Port is in use or access denied (another app may hold it; on Linux the user must be in the dialout group)",
        FileNotFoundException => "Port not found",
        ArgumentException => "Invalid port name",
        IOException io => io.Message,
        _ => ex.Message,
    };

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        _store.Changed -= OnSettingsChanged;
        foreach (var s in _slots)
        {
            s.Live?.Dispose();
            s.Live = null;
        }
        base.Dispose();
    }
}
