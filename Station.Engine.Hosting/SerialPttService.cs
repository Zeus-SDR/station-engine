// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// Serial PTT switch input (Thetis "Bit Bang PTT" parity, mechanism only). The
// operator wires a footswitch/hand switch between a USB-serial adapter's RTS
// or DTR line and a modem status pin (CTS and/or DSR); this service opens the
// port (9600/8/N/1 — baud is irrelevant, no data channel is used), or borrows
// the already-open handle when the same device is enabled for serial CAT. It
// asserts RTS+DTR (that supplies the +V the switch pulls the sensed pin to)
// and polls the status pins. A pin edge feeds the SHARED ExternalPttService via
// HandleSerialPtt, so hang/ownership/arbitration behavior is identical to the
// radio's hardware PTT-IN — nothing about MOX release logic lives here.
//
// Lifecycle mirrors CatSerialService: a settings change (store Changed)
// cancels the per-pass wake token, tears the port down, and re-resolves from
// disk — no server restart. Open/poll failures (port yanked, USB unplugged)
// record a status error, back off 5 s, and retry while still enabled.

using System.IO.Ports;
using Zeus.Contracts;
using Zeus.Server.Cat;

namespace Zeus.Server;

public sealed class SerialPttService : BackgroundService
{
    // Thetis PollPTT cadence: ~10 ms while unkeyed, ~1 ms while keyed (faster
    // unkey detection mid-transmission). No debounce — pin asserted = keyed,
    // exactly like Thetis. Note: the Windows timer resolution floor (~15.6 ms)
    // means Task.Delay(1 ms) there effectively polls at the system tick — the
    // keyed cadence is best-effort on Windows, matching Thetis's intent.
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan KeyedPollInterval = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan SharedHandleWaitInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReopenBackoff = TimeSpan.FromSeconds(5);

    private readonly ILogger<SerialPttService> _log;
    private readonly SerialPttSettingsStore _store;
    private readonly ExternalPttService _externalPtt;
    private readonly Func<string, ISerialPttPort> _portFactory;
    private readonly CatSerialConfigStore? _catStore;
    private readonly ISerialPttPinSource? _catSerial;

    // Live status for the REST snapshot. Volatile: written by the run loop,
    // read by endpoint threads.
    private volatile bool _portOpen;
    private volatile string? _error;
    private volatile bool _keyed;
    private volatile bool _sharedWithCat;
    private volatile bool _ctsHolding;
    private volatile bool _dsrHolding;
    private volatile bool _rtsAsserted;
    private volatile bool _dtrAsserted;

    // Per-pass wake token; a settings change cancels it to force a re-resolve.
    private volatile CancellationTokenSource? _wakeCts;

    public SerialPttService(
        ILogger<SerialPttService> log,
        SerialPttSettingsStore store,
        ExternalPttService externalPtt,
        CatSerialConfigStore catStore,
        CatSerialService catSerial)
        : this(log, store, externalPtt, portName => new SystemSerialPttPort(portName), catStore, catSerial)
    {
    }

    // Test seam: injectable port factory so unit tests drive a fake — never a
    // real serial device.
    internal SerialPttService(
        ILogger<SerialPttService> log,
        SerialPttSettingsStore store,
        ExternalPttService externalPtt,
        Func<string, ISerialPttPort> portFactory)
        : this(log, store, externalPtt, portFactory, null, null)
    {
    }

    internal SerialPttService(
        ILogger<SerialPttService> log,
        SerialPttSettingsStore store,
        ExternalPttService externalPtt,
        Func<string, ISerialPttPort> portFactory,
        CatSerialConfigStore? catStore,
        ISerialPttPinSource? catSerial)
    {
        _log = log;
        _store = store;
        _externalPtt = externalPtt;
        _portFactory = portFactory;
        _catStore = catStore;
        _catSerial = catSerial;
        _store.Changed += OnSettingsChanged;
        if (_catStore is not null) _catStore.Changed += OnSettingsChanged;
    }

