// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Diagnostics;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Family-neutral adapter over the existing Protocol-1, Protocol-2 and
/// Protocol-3 HPSDR orchestration. This type is intentionally not registered
/// with the runtime composition root in Phase 0.
/// </summary>
public sealed class HpsdrSessionFacade : IRadioSession
{
    private readonly RadioService _radio;
    private readonly DspPipelineService _pipeline;
    private readonly Func<CancellationToken, Task>? _connectAsync;
    private readonly Func<CancellationToken, Task>? _disconnectAsync;
    private readonly Func<StateDto> _snapshot;
    private readonly ILogger<HpsdrSessionFacade>? _log;
    private readonly object _stateSync = new();
    private StateDto _lastState;
    private RadioSessionFault? _lastFault;
    private int _connectOperationActive;
    private int _disconnectOperationActive;
    private int _disposed;

    /// <summary>
    /// Creates an observer/control facade for orchestration that has already
    /// established a session. A connect callback is required to initiate a new
    /// connection because the existing P1/P2/P3 selection remains in its
    /// current composition layer.
    /// </summary>
    public HpsdrSessionFacade(RadioService radio, DspPipelineService pipeline)
        : this(radio, pipeline, connectAsync: null, disconnectAsync: null, logger: null)
    {
    }

    /// <summary>
    /// Creates a facade with the existing connection and disconnection
    /// orchestration supplied as callbacks. The callbacks adapt the established
    /// protocol-specific flows; the facade does not move or reproduce them.
    /// </summary>
    public HpsdrSessionFacade(
        RadioService radio,
        DspPipelineService pipeline,
        Func<CancellationToken, Task>? connectAsync,
        Func<CancellationToken, Task>? disconnectAsync,
        ILogger<HpsdrSessionFacade>? logger = null)
        : this(radio, pipeline, connectAsync, disconnectAsync, logger, snapshot: null)
    {
    }

    internal HpsdrSessionFacade(
        RadioService radio,
        DspPipelineService pipeline,
        Func<CancellationToken, Task>? connectAsync,
        Func<CancellationToken, Task>? disconnectAsync,
        ILogger<HpsdrSessionFacade>? logger,
        Func<StateDto>? snapshot)
    {
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _connectAsync = connectAsync;
        _disconnectAsync = disconnectAsync;
        _snapshot = snapshot ?? radio.Snapshot;
        _log = logger;
        _lastState = radio.Snapshot();

        _radio.StateChanged += OnRadioStateChanged;
        _radio.MoxChanged += OnMoxChanged;
        _pipeline.RxMeterUpdated += OnRxMeterUpdated;
    }

    public RadioFamily Family => RadioFamily.Hpsdr;

    public RadioIdentity Identity => BuildIdentity(_snapshot());

    public SessionCapabilities Capabilities => BuildCapabilities(_snapshot());

    public RadioSessionState State
    {
        get
        {
            return SessionStateFor(_snapshot());
        }
    }

    public event Action<IRadioSessionDelta>? DeltaReceived;
    public event Action<RadioSessionFault>? Faulted;

    public void RequestResync()
    {
        ThrowIfDisposed();
        var state = _snapshot();
        var sessionState = SessionStateFor(state);
        if (sessionState == RadioSessionState.Connected)
            PublishDelta(FullOperatingContext(state));
        PublishDelta(new TransmitStateDelta(IsTransmitting: CurrentTransmitTruth()));
        PublishDelta(new ConnectionStateDelta(
            State: sessionState,
            Identity: BuildIdentity(state),
            Capabilities: BuildCapabilities(state)));
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        if (_connectAsync is null)
        {
            if (_radio.IsConnected)
            {
                ClearFaultIfPresent(publishConnectionDelta: false);
                RequestResync();
                return;
            }
            throw new InvalidOperationException(
                "No existing HPSDR connection orchestration was supplied to this facade.");
        }

        try
        {
            Interlocked.Increment(ref _connectOperationActive);
            try
            {
                await _connectAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _connectOperationActive);
            }
            ClearFaultIfPresent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishFault("connect", ex);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (_disconnectAsync is null && _radio.IsProtocol3Active)
        {
            throw new InvalidOperationException(
                "Protocol-3 disconnection must use the existing sidecar orchestration callback.");
        }

        try
        {
            Interlocked.Increment(ref _disconnectOperationActive);
            try
            {
                if (_disconnectAsync is not null)
                {
                    await _disconnectAsync(ct).ConfigureAwait(false);
                }
                else if (_radio.IsProtocol2Active)
                {
                    await _pipeline.DisconnectP2Async(ct).ConfigureAwait(false);
                }
                else
                {
                    await _radio.DisconnectAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _disconnectOperationActive);
            }

            ClearFaultIfPresent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishFault("disconnect", ex);
            throw;
        }
    }

