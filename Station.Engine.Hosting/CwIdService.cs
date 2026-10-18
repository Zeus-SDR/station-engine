// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using System.Text.RegularExpressions;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// CW station ID timer. Once the operator presses Start, the callsign is sent
/// in Morse at least every <see cref="CwIdSettingsDto.IntervalMinutes"/>
/// minutes of operating, mixed into the operator's own voice transmission.
///
/// <para><b>The timer never starts a transmission.</b> Its ID reaches the air only
/// through <see cref="MixTxBlock"/>, which <see cref="TxAudioIngest"/> calls
/// with mic blocks already on their way to a keyed TXA, and
/// <see cref="MixTailBlock"/>, which finishes an ID already on the air while an
/// operator release holds the key. When the
/// interval expires while the operator is listening, the ID is marked due and
/// goes out at the start of their next voice transmission.</para>
///
/// <para>Timing: a period starts at the first keyed block after Start (or after
/// the previous ID). Stop arms one final ID (end-of-contact identification)
/// that goes out on the next transmission, unless the operator never
/// transmitted since Start. An ID already on the air when the operator
/// un-keys is finished before the key drops: <see cref="TxService"/> holds the
/// release while <see cref="TxAudioIngest.DrainCwIdTail"/> clocks the rest of
/// it through <see cref="MixTailBlock"/>, so it goes out once instead of being
/// cut and resent. Only an emergency release (trip, disconnect) cuts it short;
/// that partial ID is cancelled and stays due.</para>
/// <para>An explicit Instant ID request uses the transmit service's guarded
/// key/send/release transaction and the same synthesized audio tail.</para>
/// <para><b>On the 10</b> replaces the interval with the station clock: the ID
/// comes due on every 10-minute mark (:00, :10, :20 ... local time). Keyed in
/// a voice mode, it is mixed into the transmission as above. Idle, the
/// schedule is the one path that starts a transmission: it sends through the
/// same guarded Instant ID transaction, which refuses while anything else is
/// transmitting and outside the voice modes.</para>
/// </summary>
public sealed class CwIdService : IDisposable
{
    /// <summary>Keyed time required before an ID starts, so a PTT blip or the
    /// amp T/R settle window cannot swallow the first characters.</summary>
    internal const long StartHoldMs = 400;

    /// <summary>Spacing of the On the 10 clock marks.</summary>
    internal static readonly TimeSpan OnTheTenSpacing = TimeSpan.FromMinutes(10);

    /// <summary>How often the On the 10 schedule checks for an idle mark.</summary>
    internal const int OnTheTenTickMs = 250;

    /// <summary>Wait after a refused idle send (wrong mode, TX inhibited,
    /// another transmission starting) before the schedule tries again.</summary>
    internal const long OnTheTenRetryMs = 5_000;