    public bool PortOpen => _portOpen;
    public string? Error => _error;
    /// <summary>Last asserted switch level (pin edge fed downstream).</summary>
    public bool Keyed => _keyed;

    /// <summary>Live status snapshot: persisted config + port state + the
    /// host's enumerable serial devices (shared with serial CAT so both
    /// pickers list the same ports).</summary>
    public SerialPttStatus Snapshot() => new(
        Config: _store.Get(),
        PortOpen: _portOpen,
        Error: _error,
        Keyed: _keyed,
        AvailablePorts: SerialPortEnumeration.AvailablePorts(),
        GeneratedUtc: DateTimeOffset.UtcNow,
        SharedWithCat: _sharedWithCat,
        CtsHolding: _ctsHolding,
        DsrHolding: _dsrHolding,
        RtsAsserted: _rtsAsserted,
        DtrAsserted: _dtrAsserted);

    private void OnSettingsChanged()
    {
        try { _wakeCts?.Cancel(); }
        catch (ObjectDisposedException) { /* loop already advanced */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _wakeCts = wake;
            var ct = wake.Token;

            var config = _store.Get();
            if (!config.Enabled || string.IsNullOrWhiteSpace(config.PortName))
            {
                _portOpen = false;
                _error = null;
                _keyed = false;
                ClearLineStatus();
                // Disabled / unconfigured — idle until a settings change or
                // shutdown wakes the loop.
                await DelaySafe(Timeout.InfiniteTimeSpan, ct);
                continue;
            }

            if (IsSharedWithCat(config))
                await RunSharedCatPortAsync(config, ct);
            else
                await RunPortAsync(config, ct);
        }
    }

    private bool IsSharedWithCat(SerialPttConfig config) =>
        _catStore?.Get().Any(p => p.Enabled
            && SerialPortEnumeration.PortNameEquals(p.PortName.Trim(), config.PortName)) == true;

