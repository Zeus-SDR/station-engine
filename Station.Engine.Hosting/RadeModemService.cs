// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Engine RADE status. Fields after <c>TxMicClip</c> were added for RADE V2 and
/// default to the pre-V2 meaning, so an older product that does not read them
/// keeps working, and an older engine that does not send them reads (in a new
/// product) as "V2 unavailable".
/// </summary>
/// <param name="V2Available">The loaded shim can also run RADE V2 (capability, not masked by routing).</param>
/// <param name="Protocol">
/// The protocol in force while RADE is routed: "V1" or "V2". Under Auto it is
/// the one currently locked on receive, else the one the next over transmits.
/// Null when RADE is not routed.
/// </param>
/// <param name="Selection">What was selected ("V1", "V2" or "Auto") while RADE is routed; else null.</param>
/// <param name="BandConventionLsb">
/// The receiver in FreeDV currently rides LSB by band convention (below 10 MHz,
/// outside 60 m), ignoring the RADE V2 USB override. False when no receiver is
/// in FreeDV, and from an older engine.
/// </param>
/// <param name="Transmitting">
/// The station is transmitting (MOX, TUN, CW or two-tone keyed, including the
/// FreeDV tail, which holds the key). A RADE selection sent now is applied
/// when this goes false; the product's AUTO scan pauses while it is true.
/// </param>
public sealed record RadeStatusResponse(
    bool Available,
    bool Active,
    bool Synced,
    double SnrDb,
    string? RxText,
    string? LibraryVersion,
    string Rid,
    double TxMicLevelDb = -120,
    bool TxMicClip = false,
    bool V2Available = false,
    string? Protocol = null,
    string? Selection = null,
    bool BandConventionLsb = false,
    bool Transmitting = false);

/// <summary>
/// RADE route selection. <paramref name="Protocol"/> is "V1", "V2" or "Auto";
/// absent (an older product) means V1. <paramref name="PreferredProtocol"/> is
/// the operator's saved manual RADE protocol ("V1"/"V2"), used only as the Auto
/// transmit fallback before anything has been received.
/// </summary>
public sealed record RadeSelectRequest(
    bool Active,
    string? TxText = null,
    string? Protocol = null,
    string? PreferredProtocol = null);

/// <summary>
/// Pre-encoder TX mic conditioning config. Null fields leave that setting
/// unchanged — mirrors <see cref="RadeSelectRequest"/> and the product's
/// FreeDvConfigRequest convention.
/// </summary>
public sealed record RadeMicConfig(
    bool? AgcEnabled = null,
    double? AgcTargetDb = null,
    bool? EqEnabled = null,
    double? EqBassDb = null,
    double? EqMidDb = null,
    double? EqTrebleDb = null);

internal interface IRadeModemCore : IDisposable
{
    bool RadeAvailable { get; }
    bool V2Available { get; }
    RadeProtocolSelection Selection { get; }
    RadeProtocol ProtocolInForce { get; }
    bool Active { get; }
    bool Synced { get; }
    double SnrDb { get; }
    double TxMicLevelDb { get; }
    bool TxMicClip { get; }
    string? RxText { get; }
    string? LibraryVersion { get; }
    void Activate();
    void Deactivate();
    void ProcessRxInPlace(Span<float> block48k);
    void ProcessTxInPlace(Span<float> block48k);
    void FlushRx();
    void FlushTx();
    int FinishTx();
    int TxPendingOutSamples();
    int DrainTo(Span<float> block48k);
    void SetTxText(string? text);
    void SetMicConfig(RadeMicConfig config);
    void SetProtocol(RadeProtocolSelection selection, RadeProtocol preferred);
    void SetTxRidesLsbProvider(Func<bool>? provider);
}

/// <summary>
/// Engine-owned RADE modem adapter. After FinishTx, ProcessTx drains the exact
/// queued tail until FlushTx starts the next over.
/// </summary>
public sealed class RadeModemService : IAudioModemPort, IDisposable
{
    private readonly object _controlGate = new();
    private readonly IRadeModemCore _modem;
    private int _tailDraining;
    private int _disposed;

