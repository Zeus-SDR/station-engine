// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

public sealed record RadeStatusResponse(
    bool Available,
    bool Active,
    bool Synced,
    double SnrDb,
    string? RxText,
    string? LibraryVersion,
    string Rid,
    double TxMicLevelDb = -120,
    bool TxMicClip = false);

public sealed record RadeSelectRequest(bool Active, string? TxText = null);

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
                "RADE native ready rid={Rid} sha256={Sha256} source={Source} path={Path}",
                RadeNativeLoader.CurrentRid,
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
    public bool Active => _modem.Active && Available;
    public int PendingTxSamples => Math.Max(0, _modem.TxPendingOutSamples());

    internal void Select(bool active, string? txText)
    {
        lock (_controlGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (txText is not null) _modem.SetTxText(txText);
            if (active && Available)
            {
                if (!_modem.Active) _modem.Activate();
            }
            else if (_modem.Active)
            {
                Volatile.Write(ref _tailDraining, 0);
                _modem.Deactivate();
            }
        }
    }

    internal void SetMicConfig(RadeMicConfig config)
    {
        lock (_controlGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _modem.SetMicConfig(config);
        }
    }

    internal RadeStatusResponse Snapshot(bool routedActive) => new(
        Available,
        routedActive && Active,
        routedActive && _modem.Synced,
        routedActive ? Math.Round(_modem.SnrDb, 1) : 0,
        routedActive ? _modem.RxText : null,
        _modem.LibraryVersion,
        RadeNativeLoader.CurrentRid,
        routedActive ? Math.Round(_modem.TxMicLevelDb, 1) : -120,
        routedActive && _modem.TxMicClip);

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
/// </summary>
public sealed class CompositeAudioModemPort : IAudioModemPort, IDisposable
{
    private const byte FreeDvModeByte = 10;
    private readonly object _routingGate = new();
    private readonly RadioService? _radio;
    private readonly RadeModemService _rade;
    private readonly IAudioModemPort _classic;
    private int _radeSelected;
    private int _modeByte;
    private int _disposed;

    public CompositeAudioModemPort(
        RadioService radio,
        RadeModemService rade,
        ModeModemLeasePort classic)
    {
        _radio = radio;
        _rade = rade;
        _classic = classic;
        _radio.SetModemAvailability(() => Available);
    }

    internal CompositeAudioModemPort(
        RadeModemService rade,
        IAudioModemPort classic)
    {
        _rade = rade;
        _classic = classic;
    }

    internal bool RadeSelected => Volatile.Read(ref _radeSelected) != 0;

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

    internal bool SelectRade(bool active, string? txText)
    {
        lock (_routingGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (active && !_rade.Available) return false;

            if (active)
            {
                // Keep the classic lease's RADIO-mode lifecycle intact. A
                // synthetic leave-mode here would race back to ZeusProduct as
                // if the operator had left FreeDV and immediately deselect
                // RADE. Routing priority below prevents any classic audio call
                // while RADE is selected; the dormant lease remains attached.
                _rade.Select(true, txText);
                Volatile.Write(ref _radeSelected, 1);
            }
            else
            {
                Volatile.Write(ref _radeSelected, 0);
                _rade.Select(false, txText);
                _classic.SyncMode((byte)Volatile.Read(ref _modeByte));
            }
            return true;
        }
    }

    internal RadeStatusResponse RadeStatus() =>
        _rade.Snapshot(RadeSelected && Volatile.Read(ref _modeByte) == FreeDvModeByte);

    internal void SetRadeMicConfig(RadeMicConfig config) => _rade.SetMicConfig(config);

    public void SyncMode(byte rxModeByte)
    {
        // SyncMode and SelectRade are control-plane operations. Serialising
        // both closes the handoff race where a new mode byte arrived between a
        // selector's read and its final classic activation. Audio processing
        // itself remains lock-free.
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
        }
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
        try { _radio?.SetModemAvailability(null); }
        finally { _rade.Dispose(); }
    }
}
