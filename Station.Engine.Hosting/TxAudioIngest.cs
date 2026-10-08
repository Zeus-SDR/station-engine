// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Internal tag identifying which producer fed a TX-audio block into
/// <see cref="TxAudioIngest.OnMicPcmBytes"/>. Carried explicitly (NOT inferred
/// from recency) so the in-lock single-select gate can arbitrate the host mic
/// against a radio jack deterministically across a source switch
/// (external-audio-jacks re-port). TCI/WAV remain operator-explicit overrides
/// and bypass the host/radio arbitration — their precedence is the existing
/// recency hysteresis.
/// </summary>
internal enum MicBlockSource
{
    /// <summary>Browser / native host microphone (the default TX-audio source).</summary>
    Host = 0,
    /// <summary>Browser microphone explicitly supplied by a LAN/mobile client.</summary>
    BrowserMic,
    /// <summary>Radio-digitised jack audio (Saturn mic/line-in/XLR via UDP 1026).</summary>
    RadioMic,
    /// <summary>Dedicated test-tools virtual cable capture.</summary>
    VirtualCable,
    /// <summary>TCI client TX audio (MSHV / WSJT-X …). Operator-explicit override.</summary>
    Tci,
    /// <summary>WAV-recording playback to the air. Operator-explicit override.</summary>
    Wav,
    /// <summary>Out-of-process product-plugin TX injection lease.</summary>
    ProductPlugin,
}

/// <summary>
/// Optional native-capture lifetime guard. Browser, radio, TCI, WAV, and
/// Product-plugin callers omit it and retain their existing behavior.
/// </summary>
internal readonly record struct MicBlockValidity(
    Func<long, long, bool> Validator,
    long Generation,
    long EnqueuedAt,
    Func<long, bool>? BufferedValidator = null)
{
    public bool IsCurrent => Validator(Generation, EnqueuedAt);
    public bool IsBufferedCurrent => BufferedValidator?.Invoke(Generation) ?? IsCurrent;
}

/// <summary>
/// Bridges browser-side mic audio to WDSP TXA and onward to the EP2 IQ
/// payload. Inputs are 960-sample f32le blocks from the /ws MicPcm frame
/// (20 ms @ 48 kHz mono); the service accumulates into WDSP's native block
/// size, calls <see cref="IDspEngine.ProcessTxBlock"/>, and pushes the
/// resulting modulated IQ into <see cref="TxIqRing"/> for
/// <see cref="Protocol1Client"/> to pull at EP2 packet rate.
///
/// Threading: <see cref="OnMicPcmBytes"/> runs on the StreamingHub receive
/// loop thread. We hold <see cref="_sync"/> for the duration of the flush
/// so back-to-back mic frames don't interleave into the same WDSP block
/// half-written.
///
/// Lifecycle: constructed via DI (singleton), subscribes to
/// <see cref="StreamingHub.MicPcmReceived"/> immediately. Drops input
/// silently when the engine is Synthetic (no TXA available) or MOX is off —
/// the ring stays empty in those cases so the EP2 packer emits silence.
/// </summary>
public sealed class TxAudioIngest : IDisposable
{
    private const int MicBlockSamples = 960;   // 20 ms @ 48 kHz (matches front-end worklet)
    private const int MicBlockBytes = MicBlockSamples * 4;

    private readonly TxIqRing _ring;
    private readonly Func<IDspEngine?> _engineProvider;
    private readonly Func<bool> _isMoxOn;
    private readonly ILogger<TxAudioIngest> _log;
    private readonly StreamingHub _hub;
    private readonly Action<ReadOnlyMemory<byte>> _handler;
    private Action<int>? _onWdspConsumed;
    private readonly Func<long> _stopwatchTicks;
    private readonly Action<int, Func<bool>?> _voiceTailWait;

    private readonly object _sync = new();
    // Accumulator scratch — sized to at least one WDSP block plus one frontend
    // block (1024 + 960 = 1984) so we can always append a new arrival before
    // draining. The excess gets shifted back after each flush.
    private readonly float[] _accumulator = new float[2048];
    private int _accumulatorFill;
    // Sized for the larger of the P1 (1024 in / 2048 iq) and P2
    // (512 in / 4096 iq) profiles so we don't reallocate at protocol switch.
    private readonly float[] _scratchMic = new float[1024];
    private readonly float[] _scratchIq = new float[4096];
    // All-zero IQ used to substitute silence during the pre-key (MOX) delay
    // window (issue #630). Same size as _scratchIq, never written, so the
    // modulated samples in _scratchIq stay intact for the peak diagnostic — we
    // mute by writing FROM this buffer, not by zeroing _scratchIq in place.
    private readonly float[] _muteIq = new float[4096];
    private const int IdlePreRollSamples = 3 * MicBlockSamples;
    private const int MaxVoiceOnsetSamples = 10 * MicBlockSamples;
    private readonly Queue<(float[] Samples, MicBlockSource Source, MicBlockValidity? Validity)> _idlePreRoll = new();
    private readonly Queue<(float[] Samples, MicBlockSource Source, MicBlockValidity? Validity)> _voiceOnset = new();
    private int _idlePreRollSamples;
    private int _voiceOnsetSamples;
    private int _preKeyZeroRemainder;
    private bool _voiceOnsetArmed;
    // While a tail replays held speech, a concurrent live mic callback must
    // yield without erasing the replay's partial WDSP input block.
    private bool _preserveOnsetAccumulatorDuringTail;

    // Exclusive TXA utility path. Non-zero while the mic hot path must yield TX
    // to a tail drain or key-down prime (no double-feed into WDSP fexchange2),
    // exactly as it defers to TxTuneDriver. Also the re-entrancy guard (CAS) so
    // rapid edge transitions can't both drive ProcessTxBlock. Dedicated scratch
    // so API-thread work never aliases _scratchMic/_scratchIq.
    private int _tailDraining;
    private readonly float[] _tailMic = new float[1024];
    private readonly float[] _tailIq = new float[4096];
    private const int TxRateHz = 48000;          // TX block / DAC rate
    // Bench-tunable on the G2. The tail drain paces the queued modem audio out at
    // the DAC rate, so its time budget must cover the ACTUAL backlog FinishTx
    // queued (residual frame + RADE EOO). The OLD fixed 350 ms budget guillotined
    // any larger tail mid-symbol — the far-station "garble at end of over". Budget
    // is now backlog-derived (pending + slack), bounded only by a runaway ceiling.
    // The guard is the extra hold after the last block so the radio ring/FIFO
    // finishes transmitting before PTT drops (raise if the very end is still
    // clipped on air; lower if there is dead carrier at the end).
    private const int FreeDvTxTailDrainSlackMs = 120;   // headroom over the measured backlog
    private const int FreeDvTxTailCeilingMs = 1200;     // absolute runaway cap on the drain
    private const int FreeDvTxTailGuardMs = 160;        // post-drain FIFO flush hold
    private const int RogerBeepDurationMs = 180;
    private const int RogerBeepDspFlushCeilingMs = 200;
    private const int RogerBeepFlushSilentBlocks = 2;
    private const float RogerBeepFlushIqThreshold = 0.001f; // -60 dBFS
    private const int RogerBeepTransportDrainTimeoutMs = 500;
    private const int RogerBeepTailGuardMs = 120;
    // Voice unkey tail. Only speech still inside TXA (EstimateVoiceTxDspLatencyMs)
    // and the host transport backlog may lengthen a release; the rest is a small
    // fixed margin, so the radio returns to receive without a felt hang.
    private const int VoiceTailSettleMinMs = 20;   // one 20 ms mic frame still in capture
    private const int VoiceTailSettleMaxMs = 200;
    private const int VoiceTailQuietBlocks = 1;
    // IQ the radio still holds after the host transport goes idle. HL2
    // gateware buffers 20 ms by default (register 0x17 unset); the G2 Saturn
    // DUC FIFO holds up to 4096 words (~28 ms at 192 kHz). The P2 pacer only
    // targets ~6.5 ms of that, so bursts and clock drift can fill the rest.
    internal const int VoiceTailRadioFifoGuardMs = 30;
    // A flush block that wakes late is clocked immediately to catch up, so a
    // coarse OS timer (15.6 ms on Windows) cannot stretch the flush; only a
    // stall longer than this re-anchors the pace.
    private const int VoiceTailMaxCatchUpMs = 50;
    private const double RogerBeepFrequencyHz = 1000.0;
    private const float RogerBeepMagnitude = 0.60f;
    private const int KeyDownPrimeBlocks = 12;
    private const float MonitorPreviewOpenPeak = 0.012f; // ~-38 dBFS
    private const float MonitorPreviewOpenRms = 0.003f;  // ~-50 dBFS
    // Hold the preview gate open across ordinary phrase pauses. A 300 ms hang
    // (the original 15 blocks) closed on every breath between phrases, so the
    // off-air MON preview sounded like a DEXP/noise gate even with DEXP off
    // (#2515) while keyed TX monitoring stayed clean. Sustained idle still
    // closes the gate, so an unattended mic never clocks noise through TXA.
    private const int MonitorPreviewHangMs = 1500;
    private const int MicSamplesPerMs = 48;              // mic path is fixed at 48 kHz
    internal const int MonitorPreviewHangBlocks =
        MonitorPreviewHangMs * MicSamplesPerMs / MicBlockSamples; // 75 blocks of 20 ms
    // All-zero mic block substituted for gated idle blocks so the monitor
    // stream stays gapless while the preview gate is closed. Never written.
    private static readonly float[] SilentMicBlock = new float[MicBlockSamples];

    private long _totalMicSamples;
    private long _totalTxBlocks;
    // Hang countdown in samples so sub-block (ASIO-cadence) input times the
    // same 1.5 s as 20 ms blocks.
    private int _monitorPreviewHangSamples;

    internal bool IsFreeDvTailDraining => Volatile.Read(ref _tailDraining) != 0;
    private long _droppedFrames;
    // Tracks the last-seen MOX state so Clear() fires exactly once per MOX
    // falling edge instead of on every mic frame that happens to arrive while
    // MOX is off. The hot-loop Clear caused a race with the MOX rising edge:
    // client-optimistic mic frames can reach the hub before /api/tx/mox has
    // flipped the server's IsMoxOn, and the pre-flip frames were wiping the
    // ring of IQ Protocol1Client had just produced.
    private bool _lastSeenMox;
    // TCI-source recency: set on every OnMicPcmBytesFromTci call. If a frame
    // from the mic source arrives within TciHysteresisMs of the last TCI feed,
    // it is silently dropped — only the TCI source is authoritative for that
    // window. This prevents NativeMicCapture's always-on capture stream from
    // injecting mic-silence blocks into the accumulator while a TCI client
    // (MSHV, TCI Remote, …) is the actual audio source. 500 ms covers the
    // 42.67 ms TX_CHRONO cadence with >10× margin while remaining short
    // enough that a genuine fallback to mic happens instantly after TCI stops.
    private const int TciHysteresisMs = 500;
    private long _lastTciTickMs;
    // WAV-playback-source recency: set on every OnMicPcmBytesFromWav call so a
    // concurrent native-mic frame within TciHysteresisMs is suppressed -- the
    // recording replaces the live mic on the air, they never mix.
    private long _lastWavTickMs;
    // Browser/mobile mic recency: desktop mode can have either NativeMicCapture
    // or a radio-digitised jack running continuously, so remote WebSocket mic
    // frames must temporarily own the "live mic" source. Otherwise mobile PTT
    // mixes phone audio with the station source and feeds TXA at roughly 2x
    // realtime.
    private long _lastBrowserMicTickMs;

    // The currently-armed TX-audio source for HOST↔RADIO arbitration
    // (external-audio-jacks re-port, "atomic single-select gate"). Read and
    // written ONLY under _sync. OnMicPcmBytes rejects any Host- or
    // RadioMic-tagged block whose tag != _activeSource, so when a radio jack is
    // armed the host mic is dropped immediately by the in-lock compare (no
    // overlap window) and vice versa. TCI/WAV-tagged blocks bypass this compare.
    // Default Host so a fresh / un-switched ingest is byte/behaviour-identical
    // to today.
    private MicBlockSource _activeSource = MicBlockSource.Host;
    // The normal operator-selected source is retained while the separately
    // managed virtual-cable test route is active. Disabling that route restores
    // this source without changing the radio's audio-source setting.
    private MicBlockSource _normalActiveSource = MicBlockSource.Host;
    private bool _virtualCableEnabled;
    // Source whose samples currently occupy the WDSP accumulator. Read/written
    // ONLY under _sync. Enforces that a partially-filled accumulator is NEVER
    // topped up by a different source: between the cheap top-of-method gate and
    // the accumulation lock, _activeSource can flip, so the AUTHORITATIVE
    // arbitration is re-done inside the accumulation lock against this owner.
    private MicBlockSource _accumulatorSource = MicBlockSource.Host;