    public RadeModemService(ILogger<RadeModemService> log)
        : this(new RadeModem(log))
    {
        if (_modem.RadeAvailable)
        {
            log.LogInformation(
                "RADE native ready rid={Rid} v2={V2} sha256={Sha256} source={Source} path={Path}",
                RadeNativeLoader.CurrentRid,
                _modem.V2Available,
                RadeNativeLoader.SelectedSha256,
                IsReplacement(RadeNativeLoader.SelectedPath) ? "replacement" : "bundled",
                RadeNativeLoader.SelectedPath);
        }
        else
        {
            log.LogWarning(
                "RADE native unavailable rid={Rid} reason={Reason}",
                RadeNativeLoader.CurrentRid,
                RadeNativeLoader.Failure ?? "probe failed");
        }
    }

    internal RadeModemService(IRadeModemCore modem)
    {
        _modem = modem;
    }

    public bool Available => _modem.RadeAvailable;
    public bool V2Available => _modem.RadeAvailable && _modem.V2Available;
    public bool Active => _modem.Active && Available;
    public int PendingTxSamples => Math.Max(0, _modem.TxPendingOutSamples());

    /// <summary>
    /// Route RADE on or off with the given protocol selection. Returns false
    /// (and changes nothing) when V2 is demanded on a library that cannot run
    /// it: the engine never substitutes V1 for an explicit V2 pick.
    /// </summary>
    internal bool Select(
        bool active,
        string? txText,
        RadeProtocolSelection selection = RadeProtocolSelection.V1,
        RadeProtocol preferred = RadeProtocol.None)
    {
        lock (_controlGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (active && selection == RadeProtocolSelection.V2 && !V2Available) return false;
            if (txText is not null) _modem.SetTxText(txText);
            _modem.SetProtocol(selection, preferred);
            if (active && Available)
            {
                if (!_modem.Active) _modem.Activate();
            }
            else if (_modem.Active)
            {
                Volatile.Write(ref _tailDraining, 0);
                _modem.Deactivate();
            }
            return true;
        }
    }

    /// <summary>
    /// Where the modem learns whether the transmitter rides LSB: the TX
    /// carrier's effective sideband as the TX path resolves it (TX receiver,
    /// split frequency and the USB override included). Under Auto that forces
    /// V1: V2 is USB-only. Read once per over latch and for status.
    /// </summary>
    internal void SetTxRidesLsbProvider(Func<bool>? provider) =>
        _modem.SetTxRidesLsbProvider(provider);

    internal void SetMicConfig(RadeMicConfig config)
    {
        lock (_controlGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _modem.SetMicConfig(config);
        }
    }

    internal RadeStatusResponse Snapshot(bool routedActive, bool bandConventionLsb = false, bool transmitting = false) => new(
        Available,
        routedActive && Active,
        routedActive && _modem.Synced,
        routedActive ? Math.Round(_modem.SnrDb, 1) : 0,
        routedActive ? _modem.RxText : null,
        _modem.LibraryVersion,
        RadeNativeLoader.CurrentRid,
        routedActive ? Math.Round(_modem.TxMicLevelDb, 1) : -120,
        routedActive && _modem.TxMicClip,
        V2Available,
        routedActive && Active ? RadeProtocols.ToWire(_modem.ProtocolInForce) : null,
        routedActive ? RadeProtocols.ToWire(_modem.Selection) : null,
        bandConventionLsb,
        transmitting);

    public void SyncMode(byte rxModeByte) { }
    public void ProcessRx(Span<float> block48k) => _modem.ProcessRxInPlace(block48k);

    public void ProcessTx(Span<float> block48k)
    {
        if (Volatile.Read(ref _tailDraining) != 0)
            _modem.DrainTo(block48k);
        else
            _modem.ProcessTxInPlace(block48k);
    }

    public void FlushRx() => _modem.FlushRx();

    public void FlushTx()
    {
        Volatile.Write(ref _tailDraining, 0);
        _modem.FlushTx();
    }

    public int FinishTx()
    {
        int pending = _modem.FinishTx();
        Volatile.Write(ref _tailDraining, pending > 0 ? 1 : 0);
        return pending;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_controlGate)
        {
            Volatile.Write(ref _tailDraining, 0);
            _modem.Dispose();
        }
    }

    private static bool IsReplacement(string? selectedPath)
    {
        if (string.IsNullOrEmpty(selectedPath)) return false;
        string replacement = Path.GetFullPath(RadeNativeLoader.DefaultOverrideDirectory());
        string? selectedDirectory = Path.GetDirectoryName(Path.GetFullPath(selectedPath));
        return string.Equals(replacement, selectedDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                RadeNativeLoader.OverrideDirectoryEnvironmentVariable));
    }
}

