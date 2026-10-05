// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Feeds demodulated RX audio into the Protocol-1 <see cref="RxAudioRing"/> so a
/// connected P1 radio's onboard codec drives its speaker / headphone / line-out
/// jacks. The ring is drained by <c>Protocol1Client</c>'s EP2 TX loop and packed
/// into the L/R slots of the frame it already sends continuously — no extra
/// socket, no platform gate. Works in every host mode and on every OS.
///
/// This is the Protocol-1 counterpart to <see cref="SaturnSpeakerAudioSink"/>,
/// which owns the Protocol-2 speaker path (UDP 1028 → radio codec). The two are
/// mutually exclusive at runtime: this sink no-ops when P1 isn't active, and the
/// P2 sink no-ops when P2 isn't connected. They share the same operator opt-in
/// (<see cref="RadioSpeakerSettingsStore"/>) so the single Settings → Radio
/// toggle governs the radio-speaker output regardless of protocol — the
/// <see cref="AvailableForConnectedBoard"/> surface reports True whenever any
/// codec board is connected (issue #1122).
///
/// Gating (all must hold, re-checked per frame so a mid-session toggle or MOX
/// transition takes effect immediately):
///   • operator opted in (RadioSpeakerSettingsStore.Enabled, default off),
///     except keyed CW sidetone on codec boards other than HL2+ (it stands in
///     for the FPGA sidetone, which ignores the opt-in)
///   • a Protocol-1 client is connected (so the ring is actually drained)
///   • the board has a codec (including an explicitly configured HL2+)
///   • not transmitting (don't push TX-monitor audio to the radio speaker),
///     except full-duplex (DUP) receive audio and CW sidetone on their own lanes
///   • the frame is the expected 48 kHz mono RX audio
/// When any check fails the frame is dropped and the ring is left to drain to
/// silence, so the wire reverts to byte-identical "no RX audio" behaviour.
/// </summary>
public sealed class RadioSpeakerAudioSink : IRxAudioSink, IDisposable
{
    private const uint ExpectedSampleRateHz = 48_000;

    private readonly RadioService _radio;
    private readonly RxAudioRing _ring;
    private readonly RadioSpeakerSettingsStore _settings;
    private readonly RxAudioMuteState _muteState;

    public RadioSpeakerAudioSink(
        RadioService radio,
        RxAudioRing ring,
        RadioSpeakerSettingsStore settings,
        RxAudioMuteState? muteState = null)
    {
        _radio = radio;
        _ring = ring;
        _settings = settings;
        // Null in tests that don't exercise mute — a private instance keeps
        // the field non-null so the ctor stays legacy-compatible.
        _muteState = muteState ?? new RxAudioMuteState();
        // Drop any buffered tail when the operator turns the feature off so a
        // later re-enable starts clean rather than replaying stale audio.
        _settings.Changed += OnSettingsChanged;
        _muteState.Changed += OnMuteChanged;
        _radio.Connected += OnConnected;
        _radio.AudioFrontEndChanged += OnAudioFrontEndChanged;
        _radio.MoxChanged += OnMoxChanged;
        UpdateCodecSpeaker();
    }

    /// <summary>True when a codec-equipped radio is currently connected (P1 or P2),
    /// so the <c>/api/radio/speaker-output</c> endpoint can surface the toggle and
    /// the UI shows it whenever it does something. The actual audio routing is
    /// split: this sink handles P1, <see cref="SaturnSpeakerAudioSink"/> handles
    /// P2 — both share the same opt-in.</summary>
    public bool AvailableForConnectedBoard()
    {
        if (!_radio.IsConnected) return false;
        return _radio.AudioCapabilities.HasOnboardCodec;
    }

    public void Publish(in AudioFrame frame)
    {
        if (frame.Channels != 1 || frame.SampleRateHz != ExpectedSampleRateHz) return;
        if (!_settings.Enabled) return;
        // The P1 ring is consumed by Protocol1Client's EP2 TX loop; under P2 the
        // ring has no consumer and SaturnSpeakerAudioSink handles the wire
        // instead, so don't write here.
        if (!_radio.IsProtocol1Active) return;
        // Operator mute (issue #1252): silence the radio's own speaker jack in
        // sync with the PC playback path. Drop the buffered tail so unmute
        // doesn't replay pre-mute audio into the EP2 L/R slots.
        if (_muteState.IsMuted)
        {
            _ring.Clear();
            return;
        }
        if (_radio.IsMox)
        {
            // While transmitting, the ordinary lane (TX-monitor, suppressed
            // silence) is not for the radio speaker; only the DUP lane below
            // reaches the EP2 L/R slots. Drop the pre-key tail once, on the
            // key-down edge, so receive resumes from live audio rather than
            // replaying it — without wiping DUP audio queued during the over.
            EnterKeyedInterval();
            return;
        }
        _keyedIntervalEntered = false;
        _ring.KeyedDrainArmed = false;
        if (!_radio.AudioCapabilities.HasOnboardCodec) return;

        _ring.Write(frame.Samples.Span);
    }