    /// <summary>
    /// Arm the TX-audio source for the HOST↔RADIO single-select gate
    /// (external-audio-jacks re-port). Called by the pipeline when the resolved
    /// <see cref="TxAudioSource"/> changes. Under <see cref="_sync"/> this sets
    /// the new active source AND clears the WDSP accumulator so no half-block of
    /// the old source survives onto the new source — the new source fills from
    /// empty. The 1026 re-blocker (owned upstream) is reset by the same caller.
    /// Maps every radio jack (Mic/Line-In/XLR) onto
    /// <see cref="MicBlockSource.RadioMic"/> because they all arrive on the one
    /// UDP-1026 stream; only Host is distinct here. TCI/WAV are not selectable
    /// sources — they override transiently.
    /// </summary>
    internal void SetActiveSource(TxAudioSource source)
    {
        var mapped = source == TxAudioSource.Host
            ? MicBlockSource.Host
            : MicBlockSource.RadioMic;
        lock (_sync)
        {
            _normalActiveSource = mapped;
            if (_virtualCableEnabled || _activeSource == mapped) return;
            SetActiveSourceLocked(mapped);
        }
    }

    /// <summary>
    /// Enables the test-tools virtual cable as an independent live TX source.
    /// This only selects audio; it never changes MOX or PTT. The ordinary
    /// Host/Radio source selection is preserved and restored when disabled.
    /// </summary>
    internal void SetVirtualCableEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_virtualCableEnabled == enabled) return;
            _virtualCableEnabled = enabled;
            // A browser/mobile stream may have owned the ordinary live source
            // immediately before the cable was selected. Clear its recency
            // stamp while holding the same gate so it cannot delay the cable.
            if (enabled) Volatile.Write(ref _lastBrowserMicTickMs, 0);
            SetActiveSourceLocked(enabled ? MicBlockSource.VirtualCable : _normalActiveSource);
        }
    }

    private void SetActiveSourceLocked(MicBlockSource source)
    {
        if (_activeSource == source) return;
        _activeSource = source;
        _idlePreRoll.Clear();
        _voiceOnset.Clear();
        _idlePreRollSamples = 0;
        _voiceOnsetSamples = 0;
        _preKeyZeroRemainder = 0;
        _voiceOnsetArmed = false;
            // Quiesce: drop any partially-accumulated old-source audio so it
            // can't stitch onto the post-switch source mid-WDSP-block.
        _accumulatorFill = 0;
    }

    internal void BeginVoiceOnsetBuffer()
    {
        lock (_sync)
        {
            _voiceOnset.Clear();
            _voiceOnsetSamples = 0;
            _preKeyZeroRemainder = 0;
            while (_idlePreRoll.TryDequeue(out var block))
            {
                if (!IsBufferedCurrent(block.Validity)) continue;
                _voiceOnset.Enqueue(block);
                _voiceOnsetSamples += block.Samples.Length;
            }
            _idlePreRollSamples = 0;
            _voiceOnsetArmed = true;
        }
    }

    internal void CancelVoiceOnsetBuffer()
    {
        lock (_sync)
        {
            _voiceOnsetArmed = false;
            _voiceOnset.Clear();
            _idlePreRoll.Clear();
            _voiceOnsetSamples = 0;
            _idlePreRollSamples = 0;
            _preKeyZeroRemainder = 0;
        }
    }

    private void SendPreKeyZeroIq(int micSamples)
    {
        var engine = _engineProvider();
        int blockSize = engine?.TxBlockSamples ?? 0;
        int iqOut = engine?.TxOutputSamples ?? 0;
        if (blockSize <= 0 || iqOut <= 0) return;

        // Preserve the source's 48 kHz cadence even for ASIO chunks smaller
        // than 960 samples. No WDSP mic block is processed here, so buffered
        // speech cannot be consumed by the RF mute window or be preceded by
        // a partially accumulated silent mic block when replay starts.
        int scaled = micSamples * iqOut + _preKeyZeroRemainder;
        int pairs = scaled / blockSize;
        _preKeyZeroRemainder = scaled % blockSize;
        int maxPairs = _muteIq.Length / 2;
        while (pairs > 0)
        {
            int chunk = Math.Min(pairs, maxPairs);
            _ring.Write(new ReadOnlySpan<float>(_muteIq, 0, 2 * chunk));
            _forwardP2?.Invoke(new ReadOnlyMemory<float>(_muteIq, 0, 2 * chunk));
            pairs -= chunk;
        }
    }

    private int DrainVoiceOnsetPaced(long frequency, Func<bool>? shouldAbort, Action<int, Func<bool>?>? wait = null)
    {
        lock (_sync) _voiceOnsetArmed = false;
        long paceAt = _stopwatchTicks();
        int drained = 0;
        while (true)
        {
            if (shouldAbort?.Invoke() == true) break;
            (float[] Samples, MicBlockSource Source, MicBlockValidity? Validity) block;
            lock (_sync)
            {
                if (!_voiceOnset.TryDequeue(out block)) break;
                _voiceOnsetSamples -= block.Samples.Length;
                if (IsBufferedCurrent(block.Validity))
                    ProcessMicPcm(block.Samples, block.Source, block.Validity,
                        bypassOnset: true, tailReplay: true);
            }
            drained++;
            paceAt += (long)(frequency * (double)block.Samples.Length / TxRateHz);
            long waitTicks = paceAt - _stopwatchTicks();
            if (waitTicks > 0)
                (wait ?? SleepUnlessAborted)((int)Math.Ceiling(waitTicks * 1000.0 / frequency), shouldAbort);
        }
        return drained;
    }

    private bool HoldOrReplayVoiceOnset(
        ReadOnlySpan<float> samples, MicBlockSource source, MicBlockValidity? validity,
        bool moxNow)
    {
        lock (_sync)
        {
            if (source is not (MicBlockSource.Host or MicBlockSource.BrowserMic or MicBlockSource.RadioMic))
                return false;
            if ((source == MicBlockSource.BrowserMic && _activeSource == MicBlockSource.VirtualCable)
                || (source != MicBlockSource.BrowserMic && source != _activeSource))
                return false;

            if (!_voiceOnsetArmed)
            {
                if (!moxNow && IsBufferedCurrent(validity))
                {
                    if (_idlePreRoll.TryPeek(out var prior) && prior.Source != source)
                    {
                        _idlePreRoll.Clear();
                        _idlePreRollSamples = 0;
                    }
                    while (_idlePreRollSamples + samples.Length > IdlePreRollSamples
                           && _idlePreRoll.TryDequeue(out var old))
                        _idlePreRollSamples -= old.Samples.Length;
                    _idlePreRoll.Enqueue((samples.ToArray(), source, validity));
                    _idlePreRollSamples += samples.Length;
                }
                return false;
            }

            if (!IsBufferedCurrent(validity)) return true;
            if (_voiceOnset.TryPeek(out var previous) && previous.Source != source)
            {
                _voiceOnset.Clear();
                _voiceOnsetSamples = 0;
            }
            long openAt = _preKeyOpenAtTicks();
            bool gateOpen = moxNow && !TxService.IsPreKeyMuteOpen(openAt, _stopwatchTicks());
            if (gateOpen)
            {
                if (!_voiceOnset.TryDequeue(out var oldest)) return false;
                _voiceOnsetSamples -= oldest.Samples.Length;
                // Replace exactly one queued frame with this arrival, then
                // replay exactly one. A full queue does not shed an extra
                // opening frame merely because the gate just opened.
                _voiceOnset.Enqueue((samples.ToArray(), source, validity));
                _voiceOnsetSamples += samples.Length;
                if (IsBufferedCurrent(oldest.Validity))
                    ProcessMicPcm(oldest.Samples, oldest.Source, oldest.Validity,
                        bypassOnset: true);
                return true;
            }

            while (_voiceOnsetSamples + samples.Length > MaxVoiceOnsetSamples
                   && _voiceOnset.TryDequeue(out var old))
            {
                _voiceOnsetSamples -= old.Samples.Length;
                _log.LogWarning("tx.voice.onset bounded queue overflow; oldest {Samples} samples dropped",
                    old.Samples.Length);
            }
            _voiceOnset.Enqueue((samples.ToArray(), source, validity));
            _voiceOnsetSamples += samples.Length;
            if (moxNow) SendPreKeyZeroIq(samples.Length);
            return true;
        }
    }

    /// <summary>Sources whose audio is operator speech (or a recorded voice
    /// message) and may carry a CW ID. TCI and the virtual cable are digital-mode
    /// feeds, and a speech-bypassed product-plugin lease is linear digital audio
    /// (FT8); a tone summed into those would corrupt the data signal.</summary>
    private bool CarriesCwId(MicBlockSource source) => source switch
    {
        MicBlockSource.Host or MicBlockSource.BrowserMic or MicBlockSource.RadioMic => true,
        MicBlockSource.ProductPlugin => Volatile.Read(ref _productPluginSpeechBypassGeneration) == 0,
        _ => false,
    };

    private bool _instantCwIdReserved;
    internal bool ReserveInstantCwId()
    {
        lock (_sync)
        {
            if (_instantCwIdReserved || _tailDraining != 0 || _audioModem.Active) return false;
            var engine = _engineProvider();
            if (engine is null || engine.TxBlockSamples <= 0 || engine.TxOutputSamples <= 0
                || engine.TxBlockSamples > _tailMic.Length || engine.TxOutputSamples > _tailIq.Length / 2) return false;
            _instantCwIdReserved = true;
            _voiceOnsetArmed = false;
            _voiceOnset.Clear();
            _voiceOnsetSamples = 0;
            _accumulatorFill = 0;
            _ring.Clear();
            return true;
        }
    }

    internal void ReleaseInstantCwId()
    {
        lock (_sync) _instantCwIdReserved = false;
    }

    /// <summary>Current armed source for the host/radio gate (test/diagnostic).</summary>
    internal MicBlockSource ActiveSource { get { lock (_sync) return _activeSource; } }

    /// <summary>
    /// True when a TCI client is the authoritative source of TX audio right now
    /// (within the hysteresis window). Used by the TX audio plugin bridge to
    /// bypass the operator's insert plugins (EQ, comp, VSTs, etc.) for remote
    /// sources — their audio has already been processed on the client side.
    /// Local mic and WAV playback are never considered "remote" for this check.
    /// </summary>
    internal bool IsTciTxAudioActive
    {
        get
        {
            long last = Volatile.Read(ref _lastTciTickMs);
            return last != 0 && (Environment.TickCount64 - last) < TciHysteresisMs;
        }
    }
    // Diagnostic: log peak of mic-in and IQ-out once per second of TX. If
    // mic-peak is high but iq-peak is ~0, WDSP TXA is producing silence
    // despite good input. If mic-peak itself is ~0, the uplink is broken.
    private DateTime _lastPeakLogUtc;
    private float _peakMicAccum;
    private float _peakIqAccum;
    private int _peakBlocksAccum;

    private readonly CwIdService? _cwId;

    // FM access tone burst (see FmToneBurstGenerator for the level
    // reasoning). Armed by TxService, rendered in place of the mic on the hot
    // path below. Both fields are read/written only under _sync.
    private readonly FmToneBurstGenerator _fmBurst = new(TxRateHz);
    private bool _fmBurstBypassApplied;

    /// <summary>Arm an FM tone burst for the current over. It starts on the
    /// next keyed voice block (after any pre-key mute) and replaces the mic
    /// for its duration. Never keys the transmitter.</summary>
    internal void ArmFmToneBurst(double toneHz, int durationMs, double micGainDb)
    {
        lock (_sync) _fmBurst.Arm(toneHz, durationMs, micGainDb);
        _log.LogInformation(
            "tx.fm.burst armed freqHz={Freq:F0} durationMs={Ms} micGainDb={Mic:F1} amplitude={Amp:F3}",
            toneHz, durationMs, micGainDb, _fmBurst.Amplitude);
    }

    /// <summary>Stop any pending / playing burst and restore the speech
    /// processors it bypassed. Called on every release.</summary>
    internal void CancelFmToneBurst()
    {
        var engine = _engineProvider();
        lock (_sync) CancelFmToneBurstLocked(engine);
    }

    internal bool IsFmToneBurstActive { get { lock (_sync) return _fmBurst.IsActive; } }

    // Caller holds _sync.
    private void CancelFmToneBurstLocked(IDspEngine? engine)
    {
        _fmBurst.Cancel();
        if (!_fmBurstBypassApplied) return;
        _fmBurstBypassApplied = false;
        try { engine?.SetTxRogerBeepBypass(false); }
        catch (Exception ex) { _log.LogWarning(ex, "tx.fm.burst bypass restore threw"); }
    }

    public TxAudioIngest(
        TxIqRing ring,
        DspPipelineService pipeline,
        TxService tx,
        StreamingHub hub,
        ILogger<TxAudioIngest> log,
        IAudioModemPort? audioModem = null,
        IProductTxAudioPort? productAudio = null,
        ProductPluginAudioPort? productPluginAudio = null,
        CwIdService? cwId = null)
        : this(ring, () => pipeline.CurrentEngine, () => tx.IsMoxOn, hub, log,
               forwardP2: iq => pipeline.ForwardTxIqToP2(iq.Span),
               drainTxTransport: pipeline.DrainTxIqTransportTail,
               txOwnedByTuneDriver: () => !tx.IsMicIqProducerAllowed,
               preKeyOpenAtTicks: () => tx.PreKeyOpenAtTicks,
               txTransportLevel: pipeline.TxTransportLevel48k,
               setP2TimingExact: pipeline.SetTxIqTimingExact,
               isVoiceTxMode: () => tx.IsVoiceTxModeNow,
               voiceDspLatencyMs: pipeline.EstimateVoiceTxDspLatencyMs,
               audioModem: audioModem,
               productAudio: productAudio,
               productPluginAudio: productPluginAudio,
               cwId: cwId)
    {
    }

    /// <summary>Test-only constructor that wires the engine + MOX lookups
    /// through plain delegates so unit tests don't need a live pipeline.
    /// <paramref name="forwardP2"/> is called with the same IQ block that's
    /// handed to the P1 ring so mic MOX on a Protocol 2 radio (G2 MkII) has
    /// a TX path. Null in tests that don't exercise the P2 forward.</summary>
    internal TxAudioIngest(
        TxIqRing ring,
        Func<IDspEngine?> engineProvider,
        Func<bool> isMoxOn,
        StreamingHub hub,
        ILogger<TxAudioIngest> log,
        Action<ReadOnlyMemory<float>>? forwardP2 = null,
        Func<TimeSpan, bool>? drainTxTransport = null,
        Action<int>? onWdspConsumed = null,
        Func<bool>? txOwnedByTuneDriver = null,
        Func<long>? preKeyOpenAtTicks = null,
        Func<long>? stopwatchTicks = null,
        IAudioModemPort? audioModem = null,
        IProductTxAudioPort? productAudio = null,
        ProductPluginAudioPort? productPluginAudio = null,
        CwIdService? cwId = null,
        Func<TxTransportLevel>? txTransportLevel = null,
        Func<bool>? isVoiceTxMode = null,
        Func<int>? voiceDspLatencyMs = null,
        Func<int>? voiceRadioFifoGuardMs = null,
        Action<bool>? setP2TimingExact = null,
        Action<int, Func<bool>?>? voiceTailWait = null)
    {
        _ring = ring;
        _txTransportLevel = txTransportLevel ?? (static () => TxTransportLevel.Unknown);
        _isVoiceTxMode = isVoiceTxMode ?? (static () => true);
        _cwId = cwId;
        _engineProvider = engineProvider;
        _isMoxOn = isMoxOn;
        _audioModem = audioModem ?? new NullAudioModemPort();
        _productAudio = productAudio ?? new NullProductTxAudioPort();
        _productPluginAudio = productPluginAudio;
        _forwardP2 = forwardP2;
        _setP2TimingExact = setP2TimingExact;
        _drainTxTransport = drainTxTransport;
        _voiceDspLatencyMs = voiceDspLatencyMs ?? (static () => 0);
        _voiceRadioFifoGuardMs = voiceRadioFifoGuardMs
            ?? (static () => VoiceTailRadioFifoGuardMs);
        _onWdspConsumed = onWdspConsumed;
        _txOwnedByTuneDriver = txOwnedByTuneDriver ?? (static () => false);
        _preKeyOpenAtTicks = preKeyOpenAtTicks ?? (static () => 0L);
        _stopwatchTicks = stopwatchTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _voiceTailWait = voiceTailWait ?? SleepUnlessAborted;
        _hub = hub;
        _log = log;
        _handler = OnMicPcmBytesFromBrowserMic;
        _hub.MicPcmReceived += _handler;
        _productPluginAudio?.ConfigureTxSink(OnProductPluginTxAudio);
        _productPluginAudio?.ConfigureTxActiveSink(SetProductPluginInjectionActive);
        _productPluginAudio?.ConfigureTxSpeechBypassSink(SetProductPluginSpeechBypass);
    }

    /// <summary>
    /// FreeDV end-of-over TX tail. Called by <see cref="TxService"/> on a genuine
    /// un-key, BEFORE the wire MOX bit drops and while the WDSP TXA is still up.
    /// Completes the final modem frame (RADE also appends its EOO callsign), then
    /// clocks the queued modem audio out to the radio at the DAC rate so the
    /// receiver gets WHOLE OFDM frames instead of a mid-symbol cut — the root
    /// cause of end-of-over garble. Blocks the caller for the bounded tail
    /// duration, then returns so PTT can drop. No-op unless FreeDV is engaged and
    /// the engine is a real (TXA-capable) backend. Sets <see cref="_tailDraining"/>
    /// so the mic hot path yields TX exclusively (no double-feed into fexchange2),
    /// mirroring the tune-driver handoff. Runs on the un-key (API) thread; the
    /// mic ingest runs on the audio thread — the _sync barrier below quiesces any
    /// in-flight mic block before the drain feeds the ring.
    /// </summary>
    public void DrainFreeDvTxTail()
    {
        if (!_audioModem.Active) return;
        if (_txOwnedByTuneDriver()) return;        // TUN/two-tone owns TX
        var engine = _engineProvider();
        int blockSize = engine?.TxBlockSamples ?? 0;
        int iqOut = engine?.TxOutputSamples ?? 0;
        if (engine is null || blockSize <= 0 || iqOut <= 0
            || blockSize > _tailMic.Length || 2 * iqOut > _tailIq.Length)
            return;

        // Claim TX exclusively (CAS — bail if another tail drain is already
        // running). Then take _sync as a barrier so any mic critical section
        // already in flight finishes before we feed the ring.
        if (Interlocked.CompareExchange(ref _tailDraining, 1, 0) != 0) return;
        lock (_sync) { _accumulatorFill = 0; }
        try
        {
            // Complete the final frame (residual + RADE EOO) so a well-formed last
            // OFDM symbol exists, and capture the queued 48 kHz backlog so the drain
            // budget can cover it — a fixed budget truncated a large tail on air.
            int pendingOut = _audioModem.FinishTx();
            double pendingMs = pendingOut * 1000.0 / TxRateHz;

            long freq = System.Diagnostics.Stopwatch.Frequency;
            long periodTicks = (long)(freq * (double)blockSize / TxRateHz);
            long startTs = System.Diagnostics.Stopwatch.GetTimestamp();
            long deadline = startTs;
            // Budget = the measured backlog + slack, bounded by the runaway ceiling.
            // Draining paces at the DAC rate, so this many ms of wall-time is what it
            // takes to clock the whole tail out; sizing it to the backlog (not a
            // fixed 350 ms) means the final data + EOO frame is never guillotined.
            double budgetMs = Math.Min(pendingMs + FreeDvTxTailDrainSlackMs, FreeDvTxTailCeilingMs);
            long hardStop = startTs + (long)(freq * (budgetMs / 1000.0));
            int idle = 0, drainedBlocks = 0;
            bool queueEmptied = false;
            while (System.Diagnostics.Stopwatch.GetTimestamp() < hardStop)
            {
                int pendingBefore = _audioModem.PendingTxSamples;
                _audioModem.ProcessTx(new Span<float>(_tailMic, 0, blockSize));
                int real = Math.Max(0, pendingBefore - _audioModem.PendingTxSamples);
                // DrainTx silence-pads a short block; once the queue is empty
                // (two fully-silent blocks) stop so we don't key dead carrier.
                if (real == 0) { if (++idle >= 2) { queueEmptied = true; break; } }
                else idle = 0;

                int produced = engine.ProcessTxBlock(
                    new ReadOnlySpan<float>(_tailMic, 0, blockSize),
                    new Span<float>(_tailIq, 0, 2 * iqOut));
                if (produced > 0)
                {
                    var iqSpan = new ReadOnlySpan<float>(_tailIq, 0, 2 * produced);
                    _ring.Write(iqSpan);                                   // P1 EP2 packer
                    _forwardP2?.Invoke(new ReadOnlyMemory<float>(_tailIq, 0, 2 * produced)); // P2 DUC
                    drainedBlocks++;
                }

                // Pace at the DAC rate so the radio FIFO transmits as we feed it.
                deadline += periodTicks;
                long remaining = deadline - System.Diagnostics.Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    int ms = (int)(remaining * 1000 / freq);
                    if (ms > 0) Thread.Sleep(ms);
                }
                else deadline = System.Diagnostics.Stopwatch.GetTimestamp(); // re-anchor if behind
            }

            double drainMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTs) * 1000.0 / freq;

            // Hold long enough for the radio ring/FIFO to finish the last blocks
            // before PTT drops (bench-tunable on the G2).
            Thread.Sleep(FreeDvTxTailGuardMs);
            // On-air evidence for the end-of-over garble: term=drained means the
            // whole tail clocked out (any residual clip is FIFO latency → raise the
            // guard); term=ceiling means the backlog outran the budget (raise the
            // ceiling). pendingMs is the true tail length.
            _log.LogInformation(
                "freedv.tx.tail dropping PTT: pendingMs={Pending:F0} blocks={Blocks} drainMs={Drain:F0} term={Term} guardMs={Guard}",
                pendingMs, drainedBlocks, drainMs, queueEmptied ? "drained" : "ceiling", FreeDvTxTailGuardMs);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "freedv.tx.tail drain threw");
        }
        finally
        {
            // Mirror the old MOX-falling-edge cleanup so the next over starts
            // clean. The ring is already drained (we paced + guarded), so Clear
            // only removes any safety remainder. _lastSeenMox=false stops the
            // audio thread re-running the falling-edge clear after _moxOn flips.
            lock (_sync)
            {
                _ring.Clear();
                _accumulatorFill = 0;
                SetRecordingBypassLocked(null, false);
                _lastSeenMox = false;
            }
            _audioModem.FlushTx();
            Volatile.Write(ref _tailDraining, 0);
        }
    }

    /// <summary>
    /// Voice-mode end-of-over TX tail. The operator-configured delay is a wire
    /// MOX hold, not just a sleep: while the hold is active, allow late mic
    /// frames to arrive for a bounded portion, then quiesce the mic hot path,
    /// pad any partial WDSP accumulator to one full TX block, push that IQ to
    /// the P1/P2 transport, and wait out the remainder of the configured hold.
    /// Without the pad/flush step, a final &lt;TXA block-size syllable can sit in
    /// <see cref="_accumulator"/> forever and still be cut off even though MOX
    /// was held.
    /// </summary>
    public bool DrainVoiceTxTail(int tailDelayMs, Func<bool>? shouldAbort = null)
    {
        if (_audioModem.Active) return false;
        if (_txOwnedByTuneDriver()) return false;

        var engine = _engineProvider();
        int blockSize = engine?.TxBlockSamples ?? 0;
        int iqOut = engine?.TxOutputSamples ?? 0;
        if (engine is null || blockSize <= 0 || iqOut <= 0
            || blockSize > _tailMic.Length || 2 * iqOut > _tailIq.Length)
            return false;

        long freq = System.Diagnostics.Stopwatch.Frequency;
        long startTs = _stopwatchTicks();
        long deadline = startTs + (long)(freq * (tailDelayMs / 1000.0));

        // Keep ingress open for the first third of the configured hold (at
        // least one mic frame) so delayed capture/browser frames remain
        // eligible, reserving the rest for the WDSP flush and transport drain.
        var plan = PlanVoiceTail(tailDelayMs, _voiceDspLatencyMs(), blockSize, _voiceRadioFifoGuardMs());
        int settleMs = plan.SettleMs;
        _voiceTailWait(settleMs, shouldAbort);
        if (shouldAbort?.Invoke() == true) return false;

        lock (_sync)
        {
            if (Interlocked.CompareExchange(ref _tailDraining, 1, 0) != 0) return false;
            _preserveOnsetAccumulatorDuringTail = true;
        }

        int residualSamples = 0;
        int produced = 0;
        int onsetDrained = 0;
        bool transportDrained = true;
        try
        {
            onsetDrained = DrainVoiceOnsetPaced(freq, shouldAbort, _voiceTailWait);
            if (shouldAbort?.Invoke() == true) return false;

            lock (_sync)
            {
                residualSamples = _accumulatorFill;
                if (residualSamples > 0)
                {
                    Array.Clear(_tailMic, 0, blockSize);
                    Array.Copy(_accumulator, 0, _tailMic, 0, residualSamples);
                    _accumulatorFill = 0;
                }
            }

            if (residualSamples > 0)
            {
                produced = engine.ProcessTxBlock(
                    new ReadOnlySpan<float>(_tailMic, 0, blockSize),
                    new Span<float>(_tailIq, 0, 2 * iqOut));
                if (produced > 0)
                {
                    var iqSpan = new ReadOnlySpan<float>(_tailIq, 0, 2 * produced);
                    _ring.Write(iqSpan);
                    _forwardP2?.Invoke(new ReadOnlyMemory<float>(_tailIq, 0, 2 * produced));
                    lock (_sync) _totalTxBlocks++;
                    var onConsumed = Volatile.Read(ref _onWdspConsumed);
                    onConsumed?.Invoke(blockSize);
                }
            }

            // A transport queue can be empty while the final syllable is still
            // inside TXA's FIR/lookahead stages. Advance TXA with real-time
            // silence before testing for quiet output. The initial output can
            // itself be silent while a delayed syllable is still on its way.
            int dspLatencyMs = plan.DspLatencyMs;
            int minFlushBlocks = plan.MinFlushBlocks;
            int maxFlushBlocks = plan.MaxFlushBlocks;
            long periodTicks = (long)(freq * (double)blockSize / TxRateHz);
            long maxLagTicks = freq * VoiceTailMaxCatchUpMs / 1000;
            int quietBlocks = 0;
            int flushBlocks = 0;
            long paceAt = _stopwatchTicks();
            Array.Clear(_tailMic, 0, blockSize);
            float iqPeak = 0f;
            for (int b = 0; b < maxFlushBlocks; b++)
            {
                if (shouldAbort?.Invoke() == true) return false;
                ClockTailBlock(engine, blockSize, iqOut, ref iqPeak, out float blockPeak);
                flushBlocks++;
                quietBlocks = b >= minFlushBlocks && blockPeak <= RogerBeepFlushIqThreshold
                    ? quietBlocks + 1
                    : 0;
                if (quietBlocks >= VoiceTailQuietBlocks) break;
                long now = _stopwatchTicks();
                paceAt = AdvanceFlushPace(paceAt, now, periodTicks, maxLagTicks);
                if (paceAt > now)
                    _voiceTailWait((int)Math.Ceiling((paceAt - now) * 1000.0 / freq), shouldAbort);
            }

            if (shouldAbort?.Invoke() == true) return false;
            int backlogSamples = _txTransportLevel().BacklogSamples;
            int transportBudgetMs = Math.Clamp(
                backlogSamples < 0 ? 5000
                    : backlogSamples / TxLatencyGovernor.SamplesPerMs + 250,
                RogerBeepTransportDrainTimeoutMs, 5000);
            long transportStop = _stopwatchTicks()
                + (long)(freq * transportBudgetMs / 1000.0);
            do
            {
                if (shouldAbort?.Invoke() == true) return false;
                transportDrained = _drainTxTransport?.Invoke(TimeSpan.FromMilliseconds(10)) ?? true;
            } while (!transportDrained && _stopwatchTicks() < transportStop);
            if (!transportDrained)
                _log.LogWarning("tx.voice.tail transport did not drain before bounded timeout budgetMs={Budget}", transportBudgetMs);
            // The radio FIFO can still hold the last emitted IQ after the
            // host queue goes idle: hold for its depth, then honor the
            // operator's requested hold as a minimum before unkey.
            int guardMs = plan.GuardMs;
            _voiceTailWait(guardMs, shouldAbort);
            if (shouldAbort?.Invoke() == true) return false;
            long holdRemaining = deadline - _stopwatchTicks();
            if (holdRemaining > 0)
                _voiceTailWait((int)Math.Ceiling(holdRemaining * 1000.0 / freq), shouldAbort);
            if (shouldAbort?.Invoke() == true) return false;

            double elapsedMs = (_stopwatchTicks() - startTs)
                * 1000.0 / freq;
            _log.LogInformation(
                "tx.voice.tail dropping PTT: delayMs={Delay} settleMs={Settle} onsetBlocks={OnsetBlocks} residualSamples={Residual} produced={Produced} dspLatencyMs={DspLatency} flushBlocks={FlushBlocks} dspFlushed={DspFlushed} transportDrained={TransportDrained} guardMs={Guard} elapsedMs={Elapsed:F1}",
                tailDelayMs, settleMs, onsetDrained, residualSamples, produced, dspLatencyMs, flushBlocks,
                quietBlocks >= VoiceTailQuietBlocks, transportDrained, guardMs, elapsedMs);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tx.voice.tail drain threw");
            return false;
        }
        finally
        {
            lock (_sync)
            {
                _voiceOnsetArmed = false;
                _voiceOnset.Clear();
                _voiceOnsetSamples = 0;
                _preserveOnsetAccumulatorDuringTail = false;
                _ring.Clear();
                _accumulatorFill = 0;
                SetRecordingBypassLocked(null, false);
                _lastSeenMox = false;
            }
            Volatile.Write(ref _tailDraining, 0);
        }
    }

    /// <summary>
    /// Clock a short voice-mode roger beep through the normal TX chain before
    /// PTT drops. Called by <see cref="TxService"/> after an accepted local
    /// MOX release while the wire MOX bit is still asserted.
    /// </summary>
    public bool DrainRogerBeepTail()
    {
        if (_audioModem.Active) return false;
        if (_txOwnedByTuneDriver()) return false;

        var engine = _engineProvider();
        int blockSize = engine?.TxBlockSamples ?? 0;
        int iqOut = engine?.TxOutputSamples ?? 0;
        if (engine is null || blockSize <= 0 || iqOut <= 0
            || blockSize > _tailMic.Length || 2 * iqOut > _tailIq.Length)
            return false;

        if (Interlocked.CompareExchange(ref _tailDraining, 1, 0) != 0) return false;
        lock (_sync) { _accumulatorFill = 0; }
        bool emitted = false;
        bool bypassApplied = false;
        try
        {
            bypassApplied = true;
            engine.SetTxRogerBeepBypass(true);
            int totalSamples = Math.Max(1, TxRateHz * RogerBeepDurationMs / 1000);
            double phase = 0.0;
            double phaseStep = 2.0 * Math.PI * RogerBeepFrequencyHz / TxRateHz;
            long freq = System.Diagnostics.Stopwatch.Frequency;
            long periodTicks = (long)(freq * (double)blockSize / TxRateHz);
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp();
            int sampleIndex = 0;
            int blocks = 0;
            float micPeak = 0f;
            float iqPeak = 0f;

            while (sampleIndex < totalSamples)
            {
                int active = Math.Min(blockSize, totalSamples - sampleIndex);
                Array.Clear(_tailMic, 0, blockSize);
                for (int i = 0; i < active; i++)
                {
                    float env = RogerBeepEnvelope(sampleIndex + i, totalSamples);
                    float sample = (float)(Math.Sin(phase) * RogerBeepMagnitude * env);
                    _tailMic[i] = sample;
                    float sampleAbs = sample;
                    if (sampleAbs < 0) sampleAbs = -sampleAbs;
                    if (sampleAbs > micPeak) micPeak = sampleAbs;
                    phase += phaseStep;
                    if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                }

                if (ClockTailBlock(engine, blockSize, iqOut, ref iqPeak, out _) > 0)
                {
                    emitted = true;
                    blocks++;
                }

                sampleIndex += active;
                deadline += periodTicks;
                if (!SleepToBlockDeadline(deadline, freq))
                {
                    deadline = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }

            var flush = FlushTxTailAndTransport(engine, blockSize, iqOut, periodTicks, freq, ref deadline, ref iqPeak);
            if (flush.EmittedBlocks > 0) emitted = true;
            blocks += flush.EmittedBlocks;
            _log.LogInformation(
                "tx.rogerBeep.tail dropping PTT: blocks={Blocks} flushBlocks={FlushBlocks} dspFlushed={DspFlushed} flushCeilingMs={FlushCeiling} durationMs={Duration} freqHz={Freq:F0} micPeak={MicPeak:F4} iqPeak={IqPeak:F4} transportDrained={TransportDrained} transportBudgetMs={TransportBudget} guardMs={Guard}",
                blocks, flush.FlushBlocks, flush.DspFlushed, RogerBeepDspFlushCeilingMs,
                RogerBeepDurationMs, RogerBeepFrequencyHz, micPeak, iqPeak,
                flush.TransportDrained, RogerBeepTransportDrainTimeoutMs, RogerBeepTailGuardMs);
            return emitted;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tx.rogerBeep.tail drain threw");
            return false;
        }
        finally
        {
            if (bypassApplied)
            {
                try { engine.SetTxRogerBeepBypass(false); }
                catch (Exception ex) { _log.LogWarning(ex, "tx.rogerBeep.bypass restore threw"); }
            }
            lock (_sync)
            {
                _ring.Clear();
                _accumulatorFill = 0;
                SetRecordingBypassLocked(null, false);
                _lastSeenMox = false;
            }
            Volatile.Write(ref _tailDraining, 0);
        }
    }

    /// <summary>
    /// Finish a CW station ID that was on the air when the operator un-keyed.
    /// Called by <see cref="TxService"/> on an accepted MOX release while the
    /// wire MOX bit is still asserted, so the ID goes out once, complete,
    /// instead of being cut and resent on every following over. The operator's
    /// last partial mic block goes out first (with the ID continuing across
    /// it), then the rest of the ID is clocked at real time through the normal
    /// TX chain, and the TXA/transport tail is flushed before the key drops.
    /// <paramref name="shouldAbort"/> is polled every block: an emergency
    /// release (trip, disconnect) stops the hold at once and the partial ID
    /// stays due. <paramref name="onHold"/> runs once the hold is committed,
    /// before the first ID block, so the caller can tell clients the key is
    /// still down. Returns true when the hold ran (TX was held for the ID).
    /// </summary>
    public bool DrainCwIdTail(Func<bool>? shouldAbort = null, Action? onHold = null)
    {
        if (_cwId is not { } cwId || !cwId.IsSending) return false;
        if (_audioModem.Active) return false;
        if (_txOwnedByTuneDriver()) return false;

        var engine = _engineProvider();
        int blockSize = engine?.TxBlockSamples ?? 0;
        int iqOut = engine?.TxOutputSamples ?? 0;
        if (engine is null || blockSize <= 0 || iqOut <= 0
            || blockSize > _tailMic.Length || 2 * iqOut > _tailIq.Length)
            return false;

        // Barrier: the mic hot path checks _tailDraining under _sync, so from
        // here this thread is the only TXA feeder. The flag is raised under
        // _sync so a late live mic frame cannot discard the accumulator
        // while held onset frames are replayed and the final partial block
        // is captured. The ID continues across that block without a gap.
        int residualSamples = 0;
        lock (_sync)
        {
            if (Interlocked.CompareExchange(ref _tailDraining, 1, 0) != 0) return false;
            _preserveOnsetAccumulatorDuringTail = true;
        }
        long freq = System.Diagnostics.Stopwatch.Frequency;
        long startTs = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            try { onHold?.Invoke(); }
            catch (Exception ex) { _log.LogWarning(ex, "tx.cwid.tail hold notification threw"); }

            // Finish voice already held by the UI key transaction before the
            // station ID starts. The CW-ID tail otherwise skips the ordinary
            // voice tail and would discard these final queued syllables.
            int onsetDrained = _instantCwIdReserved ? 0 : DrainVoiceOnsetPaced(freq, shouldAbort);
            if (shouldAbort?.Invoke() == true) return false;
            lock (_sync)
            {
                residualSamples = _instantCwIdReserved ? 0 : Math.Min(_accumulatorFill, blockSize);
                Array.Clear(_tailMic, 0, blockSize);
                if (residualSamples > 0)
                    Array.Copy(_accumulator, 0, _tailMic, 0, residualSamples);
                _accumulatorFill = 0;
            }

            long periodTicks = (long)(freq * (double)blockSize / TxRateHz);
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp();
            int idBlocks = 0;
            int blocks = 0;
            float iqPeak = 0f;
            bool aborted = false;
            while (true)
            {
                if (shouldAbort?.Invoke() == true) { aborted = true; break; }
                if (idBlocks > 0) Array.Clear(_tailMic, 0, blockSize);
                if (!cwId.MixTailBlock(new Span<float>(_tailMic, 0, blockSize))) break;
                idBlocks++;
                blocks += ClockTailBlock(engine, blockSize, iqOut, ref iqPeak, out _);
                deadline += periodTicks;
                if (!SleepToBlockDeadline(deadline, freq))
                    deadline = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            if (aborted || shouldAbort?.Invoke() == true)
            {
                _log.LogWarning(
                    "tx.cwid.tail aborted by emergency release: idBlocks={IdBlocks} elapsedMs={Elapsed:F1}",
                    idBlocks, (System.Diagnostics.Stopwatch.GetTimestamp() - startTs) * 1000.0 / freq);
                return idBlocks > 0;
            }

            var flush = FlushTxTailAndTransport(
                engine, blockSize, iqOut, periodTicks, freq, ref deadline, ref iqPeak, shouldAbort);
            _log.LogInformation(
                "tx.cwid.tail dropping PTT: idBlocks={IdBlocks} onsetBlocks={OnsetBlocks} residualSamples={Residual} blocks={Blocks} flushBlocks={FlushBlocks} dspFlushed={DspFlushed} iqPeak={IqPeak:F4} transportDrained={TransportDrained} emergencyCut={EmergencyCut} elapsedMs={Elapsed:F1}",
                idBlocks, onsetDrained, residualSamples, blocks + flush.EmittedBlocks, flush.FlushBlocks, flush.DspFlushed,
                iqPeak, flush.TransportDrained, shouldAbort?.Invoke() == true,
                (System.Diagnostics.Stopwatch.GetTimestamp() - startTs) * 1000.0 / freq);
            return idBlocks > 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tx.cwid.tail drain threw");
            return false;
        }
        finally
        {
            lock (_sync)
            {
                _voiceOnsetArmed = false;
                _voiceOnset.Clear();
                _voiceOnsetSamples = 0;
                _preserveOnsetAccumulatorDuringTail = false;
                _ring.Clear();
                _accumulatorFill = 0;
                SetRecordingBypassLocked(null, false);
                _lastSeenMox = false;
            }
            Volatile.Write(ref _tailDraining, 0);
        }
    }

    /// <summary>Run <see cref="_tailMic"/> through TXA and publish the IQ to
    /// both transports. Returns 1 when IQ was produced, else 0;
    /// <paramref name="blockPeak"/> is this block's IQ peak.</summary>
    private int ClockTailBlock(IDspEngine engine, int blockSize, int iqOut, ref float iqPeak, out float blockPeak)
    {
        blockPeak = 0f;
        int produced = engine.ProcessTxBlock(
            new ReadOnlySpan<float>(_tailMic, 0, blockSize),
            new Span<float>(_tailIq, 0, 2 * iqOut));
        if (produced <= 0) return 0;
        var iqSpan = new ReadOnlySpan<float>(_tailIq, 0, 2 * produced);
        for (int i = 0; i < iqSpan.Length; i++)
        {
            float sampleAbs = iqSpan[i];
            if (sampleAbs < 0) sampleAbs = -sampleAbs;
            if (sampleAbs > blockPeak) blockPeak = sampleAbs;
        }
        if (blockPeak > iqPeak) iqPeak = blockPeak;
        _ring.Write(iqSpan);
        _forwardP2?.Invoke(new ReadOnlyMemory<float>(_tailIq, 0, 2 * produced));
        return 1;
    }

    /// <summary>
    /// Shared end of a synthesized release tail (roger beep, CW ID): clock
    /// silence through TXA until its delayed output decays (two quiet blocks,
    /// bounded by <see cref="RogerBeepDspFlushCeilingMs"/>), let the P1/P2
    /// transport drain, then hold a short guard so the last IQ reaches the
    /// radio before the wire MOX bit drops. <paramref name="shouldAbort"/>
    /// (an emergency release waiting on the key) ends the flush at the next
    /// block and skips the transport drain and guard.
    /// </summary>
    private TxTailFlush FlushTxTailAndTransport(
        IDspEngine engine, int blockSize, int iqOut, long periodTicks, long freq,
        ref long deadline, ref float iqPeak, Func<bool>? shouldAbort = null)
    {
        Array.Clear(_tailMic, 0, blockSize);
        int maxFlushBlocks = Math.Max(
            1,
            TxRateHz * RogerBeepDspFlushCeilingMs / 1000 / blockSize);
        int silentBlocks = 0;
        int flushBlocks = 0;
        int emittedBlocks = 0;
        for (int b = 0; b < maxFlushBlocks; b++)
        {
            if (shouldAbort?.Invoke() == true)
                return new TxTailFlush(emittedBlocks, flushBlocks, DspFlushed: false, TransportDrained: false);
            emittedBlocks += ClockTailBlock(engine, blockSize, iqOut, ref iqPeak, out float blockPeak);
            flushBlocks++;
            silentBlocks = blockPeak <= RogerBeepFlushIqThreshold
                ? silentBlocks + 1
                : 0;
            deadline += periodTicks;
            if (!SleepToBlockDeadline(deadline, freq))
            {
                deadline = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            if (silentBlocks >= RogerBeepFlushSilentBlocks) break;
        }
        bool dspFlushed = silentBlocks >= RogerBeepFlushSilentBlocks;
        if (shouldAbort?.Invoke() == true)
            return new TxTailFlush(emittedBlocks, flushBlocks, dspFlushed, TransportDrained: false);

        bool transportDrained = _drainTxTransport?.Invoke(
            TimeSpan.FromMilliseconds(RogerBeepTransportDrainTimeoutMs)) ?? true;
        SleepUnlessAborted(RogerBeepTailGuardMs, shouldAbort);
        return new TxTailFlush(emittedBlocks, flushBlocks, dspFlushed, transportDrained);
    }

    private const int TailAbortPollMs = 10;

    /// <summary>
    /// Next flush deadline after clocking one block. A block that woke late is
    /// caught up (the next one is clocked without sleeping) so timer overshoot
    /// never accumulates; only a stall longer than
    /// <paramref name="maxLagTicks"/> re-anchors the pace to now.
    /// </summary>
    internal static long AdvanceFlushPace(long paceAt, long now, long periodTicks, long maxLagTicks)
    {
        long next = paceAt + periodTicks;
        return now - next > maxLagTicks ? now : next;
    }

    internal readonly record struct VoiceTailPlan(
        int SettleMs, int DspLatencyMs, int MinFlushBlocks, int MaxFlushBlocks, int GuardMs);

    /// <summary>
    /// Fixed costs of a voice release. The flush covers the speech still inside
    /// TXA (whole blocks at the 48 kHz input cadence) and then needs
    /// <see cref="VoiceTailQuietBlocks"/> quiet block; settle and guard are the
    /// only waits that do not carry speech.
    /// </summary>
    internal static VoiceTailPlan PlanVoiceTail(
        int tailDelayMs, int dspLatencyMs, int blockSize, int radioFifoGuardMs)
    {
        int settleMs = Math.Clamp(tailDelayMs / 3, VoiceTailSettleMinMs, VoiceTailSettleMaxMs);
        int latencyMs = Math.Clamp(dspLatencyMs, 0, 2000);
        double periodMs = blockSize * 1000.0 / TxRateHz;
        int minFlushBlocks = Math.Max(1, (int)Math.Ceiling(latencyMs / periodMs));
        int maxFlushBlocks = Math.Max(minFlushBlocks + VoiceTailQuietBlocks,
            (int)Math.Ceiling(Math.Min(5000, latencyMs + 500) / periodMs));
        return new VoiceTailPlan(settleMs, latencyMs, minFlushBlocks, maxFlushBlocks,
            Math.Clamp(radioFifoGuardMs, 0, 200));
    }

    /// <summary>Sleep <paramref name="ms"/>, returning early (within
    /// <see cref="TailAbortPollMs"/>) once <paramref name="shouldAbort"/> fires.</summary>
    private static void SleepUnlessAborted(int ms, Func<bool>? shouldAbort)
    {
        if (shouldAbort is null) { Thread.Sleep(ms); return; }
        long end = System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(System.Diagnostics.Stopwatch.Frequency * (ms / 1000.0));
        while (!shouldAbort())
        {
            long remaining = end - System.Diagnostics.Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            int step = (int)Math.Ceiling(remaining * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            Thread.Sleep(Math.Min(step, TailAbortPollMs));
        }
    }

    private readonly record struct TxTailFlush(
        int EmittedBlocks, int FlushBlocks, bool DspFlushed, bool TransportDrained);

    private static bool SleepToBlockDeadline(long deadlineTicks, long stopwatchFrequency)
    {
        long remaining = deadlineTicks - System.Diagnostics.Stopwatch.GetTimestamp();
        if (remaining <= 0) return false;
        int ms = (int)Math.Ceiling(remaining * 1000.0 / stopwatchFrequency);
        if (ms > 0) Thread.Sleep(ms);
        return true;
    }

    /// <summary>
    /// Prime WDSP TXA with silence immediately after MOX-on, before the radio
    /// wire key is asserted. Some TXA backends retain output from the previous
    /// tail; this clocks that state through and discards it so the next over
    /// starts with fresh mic audio, not the previous roger beep.
    /// </summary>
    public bool PrimeTxDspForKeyDown()
    {
        // Every key-down starts the latency governor from scratch, even when
        // the prime itself is skipped (FreeDV, TUN).
        lock (_sync) _latencyGovernor.Reset();
        if (_audioModem.Active) return false;
        if (_txOwnedByTuneDriver()) return false;

        var engine = _engineProvider();
        int blockSize = engine?.TxBlockSamples ?? 0;
        int iqOut = engine?.TxOutputSamples ?? 0;
        if (engine is null || blockSize <= 0 || iqOut <= 0
            || blockSize > _tailMic.Length || 2 * iqOut > _tailIq.Length)
            return false;

        if (Interlocked.CompareExchange(ref _tailDraining, 1, 0) != 0) return false;
        lock (_sync) { _accumulatorFill = 0; }
        int blocks = 0;
        float iqPeak = 0f;
        long startTs = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Array.Clear(_tailMic, 0, blockSize);
            for (int b = 0; b < KeyDownPrimeBlocks; b++)
            {
                int produced = engine.ProcessTxBlock(
                    new ReadOnlySpan<float>(_tailMic, 0, blockSize),
                    new Span<float>(_tailIq, 0, 2 * iqOut));
                if (produced <= 0) continue;

                var iqSpan = new ReadOnlySpan<float>(_tailIq, 0, 2 * produced);
                for (int i = 0; i < iqSpan.Length; i++)
                {
                    float sampleAbs = iqSpan[i];
                    if (sampleAbs < 0) sampleAbs = -sampleAbs;
                    if (sampleAbs > iqPeak) iqPeak = sampleAbs;
                }
                blocks++;
            }

            double primeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTs)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _log.LogInformation(
                "tx.keyDownPrime beforeWireKey blocks={Blocks} iqPeak={IqPeak:F4} elapsedMs={Elapsed:F2}",
                blocks, iqPeak, primeMs);
            return blocks > 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tx.keyDownPrime threw");
            return false;
        }
        finally
        {
            lock (_sync)
            {
                _ring.Clear();
                _accumulatorFill = 0;
                SetRecordingBypassLocked(null, false);
                _lastSeenMox = false;
            }
            Volatile.Write(ref _tailDraining, 0);
        }
    }

    private static float RogerBeepEnvelope(int sample, int totalSamples)
    {
        int fadeSamples = Math.Min(TxRateHz / 200, Math.Max(1, totalSamples / 4)); // <= 5 ms
        if (sample < fadeSamples) return sample / (float)fadeSamples;
        int remaining = totalSamples - sample - 1;
        if (remaining < fadeSamples) return Math.Max(0f, remaining / (float)fadeSamples);
        return 1f;
    }

    // FreeDV digital-voice modem coordinator. When FreeDV is the active mode,
    // mic speech is replaced (in place, pre-WDSP) with the transmitted modem
    // signal so WDSP's USB TXA modulates the modem audio onto the carrier.
    // Null in unit tests.
    private readonly IAudioModemPort _audioModem;
    private readonly IProductTxAudioPort _productAudio;
    private readonly ProductPluginAudioPort? _productPluginAudio;
    private IDspEngine? _recordingBypassEngine;
    private int _productPluginInjectionActive;
    private readonly object _productPluginSpeechBypassGate = new();
    private long _productPluginSpeechBypassGeneration;

    private readonly Action<ReadOnlyMemory<float>>? _forwardP2;
    private readonly Action<bool>? _setP2TimingExact;
    private readonly Func<TimeSpan, bool>? _drainTxTransport;
    private readonly Func<int> _voiceDspLatencyMs;
    private readonly Func<int> _voiceRadioFifoGuardMs;
    // Live-voice latency governor (see TxLatencyGovernor). Guarded by _sync.
    private readonly TxLatencyGovernor _latencyGovernor = new();
    private readonly Func<TxTransportLevel> _txTransportLevel;
    // Browser/mobile mic arrives over a network hop with no jitter buffer;
    // start its cushion wider than a local sound card's.
    private const int BrowserMicMinCushionSamples = 30 * TxLatencyGovernor.SamplesPerMs;
    private readonly Func<bool> _isVoiceTxMode;
    // True while TUN or the two-tone test is active. TxTuneDriver is the sole TX
    // driver in those states; this mic-ingest path must NOT also run ProcessTxBlock
    // or push IQ, or two threads drive the same TXA (fexchange2) and BOTH feed the
    // radio → double-fed / corrupted signal. Desktop-only symptom (#559): native
    // mic capture keeps feeding here during a two-tone; web has no native mic so
    // only TxTuneDriver drove it and it stayed clean.
    private readonly Func<bool> _txOwnedByTuneDriver;
    // Pre-key (MOX) delay window deadline in Stopwatch ticks, supplied by
    // TxService (issue #630). While Stopwatch.GetTimestamp() is below this
    // value AND the block is genuine live-mic IQ (not WAV-over-air playback),
    // we substitute silence for the modulated IQ so an external amp's T/R relay
    // settles before RF appears. 0 = no window (the default-0 setting, CW, TUN,
    // two-tone, TCI, hardware-PTT — all unset or cleared by TxService).
    private readonly Func<long> _preKeyOpenAtTicks;

    // Cross-thread handoff: written from the TCI timer thread (Start/Stop of
    // the TX_CHRONO service), read every audio block from the WDSP worker.
    // x86/TSO hides the missing fence, but Apple-Silicon / Pi-class ARM does
    // not. Mirror the Interlocked.Exchange pattern used for _txChronoTimer.
    internal void SetWdspConsumedCallback(Action<int>? cb)
        => Interlocked.Exchange(ref _onWdspConsumed, cb);

    /// <summary>Raised for every valid mic block (960 samples f32le @ 48 kHz)
    /// as it enters the ingest, before any MOX/monitor gating. The WAV recorder
    /// taps this to capture the raw transmit-side mic audio silently. The source
    /// tag lets metering reject a host/radio block if the armed source changed
    /// after the early gate. Payload is valid only for the synchronous handler
    /// — copy if retained.</summary>
    internal event Action<ReadOnlyMemory<byte>, MicBlockSource>? MicPcmTapped;
    public long TotalMicSamples { get { lock (_sync) return _totalMicSamples; } }
    public long TotalTxBlocks { get { lock (_sync) return _totalTxBlocks; } }
    public long DroppedFrames { get { lock (_sync) return _droppedFrames; } }

    public void Dispose()
    {
        _hub.MicPcmReceived -= _handler;
        lock (_sync) SetRecordingBypassLocked(null, false);
        lock (_productPluginSpeechBypassGate)
        {
            _engineProvider()?.SetTxInjectedAudioBypass(false);
            _productPluginSpeechBypassGeneration = 0;
        }
        _productPluginAudio?.ConfigureTxSpeechBypassSink(null);
    }

    /// <summary>
    /// Source-tagged entry point for TCI TX audio (from
    /// <see cref="Zeus.Server.Tci.TciTxAudioReceiver"/>). Updates the TCI
    /// recency timestamp so a concurrent <see cref="OnMicPcmBytesFromMic"/>
    /// call within <see cref="TciHysteresisMs"/> is silently suppressed.
    /// </summary>
    internal void OnMicPcmBytesFromTci(ReadOnlyMemory<byte> f32lePayload)
    {
        Volatile.Write(ref _lastTciTickMs, Environment.TickCount64);
        OnMicPcmBytes(f32lePayload, MicBlockSource.Tci);
    }

    /// <summary>
    /// Source-tagged entry point for browser/mobile mic audio from
    /// <see cref="StreamingHub.MicPcmReceived"/>. Browser mic is still local
    /// audio for the processing chain, but it owns the live-mic source while
    /// fresh so desktop <see cref="NativeMicCapture"/> cannot double-feed TXA.
    /// </summary>
    internal void OnMicPcmBytesFromBrowserMic(ReadOnlyMemory<byte> f32lePayload)
    {
        // A test-tools cable is an explicit exclusive source. Do not stamp the
        // browser recency clock here: doing so would both admit this frame and
        // suppress the cable for its hysteresis window.
        long now = Environment.TickCount64;
        lock (_sync)
        {
            if (_activeSource == MicBlockSource.VirtualCable) return;
            Volatile.Write(ref _lastBrowserMicTickMs, now);
        }
        if (ShouldSuppressForAuthoritativeSource(now)) return;
        OnMicPcmBytes(f32lePayload, MicBlockSource.BrowserMic);
    }

    /// <summary>
    /// Source-tagged entry point for the desktop native mic path. Drops the
    /// block silently if TCI, WAV playback, or browser/mobile mic fed within
    /// the last <see cref="TciHysteresisMs"/> milliseconds so sources never
    /// mix or overrun the TX path.
    /// </summary>
    internal void OnMicPcmBytesFromMic(ReadOnlyMemory<byte> f32lePayload)
    {
        long now = Environment.TickCount64;
        if (ShouldSuppressForAuthoritativeSource(now)) return;
        long lastBrowserMic = Volatile.Read(ref _lastBrowserMicTickMs);
        if (lastBrowserMic != 0 && now - lastBrowserMic < TciHysteresisMs) return;
        OnMicPcmBytes(f32lePayload, MicBlockSource.Host);
    }

    internal void OnMicPcmBytesFromMic(
        ReadOnlyMemory<byte> f32lePayload,
        MicBlockValidity validity)
    {
        _hostFanoutFill = 0; // never splice a partial ASIO block into the fan-out
        long now = Environment.TickCount64;
        if (ShouldSuppressForAuthoritativeSource(now)) return;
        long lastBrowserMic = Volatile.Read(ref _lastBrowserMicTickMs);
        if (lastBrowserMic != 0 && now - lastBrowserMic < TciHysteresisMs) return;
        OnMicPcmBytes(f32lePayload, MicBlockSource.Host, validity);
    }

    // ASIO fast-path state. Touched only by the single native-capture worker
    // thread (the same thread that calls OnMicPcmBytesFromMic).
    private readonly float[] _hostFanout = new float[MicBlockSamples];
    private readonly byte[] _hostFanoutPayload = new byte[MicBlockBytes];
    private int _hostFanoutFill;
    private bool _hostFanoutAirPerChunk;
    private long _hostFanoutGeneration = -1;

    /// <summary>
    /// Host mic at the ASIO driver's buffer cadence. Each chunk goes to the air
    /// path (WDSP) as soon as it arrives instead of waiting for a 20 ms block,
    /// while a 960-sample re-blocker keeps every fan-out consumer (friend-PTT
    /// native mic, taps, meters, product-plugin publish) on its fixed 20 ms
    /// contract. With a Product/VST chain leased the air path stays on whole
    /// 960 blocks — that chain aligns wet/dry by block count — latched only at
    /// block boundaries so no sample is ever sent twice or skipped.
    /// </summary>
    private static string FormatGapMs(double ms) =>
        ms < 0 ? "n/a" : ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    internal void OnHostMicSamplesFromMic(ReadOnlySpan<float> samples, MicBlockValidity validity)
    {
        if (samples.IsEmpty) return;
        // A capture route change / ASIO restart bumps the generation: never
        // splice the old partial 20 ms block onto the new stream.
        if (!IsCurrent(validity) || validity.Generation != _hostFanoutGeneration)
        {
            _hostFanoutFill = 0;
            _hostFanoutGeneration = validity.Generation;
            if (!IsCurrent(validity)) return;
        }
        long now = Environment.TickCount64;
        long lastBrowserMic = Volatile.Read(ref _lastBrowserMicTickMs);
        if (ShouldSuppressForAuthoritativeSource(now)
            || (lastBrowserMic != 0 && now - lastBrowserMic < TciHysteresisMs))
        {
            _hostFanoutFill = 0;
            return;
        }

        int offset = 0;
        while (offset < samples.Length)
        {
            if (_hostFanoutFill == 0) _hostFanoutAirPerChunk = !_productAudio.Active;
            int take = Math.Min(MicBlockSamples - _hostFanoutFill, samples.Length - offset);
            var chunk = samples.Slice(offset, take);
            chunk.CopyTo(_hostFanout.AsSpan(_hostFanoutFill));
            _hostFanoutFill += take;
            offset += take;

            // Cheap pre-check mirroring OnMicPcmBytes' early gate so an inactive
            // host mic never touches the shared monitor-preview gate or the
            // drop counters; ProcessMicPcm's in-lock gate stays authoritative.
            if (_hostFanoutAirPerChunk
                && Volatile.Read(ref _productPluginInjectionActive) == 0
                && ActiveSource == MicBlockSource.Host)
            {
                ProcessMicPcm(chunk, MicBlockSource.Host, validity);
            }

            if (_hostFanoutFill == MicBlockSamples)
            {
                _hostFanoutFill = 0;
                MemoryMarshal.AsBytes(_hostFanout.AsSpan()).CopyTo(_hostFanoutPayload);
                OnMicPcmBytes(_hostFanoutPayload, MicBlockSource.Host, validity,
                    processAir: !_hostFanoutAirPerChunk);
            }
        }
    }

    /// <summary>
    /// Source-tagged entry point for radio-digitised jack audio (Saturn mic /
    /// line-in / balanced-XLR, arriving on UDP 1026 and re-blocked to 960
    /// samples upstream — external-audio-jacks re-port). Tagged
    /// <see cref="MicBlockSource.RadioMic"/>; the in-lock
    /// <see cref="_activeSource"/> compare in <see cref="OnMicPcmBytes"/> drops
    /// it unless a radio jack is the armed source, so it can never leak onto the
    /// air under Host. Like the host mic, it yields to a recent TCI/WAV override
    /// so a remote/playback source is never mixed with radio-jack audio.
    /// </summary>
    internal void OnMicPcmBytesFromRadioMic(ReadOnlyMemory<byte> f32lePayload)
    {
        long now = Environment.TickCount64;
        if (ShouldSuppressForAuthoritativeSource(now)) return;
        long lastBrowserMic = Volatile.Read(ref _lastBrowserMicTickMs);
        if (lastBrowserMic != 0 && now - lastBrowserMic < TciHysteresisMs) return;
        OnMicPcmBytes(f32lePayload, MicBlockSource.RadioMic);
    }

    /// <summary>
    /// Source-tagged entry point for the independently managed TX Testing
    /// Tools virtual-cable capture. Like other live sources it yields to a
    /// current TCI/WAV override and never changes MOX/PTT state itself.
    /// </summary>
    internal void OnMicPcmBytesFromVirtualCable(
        ReadOnlyMemory<byte> f32lePayload,
        MicBlockValidity validity)
    {
        long now = Environment.TickCount64;
        if (ShouldSuppressForAuthoritativeSource(now)) return;
        if (ActiveSource != MicBlockSource.VirtualCable)
        {
            long lastBrowserMic = Volatile.Read(ref _lastBrowserMicTickMs);
            if (lastBrowserMic != 0 && now - lastBrowserMic < TciHysteresisMs) return;
        }
        OnMicPcmBytes(f32lePayload, MicBlockSource.VirtualCable, validity);
    }

    private bool ShouldSuppressForAuthoritativeSource(long now)
    {
        long lastTci = Volatile.Read(ref _lastTciTickMs);
        if (lastTci != 0 && now - lastTci < TciHysteresisMs) return true;
        long lastWav = Volatile.Read(ref _lastWavTickMs);
        return lastWav != 0 && now - lastWav < TciHysteresisMs;
    }

    /// <summary>Source-tagged entry point for WAV-recording playback to the
    /// air (driven by the com.kb2uka.recorder plugin via
    /// <see cref="PluginPlaybackSink"/>'s over-air path). Stamps the
    /// WAV recency timestamp so the live native mic is suppressed for the clip,
    /// then reblocks it for TX modulation with the audio suite and mic gain
    /// bypassed so the recording retains its original audio. The caller
    /// keys MOX; this method does not touch MOX.</summary>
    internal void OnMicPcmBytesFromWav(ReadOnlyMemory<byte> f32lePayload)
    {
        Volatile.Write(ref _lastWavTickMs, Environment.TickCount64);
        OnMicPcmBytes(f32lePayload, MicBlockSource.Wav);
    }

    // Internal so tests can drive the ingest directly without standing up a WS.
    // Untagged overload defaults to Host (host path byte/behaviour-identical to
    // today).
    internal void OnMicPcmBytes(ReadOnlyMemory<byte> f32lePayload)
        => OnMicPcmBytes(f32lePayload, MicBlockSource.Host);

    // The source tag arbitrates the HOST↔RADIO single-select gate IN-LOCK (see
    // _activeSource); TCI/WAV bypass that compare as operator-explicit overrides.
    internal void OnMicPcmBytes(ReadOnlyMemory<byte> f32lePayload, MicBlockSource source)
        => OnMicPcmBytes(f32lePayload, source, validity: null);

    private void OnMicPcmBytes(
        ReadOnlyMemory<byte> f32lePayload,
        MicBlockSource source,
        MicBlockValidity? validity,
        bool processAir = true)
    {
        if (!IsCurrent(validity)) return;
        if (f32lePayload.Length != MicBlockBytes)
        {
            lock (_sync) _droppedFrames++;
            return;
        }

        // Early host/radio gate (external-audio-jacks re-port). The compare and
        // friend-PTT publication share the _sync lock with SetActiveSource, so
        // a completed source switch can never be followed by one stale chat block. The later
        // accumulator gate remains authoritative for the separate WDSP append,
        // since _activeSource can flip again after this lock is released. TCI/WAV
        // bypass both the live-mic bridge and this compare.
        if (source is MicBlockSource.Host or MicBlockSource.BrowserMic or MicBlockSource.RadioMic or MicBlockSource.VirtualCable)
        {
            lock (_sync)
            {
                if ((source == MicBlockSource.BrowserMic
                        && _activeSource == MicBlockSource.VirtualCable)
                    || (source != MicBlockSource.BrowserMic && source != _activeSource))
                {
                    _droppedFrames++;
                    return;
                }
                // Friend PTT hears the same selected live source that MOX would
                // transmit, but this pre-MOX feed never keys the radio.
                _hub.BroadcastNativeMicPcm(f32lePayload.Span);
            }
        }

        // Non-destructive mic tap, fired BEFORE the MOX/monitor gate so a
        // recorder can capture the raw mic whether or not the operator is
        // keyed or monitoring (silent capture). The engine mic-meter service
        // also observes this seam, so desktop native mic, browser mic, radio
        // jack, TCI, and playback all use the same pre-MOX metering path.
        MicPcmTapped?.Invoke(f32lePayload, source);
        Span<float> samples = stackalloc float[MicBlockSamples];
        var bytes = f32lePayload.Span;
        for (var index = 0; index < samples.Length; index++)
            samples[index] = BinaryPrimitives.ReadSingleLittleEndian(
                bytes.Slice(index * sizeof(float), sizeof(float)));
        _productPluginAudio?.PublishTxMic(TxRateHz, samples);
        if (!processAir) return; // ASIO fast path already sent these samples
        if (Volatile.Read(ref _productPluginInjectionActive) != 0)
        {
            lock (_sync) _droppedFrames++;
            return;
        }
        ProcessMicPcm(samples, source, validity);
    }

    private void OnProductPluginTxAudio(ReadOnlySpan<float> samples)
    {
        if (samples.Length != MicBlockSamples
            || Volatile.Read(ref _productPluginInjectionActive) == 0)
        {
            lock (_sync) _droppedFrames++;
            return;
        }
        ProcessMicPcm(samples, MicBlockSource.ProductPlugin);
    }

    private void SetProductPluginInjectionActive(bool active)
    {
        Volatile.Write(ref _productPluginInjectionActive, active ? 1 : 0);
        lock (_sync)
        {
            _accumulatorFill = 0;
            if (!active) _ring.Clear();
        }
    }

    private bool SetProductPluginSpeechBypass(long generation, bool bypass)
    {
        lock (_productPluginSpeechBypassGate)
        {
            if (bypass)
            {
                if (generation < _productPluginSpeechBypassGeneration) return false;
                if (generation == _productPluginSpeechBypassGeneration) return true;
                var engine = _engineProvider();
                if (engine is null) return false;
                _productPluginSpeechBypassGeneration = generation;
                engine.SetTxInjectedAudioBypass(true);
                return true;
            }

            if (generation != _productPluginSpeechBypassGeneration) return true;
            _productPluginSpeechBypassGeneration = 0;
            _engineProvider()?.SetTxInjectedAudioBypass(false);
            return true;
        }
    }

    // Caller holds _sync. fexchange2 submits input before the WDSP worker
    // consumes it, so recording bypass must remain latched between frames.
    private void SetRecordingBypassLocked(IDspEngine? engine, bool recording)
    {
        if (_recordingBypassEngine is { } previous && (!recording || previous != engine))
        {
            _recordingBypassEngine = null;
            RestoreRecordingBypass(previous);
        }
        if (!recording || engine is null || _recordingBypassEngine == engine) return;
        _recordingBypassEngine = engine;
        try { engine.SetTxRecordingBypass(true); }
        catch
        {
            _recordingBypassEngine = null;
            RestoreRecordingBypass(engine);
            throw;
        }
    }

    private void RestoreRecordingBypass(IDspEngine engine)
    {
        // Cleanup must never prevent a tail drain from relinquishing TXA.
        try { engine.SetTxRecordingBypass(false); }
        catch (Exception ex) { _log.LogWarning(ex, "tx.recording.bypass restore threw"); }
    }

    internal bool SetProductPluginSpeechBypassForTest(long generation, bool bypass) =>
        SetProductPluginSpeechBypass(generation, bypass);

    internal static bool IsTimingExactSource(MicBlockSource source, bool modemActive, bool voiceMode) =>
        source is not (MicBlockSource.Host or MicBlockSource.BrowserMic or MicBlockSource.RadioMic)
        || modemActive
        || !voiceMode;

    private void ProcessMicPcm(
        ReadOnlySpan<float> samples,
        MicBlockSource source,
        MicBlockValidity? validity = null,
        bool bypassOnset = false,
        bool tailReplay = false)
    {
        if (!bypassOnset && HoldOrReplayVoiceOnset(samples, source, validity, _isMoxOn()))
            return;

        // Gate: process mic samples when MOX is on (normal TX) OR when the TX
        // monitor is on (preview without keying so the operator can hear
        // their VST chain / EQ / leveler before going on the air). When both
        // are off the chain doesn't run — pre-monitor behaviour, plus the
        // mic-leak protection that motivated the original gate.
        //
        // The ring-clear / accumulator-clear on the MOX falling edge stays
        // tied to MOX, not monitor: dropping accumulator state on a monitor
        // toggle would chop mid-syllable for no benefit, and the IQ ring is
        // only consumed during MOX anyway.
        var engine = _engineProvider();
        bool monitorOn = engine?.IsTxMonitorOn ?? false;
        bool moxNow = _isMoxOn();
        if (!moxNow && !monitorOn)
        {
            lock (_sync)
            {
                if (_accumulatorFill > 0) _accumulatorFill = 0;
                _latencyGovernor.Reset();
                SetRecordingBypassLocked(null, false);
                if (_fmBurst.IsActive || _fmBurstBypassApplied) CancelFmToneBurstLocked(engine);
                if (_lastSeenMox)
                {
                    // MOX fell since our last frame — drain the IQ ring so the
                    // next keyed TX starts clean, without the tail of this one.
                    _ring.Clear();
                    _audioModem.FlushTx();
                    _lastSeenMox = false;
                }
            }
            return;
        }
        if (!moxNow && _lastSeenMox)
        {
            // MOX fell while monitor is on. Drain the IQ ring so the next
            // key-down isn't tailed by stale RF samples, but keep the
            // accumulator + chain feed running for the preview.
            lock (_sync)
            {
                _ring.Clear();
                _audioModem.FlushTx();
                CancelFmToneBurstLocked(engine);
                SetRecordingBypassLocked(null, false);
                _lastSeenMox = false;
            }
        }
        // Latch the MOX rising edge so the next falling edge will drain the
        // ring. Monitor-only operation never sets _lastSeenMox so it's a true
        // edge tracker for keyed TX.
        if (moxNow && !_lastSeenMox) { lock (_sync) _lastSeenMox = true; }

        int blockSize = engine?.TxBlockSamples ?? 0;
        int iqOut = engine?.TxOutputSamples ?? 0;
        if (engine is null || blockSize <= 0 || iqOut <= 0
            || blockSize > _scratchMic.Length || 2 * iqOut > _scratchIq.Length)
        {
            // Synthetic engine, no TXA open, or a protocol whose block size
            // exceeds our scratch buffers. Swallow samples quietly.
            return;
        }

        // TUN / two-tone active → TxTuneDriver is the sole TX driver. Running
        // ProcessTxBlock here too would put two threads on one TXA's fexchange2
        // AND push a second IQ stream to the radio (double-feed → corrupted /
        // dirty signal; PS then calibrates on garbage). The mic isn't
        // transmitted during a tune/two-tone anyway, so drop the batch and reset
        // the accumulator so it can't back up. Fixes the desktop-only dirty
        // two-tone in #559 (native mic capture kept feeding here; web had no
        // native mic so only TxTuneDriver drove it → clean).
        if (_txOwnedByTuneDriver())
        {
            lock (_sync)
            {
                _accumulatorFill = 0;
                _latencyGovernor.Reset();
                SetRecordingBypassLocked(null, false);
            }
            return;
        }

        // TX Preview is a local monitor path, not the on-air path. While MOX is
        // off, do not clock idle mic/noise through TXA: the leveler/CFC/
        // ALC stack can turn a tiny idle floor or plugin self-noise into an
        // audible, self-rising monitor tone. Speech opens the preview gate
        // immediately and the hang keeps it open through phrase pauses so the
        // preview does not sound gated; only sustained idle closes it. Keyed
        // TX, TCI, and WAV playback bypass this gate entirely.
        //
        // Substitute digital silence rather than dropping the block: while the
        // monitor is on it REPLACES the RX audio at every sink, so a dropped
        // block starves the playback ring — the native output falls into its
        // ~90 ms rebuffer cycle on every speech pause and each word onset comes
        // back clipped (the "choppy preview" report). Zeros through the chain
        // keep the monitor stream gapless and the TXA/VST state warm, and the
        // leveler cannot raise a tone out of exact zeros, so the gate's original
        // purpose is preserved.
        if (monitorOn && !moxNow
            && source is MicBlockSource.Host or MicBlockSource.BrowserMic or MicBlockSource.RadioMic or MicBlockSource.VirtualCable
            && ShouldSuppressIdleMonitorPreviewBlock(samples))
        {
            samples = SilentMicBlock.AsSpan(0, samples.Length);
        }

        lock (_sync)
        {
            // FreeDV end-of-over tail is clocking the final frame out on the
            // un-key thread; yield TX exclusively (no second feeder on the WDSP
            // TXA / radio IQ ring). Authoritative check under _sync — the drain
            // sets _tailDraining then takes _sync as a barrier, so any frame that
            // reaches here after that point bails. Drop the accumulator so no
            // pre-tail remainder stitches onto the next over.
            if (_instantCwIdReserved || (!tailReplay && Volatile.Read(ref _tailDraining) != 0))
            {
                if (!_preserveOnsetAccumulatorDuringTail) _accumulatorFill = 0;
                return;
            }

            // ATOMIC single-select gate (external-audio-jacks re-port, the
            // crux). This is the AUTHORITATIVE host/radio arbitration: it runs
            // in the SAME critical section as the accumulator append, so no flip
            // of _activeSource (by SetActiveSource, also under _sync) can slip
            // between "decide" and "append". A Host/RadioMic block whose tag no
            // longer matches the armed source is dropped, so two producer threads
            // straddling a switch resolve to exactly one contributor per WDSP
            // block — no double-feed. TCI/WAV bypass this (operator-explicit
            // overrides; recency-gated above). Under Host with a Host block this
            // is a pure no-op → host path byte/behaviour-identical to today.
            if ((source is MicBlockSource.Host or MicBlockSource.RadioMic or MicBlockSource.VirtualCable
                 || (source == MicBlockSource.BrowserMic && _activeSource == MicBlockSource.VirtualCable))
                && source != _activeSource)
            {
                _droppedFrames++;
                return;
            }
            // Guard the accumulator owner: if a half-filled block belongs to a
            // different source than the one now appending (possible when the
            // active source flipped while data sat buffered), drop the stale
            // remainder so the two sources never mix inside one WDSP block.
            // SetActiveSource already clears on the host/radio switch; this
            // covers the TCI/WAV interleave as well.
            if (_accumulatorFill > 0 && _accumulatorSource != source)
                _accumulatorFill = 0;
            _accumulatorSource = source;
            SetRecordingBypassLocked(engine, source == MicBlockSource.Wav);

            // Decode f32le into accumulator. WDSP wants -1..+1 range; browser
            // ships the same convention.
            // 960 for every 20 ms source; the driver buffer size on the ASIO
            // fast path (OnHostMicSamplesFromMic).
            int need = samples.Length;
            if (_accumulatorFill + need > _accumulator.Length)
            {
                // Should only happen if BlockSamples grew unexpectedly. Treat
                // as a protocol mismatch — drop the accumulator to avoid
                // writing past the array bound.
                _accumulatorFill = 0;
                _droppedFrames++;
                return;
            }
            var incoming = _accumulator.AsSpan(_accumulatorFill, need);
            for (int i = 0; i < need; i++)
                incoming[i] = DspPipelineService.SanitizeAudioSample(samples[i]);

            // Product audio is paced by the native 20 ms microphone cadence,
            // not WDSP's protocol-dependent 512/256-sample drain geometry.
            // Process the complete sanitized ingress frame once, then retain
            // its result for WDSP reblocking below. The modem still replaces
            // that processed speech after reblocking, preserving the existing
            // Product -> modem -> WDSP ordering. Linear ProductPlugin sources
            // continue to bypass the operator's Audio Suite when requested.
            // Whole 20 ms blocks only: the chain aligns wet/dry by block count.
            // A sub-block ASIO chunk (only possible for the <=20 ms before the
            // fast path latches onto a newly-leased chain) passes through dry.
            // Voice shaping must never touch a data waveform: in DIGU/DIGL the
            // suite steps aside, mirroring the in-engine plugin handler. FreeDV
            // still gets processed speech — the codec encodes the voice.
            if (source != MicBlockSource.Wav
                && _productAudio.Active
                && need == MicBlockSamples
                && (_isVoiceTxMode() || _audioModem.Active)
                && (source != MicBlockSource.ProductPlugin
                    || Volatile.Read(ref _productPluginSpeechBypassGeneration) == 0))
            {
                _productAudio.ProcessTx(incoming);
            }
            if (!(bypassOnset ? IsBufferedCurrent(validity) : IsCurrent(validity)))
            {
                _accumulatorFill = 0;
                return;
            }
            // Keep live voice near real time: trim transport backlog by
            // dropping silent 1 ms chunks between words. Operator speech only —
            // digital, FreeDV, TCI/WAV/cable and plugin audio are timing-exact
            // streams and pass through untouched.
            int kept = need;
            bool timingExact = IsTimingExactSource(source, _audioModem.Active, _isVoiceTxMode());
            if (moxNow && !timingExact)
            {
                _latencyGovernor.ObserveTransport(
                    _txTransportLevel(),
                    source == MicBlockSource.BrowserMic
                        ? BrowserMicMinCushionSamples
                        : TxLatencyGovernor.DefaultMinTargetSamples,
                    need);
                kept = _latencyGovernor.Compact(incoming);
            }
            else
            {
                // Unkeyed, or a timing-exact source/mode took over mid-over:
                // no stale budget may carry into the next governed block.
                _latencyGovernor.Reset();
            }
            _accumulatorFill += kept;
            _totalMicSamples += need;

            while (_accumulatorFill >= blockSize)
            {
                Array.Copy(_accumulator, 0, _scratchMic, 0, blockSize);
                // FreeDV TX insert: replace the mic-speech block with the
                // FreeDV modem signal (in place, same count, internally
                // buffered). WDSP's USB TXA then SSB-modulates the modem audio.
                // No-op unless FreeDV is the active mode.
                if (_audioModem.Active)
                    _audioModem.ProcessTx(new Span<float>(_scratchMic, 0, blockSize));
                // FM access tone burst: replaces (mutes) the mic for its
                // duration, with the speech processors bypassed like the
                // roger beep. Held off during the pre-key mute so the whole
                // burst goes out after the amp relay has settled.
                else if (moxNow && _fmBurst.IsActive && CarriesCwId(source))
                {
                    long openAt = _preKeyOpenAtTicks();
                    bool preKeyMuted = openAt != 0L
                        && TxService.IsPreKeyMuteOpen(openAt, _stopwatchTicks());
                    if (!preKeyMuted)
                    {
                        if (!_fmBurstBypassApplied)
                        {
                            engine.SetTxRogerBeepBypass(true);
                            _fmBurstBypassApplied = true;
                        }
                        _fmBurst.Render(new Span<float>(_scratchMic, 0, blockSize));
                    }
                }
                // CW station ID: sum the due ID tone into this keyed voice
                // block. Only on the air path (MOX), never over FreeDV, and
                // held off during the pre-key mute so no dit is lost.
                else if (moxNow && _cwId is { } cwId && CarriesCwId(source))
                {
                    long openAt = _preKeyOpenAtTicks();
                    bool preKeyMuted = openAt != 0L
                        && TxService.IsPreKeyMuteOpen(openAt, _stopwatchTicks());
                    cwId.MixTxBlock(new Span<float>(_scratchMic, 0, blockSize), canSend: !preKeyMuted);
                }
                int produced;
                bool recording = source == MicBlockSource.Wav;
                try
                {
                    produced = engine.ProcessTxBlock(
                        new ReadOnlySpan<float>(_scratchMic, 0, blockSize),
                        new Span<float>(_scratchIq, 0, 2 * iqOut));
                }
                catch
                {
                    if (recording) SetRecordingBypassLocked(null, false);
                    throw;
                }
                // Burst finished on this block: speech processing resumes.
                if (_fmBurstBypassApplied && !_fmBurst.IsActive)
                    CancelFmToneBurstLocked(engine);
                // Product/WDSP calls may stall after the native capture route
                // changed or the block deadline expired. Never publish their
                // now-stale IQ to either radio transport.
                if (!(bypassOnset ? IsBufferedCurrent(validity) : IsCurrent(validity)))
                {
                    _accumulatorFill = 0;
                    return;
                }
                if (produced > 0)
                {
                    var iqSpan = new ReadOnlySpan<float>(_scratchIq, 0, 2 * produced);
                    // Only push the modulated IQ to the radio while MOX is
                    // asserted. When the chain is running for monitor-only
                    // (preview without keying) the IQ has been generated for
                    // the engine's monitor RXA channel to demod inside
                    // ProcessTxBlock — but it must NOT hit the wire, otherwise
                    // a monitor toggle would put the radio on the air.
                    if (moxNow)
                    {
                        // Pre-key (MOX) delay window (issue #630): if still inside
                        // the window, substitute silence for the modulated IQ so
                        // the amp T/R relay settles before RF appears. We still
                        // WRITE (a zero block of the same length) rather than drop
                        // — dropping would starve the P2 DUC FIFO and produce the
                        // exact bare/gappy carrier the feature exists to prevent.
                        // WAV-over-air playback is exempt: the operator already
                        // keyed and the clip head is position-locked, so muting it
                        // would clip the intro.
                        long openAt = _preKeyOpenAtTicks();
                        bool wavRecent = false;
                        if (openAt != 0L)
                        {
                            long lastWav = Volatile.Read(ref _lastWavTickMs);
                            wavRecent = lastWav != 0
                                && Environment.TickCount64 - lastWav < TciHysteresisMs;
                        }
                        bool mute = openAt != 0L
                            && TxService.IsPreKeyMuteOpen(openAt, _stopwatchTicks())
                            && !wavRecent;

                        // P1 path — EP2 packer in Protocol1Client drains the ring.
                        _ring.Write(mute
                            ? new ReadOnlySpan<float>(_muteIq, 0, 2 * produced)
                            : iqSpan);
                        // P2 path — Protocol2Client's 1029-port DUC sender. No-op
                        // when P2 isn't the active backend so both protocols share
                        // this seam cleanly. Mirrors TxTuneDriver's dual-write.
                        _setP2TimingExact?.Invoke(timingExact);
                        _forwardP2?.Invoke(mute
                            ? new ReadOnlyMemory<float>(_muteIq, 0, 2 * produced)
                            : new ReadOnlyMemory<float>(_scratchIq, 0, 2 * produced));
                    }
                    _totalTxBlocks++;
                    var onConsumed = Volatile.Read(ref _onWdspConsumed);
                    onConsumed?.Invoke(blockSize);

                    // Accumulate peaks for the 1 Hz diagnostic log.
                    float micPeak = 0f;
                    for (int s = 0; s < blockSize; s++)
                    {
                        float a = _scratchMic[s];
                        if (a < 0) a = -a;
                        if (a > micPeak) micPeak = a;
                    }
                    float iqPeak = 0f;
                    for (int s = 0; s < 2 * produced; s++)
                    {
                        float a = _scratchIq[s];
                        if (a < 0) a = -a;
                        if (a > iqPeak) iqPeak = a;
                    }
                    if (micPeak > _peakMicAccum) _peakMicAccum = micPeak;
                    if (iqPeak > _peakIqAccum) _peakIqAccum = iqPeak;
                    _peakBlocksAccum++;
                    var now = DateTime.UtcNow;
                    if (now - _lastPeakLogUtc >= TimeSpan.FromSeconds(1))
                    {
                        int floor = _latencyGovernor.LastFloorSamples;
                        string floorMs = floor < 0 ? "n/a" : (floor / (double)TxLatencyGovernor.SamplesPerMs).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                        if (_productAudio.Active)
                        {
                            // prodDry > 0 during an over means unprocessed mic
                            // blocks were spliced into the Audio Suite stream.
                            // Logged at Warning then, so a problem report sent
                            // long after the over still carries the evidence
                            // (reports keep every WARN but only recent INFO).
                            var prod = _productAudio.TakeTxWindowDiagnostics();
                            _log.Log(
                                prod.DryFallback > 0 && moxNow ? LogLevel.Warning : LogLevel.Information,
                                "tx.peaks blocks={Blocks} mic={Mic:F4} iq={Iq:F4} backlogFloorMs={FloorMs} cushionMs={CushionMs} overTrimMs={TrimMs} prodBlocks={ProdBlocks} prodWet={ProdWet} prodDry={ProdDry} prodPriming={ProdPriming} prodBusy={ProdBusy} prodRingFull={ProdRingFull} prodGapMinMs={ProdGapMin} prodGapMaxMs={ProdGapMax}",
                                _peakBlocksAccum, _peakMicAccum, _peakIqAccum, floorMs,
                                _latencyGovernor.TargetSamples / TxLatencyGovernor.SamplesPerMs,
                                _latencyGovernor.DroppedSamples / TxLatencyGovernor.SamplesPerMs,
                                prod.Blocks, prod.Processed, prod.DryFallback, prod.Priming, prod.Busy, prod.RingFull,
                                FormatGapMs(prod.MinGapMs), FormatGapMs(prod.MaxGapMs));
                        }
                        else
                        {
                            _log.LogInformation(
                                "tx.peaks blocks={Blocks} mic={Mic:F4} iq={Iq:F4} backlogFloorMs={FloorMs} cushionMs={CushionMs} overTrimMs={TrimMs}",
                                _peakBlocksAccum, _peakMicAccum, _peakIqAccum, floorMs,
                                _latencyGovernor.TargetSamples / TxLatencyGovernor.SamplesPerMs,
                                _latencyGovernor.DroppedSamples / TxLatencyGovernor.SamplesPerMs);
                        }
                        _lastPeakLogUtc = now;
                        _peakMicAccum = 0f;
                        _peakIqAccum = 0f;
                        _peakBlocksAccum = 0;
                    }
                }
                // Shift remainder down — typically ~64 leftover samples (960 %
                // 1024 carry). Array.Copy handles overlapping source/dest.
                int remainder = _accumulatorFill - blockSize;
                if (remainder > 0)
                    Array.Copy(_accumulator, blockSize, _accumulator, 0, remainder);
                _accumulatorFill = remainder;
            }
        }
    }

    private static bool IsCurrent(MicBlockValidity? validity) =>
        validity is not { } guarded || guarded.IsCurrent;

    private static bool IsBufferedCurrent(MicBlockValidity? validity) =>
        validity is not { } guarded || guarded.IsBufferedCurrent;

    private bool ShouldSuppressIdleMonitorPreviewBlock(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        double sumSquares = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];
            if (!float.IsFinite(sample)) sample = 0f;
            float abs = sample < 0f ? -sample : sample;
            if (abs > peak) peak = abs;
            sumSquares += sample * sample;
        }
        float rms = (float)Math.Sqrt(sumSquares / Math.Max(1, samples.Length));

        lock (_sync)
        {
            if (peak >= MonitorPreviewOpenPeak || rms >= MonitorPreviewOpenRms)
            {
                _monitorPreviewHangSamples = MonitorPreviewHangBlocks * MicBlockSamples;
                return false;
            }
            if (_monitorPreviewHangSamples > 0)
            {
                _monitorPreviewHangSamples -= samples.Length;
                return false;
            }
            return true;
        }
    }
}
