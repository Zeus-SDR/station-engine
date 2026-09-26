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
/// <para><b>It never starts a transmission.</b> The ID reaches the air only
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
/// </summary>
public sealed class CwIdService : IDisposable
{
    /// <summary>Keyed time required before an ID starts, so a PTT blip or the
    /// amp T/R settle window cannot swallow the first characters.</summary>
    internal const long StartHoldMs = 400;

    private static readonly Regex CallsignPattern =
        new("^[A-Z0-9]+(/[A-Z0-9]+)*$", RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly CwIdToneMixer _mixer = new();
    private readonly Func<RxMode> _mode;
    private readonly Func<long> _nowMs;
    private readonly ILogger<CwIdService> _log;
    private readonly Action? _unsubscribe;

    private CwIdSettingsDto _settings;
    private bool _running;
    private bool _finalArmed;
    private bool _sendingFinal;
    private bool _transmittedSinceStart;
    private string? _callsign;
    private long? _periodStartMs;
    private long? _keyedSinceMs;
    private DateTime? _lastIdUtc;

    public CwIdService(CwIdSettingsStore store, TxService tx, RadioService radio, ILogger<CwIdService> log)
        : this(store.Get(), () => radio.CurrentMode, () => Environment.TickCount64, log)
    {
        tx.TxActiveChanged += OnTxActiveChanged;
        _unsubscribe = () => tx.TxActiveChanged -= OnTxActiveChanged;
    }

    internal CwIdService(CwIdSettingsDto settings, Func<RxMode> mode, Func<long> nowMs, ILogger<CwIdService> log)
    {
        _settings = settings;
        _mode = mode;
        _nowMs = nowMs;
        _log = log;
    }

    /// <summary>Upper-case and validate a callsign: letters, digits and
    /// '/'-separated portable/mobile suffixes, 3..15 characters.</summary>
    public static bool TryNormalizeCallsign(string? raw, out string callsign)
    {
        callsign = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return callsign.Length is >= 3 and <= 15 && CallsignPattern.IsMatch(callsign);
    }

    public bool Start(string? rawCallsign, out string? error)
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
        }
        _log.LogInformation("cwid.start callsign={Callsign}", callsign);
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
            if (_running && _periodStartMs is long start)
                secondsUntilDue = (int)Math.Max(0, (IntervalMs - (now - start) + 999) / 1000);
            return new CwIdStatusDto(
                Running: _running,
                Callsign: _callsign,
                Due: IsDueLocked(now),
                Sending: _mixer.Active,
                FinalIdArmed: _finalArmed,
                SecondsUntilDue: secondsUntilDue,
                LastIdUtc: _lastIdUtc,
                Settings: _settings);
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
            if (_mixer.MixInto(block)) CompleteLocked(_nowMs());
            return true;
        }
    }

    internal void OnTxActiveChanged(bool active)
    {
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
            if (_sendingFinal) _finalArmed = true;
            _sendingFinal = false;
        }
        _log.LogInformation("cwid.interrupted by emergency release; ID stays due");
    }

    public void Dispose() => _unsubscribe?.Invoke();

    private long IntervalMs => _settings.IntervalMinutes * 60_000L;

    private bool IsDueLocked(long now) =>
        _finalArmed || (_running && _periodStartMs is long start && now - start >= IntervalMs);

    private void CompleteLocked(long now)
    {
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
        }
    }
}
