// SPDX-License-Identifier: GPL-2.0-or-later
using System.Threading.Channels;
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

public static class Hl2IoBoardServiceCollectionExtensions
{
    public static IServiceCollection AddHl2IoBoard(this IServiceCollection services)
    {
        services.AddSingleton<Hl2IoBoardSettingsStore>();
        services.AddSingleton<Hl2IoBoardService>();
        services.AddHostedService(sp => sp.GetRequiredService<Hl2IoBoardService>());
        return services;
    }
}

public sealed record Hl2IoBoardView(Hl2IoBoardStatus Status, IReadOnlyList<Hl2IoBoardBand> Bands, string Tuning, string? TunerError = null);

/// <summary>Connection-scoped IO Board polling and explicitly requested operator commands.</summary>
public sealed class Hl2IoBoardService : BackgroundService
{
    private sealed record Command(string Action, byte Outputs, Protocol1Client Client, CancellationToken Token)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly Hl2IoBoardSettingsStore _settings;
    private readonly ILogger<Hl2IoBoardService> _log;
    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(8);
    private volatile Hl2IoBoardStatus _status = new();
    private volatile Hl2IoBoardTuner? _tuner;
    private long _foreignIntent;
    private long _tuneIntentEpoch;
    private string? _inhibit;
    internal Func<Protocol1Client, IHl2I2cTransport> TransportFactory { get; set; } = client => client.Hl2I2c;
    internal Func<Protocol1Client, bool>? ConnectionCurrentOverrideForTests { get; set; }

    public Hl2IoBoardService(RadioService radio, TxService tx, Hl2IoBoardSettingsStore settings, ILogger<Hl2IoBoardService> log)
    {
        _radio = radio;
        _tx = tx;
        _settings = settings;
        _log = log;
    }

    public Hl2IoBoardView Get()
    {
        var tuner = _tuner;
        return new(_status, _settings.GetAll(), tuner?.State ?? "idle", tuner?.Error);
    }