/// <summary>
/// Linearizable priority router: an engine-hosted RADE selection wins; all
/// other selections delegate to the unchanged classic mode-modem lease.
/// <para>
/// A RADE protocol selection never takes effect while transmitting (MOX, TUN,
/// CW, two-tone, or the FreeDV tail, all of which hold the transmitter's
/// transition lock or keep the radio keyed). It is stored as pending and
/// applied through <see cref="TxService.TryRunWithTransmitIdle"/>, which also
/// keeps it from landing between key-on admission and the wire key. The
/// FreeDV USB override (RADE V2) is published from inside that same
/// transmit-idle section, so the sideband TX safety admitted is the sideband
/// transmitted. Releasing RADE (leaving FreeDV, a classic pick, dispose) stays
/// immediate.
/// </para>
/// </summary>
public sealed class CompositeAudioModemPort : IAudioModemPort, IDisposable
{
    private const byte FreeDvModeByte = 10;
    private const int PendingRetryMs = 50;
    private readonly object _routingGate = new();
    private readonly RadioService? _radio;
    private readonly RadeModemService _rade;
    private readonly IAudioModemPort _classic;
    // Runs an operation only while every transmit intent is idle, serialised
    // against key-on/key-off; returns false (operation not run) otherwise.
    private readonly Func<Action, bool> _runWhenTransmitIdle;
    private readonly ILogger? _log;
    private int _applyFailureLogged;
    private int _radeSelected;
    private int _modeByte;
    private int _disposed;
    private RadeProtocolSelection _selection = RadeProtocolSelection.V1; // applied; under _routingGate
    private PendingSelection? _pending;                                   // under _routingGate
    private bool _sidebandRefreshPending;                                 // under _routingGate
    private Timer? _retry;                                                // under _routingGate
    // RADE V2 is USB on every band upstream. True exactly while RADE is routed
    // (selected AND the radio is in FreeDV) with the APPLIED selection exactly
    // V2; false for V1, Auto (which keeps the band convention), classic modes,
    // RADE released, FreeDV left and after dispose. Written under
    // _routingGate, read lock-free by RadioService's override provider.
    private volatile bool _forcesFreeDvUsb;

    private sealed record PendingSelection(string? TxText, RadeProtocolSelection Selection, RadeProtocol Preferred);

    public CompositeAudioModemPort(
        RadioService radio,
        RadeModemService rade,
        ModeModemLeasePort classic)
        : this(radio, rade, classic, transmitControl: null)
    {
    }

    /// <param name="transmitControl">
    /// Lazy TX-service lookup (TxService -> DspPipelineService ->
    /// IAudioModemPort would otherwise be a construction cycle). Null resolves
    /// to "no transmitter": selections apply at once.
    /// </param>
    internal CompositeAudioModemPort(
        RadioService radio,
        RadeModemService rade,
        ModeModemLeasePort classic,
        Func<TxService?>? transmitControl,
        ILogger<CompositeAudioModemPort>? log = null)
        : this(rade, classic, radio, TransmitIdleGate(transmitControl), log)
    {
        radio.SetModemAvailability(() => Available);
    }

    internal CompositeAudioModemPort(
        RadeModemService rade,
        IAudioModemPort classic,
        RadioService? radio = null,
        Func<Action, bool>? runWhenTransmitIdle = null,
        ILogger? log = null)
    {
        _log = log;
        _radio = radio;
        _rade = rade;
        _classic = classic;
        _runWhenTransmitIdle = runWhenTransmitIdle ?? (operation => { operation(); return true; });
        _radio?.SetFreeDvForcesUsbProvider(() => _forcesFreeDvUsb);
        if (_radio is not null)
            _rade.SetTxRidesLsbProvider(() => _radio.FreeDvTxRidesLsb);
    }

    private static Func<Action, bool> TransmitIdleGate(Func<TxService?>? transmitControl) => operation =>
    {
        var tx = transmitControl?.Invoke();
        if (tx is null) { operation(); return true; }
        return tx.TryRunWithTransmitIdle(operation, out _);
    };

    internal bool RadeSelected => Volatile.Read(ref _radeSelected) != 0;

    /// <summary>The composite's current decision: FreeDV must ride USB (RADE V2 routed).</summary>
    internal bool ForcesFreeDvUsb => _forcesFreeDvUsb;