    // Poll the modem pins through CAT's live handle. This method never closes
    // or disposes the borrowed handle; CatSerialService remains its sole owner.
    private async Task RunSharedCatPortAsync(SerialPttConfig config, CancellationToken ct)
    {
        _sharedWithCat = true;
        bool asserted = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? catError = null;
                ISerialPttPins? pins = null;
                if (_catSerial is null ||
                    !_catSerial.TryBorrowModemPins(config.PortName, out pins, out catError) ||
                    pins is null)
                {
                    _portOpen = false;
                    _error = catError ?? $"Waiting for CAT to open {config.PortName}";
                    _ctsHolding = false;
                    _dsrHolding = false;
                    _rtsAsserted = false;
                    _dtrAsserted = false;
                    if (asserted)
                    {
                        asserted = false;
                        ReleaseKeyedInput();
                    }
                    await DelaySafe(SharedHandleWaitInterval, ct);
                    continue;
                }

                try
                {
                    // Borrow once per CAT connection. Store/config lookups are
                    // never performed at the 1-10 ms modem-pin poll cadence.
                    while (!ct.IsCancellationRequested)
                    {
                        string? inputError = null;
                        bool cts = ReadModemPin(
                            () => pins.CtsHolding, "CTS", config.SenseCts, ref inputError);
                        bool dsr = ReadModemPin(
                            () => pins.DsrHolding, "DSR", config.SenseDsr, ref inputError);
                        _ctsHolding = cts;
                        _dsrHolding = dsr;
                        _rtsAsserted = pins.RtsAsserted;
                        _dtrAsserted = pins.DtrAsserted;
                        _portOpen = true;
                        _error = CombineErrors(pins.LineControlError, inputError);

                        bool now = (config.SenseCts && cts) || (config.SenseDsr && dsr);
                        if (now != asserted)
                        {
                            asserted = now;
                            HandlePinEdge(now, config, sharedWithCat: true);
                        }
                        await DelaySafe(asserted ? KeyedPollInterval : IdlePollInterval, ct);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _portOpen = false;
                    _error = SerialPortEnumeration.Describe(ex);
                    if (asserted)
                    {
                        asserted = false;
                        ReleaseKeyedInput();
                    }
                    await DelaySafe(IdlePollInterval, ct);
                }
            }
        }
        finally
        {
            if (asserted) ReleaseKeyedInput();
            _portOpen = false;
            _sharedWithCat = false;
            ClearLineStatus();
        }
    }

    // Open → poll → reconnect loop for the configured port. Catches its own
    // faults (port busy/yanked) and backs off; only returns when the wake/stop
    // token cancels.
    private async Task RunPortAsync(SerialPttConfig config, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ISerialPttPort? port = null;
            bool asserted = false;
            try
            {
                port = _portFactory(config.PortName);
                port.Open();
                _portOpen = true;
                _error = port.LineControlError;
                _sharedWithCat = false;
                _rtsAsserted = port.RtsAsserted;
                _dtrAsserted = port.DtrAsserted;
                _log.LogInformation("serialptt.open port={Port} cts={Cts} dsr={Dsr}",
                    config.PortName, config.SenseCts, config.SenseDsr);

                while (!ct.IsCancellationRequested)
                {
                    // Report both raw levels; only selected lines key PTT.
                    string? inputError = null;
                    bool cts = ReadModemPin(
                        () => port.CtsHolding, "CTS", config.SenseCts, ref inputError);
                    bool dsr = ReadModemPin(
                        () => port.DsrHolding, "DSR", config.SenseDsr, ref inputError);
                    _ctsHolding = cts;
                    _dsrHolding = dsr;
                    _rtsAsserted = port.RtsAsserted;
                    _dtrAsserted = port.DtrAsserted;
                    _error = CombineErrors(port.LineControlError, inputError);
                    bool now = (config.SenseCts && cts) || (config.SenseDsr && dsr);
                    if (now != asserted)
                    {
                        asserted = now;
                        HandlePinEdge(now, config, sharedWithCat: false);
                    }
                    await DelaySafe(asserted ? KeyedPollInterval : IdlePollInterval, ct);
                }
            }
            // A requested cancellation (settings change / shutdown) is a clean
            // teardown — never a fault to surface in status or log as an error.
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _error = SerialPortEnumeration.Describe(ex);
                _log.LogWarning(ex, "serialptt.error port={Port}; retrying", config.PortName);
            }
            finally
            {
                // A closed/yanked/disabled port while keyed must never wedge
                // MOX: feed the release edge with the gate forced ON (releasing
                // what this source owns is always safe; the shared engine
                // no-ops unless it holds the ownership claim). Clear _keyed
                // BEFORE _portOpen so a status snapshot never reports the
                // inconsistent (closed, still-keyed) pair.
                if (asserted)
                {
                    asserted = false;
                    ReleaseKeyedInput();
                }
                _portOpen = false;
                ClearLineStatus();
                if (port is not null)
                {
                    try { port.Close(); } catch { /* teardown is best-effort */ }
                    // A yanked adapter can make Dispose throw too (IOException
                    // on Unix) — it must never escape ExecuteAsync and kill
                    // the hosted service.
                    try { port.Dispose(); } catch { /* teardown is best-effort */ }
                }
            }

            // Backoff before reopen (busy / unplugged). Cancellable so a
            // settings change or shutdown doesn't wait it out.
            if (!ct.IsCancellationRequested)
                await DelaySafe(ReopenBackoff, ct);
        }
        _portOpen = false;
    }

    private void ReleaseKeyedInput()
    {
        _keyed = false;
        _log.LogInformation("serialptt.edge keyed=false reason=teardown");
        try { _externalPtt.HandleSerialPtt(false, gateOn: true); }
        catch (Exception ex) { _log.LogDebug(ex, "serialptt.release.faulted"); }
    }

    private void HandlePinEdge(bool keyed, SerialPttConfig config, bool sharedWithCat)
    {
        _keyed = keyed;
        _log.LogInformation(
            "serialptt.edge keyed={Keyed} source={Source} port={Port} cts={Cts} dsr={Dsr}",
            keyed, sharedWithCat ? "cat-shared" : "dedicated", config.PortName,
            _ctsHolding, _dsrHolding);
        // Evaluate the enable gate per edge (same hot-toggle semantics as the
        // PTT-IN gate): a toggle takes effect on the next edge.
        _externalPtt.HandleSerialPtt(keyed, _store.Get().Enabled);
    }

    private void ClearLineStatus()
    {
        _ctsHolding = false;
        _dsrHolding = false;
        _rtsAsserted = false;
        _dtrAsserted = false;
    }

    private static bool ReadModemPin(
        Func<bool> read,
        string pinName,
        bool required,
        ref string? error)
    {
        try { return read(); }
        catch (Exception ex) when (!required)
        {
            error = CombineErrors(error, $"Unable to read {pinName}: {ex.Message}");
            return false;
        }
    }

    private static string? CombineErrors(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return string.IsNullOrWhiteSpace(second) ? null : second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first}; {second}";
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        _store.Changed -= OnSettingsChanged;
        if (_catStore is not null) _catStore.Changed -= OnSettingsChanged;
        base.Dispose();
    }
}