    public async Task ExecuteCommandAsync(string action, byte outputs, CancellationToken ct)
    {
        if (action is not ("tune" or "cancel" or "bypass" or "outputs"))
            throw new ArgumentException("Unknown IO Board command.", nameof(action));
        if (_radio.ActiveClient is not Protocol1Client client || client.BoardKind != HpsdrBoardKind.HermesLite2)
            throw new InvalidOperationException("Connect a Hermes Lite 2 first.");
        if (!_status.Present)
            throw new InvalidOperationException("HL2 IO Board is not available.");
        var command = new Command(action, outputs, client, ct);
        if (!_commands.Writer.TryWrite(command)) throw new InvalidOperationException("IO Board is busy.");
        await command.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Protocol1Client? previous = null;
        Hl2IoBoardSession? session = null;
        int polls = 0;
        _tx.TransmitRequested += OnTransmitRequested;
        _settings.Changed += OnSettingsChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = _radio.ActiveClient as Protocol1Client;
                if (client?.BoardKind != HpsdrBoardKind.HermesLite2 || _radio.Snapshot().Status != ConnectionStatus.Connected) client = null;
                if (!ReferenceEquals(previous, client))
                {
                    _tuner?.Disconnect();
                    _tuner?.Dispose();
                    _tuner = null;
                    SetInhibit(null);
                    _tx.Safety.ClearHl2IoBoard();
                    previous = client;
                    session = null;
                    _status = new(Supported: client is not null);
                    if (client is not null)
                    {
                        var candidate = new Hl2IoBoardSession(TransportFactory(client));
                        try
                        {
                            if (await candidate.DetectAsync(stoppingToken).ConfigureAwait(false))
                            {
                                if (!IsCurrent(client)) { previous = null; continue; }
                                session = candidate;
                                _tx.Safety.InvalidateHl2IoBoard();
                                _tuner = new Hl2IoBoardTuner(new TunerRadio(_tx, () => Interlocked.Read(ref _foreignIntent) == Interlocked.Read(ref _tuneIntentEpoch)), candidate.TunerCommandAsync);
                                _status = candidate.Status;
                                _log.LogInformation("hl2.io.detect hardware={Hardware} firmware={Major}.{Minor}",
                                    _status.HardwareVersion, _status.FirmwareMajor, _status.FirmwareMinor);
                            }
                            else _status = _status with { Error = "IO Board not detected" };
                        }
                        catch (Exception ex) when (ex is IOException or TimeoutException)
                        {
                            if (!IsCurrent(client)) continue;
                            _status = _status with { Error = "IO Board not detected; reconnect to retry" };
                            _log.LogDebug(ex, "hl2.io.detect unavailable");
                        }
                    }
                }

                if (session is not null && client is not null)
                {
                    try
                    {
                        await session.PollAsync(stoppingToken).ConfigureAwait(false);
                        if (!IsCurrent(client)) continue;
                        if (session.Status.Fault != 0)
                        {
                            SetInhibit($"HL2 IO Board fault {session.Status.Fault}; transmit is inhibited");
                            if (_tuner is not null) await _tuner.CancelAsync(stoppingToken).ConfigureAwait(false);
                        }
                        else
                        {
                            SetInhibit(null);
                            if (!Transmitting)
                            {
                                var state = _radio.Snapshot();
                                long frequency = RadioFrequencyResolver.TxFrequencyHz(state);
                                var txBand = _settings.GetBand(BandUtils.FreqToBand(frequency) ?? "GEN");
                                var rxBand = _settings.GetBand(BandUtils.FreqToBand(state.VfoHz) ?? "GEN");
                                if (_tx.Safety.Hl2IoBoardNeedsSync(state)
                                    || session.NeedsSynchronization(frequency, RadioFrequencyResolver.TxMode(state), rxBand.RxAntenna, txBand.TxAntenna))
                                    await MutateAccessoryAsync(client, session,
                                        () =>
                                        {
                                            // Capture settings after the mutation revision is established;
                                            // a later save invalidates that revision before admission.
                                            var current = _radio.Snapshot();
                                            long txFrequency = RadioFrequencyResolver.TxFrequencyHz(current);
                                            var transmit = _settings.GetBand(BandUtils.FreqToBand(txFrequency) ?? "GEN");
                                            var receive = _settings.GetBand(BandUtils.FreqToBand(current.VfoHz) ?? "GEN");
                                            return session.SynchronizeAsync(txFrequency, RadioFrequencyResolver.TxMode(current), receive.RxAntenna, transmit.TxAntenna, stoppingToken);
                                        }).ConfigureAwait(false);
                            }
                            if (!IsCurrent(client)) continue;
                            if (_tuner is not null)
                            {
                                if (Interlocked.Read(ref _foreignIntent) != Interlocked.Read(ref _tuneIntentEpoch))
                                    await _tuner.CancelAsync(stoppingToken).ConfigureAwait(false);
                                await _tuner.AdvanceAsync(session.Status.Tuner, stoppingToken).ConfigureAwait(false);
                            }
                        }
                        if (++polls % 10 == 0) await session.ReadOutputsAsync(stoppingToken).ConfigureAwait(false);
                        if (!IsCurrent(client)) continue;
                        _status = session.Status;
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or ArgumentException)
                    {
                        if (!IsCurrent(client)) continue;
                        SetInhibit("HL2 IO Board is unavailable; reconnect before transmitting");
                        _tuner?.Disconnect();
                        _status = session.Status with { Present = false, Error = ex.Message };
                        session = null;
                        _log.LogWarning(ex, "hl2.io.connection lost");
                    }
                }

                while (_commands.Reader.TryRead(out var command))
                {
                    try
                    {
                        command.Token.ThrowIfCancellationRequested();
                        if (session is null || !ReferenceEquals(client, command.Client) || !ReferenceEquals(_radio.ActiveClient, client))
                            throw new InvalidOperationException("IO Board connection changed.");
                        if (command.Action == "cancel")
                        {
                            if (_tuner is not null) await _tuner.CancelAsync(stoppingToken).ConfigureAwait(false);
                        }
                        else
                        {
                            if (Transmitting || session.Status.Fault != 0 || _tuner?.IsActive == true)
                                throw new InvalidOperationException("Unkey and clear any IO Board fault or tuner cycle first.");
                            if (command.Action == "outputs")
                            {
                                if (!await MutateAccessoryAsync(client!, session, () => session.SetOutputsAsync(command.Outputs, stoppingToken)).ConfigureAwait(false))
                                    throw new InvalidOperationException("Unkey before changing IO Board outputs.");
                            }
                            else
                            {
                                if (command.Action == "tune" && _tuner is not null)
                                {
                                    Interlocked.Exchange(ref _tuneIntentEpoch, Interlocked.Read(ref _foreignIntent));
                                    await _tuner.StartAsync(stoppingToken).ConfigureAwait(false);
                                }
                                else if (!await MutateAccessoryAsync(client!, session,
                                    () => session.TunerCommandAsync(2, stoppingToken)).ConfigureAwait(false))
                                    throw new InvalidOperationException("Unkey before bypassing the IO Board tuner.");
                            }
                        }
                        _status = session.Status;
                        command.Completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        command.Completion.TrySetException(ex);
                    }
                }
                await Task.Delay(100, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _tx.TransmitRequested -= OnTransmitRequested;
            _settings.Changed -= OnSettingsChanged;
            _tuner?.Disconnect();
            _tuner?.Dispose();
            _commands.Writer.TryComplete();
            while (_commands.Reader.TryRead(out var command))
                command.Completion.TrySetException(new IOException("IO Board service stopped."));
        }
    }