    /// <summary>True while a selection waits for the transmitter to go idle.</summary>
    internal bool HasPendingSelection
    {
        get { lock (_routingGate) return _pending is not null; }
    }

    // Caller holds _routingGate. Returns true when the decision changed.
    private bool UpdateForcesUsbLocked()
    {
        bool next = _disposed == 0
            && RadeSelected
            && Volatile.Read(ref _modeByte) == FreeDvModeByte
            && _selection == RadeProtocolSelection.V2
            && _rade.V2Available;
        if (next == _forcesFreeDvUsb) return false;
        _forcesFreeDvUsb = next;
        return true;
    }

    public bool Available => RadeSelected ? _rade.Available : _classic.Available;

    public bool Active
    {
        get
        {
            bool inFreeDv = Volatile.Read(ref _modeByte) == FreeDvModeByte;
            return RadeSelected
                ? inFreeDv && _rade.Active
                : _classic.Active;
        }
    }

    public int PendingTxSamples => RadeSelected
        ? _rade.PendingTxSamples
        : _classic.PendingTxSamples;

    /// <summary>
    /// Route RADE with a protocol selection, or release it. A selection made
    /// while transmitting is accepted and applied when the transmitter goes
    /// idle (the last one wins). Returns false only when RADE (or RADE V2) is
    /// unavailable.
    /// </summary>
    internal bool SelectRade(
        bool active,
        string? txText,
        RadeProtocolSelection selection = RadeProtocolSelection.V1,
        RadeProtocol preferred = RadeProtocol.None)
    {
        bool sidebandChanged;
        lock (_routingGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (active && !_rade.Available) return false;
            if (active && selection == RadeProtocolSelection.V2 && !_rade.V2Available) return false;

            if (active)
            {
                _pending = new PendingSelection(txText, selection, preferred);
                sidebandChanged = false;
            }
            else
            {
                // Release is immediate and drops any pending selection.
                _pending = null;
                Volatile.Write(ref _radeSelected, 0);
                _rade.Select(false, txText, selection, preferred);
                _classic.SyncMode((byte)Volatile.Read(ref _modeByte));
                sidebandChanged = UpdateForcesUsbLocked();
            }
        }
        if (active) return ApplyPendingWhenIdle();
        if (sidebandChanged) PublishSidebandWhenIdle();
        return true;
    }

    // Try to apply the pending selection inside the transmit-idle section;
    // retry shortly while the transmitter is busy. Never throws: a native
    // failure while opening (e.g. an incompatible replacement library) is
    // logged once and leaves RADE released, so neither the request thread nor
    // the retry timer can take the engine down. Returns false on that failure.
    private bool ApplyPendingWhenIdle()
    {
        try
        {
            if (!_runWhenTransmitIdle(ApplyPendingLocked)) ScheduleRetry();
            return true;
        }
        catch (Exception ex)
        {
            ContainApplyFailure(ex);
            return false;
        }
    }

    private void ContainApplyFailure(Exception ex)
    {
        if (Interlocked.Exchange(ref _applyFailureLogged, 1) == 0)
            _log?.LogError(ex, "RADE: applying a selection failed; RADE is released (inactive) until the next selection.");
        try
        {
            bool sidebandChanged;
            lock (_routingGate)
            {
                _pending = null;
                Volatile.Write(ref _radeSelected, 0);
                try { _rade.Select(false, null); }
                catch (Exception releaseError) { _log?.LogDebug(releaseError, "RADE: release after a failed apply also failed."); }
                sidebandChanged = UpdateForcesUsbLocked();
            }
            if (sidebandChanged) PublishSidebandWhenIdle();
        }
        catch (Exception cleanupError)
        {
            _log?.LogDebug(cleanupError, "RADE: cleanup after a failed apply failed.");
        }
    }

    // Runs inside the transmit-idle section (or directly without a transmitter).
    private void ApplyPendingLocked()
    {
        bool sidebandChanged;
        lock (_routingGate)
        {
            var pending = _pending;
            if (pending is null || _disposed != 0) return;
            _pending = null;
            // Keep the classic lease's RADIO-mode lifecycle intact. A synthetic
            // leave-mode here would race back to ZeusProduct as if the operator
            // had left FreeDV and immediately deselect RADE. Routing priority
            // prevents any classic audio call while RADE is selected; the
            // dormant lease remains attached.
            _rade.Select(true, pending.TxText, pending.Selection, pending.Preferred);
            Volatile.Write(ref _radeSelected, 1);
            _selection = pending.Selection;
            sidebandChanged = UpdateForcesUsbLocked();
            if (sidebandChanged) _sidebandRefreshPending = false;
        }
        // Still inside the transmit-idle section: the sideband cannot change
        // between a key-on admission and the wire key.
        if (sidebandChanged) _radio?.RefreshFreeDvSideband();
    }