    public ValueTask RequestFrequencyAsync(long frequencyHz, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        _radio.SetVfo(frequencyHz);
        return ValueTask.CompletedTask;
    }

    public ValueTask RequestModeAsync(RxMode mode, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        _radio.SetMode(mode);
        return ValueTask.CompletedTask;
    }

    public ValueTask RequestFilterAsync(
        int lowHz,
        int highHz,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        _radio.SetFilter(lowHz, highHz);
        return ValueTask.CompletedTask;
    }

    public ValueTask RequestAgcAsync(AgcConfig agc, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(agc);
        _radio.SetAgc(agc);
        return ValueTask.CompletedTask;
    }

    public ValueTask RequestTransmitAsync(
        AuthorizedTransmitIntent intent,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(intent);

        var state = _snapshot();
        if (state.Status != ConnectionStatus.Connected
            || !_radio.IsConnected
            || !BuildCapabilities(state).CanTransmit)
        {
            throw new InvalidOperationException(
                "The current radio session does not report transmit capability.");
        }

        // Keep the capability/connection read immediately adjacent to SetMox.
        // RadioService does not expose one lock-spanning check-and-request API,
        // so a disconnect can still land after this check; the TX interlock and
        // the disconnect false-delta convergence remain authoritative.
        // RadioService deliberately retains its established asymmetry: P1 is
        // written directly and P2 is forwarded by DspPipelineService from the
        // MoxChanged event. The facade does not invent another TX path.
        _radio.SetMox(intent.Transmit);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _radio.StateChanged -= OnRadioStateChanged;
        _radio.MoxChanged -= OnMoxChanged;
        _pipeline.RxMeterUpdated -= OnRxMeterUpdated;
    }

    private void OnRadioStateChanged(StateDto next)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        StateDto previous;
        bool enteredConnected;
        bool leftConnected;
        bool enteredError;
        bool connectionChanged;
        lock (_stateSync)
        {
            previous = _lastState;
            _lastState = next;
            enteredConnected = next.Status == ConnectionStatus.Connected
                && previous.Status != ConnectionStatus.Connected;
            leftConnected = previous.Status == ConnectionStatus.Connected
                && next.Status != ConnectionStatus.Connected;
            enteredError = next.Status == ConnectionStatus.Error
                && previous.Status != ConnectionStatus.Error;
            connectionChanged = next.Status != previous.Status
                || !string.Equals(next.Endpoint, previous.Endpoint, StringComparison.Ordinal)
                || !string.Equals(
                    next.ConnectedProtocol,
                    previous.ConnectedProtocol,
                    StringComparison.Ordinal);
            bool successfulLifecycleTransition =
                connectionChanged
                && ((Volatile.Read(ref _connectOperationActive) != 0
                        && next.Status == ConnectionStatus.Connected)
                    || (Volatile.Read(ref _disconnectOperationActive) != 0
                        && next.Status == ConnectionStatus.Disconnected));
            if (enteredConnected || successfulLifecycleTransition) _lastFault = null;
        }

        if (enteredError)
        {
            PublishFault(
                "connection",
                new InvalidOperationException("The underlying radio service entered its error state."),
                publishConnectionDelta: false);
        }

        if (enteredConnected)
        {
            PublishDelta(FullOperatingContext(next));
            PublishDelta(new TransmitStateDelta(IsTransmitting: CurrentTransmitTruth()));
        }
        else if (next.Status == ConnectionStatus.Connected)
        {
            var operating = ChangedOperatingContext(previous, next);
            if (HasReportedOperatingField(operating)) PublishDelta(operating);
        }