    private bool Transmitting => _tx.IsMoxOn || _tx.IsTunOn || _tx.IsTwoToneOn;

    private void SetInhibit(string? reason)
    {
        _tx.Safety.SetHl2IoBoardInhibit(reason);
        if (reason is not null && reason != _inhibit)
            _tx.TryTripForAlert(AlertKind.ExternalHardwareFault, reason);
        _inhibit = reason;
    }

    private bool IsCurrent(Protocol1Client client) => ConnectionCurrentOverrideForTests?.Invoke(client)
        ?? (ReferenceEquals(_radio.ActiveClient, client) && _radio.Snapshot().Status == ConnectionStatus.Connected
            && client.BoardKind == HpsdrBoardKind.HermesLite2);

    private void OnSettingsChanged()
    {
        if (_status.Present && _radio.ConnectedBoardKind == HpsdrBoardKind.HermesLite2)
        {
            _tx.Safety.InvalidateHl2IoBoard();
        }
    }

    private async Task<bool> MutateAccessoryAsync(Protocol1Client client, Hl2IoBoardSession session, Func<Task> mutation)
    {
        long? revision = null;
        if (!_tx.TryRunWithTransmitIdle(() =>
        {
            if (!IsCurrent(client) || (session.Status.Inputs & 1) == 0) return;
            revision = _tx.Safety.InvalidateHl2IoBoard();
        }, out _) || revision is null) return false;
        await mutation().ConfigureAwait(false);
        if (!IsCurrent(client)) return false;
        if (session.Status.FrequencyHz is { } frequency && session.SynchronizedMode is { } mode)
            _tx.Safety.SynchronizeHl2IoBoard(revision.Value, frequency, mode);
        return true;
    }

    private void OnTransmitRequested(MoxSource source)
    {
        if (source != MoxSource.Hl2IoBoard) Interlocked.Increment(ref _foreignIntent);
    }

    private sealed class TunerRadio(TxService tx, Func<bool> intentUnchanged) : IHl2IoBoardTunerRadio
    {
        public bool IsTransmitting => tx.IsMoxOn || tx.IsTunOn || tx.IsTwoToneOn;
        public bool IsOwner => tx.TunOwner == MoxSource.Hl2IoBoard;
        public bool TryStart(out string? error)
        {
            bool started = false;
            string? refusal = "Transmit intent changed during IO Board tuning.";
            bool admitted = tx.TryRunWithTransmitIdle(() =>
            {
                if (!intentUnchanged()) return;
                started = tx.TrySetTun(true, MoxSource.Hl2IoBoard, out refusal) && IsOwner;
                if (started && !intentUnchanged())
                {
                    StopOwned();
                    started = false;
                    refusal = "Transmit intent changed during IO Board tuning.";
                }
            }, out error);
            error ??= refusal;
            return admitted && started;
        }
        public void StopOwned()
        {
            if (IsOwner) tx.TrySetTun(false, MoxSource.Hl2IoBoard, out _);
        }
    }
}