/// <summary>Test seam over the serial device: just the modem status pins and
/// open/close. Production wraps System.IO.Ports.SerialPort; tests substitute a
/// fake so no real port is ever touched.</summary>
internal interface ISerialPttPins
{
    bool CtsHolding { get; }
    bool DsrHolding { get; }
    bool RtsAsserted { get; }
    bool DtrAsserted { get; }
    string? LineControlError { get; }
}

internal interface ISerialPttPinSource
{
    bool TryBorrowModemPins(string portName, out ISerialPttPins? pins, out string? error);
}

internal interface ISerialPttPort : ISerialPttPins, IDisposable
{
    void Open();
    void Close();
}

/// <summary>Production <see cref="ISerialPttPort"/>: fixed 9600/8/N/1,
/// Handshake.None (baud is irrelevant — no data channel), finite timeouts like
/// CatSerialPort. Asserts RTS+DTR after open so the switch has a +V to pull
/// the sensed pin to (Thetis SDRSerialPortII does the same). A partial assertion
/// failure remains usable and is surfaced in status; bring-up fails when neither
/// output can be asserted.</summary>
internal sealed class SystemSerialPttPort : ISerialPttPort
{
    private readonly SerialPort _port;
    private bool _rtsAsserted;
    private bool _dtrAsserted;
    private string? _lineControlError;

    public SystemSerialPttPort(string portName)
    {
        _port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 500,
            WriteTimeout = 500,
        };
    }

    public void Open()
    {
        _rtsAsserted = false;
        _dtrAsserted = false;
        _lineControlError = null;
        _port.Open();
        var failures = new List<string>(2);
        try { _port.RtsEnable = true; _rtsAsserted = true; }
        catch (Exception ex) { failures.Add($"RTS: {ex.Message}"); }
        try { _port.DtrEnable = true; _dtrAsserted = true; }
        catch (Exception ex) { failures.Add($"DTR: {ex.Message}"); }
        _lineControlError = failures.Count == 0
            ? null
            : $"Unable to assert serial PTT output line(s): {string.Join("; ", failures)}";
        if (!_rtsAsserted && !_dtrAsserted)
        {
            try { _port.Close(); } catch { }
            throw new IOException(_lineControlError);
        }
    }

    public void Close()
    {
        if (_port.IsOpen) _port.Close();
        _rtsAsserted = false;
        _dtrAsserted = false;
    }

    public bool CtsHolding => _port.CtsHolding;
    public bool DsrHolding => _port.DsrHolding;
    public bool RtsAsserted => _rtsAsserted;
    public bool DtrAsserted => _dtrAsserted;
    public string? LineControlError => _lineControlError;

    public void Dispose() => _port.Dispose();
}