        if (leftConnected)
            PublishDelta(new TransmitStateDelta(IsTransmitting: false));

        if (connectionChanged)
        {
            PublishDelta(new ConnectionStateDelta(
                State: SessionStateFor(next),
                Identity: BuildIdentity(next),
                Capabilities: BuildCapabilities(next)));
        }

        // RadioService invokes StateChanged synchronously without sequencing
        // concurrent publishers. This adapter preserves that inherited ordering
        // constraint and deliberately does not publish while holding _stateSync.
    }

    private void OnMoxChanged(bool on)
    {
        if (on && !_radio.IsConnected) return;
        PublishDelta(new TransmitStateDelta(IsTransmitting: on));
    }

    private void OnRxMeterUpdated(int receiver, double dbm) =>
        PublishDelta(new MeteringDelta(ReceiverIndex: receiver, SignalDbm: dbm));

    private RadioIdentity BuildIdentity(StateDto state)
    {
        string? protocol = state.ConnectedProtocol;
        string? firmware = _radio.ConnectedFirmware;
        if (string.Equals(protocol, "P3", StringComparison.OrdinalIgnoreCase))
        {
            const string model = "Unknown";
            return new RadioIdentity(
                RadioFamily.Hpsdr,
                SerialNumber: null,
                Model: model,
                DisplayName: BuildDisplayName("HPSDR Protocol 3 radio", state.Endpoint, firmware),
                FirmwareVersion: firmware,
                Endpoint: state.Endpoint);
        }

        var board = _radio.ConnectedBoardKind;
        var variant = _radio.EffectiveOrionMkIIVariant;
        string modelName = BoardModel(board, variant);
        return new RadioIdentity(
            RadioFamily.Hpsdr,
            SerialNumber: null,
            Model: modelName,
            DisplayName: BuildDisplayName(modelName, state.Endpoint, firmware),
            FirmwareVersion: firmware,
            Endpoint: state.Endpoint);
    }

    private SessionCapabilities BuildCapabilities(StateDto state)
    {
        bool protocol3 = string.Equals(
            state.ConnectedProtocol,
            "P3",
            StringComparison.OrdinalIgnoreCase);
        var board = protocol3 ? HpsdrBoardKind.Unknown : _radio.ConnectedBoardKind;
        bool knownSession = protocol3
            || (state.Status == ConnectionStatus.Connected && board != HpsdrBoardKind.Unknown);

        if (protocol3)
        {
            return new SessionCapabilities(
                CanTune: true,
                CanSetMode: true,
                CanSetFilter: true,
                CanSetAgc: true,
                CanTransmit: true,
                OwnsRxAudio: true,
                ProvidesSpectrum: true,
                ProvidesMetering: true,
                MaxReceivers: state.MaxReceivers);
        }

        var variant = _radio.EffectiveOrionMkIIVariant;
        var boardCapabilities = BoardCapabilitiesTable.For(board, variant);
        return new SessionCapabilities(
            CanTune: knownSession,
            CanSetMode: knownSession,
            CanSetFilter: knownSession,
            CanSetAgc: knownSession,
            CanTransmit: knownSession,
            OwnsRxAudio: true,
            ProvidesSpectrum: knownSession,
            ProvidesMetering: knownSession,
            MaxReceivers: knownSession ? state.MaxReceivers : 0,
            MaxRxSampleRateHz: knownSession ? boardCapabilities.MaxRxSampleRateHz : 0);
    }

    private RadioSessionState SessionStateFor(StateDto state)
    {
        lock (_stateSync)
        {
            if (_lastFault is not null) return RadioSessionState.Faulted;
        }

        return MapState(state.Status);
    }

    internal static RadioSessionState MapState(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Disconnected => RadioSessionState.Disconnected,
        ConnectionStatus.Connecting => RadioSessionState.Connecting,
        ConnectionStatus.Connected => RadioSessionState.Connected,
        ConnectionStatus.Error => RadioSessionState.Faulted,
        // Future wire states are not a healthy disconnect. Unknown keeps every
        // caller fail-closed until the new state receives an explicit mapping.
        _ => RadioSessionState.Unknown,
    };

    private static OperatingContextDelta FullOperatingContext(StateDto state) => new(
        FrequencyHz: state.VfoHz,
        Mode: state.Mode,
        FilterLowHz: state.FilterLowHz,
        FilterHighHz: state.FilterHighHz,
        Agc: state.Agc);

    private static OperatingContextDelta ChangedOperatingContext(StateDto previous, StateDto next) => new(
        FrequencyHz: next.VfoHz != previous.VfoHz ? next.VfoHz : null,
        Mode: next.Mode != previous.Mode ? next.Mode : null,
        FilterLowHz: next.FilterLowHz != previous.FilterLowHz ? next.FilterLowHz : null,
        FilterHighHz: next.FilterHighHz != previous.FilterHighHz ? next.FilterHighHz : null,
        Agc: !Equals(next.Agc, previous.Agc)
            ? next.Agc
            : null);

    private bool CurrentTransmitTruth()
    {
        // Read MOX first so a disconnect observed by the second read always
        // collapses to safe false. A later disconnect also publishes false.
        bool mox = _radio.IsMox;
        return _radio.IsConnected && mox;
    }

    private static bool HasReportedOperatingField(OperatingContextDelta delta) =>
        delta.FrequencyHz is not null
        || delta.Mode is not null
        || delta.ModeRaw is not null
        || delta.FilterLowHz is not null
        || delta.FilterHighHz is not null
        || delta.Agc is not null;

    private static string BoardModel(HpsdrBoardKind board, OrionMkIIVariant variant) => board switch
    {
        HpsdrBoardKind.OrionMkII => variant.ToString(),
        HpsdrBoardKind.Unknown => "Unknown HPSDR radio",
        _ => board.ToString(),
    };

    private static string BuildDisplayName(string model, string? endpoint, string? firmware)
    {
        string display = model;
        if (!string.IsNullOrWhiteSpace(firmware)) display += $" FW {firmware}";
        if (!string.IsNullOrWhiteSpace(endpoint)) display += $" ({endpoint})";
        return display;
    }

    private void PublishDelta(IRadioSessionDelta delta)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var handlers = DeltaReceived;
        if (handlers is null) return;

        foreach (Action<IRadioSessionDelta> handler in handlers.GetInvocationList())
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { handler(delta); }
            catch (Exception ex) { LogSubscriberFailure(ex, nameof(DeltaReceived), handler); }
        }
    }

    private void PublishFault(
        string operation,
        Exception exception,
        bool publishConnectionDelta = true)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var fault = new RadioSessionFault(operation, exception.Message, exception);
        lock (_stateSync) _lastFault = fault;

        var handlers = Faulted;
        if (handlers is not null)
        {
            foreach (Action<RadioSessionFault> handler in handlers.GetInvocationList())
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                try { handler(fault); }
                catch (Exception ex) { LogSubscriberFailure(ex, nameof(Faulted), handler); }
            }
        }

        if (publishConnectionDelta)
        {
            var state = _snapshot();
            PublishDelta(new ConnectionStateDelta(
                State: RadioSessionState.Faulted,
                Identity: BuildIdentity(state),
                Capabilities: BuildCapabilities(state)));
        }
    }

    private void ClearFaultIfPresent(bool publishConnectionDelta = true)
    {
        bool cleared;
        lock (_stateSync)
        {
            cleared = _lastFault is not null;
            _lastFault = null;
        }
        if (!cleared || !publishConnectionDelta) return;

        var state = _snapshot();
        PublishDelta(new ConnectionStateDelta(
            State: MapState(state.Status),
            Identity: BuildIdentity(state),
            Capabilities: BuildCapabilities(state)));
    }

    private void LogSubscriberFailure(Exception exception, string eventName, Delegate handler)
    {
        try
        {
            if (_log is not null)
            {
                _log.LogError(
                    exception,
                    "radio.session subscriber failed event={Event} handler={Handler}",
                    eventName,
                    handler.Method.Name);
                return;
            }

            Trace.TraceError(
                "radio.session subscriber failed event={0} handler={1}: {2}",
                eventName,
                handler.Method.Name,
                exception);
        }
        catch
        {
            // Subscriber isolation remains stronger than a failed logging sink.
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