    private static readonly Regex CallsignPattern =
        new("^[A-Z0-9]+(/[A-Z0-9]+)*$", RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly CwIdToneMixer _mixer = new();
    private readonly Func<RxMode> _mode;
    private readonly Func<long> _nowMs;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger<CwIdService> _log;
    private readonly Action? _unsubscribe;
    private readonly Func<string, (bool Ok, string? Error)>? _keyAndSend;
    private readonly Timer? _onTheTenTimer;
    private int _onTheTenTickActive;

    private CwIdSettingsDto _settings;
    private bool _running;
    private bool _finalArmed;
    private bool _sendingFinal;
    private bool _transmittedSinceStart;
    private string? _callsign;
    private long? _periodStartMs;
    private long? _keyedSinceMs;
    private DateTime? _lastIdUtc;
    private bool _instantSending;
    private long _instantLeadSamples;
    private string? _instantCallsign;
    private bool _onTheTen;
    private DateTimeOffset? _nextMark;
    private bool _txActive;
    private long _idleRetryAtMs;
    private DateTimeOffset? _idleFailureLoggedFor;
    internal bool InstantCompleted { get; private set; }

    internal bool BeginInstant(string callsign, int settleMs, out string? error)
    {
        lock (_sync)
        {
            if (_mixer.Active)
            {
                error = "A CW ID is already being sent.";
                return false;
            }
            _mixer.Begin(callsign, _settings.Wpm, _settings.ToneHz, _settings.LevelDb);
            _instantSending = true;
            _instantCallsign = callsign;
            if (!_running) _callsign = callsign;
            InstantCompleted = false;
            _instantLeadSamples = Math.Max(StartHoldMs, settleMs) * 48;
            _sendingFinal = _finalArmed;
            error = null;
            return true;
        }
    }

    public CwIdService(CwIdSettingsStore store, TxService tx, RadioService radio, ILogger<CwIdService> log)
        : this(store.Get(), () => radio.CurrentMode, () => Environment.TickCount64, log,
            () => DateTimeOffset.Now, null)
    {
        _keyAndSend = callsign =>
            (tx.TrySendInstantCwId(this, callsign, null, out var error), error);
        tx.TxActiveChanged += OnTxActiveChanged;
        _unsubscribe = () => tx.TxActiveChanged -= OnTxActiveChanged;
        _onTheTenTimer = new Timer(_ => OnTheTenTick(), null, OnTheTenTickMs, OnTheTenTickMs);
    }

    internal CwIdService(CwIdSettingsDto settings, Func<RxMode> mode, Func<long> nowMs, ILogger<CwIdService> log,
        Func<DateTimeOffset>? clock = null, Func<string, (bool Ok, string? Error)>? keyAndSend = null)
    {
        _settings = settings;
        _mode = mode;
        _nowMs = nowMs;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _keyAndSend = keyAndSend;
    }

    /// <summary>The first 10-minute mark of the local clock strictly after
    /// <paramref name="now"/>, in <paramref name="now"/>'s own offset.</summary>
    internal static DateTimeOffset NextMark(DateTimeOffset now)
    {
        long spacing = OnTheTenSpacing.Ticks;
        long local = now.Ticks;  // local clock time at now.Offset
        return new DateTimeOffset(local - local % spacing + spacing, now.Offset);
    }

    /// <summary>Upper-case and validate a callsign: letters, digits and
    /// '/'-separated portable/mobile suffixes, 3..15 characters.</summary>
    public static bool TryNormalizeCallsign(string? raw, out string callsign)
    {
        callsign = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return callsign.Length is >= 3 and <= 15 && CallsignPattern.IsMatch(callsign);
    }

    public bool Start(string? rawCallsign, out string? error) => Start(rawCallsign, onTheTen: false, out error);

    /// <summary>Start (or retarget) the ID. <paramref name="onTheTen"/> picks
    /// the clock-mark schedule over the interval timer; calling again while
    /// running switches between them without a final ID.</summary>
    public bool Start(string? rawCallsign, bool onTheTen, out string? error)
    {
        if (!TryNormalizeCallsign(rawCallsign, out var callsign))
        {
            error = "A valid callsign is required (letters, digits and /). Log in to QRZ or set your callsign.";
            return false;
        }
        lock (_sync)
        {
            if (!_running)
            {
                _running = true;
                _transmittedSinceStart = false;
                _periodStartMs = null;
            }
            _finalArmed = false;
            _callsign = callsign;
            if (onTheTen && !_onTheTen) _nextMark = NextMark(_clock());
            if (!onTheTen) _nextMark = null;
            _onTheTen = onTheTen;
        }
        _log.LogInformation("cwid.start callsign={Callsign} onTheTen={OnTheTen}", callsign, onTheTen);
        error = null;
        return true;
    }

    public void Stop(bool skipFinalId)
    {
        bool finalArmed;
        lock (_sync)
        {
            if (!_running && !_finalArmed) return;
            _running = false;
            _periodStartMs = null;
            _onTheTen = false;
            _nextMark = null;
            // An ID already on the air counts as the final one.
            bool idInFlight = _mixer.Active;
            _finalArmed = !skipFinalId && _transmittedSinceStart && !idInFlight;
            if (idInFlight) _sendingFinal = true;
            finalArmed = _finalArmed;
        }
        _log.LogInformation("cwid.stop skipFinal={Skip} finalArmed={Final}", skipFinalId, finalArmed);
    }

    public void ApplySettings(CwIdSettingsDto settings)
    {
        lock (_sync) _settings = settings;
    }

    public CwIdStatusDto Status()
    {
        lock (_sync)
        {
            long now = _nowMs();
            int? secondsUntilDue = null;
            if (_running && _onTheTen && _nextMark is DateTimeOffset mark)
                secondsUntilDue = (int)Math.Max(0, Math.Ceiling((mark - _clock()).TotalSeconds));
            else if (_running && _periodStartMs is long start)
                secondsUntilDue = (int)Math.Max(0, (IntervalMs - (now - start) + 999) / 1000);
            return new CwIdStatusDto(
                Running: _running,
                Callsign: _instantSending ? _instantCallsign : _callsign,
                Due: IsDueLocked(now),
                Sending: _mixer.Active,
                FinalIdArmed: _finalArmed,
                SecondsUntilDue: secondsUntilDue,
                LastIdUtc: _lastIdUtc,
                Settings: _settings,
                OnTheTen: _running && _onTheTen,
                NextIdUtc: _running && _onTheTen ? _nextMark?.UtcDateTime : null);
        }
    }

    /// <summary>
    /// Called by <see cref="TxAudioIngest"/> for each keyed voice-path TX
    /// block (48 kHz, before WDSP). <paramref name="canSend"/> is false while
    /// the block will not reach the air intact (pre-key mute window) or comes
    /// from a source that must not carry an ID. Adds the ID tone in place when
    /// one is due; otherwise leaves the block untouched.
    /// </summary>
    internal void MixTxBlock(Span<float> block, bool canSend)
    {
        // Logged after the lock so no I/O runs on the audio thread under it.
        string? startedCall = null;
        bool startedFinal = false;
        lock (_sync)
        {
            // The manual transaction owns the synthesized tail, independently
            // of microphone availability and the selected audio input.
            if (_instantSending) return;
            if (!_running && !_finalArmed && !_mixer.Active) return;
            long now = _nowMs();
            _keyedSinceMs ??= now;
            _periodStartMs ??= _running ? now : null;
            _transmittedSinceStart = true;

            if (_mixer.Active)
            {
                if (_mixer.MixInto(block)) CompleteLocked(now);
                return;
            }

            if (!canSend || !IsDueLocked(now) || !TxService.IsVoiceTxMode(_mode())) return;
            if (now - _keyedSinceMs.Value < StartHoldMs) return;

            _mixer.Begin(_callsign!, _settings.Wpm, _settings.ToneHz, _settings.LevelDb);
            _sendingFinal = _finalArmed;
            startedCall = _callsign;
            startedFinal = _sendingFinal;
            if (_mixer.MixInto(block)) CompleteLocked(now);
        }
        if (startedCall is not null)
            _log.LogInformation("cwid.send callsign={Callsign} final={Final}", startedCall, startedFinal);
    }

    /// <summary>True while an ID is on the air (started and not finished).</summary>
    internal bool IsSending
    {
        get { lock (_sync) return _mixer.Active; }
    }

    /// <summary>
    /// Release-tail path: the operator un-keyed while an ID was on the air and
    /// <see cref="TxService"/> is holding the key until it ends. Adds the next
    /// <paramref name="block"/>.Length samples of the ID onto the block and
    /// completes the ID when it ends. Returns false (block untouched) when no
    /// ID is on the air.
    /// </summary>
    internal bool MixTailBlock(Span<float> block)
    {
        lock (_sync)
        {
            if (!_mixer.Active) return false;
            if (_instantSending && _instantLeadSamples > 0)
            {
                int silence = (int)Math.Min(block.Length, _instantLeadSamples);
                block.Clear();
                _instantLeadSamples -= silence;
                block = block[silence..];
                if (block.IsEmpty) return true;
            }
            if (_mixer.MixInto(block)) CompleteLocked(_nowMs());
            return true;
        }
    }

    internal void OnTxActiveChanged(bool active)
    {
        lock (_sync) _txActive = active;
        if (active) return;
        lock (_sync)
        {
            _keyedSinceMs = null;
            if (!_mixer.Active) return;
            // Still on the air after the release: an emergency release cut
            // the tail short (a normal un-key finishes the ID first). The
            // partial ID does not count; it stays due
            // (a periodic ID because its period has not been reset, a final
            // ID by re-arming it).
            _mixer.Cancel();
            _instantSending = false;
            _instantLeadSamples = 0;
            if (_sendingFinal) _finalArmed = true;
            _sendingFinal = false;
        }
        _log.LogInformation("cwid.interrupted by emergency release; ID stays due");
    }

    /// <summary>
    /// On the 10 idle path, run every <see cref="OnTheTenTickMs"/>. When a
    /// mark has passed and nothing is transmitting, keys the radio and sends
    /// the ID through the guarded Instant ID transaction. A keyed operator gets
    /// the ID mixed into their voice by <see cref="MixTxBlock"/> instead.
    /// </summary>
    internal void OnTheTenTick()
    {
        if (_keyAndSend is null) return;
        if (Interlocked.CompareExchange(ref _onTheTenTickActive, 1, 0) != 0) return;
        try
        {
            string callsign;
            DateTimeOffset mark;
            lock (_sync)
            {
                long now = _nowMs();
                if (!_running || !_onTheTen || _finalArmed || _txActive || _mixer.Active) return;
                if (_nextMark is not DateTimeOffset m || _clock() < m || now < _idleRetryAtMs) return;
                callsign = _callsign!;
                mark = m;
            }
            var (ok, error) = _keyAndSend(callsign);
            if (ok)
            {
                _log.LogInformation("cwid.onTheTen.sent callsign={Callsign} mark={Mark:HH:mm}", callsign, mark);
                return;
            }
            bool logIt;
            lock (_sync)
            {
                _idleRetryAtMs = _nowMs() + OnTheTenRetryMs;
                logIt = _idleFailureLoggedFor != mark;
                _idleFailureLoggedFor = mark;
            }
            // Once per mark: a CW or digital mode would otherwise log every retry.
            if (logIt)
                _log.LogWarning("cwid.onTheTen.refused mark={Mark:HH:mm} error={Error}; retrying", mark, error);
        }
        catch (Exception ex)
        {
            lock (_sync) _idleRetryAtMs = _nowMs() + OnTheTenRetryMs;
            _log.LogWarning(ex, "cwid.onTheTen.failed; retrying");
        }
        finally
        {
            Volatile.Write(ref _onTheTenTickActive, 0);
        }
    }

    public void Dispose()
    {
        _onTheTenTimer?.Dispose();
        _unsubscribe?.Invoke();
    }

    private long IntervalMs => _settings.IntervalMinutes * 60_000L;

    private bool IsDueLocked(long now) =>
        _finalArmed || (_running && (_onTheTen
            ? _nextMark is DateTimeOffset mark && _clock() >= mark
            : _periodStartMs is long start && now - start >= IntervalMs));

    private void CompleteLocked(long now)
    {
        if (_instantSending)
        {
            InstantCompleted = true;
            if (_running) _transmittedSinceStart = true;
        }
        _instantSending = false;
        _instantLeadSamples = 0;
        _lastIdUtc = DateTime.UtcNow;
        if (_sendingFinal)
        {
            _finalArmed = false;
            _sendingFinal = false;
            _periodStartMs = null;
        }
        else
        {
            // Still keyed: the next period starts now.
            _periodStartMs = _running ? now : null;
            // On the 10: the next mark after this ID.
            if (_running && _onTheTen) _nextMark = NextMark(_clock());
        }
    }
}