    private void PublishSidebandWhenIdle()
    {
        if (_radio is null) return;
        bool ran;
        try
        {
            ran = _runWhenTransmitIdle(() =>
            {
                lock (_routingGate) _sidebandRefreshPending = false;
                _radio.RefreshFreeDvSideband();
            });
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "RADE: publishing the FreeDV sideband failed; retrying.");
            ran = false;
        }
        if (!ran)
        {
            lock (_routingGate) _sidebandRefreshPending = true;
            ScheduleRetry();
        }
    }

    private void ScheduleRetry()
    {
        lock (_routingGate)
        {
            if (_disposed != 0) return;
            _retry ??= new Timer(_ => RetryTick(), null, Timeout.Infinite, Timeout.Infinite);
            _retry.Change(PendingRetryMs, Timeout.Infinite);
        }
    }

    // Timer thread: must never throw (an unhandled exception here would end
    // the engine process).
    private void RetryTick()
    {
        try
        {
            bool pending, sideband;
            lock (_routingGate)
            {
                if (_disposed != 0) return;
                pending = _pending is not null;
                sideband = _sidebandRefreshPending;
            }
            if (pending) ApplyPendingWhenIdle();
            else if (sideband) PublishSidebandWhenIdle();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "RADE: pending-selection retry failed.");
        }
    }

    internal RadeStatusResponse RadeStatus() =>
        _rade.Snapshot(
            RadeSelected && Volatile.Read(ref _modeByte) == FreeDvModeByte,
            _radio?.FreeDvOwnerRidesLsbByConvention ?? false,
            _radio?.IsMox ?? false);

    internal void SetRadeMicConfig(RadeMicConfig config) => _rade.SetMicConfig(config);

    public void SyncMode(byte rxModeByte)
    {
        // Every FreeDV DSP tick calls this with an unchanged byte. That case
        // needs no lock: the decision does not depend on anything that
        // changed, and the classic forward is the same idempotent call the
        // locked path makes. Only a real change takes the routing lock, which
        // closes the handoff race where a new mode byte arrives between a
        // selector's read and its final classic activation.
        if (Volatile.Read(ref _modeByte) == rxModeByte)
        {
            if (!RadeSelected || rxModeByte != FreeDvModeByte) _classic.SyncMode(rxModeByte);
            return;
        }

        bool changed;
        lock (_routingGate)
        {
            Volatile.Write(ref _modeByte, rxModeByte);
            if (RadeSelected)
            {
                // Preserve an existing FreeDV enter lifecycle while RADE owns
                // audio. Only a real radio-mode exit is forwarded so the
                // product can disengage and the engine can release RADE.
                if (rxModeByte != FreeDvModeByte)
                    _classic.SyncMode(rxModeByte);
            }
            else
                _classic.SyncMode(rxModeByte);
            changed = UpdateForcesUsbLocked();
        }
        if (changed) PublishSidebandWhenIdle();
    }

    public void ProcessRx(Span<float> block48k)
    {
        if (RadeSelected) _rade.ProcessRx(block48k);
        else _classic.ProcessRx(block48k);
    }

    public void ProcessTx(Span<float> block48k)
    {
        if (RadeSelected) _rade.ProcessTx(block48k);
        else _classic.ProcessTx(block48k);
    }

    public void FlushRx()
    {
        if (RadeSelected) _rade.FlushRx();
        else _classic.FlushRx();
    }

    public void FlushTx()
    {
        if (RadeSelected) _rade.FlushTx();
        else _classic.FlushTx();
    }

    public int FinishTx() => RadeSelected ? _rade.FinishTx() : _classic.FinishTx();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_routingGate)
        {
            _pending = null;
            UpdateForcesUsbLocked(); // _disposed => false
            _retry?.Dispose();
            _retry = null;
        }
        try
        {
            _radio?.SetModemAvailability(null);
            _radio?.SetFreeDvForcesUsbProvider(null); // also refreshes to the band convention
        }
        finally { _rade.Dispose(); }
    }
}