    // Host CW sidetone while keyed. CWX disarms the FPGA keyer, and with it the
    // gateware's own headphone sidetone, so this lane stands in for it. The
    // FPGA paddle sidetone plays at the radio jack whether or not the operator
    // opted into radio-speaker receive audio, so this lane does too (issue
    // #2766). HL2+ is the exception: its codec output is only enabled by that
    // opt-in (UpdateCodecSpeaker).
    public void PublishCwSidetone(in AudioFrame frame)
    {
        if (frame.Channels != 1 || frame.SampleRateHz != ExpectedSampleRateHz) return;
        int moxOffGeneration = Volatile.Read(ref _moxOffGeneration);
        if (!_settings.Enabled && _radio.ConnectedBoardKind == HpsdrBoardKind.HermesLite2) return;
        if (!_radio.IsProtocol1Active || !_radio.IsMox) return;
        if (_muteState.IsMuted) return;
        if (!_radio.AudioCapabilities.HasOnboardCodec) return;

        EnterKeyedInterval();
        _beforeCwSidetoneWriteForTest?.Invoke();
        _ring.Write(frame.Samples.Span);
        _ring.KeyedDrainArmed = true;

        // An unkey can race the gate above and land before this write. Drop
        // that late block so it cannot replay on the next keyed interval.
        if (!_radio.IsMox || Volatile.Read(ref _moxOffGeneration) != moxOffGeneration)
        {
            _ring.KeyedDrainArmed = false;
            _ring.Clear();
        }
    }

    // Full-duplex (DUP) receive audio. Outside MOX (the post-TX drain) it is
    // ordinary receive audio. While keyed it keeps feeding the EP2 L/R slots,
    // which ControlFrame fills during MOX whenever this ring holds samples.
    public void PublishDuplexRx(in AudioFrame frame)
    {
        if (!_radio.IsMox)
        {
            Publish(in frame);
            return;
        }
        WriteKeyedDuplexRx(in frame);
    }

    // MON + DUP: host outputs hear receive inside the TX-monitor mix, which the
    // radio speaker never plays; this receive-only block keeps it audible here.
    // Outside MOX the TX-monitor lane already reaches this sink via Publish.
    public void PublishDuplexRxBesideTxMonitor(in AudioFrame frame)
    {
        if (_radio.IsMox) WriteKeyedDuplexRx(in frame);
    }

    private void WriteKeyedDuplexRx(in AudioFrame frame)
    {
        if (frame.Channels != 1 || frame.SampleRateHz != ExpectedSampleRateHz) return;
        if (!_settings.Enabled) return;
        if (!_radio.IsProtocol1Active) return;
        if (_muteState.IsMuted)
        {
            _ring.Clear();
            return;
        }
        if (!_radio.AudioCapabilities.HasOnboardCodec) return;

        EnterKeyedInterval();
        Volatile.Write(ref _duplexRxThisTx, 1);
        _ring.Write(frame.Samples.Span);
        _ring.KeyedDrainArmed = true;
    }

    // DSP tick thread only (every Publish* runs there), so a plain field is
    // enough to turn "clear on every keyed frame" into "clear on key-down".
    private bool _keyedIntervalEntered;
    private int _moxOffGeneration;
    // 1 once DUP receive audio was queued during the current over. Written on
    // the DSP tick, read and reset on the thread that flips MOX.
    private int _duplexRxThisTx;
    private Action? _beforeCwSidetoneWriteForTest;

    private void EnterKeyedInterval()
    {
        if (_keyedIntervalEntered) return;
        _keyedIntervalEntered = true;
        _ring.Clear();
    }

    private void OnSettingsChanged()
    {
        if (!_settings.Enabled) _ring.Clear();
        UpdateCodecSpeaker();
    }

    private void OnConnected(IProtocol1Client client) => UpdateCodecSpeaker();

    private void OnAudioFrontEndChanged(AudioFrontEndPush state)
    {
        if (!_radio.AudioCapabilities.HasOnboardCodec) _ring.Clear();
        UpdateCodecSpeaker();
    }

    private void UpdateCodecSpeaker()
    {
        var client = _radio.ActiveClient;
        if (client is not null)
            client.EnableHl2CodecSpeaker = _radio.ConnectedBoardKind == HpsdrBoardKind.HermesLite2
                && _radio.AudioCapabilities.HasOnboardCodec && _settings.Enabled;
    }

    // Disarm the keyed L/R drain on both MOX edges, from the thread that flips
    // MOX. Until the DSP tick's key-down clear runs and DUP audio re-arms it,
    // keyed EP2 frames must not drain the pre-key receive tail.
    // On unkey, drop a sidetone-only tail so it never plays after the over. DUP
    // receive audio queued during the over is ordinary receive from here on and
    // keeps draining; the pipeline never publishes the sidetone lane during a
    // DUP over, so a ring that took DUP audio holds no sidetone-only samples.
    private void OnMoxChanged(bool on)
    {
        bool duplexRxThisTx = Interlocked.Exchange(ref _duplexRxThisTx, 0) != 0;
        if (!on)
        {
            Interlocked.Increment(ref _moxOffGeneration);
            if (!duplexRxThisTx) _ring.Clear();
        }
        _ring.KeyedDrainArmed = false;
    }

    private void OnMuteChanged()
    {
        // Rising edge: drop the buffered tail so an unmute starts clean.
        // Falling edge: no-op — the ring drains naturally.
        if (_muteState.IsMuted) _ring.Clear();
    }

    internal Action? BeforeCwSidetoneWriteForTest
    {
        set => _beforeCwSidetoneWriteForTest = value;
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _muteState.Changed -= OnMuteChanged;
        _radio.Connected -= OnConnected;
        _radio.AudioFrontEndChanged -= OnAudioFrontEndChanged;
        _radio.MoxChanged -= OnMoxChanged;
    }
}
