// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// RADE (Radio Autoencoder) modem as a streaming, frame-synchronous insert in
// the Zeus audio bus — the RADE twin of FreeDvModem, full-duplex, for RADE V1
// and RADE V2 (upstream beta):
//   RX: received SSB audio -> decoded 16 kHz speech (radae_c + opus_dnn/FARGAN).
//   TX: mic speech -> RADE modem waveform (opus_dnn LPCNet analyzer + rade_tx),
//       transmitted as the REAL part of the modem IQ (inverse of the RX feed).
// Both directions absorb the 48 kHz <-> modem-rate and 48 kHz <-> 16 kHz speech
// rate changes internally with resamplers + fixed lock-free SPSC rings,
// silence-padded while starved. The V1 End-of-Over callsign is carried by the
// FreeDV reliable-text LDPC (rade_text), so it interoperates with FreeDV-GUI;
// V2 carries no callsign.
//
// PROTOCOL SELECTION. Each native context is opened for ONE protocol. A
// selection of V1 or V2 opens one RX decoder and one TX encoder of that
// protocol. Auto opens a V1 AND a V2 decoder on the same input (about 1.3% of a
// core each) plus both encoders: RadeRxArbiter picks whose speech is played
// (V2 wins; a fresh V1 lock is muted through the V1 false-lock window), and
// each over latches ONE encoder at its first block and keeps it until the over
// ends (FlushTx), so a receive lock can never switch protocol mid-over.
//
// REALTIME DISCIPLINE — identical to FreeDvModem: the hot paths (ProcessRxInPlace
// / ProcessTxInPlace) take NO lock and allocate NOTHING; every channel, ring and
// scratch buffer is built when a context opens (off the hot path), sized from
// that context and, for shared TX scratch, for the larger of the open
// protocols. Reconfiguring runs on a control thread serialised by
// _reconfigGate, handing each immutable channel set to its hot path through a
// Dekker-fenced seqlock: the control thread publishes null, full-fences, then
// spins until the busy flag clears before closing any retired handle. When no
// set is published, RX passes audio through UNCHANGED; TX SILENCES the block
// (never transmits raw mic on a RADE frequency).

using System.Threading;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

internal sealed class RadeModem : IRadeModemCore
{
    private readonly ILogger? _log;
    private readonly object _reconfigGate = new();   // serialises control-thread reconfig only
    private readonly bool _nativeAvailable;
    private readonly bool _v2Available;
    private readonly bool _micExportsAvailable;   // optional mic conditioning + meter exports

    private const int CallsignMax = 8;               // RADE_EOO_CALLSIGN_MAX

    // Output latency ceiling (48 kHz samples). ~250 ms — past this the modem
    // stalled or drifted; drop oldest to stay real-time.
    private const int MaxOutFifo = RadeResampler.FsHigh / 4;
    // FinishTx carries a full EOO frame plus the decoder-flush pad documented
    // by the native loopback harness. This is intentionally larger than the
    // live latency ceiling: it is a finite PTT-held tail, not live backlog.
    private const int TxTailCapacity = 131_072;
    private const int MaxBlock = 8192;               // generous cap on a 48 kHz audio block
    // SDR-VST3 attenuates RADE's near-full-scale modem waveform before the
    // output interpolator. Both protocols peak near 1.0 (V2's RMS is ~2.3 dB
    // lower, a higher crest factor), so the same peak-based level serves both.
    // Kept identical for live, residual, and EOO frames so transitions at the
    // end of an over do not jump in amplitude.
    internal const float TxOutputScale = 0.5f;

    // Selection (control thread writes under _reconfigGate; hot paths read).
    private volatile RadeProtocolSelection _selection = RadeProtocolSelection.V1;
    private volatile RadeProtocol _preferred = RadeProtocol.None;
    // The protocol last locked on receive since this engine started — the
    // Auto TX choice. Written by the RX hot path only.
    private volatile RadeProtocol _lastLocked = RadeProtocol.None;

    private volatile bool _active;

    // -------- RX --------
    // The published decoder set (null = passthrough). Immutable once published.
    private RxSet? _rxSet;
    private int _rxBusy;                             // 0/1 seqlock busy flag (hot path owns)
    // 48k -> 8k decimation reuses the classic FreeDV decimator (6:1), once per
    // block, feeding every decoder in the set the same samples.
    private readonly FreeDvResampler.Decimator _rxDown = FreeDvResampler.NewDecimator();
    private readonly float[] _rxDecScratch = new float[FreeDvResampler.MaxDecimatedLength(MaxBlock)];
    // Sync-driven output squelch. RADE has NO codec2-style SNR squelch, so this
    // is the ONLY thing that mutes the FARGAN decoder's noise-driven output when
    // the far station unkeys — without it you hear an R2D2 tail every un-key.
    // Owned by the RX hot path; reset when the set changes.
    private readonly RxSquelchGate _rxGate = RxSquelchGate.Default(RadeResampler.FsHigh);
    private readonly RadeRxArbiter _arbiter = new();
    private RxChannel? _rxOutput;                    // sticky output decoder (hot path owned)
    private long _rx8Clock;                          // 8 kHz samples fed (arbiter clock)
    private volatile RadeProtocol _rxLocked = RadeProtocol.None;
    private readonly byte[] _eooScratch = new byte[CallsignMax + 1]; // hot-path EOO fetch buffer

    // -------- TX --------
    // Mic 48k -> 16k speech (wideband 3:1 decimate) -> zeus_rade_tx ->
    // modem IQ @8k (real lane) -> 8k -> 48k (6:1 interpolate) -> playout.
    private TxSet? _txSet;
    private int _txBusy;
    // Set (before the gate-out fence) by every control path that CLEARS the
    // TX rings: FlushTx and PublishTx/RetireTx. DrainTo raises _txBusy, fences,
    // then checks this; the clearing path raises this, fences, then waits on
    // _txBusy. Dekker ordering means one of them always sees the other, so the
    // drain never reads a ring that is being cleared. Transient gate-outs that
    // do not clear (SetTxText, SetMicConfig) leave it false, so a tail is never
    // spliced with a silent block by an unrelated control change.
    private volatile bool _txRingClearing;
    // The encoder this over uses. Latched by the TX hot path at the over's
    // first block, cleared only with TX gated out (FlushTx / set change).
    private TxSlot? _txOverSlot;
    // Whether the transmitter rides LSB (the TX carrier's effective sideband,
    // supplied by the composite from the radio). Under Auto that forces V1 TX:
    // V2 is USB-only. Read once per over latch.
    private Func<bool>? _txRidesLsb;
    private readonly RadeResampler.Decimator _txDown = RadeResampler.NewDecimator();
    private readonly FreeDvResampler.Interpolator _txUp = FreeDvResampler.NewInterpolator();
    private readonly FreeDvSampleRing _tx16In = new(16384);   // 16 kHz speech awaiting zeus_rade_tx
    private readonly FreeDvSampleRing _txOut48 = new(TxTailCapacity); // modem audio, 48 kHz, awaiting playout
    private readonly float[] _txDecScratch = new float[RadeResampler.MaxDecimatedLength(MaxBlock)];
    // Shared encoder scratch, grown (TX gated out) to the largest open slot.
    private float[] _tx16Float = new float[640];        // n_speech 16k samples pulled from the ring
    private short[] _tx16Short = new short[640];
    private float[] _txIqOut = new float[2 * 1024];     // interleaved complex modem IQ from the encoder
    private float[] _txModemReal = new float[1024];     // the real lane (transmitted SSB audio @8k)
    private float[] _txInterp = new float[2048];        // 48k after interpolation
    // Pre-vocoder mic noise gate: drives the mic to silence during pauses so
    // rade_tx emits clean silence frames, not vocoded hiss. Reset in RetireTx/FlushTx.
    private readonly MicNoiseGate _micGate = MicNoiseGate.Default(RadeResampler.FsHigh);

    // EOO callsign to embed in the V1 TX frame (control thread writes; applied
    // at open and on change). V2 contexts ignore it natively.
    private volatile string? _txCallsign;

    // Retained pre-encoder mic conditioning (control thread writes under
    // _reconfigGate; applied at open and on change, exactly like _txCallsign).
    // Seeded to the shim's own defaults so a fresh context is bit-exact
    // passthrough until the operator touches a control.
    private bool _micAgcEnabled;
    private double _micAgcTargetDb = -18;
    private bool _micEqEnabled;
    private double _micEqBassDb;
    private double _micEqMidDb;
    private double _micEqTrebleDb;

    // Telemetry (lock-free reads)
    private volatile bool _synced;
    private double _snrDb;
    private double _txMicLevelDb = -120;
    private volatile bool _txMicClip;

    // Decoded RX callsign publish (hot path writes bytes + bumps _eooSeq; the
    // status thread rebuilds the string when the sequence changes — a seqlock, so
    // the hot path never allocates and the reader never tears).
    private readonly byte[] _rxEooBytes = new byte[CallsignMax + 1];
    private int _rxEooLen;
    private long _rxEooSeq;
    private long _rxEooSeenSeq;
    private string? _rxCallsign;
    private readonly object _rxCallsignLock = new();

    public RadeModem(ILogger? log = null, bool? nativeAvailableOverride = null, bool? v2AvailableOverride = null)
    {
        _log = log;
        _nativeAvailable = nativeAvailableOverride ?? RadeNativeLoader.TryProbeRade();
        if (_nativeAvailable)
        {
            RadeNativeMethods.zeus_rade_global_init();
            // An override may only REMOVE V2 (to exercise the V1-only path on a
            // V2-capable library); it can never claim V2 on a library without it.
            _micExportsAvailable = RadeNativeLoader.TryProbeRadeMic();
            if (!_micExportsAvailable)
                _log?.LogInformation("RADE: library has no mic conditioning/meter exports; those controls are inactive.");
            bool probed = RadeNativeLoader.TryProbeRadeV2();
            _v2Available = probed && (v2AvailableOverride ?? true);
            if (!probed)
                _log?.LogInformation("RADE: library is V1-only (no ABI 2 exports); RADE V2 unavailable.");
        }
        else
        {
            _log?.LogWarning("RADE: zeus_rade native library not found; RADE will pass audio through unchanged.");
        }
    }

    /// <summary>True when the native zeus_rade shim is loadable and RADE can decode.</summary>
    public bool RadeAvailable => _nativeAvailable;
    /// <summary>True when the loaded shim can also run RADE V2 (ABI 2).</summary>
    public bool V2Available => _v2Available;
    public bool Active => _active;
    public bool Synced => _synced;
    public double SnrDb => Volatile.Read(ref _snrDb);
    /// <summary>TX mic tap peak level in dBFS, from the most recent TX block. -120 when silent/idle.</summary>
    public double TxMicLevelDb => Volatile.Read(ref _txMicLevelDb);
    /// <summary>True if the TX mic hit full scale within roughly the last 500 ms.</summary>
    public bool TxMicClip => _txMicClip;
    public int SpeechSampleRateHz => 16000;
    public int ModemSampleRateHz => 8000;
    public RadeProtocolSelection Selection => _selection;

    public string? LibraryVersion => _nativeAvailable
        ? RadeProtocols.LibraryDescription(_selection, _active, _v2Available)
        : null;

    /// <summary>
    /// The protocol in force: the one currently locked on receive; else the one
    /// the current over is transmitting; else the one the next over would use.
    /// </summary>
    public RadeProtocol ProtocolInForce
    {
        get
        {
            var locked = _rxLocked;
            if (locked != RadeProtocol.None) return locked;
            var over = Volatile.Read(ref _txOverSlot);
            if (over is not null) return over.Protocol;
            return ResolveTxProtocol();
        }
    }

    /// <summary>The encoder protocol the NEXT over will latch (tests/telemetry).</summary>
    internal RadeProtocol NextTxProtocol => ResolveTxProtocol();

    /// <summary>The encoder protocol the CURRENT over latched, or None between overs.</summary>
    internal RadeProtocol OverTxProtocol => Volatile.Read(ref _txOverSlot)?.Protocol ?? RadeProtocol.None;

    private RadeProtocol ResolveTxProtocol() =>
        RadeProtocols.ResolveTx(_selection, _lastLocked, _preferred, _v2Available, TxRidesLsb());

    private bool TxRidesLsb()
    {
        var provider = Volatile.Read(ref _txRidesLsb);
        if (provider is null) return false;
        try { return provider(); }
        catch { return false; }
    }

    /// <summary>Source of "the transmitter rides LSB".</summary>
    public void SetTxRidesLsbProvider(Func<bool>? provider) =>
        Volatile.Write(ref _txRidesLsb, provider);

    /// <summary>
    /// Last decoded End-of-Over callsign, or null if none decoded since the modem
    /// opened. Decoded by the native shim via the FreeDV reliable-text LDPC
    /// (CRC-checked, FreeDV-GUI compatible). Drained on the status thread. RADE
    /// V2 carries no callsign: while V2 is in force this is null and any
    /// earlier V1 callsign is dropped rather than shown as if V2 sent it.
    /// </summary>
    public string? RxText
    {
        get
        {
            if (ProtocolInForce == RadeProtocol.V2)
            {
                lock (_rxCallsignLock)
                {
                    _rxCallsign = null;
                    _rxEooSeenSeq = Volatile.Read(ref _rxEooSeq);
                }
                return null;
            }

            long seq = Volatile.Read(ref _rxEooSeq);
            if (seq != _rxEooSeenSeq)
            {
                lock (_rxCallsignLock)
                {
                    // Re-read under the seqlock: copy the bytes, then confirm the
                    // sequence didn't move mid-copy. If it did, leave the cached
                    // value and let the next poll pick up the newer callsign.
                    Span<byte> buf = stackalloc byte[CallsignMax + 1];
                    int len = _rxEooLen;
                    if (len > CallsignMax) len = CallsignMax;
                    for (int i = 0; i < len; i++) buf[i] = _rxEooBytes[i];
                    long after = Volatile.Read(ref _rxEooSeq);
                    if (after == seq)
                    {
                        _rxCallsign = len > 0 ? System.Text.Encoding.ASCII.GetString(buf[..len]) : _rxCallsign;
                        _rxEooSeenSeq = seq;
                    }
                }
            }
            return _rxCallsign;
        }
    }

    // -------- control thread (rare; serialised by _reconfigGate) --------

    public void Activate()
    {
        if (!_nativeAvailable) { _active = true; return; }
        lock (_reconfigGate)
        {
            RetireRx();
            RetireTx();
            ReconcileLocked(selectionChange: false);
            _active = true;
        }
    }

    public void Deactivate()
    {
        _active = false;
        lock (_reconfigGate)
        {
            RetireRx();
            RetireTx();
        }
    }

    /// <summary>
    /// Choose the RADE protocol(s). Control thread only. While active, decoders
    /// and encoders the new selection still needs are KEPT (so Auto -> V2 does
    /// not drop an established V2 lock); only what it no longer needs closes.
    /// <paramref name="preferred"/> is the operator's saved manual RADE
    /// protocol, used only as the Auto TX fallback.
    /// </summary>
    /// <remarks>
    /// The engine applies a selection only while the transmitter is idle (see
    /// CompositeAudioModemPort), so this never runs during an over or its tail.
    /// It can run while the TX monitor previews: a latched preview encoder
    /// that the new selection drops or would not pick is released, so the
    /// next block uses the new selection.
    /// </remarks>
    public void SetProtocol(RadeProtocolSelection selection, RadeProtocol preferred)
    {
        lock (_reconfigGate)
        {
            _preferred = preferred;
            if (_selection == selection) return;
            _selection = selection;
            if (_active && _nativeAvailable) ReconcileLocked();
        }
    }

    /// <summary>
    /// Set the End-of-Over callsign (&lt;= 8 chars) transmitted in the RADE V1 EOO
    /// frame. Control thread only. Applied to the live TX contexts immediately if
    /// open, and re-applied at the next open. Empty/null clears it.
    /// </summary>
    public void SetTxText(string? text)
    {
        string? cs = NormalizeCallsign(text);
        _txCallsign = cs;
        lock (_reconfigGate)
        {
            var set = GateOutTx();
            if (set is null) return;
            try
            {
                // V2 contexts treat this as a safe native no-op.
                foreach (var slot in set.Slots)
                    RadeNativeMethods.zeus_rade_set_tx_callsign(slot.Handle, cs ?? string.Empty);
            }
            finally
            {
                Volatile.Write(ref _txSet, set);
            }
        }
    }

    /// <summary>
    /// Apply pre-encoder mic conditioning changes (null fields leave that
    /// setting unchanged). Control thread only; gates TX out for the duration
    /// of the native calls, mirroring <see cref="SetTxText"/>.
    /// <para>
    /// The settings are retained on the managed side and re-applied to every
    /// fresh TX context, because a native open starts each context at the
    /// native defaults (conditioning off, flat EQ). Without that, conditioning
    /// silently reverted to bypass on any TX reopen — a deactivate/activate
    /// cycle, a protocol change or a mode change — while the panel kept showing
    /// it ON, since the wire is write-only and the panel is its own display
    /// source of truth. Every open encoder (V1 and V2 under Auto) gets it.
    /// </para>
    /// </summary>
    public void SetMicConfig(RadeMicConfig config)
    {
        if (!_nativeAvailable) return;
        lock (_reconfigGate)
        {
            // Fold into the retained state FIRST, so a change made while TX is
            // closed still takes effect at the next open instead of being lost.
            if (config.AgcEnabled is { } agcEnabled) _micAgcEnabled = agcEnabled;
            if (config.AgcTargetDb is { } agcTargetDb) _micAgcTargetDb = agcTargetDb;
            if (config.EqEnabled is { } eqEnabled) _micEqEnabled = eqEnabled;
            if (config.EqBassDb is { } bassDb) _micEqBassDb = bassDb;
            if (config.EqMidDb is { } midDb) _micEqMidDb = midDb;
            if (config.EqTrebleDb is { } trebleDb) _micEqTrebleDb = trebleDb;

            var set = GateOutTx();
            if (set is null) return;
            try
            {
                foreach (var slot in set.Slots) ApplyMicConfigTo(slot.Handle);
            }
            finally
            {
                Volatile.Write(ref _txSet, set);
            }
        }
    }

    /// <summary>
    /// Push the retained mic conditioning onto a TX context. Values match the
    /// shim's own defaults until the operator touches a control, so this stays
    /// a bit-exact passthrough on a fresh context.
    /// </summary>
    private void ApplyMicConfigTo(IntPtr tx)
    {
        if (!_micExportsAvailable) return;
        RadeNativeMethods.zeus_rade_set_mic_agc_enabled(tx, _micAgcEnabled ? 1 : 0);
        RadeNativeMethods.zeus_rade_set_mic_agc_target_db(tx, (float)_micAgcTargetDb);
        RadeNativeMethods.zeus_rade_set_mic_eq_enabled(tx, _micEqEnabled ? 1 : 0);
        RadeNativeMethods.zeus_rade_set_mic_eq_bass_db(tx, (float)_micEqBassDb);
        RadeNativeMethods.zeus_rade_set_mic_eq_mid_db(tx, (float)_micEqMidDb);
        RadeNativeMethods.zeus_rade_set_mic_eq_treble_db(tx, (float)_micEqTrebleDb);
    }

    private static string? NormalizeCallsign(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToUpperInvariant();
        // The first line only (the FreeDV TX text often has a trailing CR/message);
        // RADE's EOO carries a single short callsign.
        int br = t.IndexOfAny(['\r', '\n', ' ']);
        if (br >= 0) t = t[..br];
        return t.Length > CallsignMax ? t[..CallsignMax] : t;
    }

    // -------- RX hot path (DSP tick thread; no lock, no alloc) --------

    public void ProcessRxInPlace(Span<float> block48k)
    {
        if (!_active) return;
        Volatile.Write(ref _rxBusy, 1);
        Thread.MemoryBarrier(); // StoreLoad fence vs. the reconfig handoff
        try
        {
            var set = Volatile.Read(ref _rxSet);
            if (set is null) return; // passthrough (native missing / reconfiguring) — leave block unchanged

            // 1. decimate 48k -> 8k real once; every decoder sees the same samples.
            int n8 = _rxDown.Process(block48k, _rxDecScratch);
            var in8 = _rxDecScratch.AsSpan(0, n8);
            _rx8Clock += n8;

            var v1 = set.V1;
            var v2 = set.V2;
            if (v1 is not null) { v1.In8.Write(in8); Decode(v1); }
            if (v2 is not null) { v2.In8.Write(in8); Decode(v2); }

            // 2. arbitrate whose speech plays.
            var locked = _arbiter.Arbitrate(
                _rx8Clock,
                v1 is not null, v1 is not null && v1.Synced,
                v2 is not null, v2 is not null && v2.Synced);
            _rxLocked = locked;
            if (locked != RadeProtocol.None && _lastLocked != locked) _lastLocked = locked;

            RxChannel? selected = locked switch
            {
                RadeProtocol.V1 => v1,
                RadeProtocol.V2 => v2,
                _ => null,
            };
            if (selected is not null) _rxOutput = selected;
            // With nothing locked the last-played decoder keeps the output, so
            // its residual drains through the squelch fade exactly as a single
            // decoder always did.
            RxChannel source = _rxOutput ?? (v2 ?? v1)!;

            // Speech from a decoder that is not playing is never heard; drop it
            // every block so it cannot become a stale backlog when it takes over.
            if (v1 is not null && !ReferenceEquals(v1, source)) v1.Out48.Clear();
            if (v2 is not null && !ReferenceEquals(v2, source)) v2.Out48.Clear();

            // 3. read decoded speech into the block; silence-pad the remainder
            // (decoded speech replaces the demod audio, exactly like FreeDvModem).
            // A decoder that claims sync but is not trusted (a V1 lock inside the
            // false-lock window) is muted outright, never faded through.
            int filled;
            if (selected is null && source.Synced)
            {
                source.Out48.Clear();
                filled = 0;
            }
            else
            {
                filled = source.Out48.Read(block48k);
            }
            if (filled < block48k.Length) block48k.Slice(filled).Clear();

            bool synced = locked != RadeProtocol.None;
            _synced = synced;
            var telemetry = selected ?? source;
            Volatile.Write(ref _snrDb, Volatile.Read(ref telemetry.SnrDb));

            // Sync-gate the decoded speech (RADE's only squelch): smooth fade-out
            // on loss of sync kills the end-of-over tail; flush the FIFO when fully
            // closed so no stale-noise backlog plays at the head of the next over.
            if (_rxGate.Process(block48k, synced))
                source.Out48.Clear();
        }
        finally
        {
            Volatile.Write(ref _rxBusy, 0);
        }
    }

    // Drain one decoder's 8k input through zeus_rade_rx, nin samples at a time.
    private void Decode(RxChannel ch)
    {
        IntPtr h = ch.Handle;
        int nin = RadeNativeMethods.zeus_rade_nin(h);
        while (nin > 0 && nin <= ch.NinMax && ch.In8.Count >= nin)
        {
            ch.In8.Read(ch.Block8.AsSpan(0, nin));
            // Pack as interleaved complex: real = sample, imag = 0. Odd indices
            // were zeroed at allocation and the decoder never writes rx_in, so
            // they stay 0 across calls — only the real lanes need refreshing.
            for (int i = 0; i < nin; i++) ch.Iq[2 * i] = ch.Block8[i];

            int npcm = RadeNativeMethods.zeus_rade_rx(h, ch.Iq, ch.Pcm);
            ch.Synced = RadeNativeMethods.zeus_rade_sync(h) != 0;
            Volatile.Write(ref ch.SnrDb, RadeNativeMethods.zeus_rade_snr_db(h));

            if (ch.Protocol == RadeProtocol.V1)
            {
                // Pull any freshly-decoded EOO callsign (cheap — just returns the
                // already-decoded string). Publish via the seqlock; no alloc.
                // V2 has no EOO text, so its decoder is never asked.
                int csn = RadeNativeMethods.zeus_rade_get_eoo_callsign(h, _eooScratch);
                if (csn > 0)
                {
                    int n = csn > CallsignMax ? CallsignMax : csn;
                    for (int i = 0; i < n; i++) _rxEooBytes[i] = _eooScratch[i];
                    _rxEooLen = n;
                    Volatile.Write(ref _rxEooSeq, _rxEooSeq + 1);
                }
            }

            if (npcm > 0)
            {
                if (npcm > ch.Pcm.Length) npcm = ch.Pcm.Length;
                ShortToFloat(ch.Pcm, ch.Speech, npcm);
                int up = ch.Up.Process(ch.Speech.AsSpan(0, npcm), ch.Interp);
                WriteBounded(ch.Out48, ch.Interp.AsSpan(0, up));
            }
            nin = RadeNativeMethods.zeus_rade_nin(h);
        }
    }

    // -------- TX hot path (mic-ingest thread; no lock, no alloc) --------

    public void ProcessTxInPlace(Span<float> block48k)
    {
        if (!_active) return;
        Volatile.Write(ref _txBusy, 1);
        Thread.MemoryBarrier();
        try
        {
            var set = Volatile.Read(ref _txSet);
            // RADE active but TX not open (native missing / reconfiguring): SILENCE
            // rather than transmit raw mic — raw SSB on a RADE frequency is worse
            // than dead air.
            if (set is null) { block48k.Clear(); return; }

            // Latch this over's encoder at its first block. It stays latched
            // until FlushTx ends the over, so a receive lock that changes the
            // Auto choice mid-over only takes effect at the next over.
            var slot = _txOverSlot;
            if (slot is null)
            {
                slot = set.Select(ResolveTxProtocol());
                Volatile.Write(ref _txOverSlot, slot);
            }
            if (slot is null) { block48k.Clear(); return; }

            int nspeech = slot.NSpeech, ntx = slot.NTxOut;
            if (nspeech <= 0 || ntx <= 0) { block48k.Clear(); return; }
            IntPtr h = slot.Handle;

            // Gate the mic to silence during pauses so rade_tx encodes clean
            // silence frames instead of vocoding background hiss into garble.
            _micGate.Process(block48k);

            // 1. decimate mic 48k -> 16k speech into the speech ring.
            int n16 = _txDown.Process(block48k, _txDecScratch);
            _tx16In.Write(_txDecScratch.AsSpan(0, n16));

            // 2. encode whole frame-groups: 16k speech -> modem IQ; keep the real
            //    lane (the SSB audio) and upsample 8k -> 48k.
            while (_tx16In.Count >= nspeech)
            {
                _tx16In.Read(_tx16Float.AsSpan(0, nspeech));
                FloatToShort(_tx16Float, _tx16Short, nspeech);
                int n = RadeNativeMethods.zeus_rade_tx(h, _tx16Short, _txIqOut);
                if (_micExportsAvailable)
                {
                    Volatile.Write(ref _txMicLevelDb, RadeNativeMethods.zeus_rade_tx_mic_level_db(h));
                    _txMicClip = RadeNativeMethods.zeus_rade_tx_mic_clip(h) != 0;
                }
                if (n > ntx) n = ntx;
                CopyScaledModemReal(_txIqOut, _txModemReal, n);
                int up = _txUp.Process(_txModemReal.AsSpan(0, n), _txInterp);
                WriteBounded(_txOut48, _txInterp.AsSpan(0, up));
            }

            // 3. Emit only complete host blocks.  On underrun, preserve the
            // partial FIFO for the next tick and send a full silent block.  A
            // partial read would splice zeros into the modem waveform and lose
            // the samples needed to restore its continuous framing.
            ReadLiveBlockOrSilence(_txOut48, block48k);
        }
        finally
        {
            Volatile.Write(ref _txBusy, 0);
        }
    }

    /// <summary>
    /// End the over on a MOX falling edge: drop buffered TX audio so the next
    /// over starts clean (mirrors FreeDvModem.FlushTx) and release the latched
    /// encoder, so the next over picks up any protocol change made meanwhile.
    /// EOO generation belongs exclusively to <see cref="FinishTx"/>, which
    /// retires the TX hot path before touching the exact tail ring.
    /// </summary>
    public void FlushTx()
    {
        if (!_nativeAvailable) return;
        lock (_reconfigGate)
        {
            _txRingClearing = true;
            try
            {
                var set = GateOutTx();
                if (set is null) return;
                _tx16In.Clear(); _txOut48.Clear();
                _txDown.Reset(); _txUp.Reset();
                _micGate.Reset();
                Volatile.Write(ref _txOverSlot, null);
                Volatile.Write(ref _txSet, set);
            }
            finally
            {
                _txRingClearing = false;
            }
        }
    }

    /// <summary>
    /// Drop buffered RX decode state on a MOX transition WITHOUT closing the
    /// modem, so the receiver resumes from an empty, unsynced state instead of
    /// dumping speech decoded from the operator's own transmission during the
    /// over (heard as an end-of-over garble in Zeus's own RX audio). Clears every
    /// decoder's input/output rings, resets the resamplers + sync squelch gate,
    /// and marks unsynced. Control thread; gates the RX hot path out via the
    /// seqlock — mirrors <see cref="FlushTx"/>. RADE has no codec2-style
    /// squelch, so this is the only thing that stops the FARGAN decoder
    /// replaying a self-decoded backlog at un-key.
    /// </summary>
    public void FlushRx()
    {
        if (!_nativeAvailable) return;
        lock (_reconfigGate)
        {
            var set = Volatile.Read(ref _rxSet);
            if (set is null) return;
            Volatile.Write(ref _rxSet, null);
            Thread.MemoryBarrier();
            SpinUntilIdle(ref _rxBusy);
            foreach (var ch in set.Channels) ch.Reset();
            _rxDown.Reset();
            ResetRxSharedState();
            Volatile.Write(ref _rxSet, set);
        }
    }

    /// <summary>
    /// End-of-over flush: encode the residual sub-frame of mic speech so a
    /// COMPLETE final RADE frame exists, then append the End-of-Over frame (V1:
    /// carries the configured callsign via the FreeDV reliable-text LDPC; V2:
    /// detection only), and report the 48 kHz output samples now pending. The
    /// TX tail drain clocks those out before PTT drops, so the over ends on a
    /// whole frame AND the EOO actually reaches the air. Uses the encoder this
    /// over latched (or, for an over with no audio yet, the one it would have
    /// latched). Control-thread only; gates the TX hot path out. Returns 0 when
    /// native is missing or TX isn't open.
    /// </summary>
    public int FinishTx()
    {
        if (!_nativeAvailable) return 0;
        lock (_reconfigGate)
        {
            var set = Volatile.Read(ref _txSet);
            if (set is null) return 0;
            var slot = Volatile.Read(ref _txOverSlot) ?? set.Select(ResolveTxProtocol());
            if (slot is null) return 0;
            int nspeech = slot.NSpeech, ntx = slot.NTxOut;
            if (nspeech <= 0 || ntx <= 0) return 0;
            IntPtr h = slot.Handle;

            Volatile.Write(ref _txSet, null);
            Thread.MemoryBarrier();
            SpinUntilIdle(ref _txBusy);
            Volatile.Write(ref _txOverSlot, slot);

            try
            {
                // Complete any residual speech into whole frame(s).
                while (_tx16In.Count > 0)
                {
                    int have = Math.Min(_tx16In.Count, nspeech);
                    _tx16In.Read(_tx16Float.AsSpan(0, have));
                    if (have < nspeech) Array.Clear(_tx16Float, have, nspeech - have);
                    FloatToShort(_tx16Float, _tx16Short, nspeech);
                    int n = RadeNativeMethods.zeus_rade_tx(h, _tx16Short, _txIqOut);
                    if (n > ntx) n = ntx;
                    CopyScaledModemReal(_txIqOut, _txModemReal, n);
                    int up = _txUp.Process(_txModemReal.AsSpan(0, n), _txInterp);
                    WriteTailExact(_txInterp.AsSpan(0, up));
                }

                // Append the EOO frame as the final modem audio.
                if (slot.NEooOut > 0)
                {
                    int n = RadeNativeMethods.zeus_rade_tx_eoo(h, _txIqOut);
                    if (n > slot.NEooOut) n = slot.NEooOut;
                    CopyScaledModemReal(_txIqOut, _txModemReal, n);
                    int up = _txUp.Process(_txModemReal.AsSpan(0, n), _txInterp);
                    WriteTailExact(_txInterp.AsSpan(0, up));
                }

                // radae_c detects EOO only after enough following modem samples
                // have clocked the complete frame through its variable-nin receive
                // state. The native proof harness uses ninMax * 8 zeros (of the
                // protocol being sent). Those samples must be part of
                // PendingTxSamples so TxAudioIngest keeps PTT asserted until the
                // EOO is actually detectable on air.
                int padRemaining = checked(slot.NinMax * 8);
                while (padRemaining > 0)
                {
                    int chunk = Math.Min(
                        padRemaining,
                        Math.Min(_txModemReal.Length, _txInterp.Length / FreeDvResampler.Factor));
                    _txModemReal.AsSpan(0, chunk).Clear();
                    int up = _txUp.Process(_txModemReal.AsSpan(0, chunk), _txInterp);
                    WriteTailExact(_txInterp.AsSpan(0, up));
                    padRemaining -= chunk;
                }

                return _txOut48.Count;
            }
            finally
            {
                // Never strand TX in the seqlock's retired state. In particular,
                // WriteTailExact deliberately throws on capacity exhaustion; a
                // failed tail build must not silence every later block until the
                // modem is reactivated.
                Volatile.Write(ref _txSet, set);
            }
        }
    }

    /// <summary>48 kHz output samples awaiting transmit (the TX tail backlog).</summary>
    public int TxPendingOutSamples() => _txOut48.Count;

    /// <summary>
    /// Drain queued modem output into <paramref name="block48k"/> WITHOUT encoding
    /// new speech; silence-pads the remainder and returns the real (non-pad)
    /// count. Used by the end-of-over tail drain while the TX hot path is quiesced
    /// (the drain owns TX exclusively). Returns 0 when nothing is pending.
    /// Holds the TX busy flag like the hot path, so a control-thread retire
    /// (RADE released mid-tail) waits for the read before clearing the ring.
    /// </summary>
    public int DrainTo(Span<float> block48k)
    {
        Volatile.Write(ref _txBusy, 1);
        Thread.MemoryBarrier();
        try
        {
            // A clear is in progress (TX retired or flushed): the tail is gone.
            if (_txRingClearing)
            {
                block48k.Clear();
                return 0;
            }
            int filled = _txOut48.Read(block48k);
            if (filled < block48k.Length) block48k.Slice(filled).Clear();
            return filled;
        }
        finally
        {
            Volatile.Write(ref _txBusy, 0);
        }
    }

    internal static void CopyScaledModemReal(float[] interleavedIq, float[] modemReal, int count)
    {
        for (int i = 0; i < count; i++)
            modemReal[i] = interleavedIq[2 * i] * TxOutputScale;
    }

    internal static bool ReadLiveBlockOrSilence(FreeDvSampleRing ring, Span<float> block)
    {
        if (ring.Count < block.Length)
        {
            block.Clear();
            return false;
        }

        ring.Read(block);
        return true;
    }

    private static void WriteBounded(FreeDvSampleRing ring, ReadOnlySpan<float> src)
    {
        if (ring.Count > MaxOutFifo) ring.Drop(ring.Count - MaxOutFifo);
        ring.Write(src); // short write (ring full) acts as a final safety drop
    }

    private void WriteTailExact(ReadOnlySpan<float> src)
    {
        int written = _txOut48.Write(src);
        if (written == src.Length) return;
        _log?.LogError(
            "RADE: TX tail capacity exhausted written={Written} requested={Requested}",
            written,
            src.Length);
        throw new InvalidOperationException("RADE TX tail exceeds its fixed capacity");
    }

    private static void ShortToFloat(short[] src, float[] dst, int n)
    {
        for (int i = 0; i < n; i++) dst[i] = src[i] * (1f / 32768f);
    }

    private static void FloatToShort(float[] src, short[] dst, int n)
    {
        for (int i = 0; i < n; i++)
            dst[i] = (short)Math.Clamp((int)MathF.Round(src[i] * 32767f), short.MinValue, short.MaxValue);
    }

    // -------- reconfig helpers (control thread, under _reconfigGate) --------

    /// <summary>
    /// Bring the published decoder and encoder sets in line with the selection.
    /// Contexts the selection still needs are kept; missing ones are opened;
    /// unneeded ones are closed after the hot path is gated out.
    /// </summary>
    private void ReconcileLocked(bool selectionChange = true)
    {
        if (!_nativeAvailable)
        {
            // Leave the sets null — RX passes through, TX silences.
            RetireRx();
            RetireTx();
            return;
        }

        bool wantV1 = _selection != RadeProtocolSelection.V2;
        bool wantV2 = _v2Available && _selection != RadeProtocolSelection.V1;

        var oldRx = Volatile.Read(ref _rxSet);
        RxChannel? rx1 = wantV1 ? oldRx?.V1 ?? OpenRx(RadeProtocol.V1) : null;
        RxChannel? rx2 = wantV2 ? oldRx?.V2 ?? OpenRx(RadeProtocol.V2) : null;
        if (!ReferenceEquals(rx1, oldRx?.V1) || !ReferenceEquals(rx2, oldRx?.V2))
            PublishRx(rx1 is null && rx2 is null ? null : new RxSet(rx1, rx2));

        var oldTx = Volatile.Read(ref _txSet);
        TxSlot? tx1 = wantV1 ? oldTx?.V1 ?? OpenTx(RadeProtocol.V1) : null;
        TxSlot? tx2 = wantV2 ? oldTx?.V2 ?? OpenTx(RadeProtocol.V2) : null;
        if (!ReferenceEquals(tx1, oldTx?.V1) || !ReferenceEquals(tx2, oldTx?.V2)
            || (selectionChange && LatchedIsStale()))
            PublishTx(tx1 is null && tx2 is null ? null : new TxSet(tx1, tx2), selectionChange);

        _log?.LogInformation(
            "RADE: selection={Selection} rx=[{RxV1}{RxV2}] tx=[{TxV1}{TxV2}] nextTx={NextTx}",
            _selection,
            rx1 is null ? "" : "V1 ", rx2 is null ? "" : "V2",
            tx1 is null ? "" : "V1 ", tx2 is null ? "" : "V2",
            ResolveTxProtocol());
    }

    private RxChannel? OpenRx(RadeProtocol protocol)
    {
        IntPtr h = OpenContext(protocol);
        if (h == IntPtr.Zero) return null;
        int ninMax = RadeNativeMethods.zeus_rade_nin_max(h);
        int maxPcm = RadeNativeMethods.zeus_rade_max_pcm_per_rx(h);
        if (ninMax <= 0 || maxPcm <= 0)
        {
            _log?.LogError("RADE: {Protocol} RX context reported invalid sizes ninMax={NinMax} maxPcm={MaxPcm}", protocol, ninMax, maxPcm);
            RadeNativeMethods.zeus_rade_close(h);
            return null;
        }
        _log?.LogInformation("RADE: opened {Protocol} RX ninMax={NinMax} maxPcm={MaxPcm}", protocol, ninMax, maxPcm);
        return new RxChannel(h, protocol, ninMax, maxPcm);
    }

    private TxSlot? OpenTx(RadeProtocol protocol)
    {
        IntPtr h = OpenContext(protocol);
        if (h == IntPtr.Zero) return null;
        var slot = new TxSlot(
            h,
            protocol,
            RadeNativeMethods.zeus_rade_n_speech_samples(h),
            RadeNativeMethods.zeus_rade_n_tx_out(h),
            RadeNativeMethods.zeus_rade_n_tx_eoo_out(h),
            RadeNativeMethods.zeus_rade_nin_max(h));
        // Apply the operator's EOO callsign (a V2 no-op) and mic conditioning
        // to the fresh context before it goes live: a fresh context opens at
        // the native defaults (off / flat), so without this the operator's AGC
        // and EQ silently revert to bypass on every TX reopen.
        RadeNativeMethods.zeus_rade_set_tx_callsign(h, _txCallsign ?? string.Empty);
        ApplyMicConfigTo(h);
        _log?.LogInformation(
            "RADE: opened {Protocol} TX speech={Speech} out={Out} eoo={Eoo} ninMax={NinMax}",
            protocol, slot.NSpeech, slot.NTxOut, slot.NEooOut, slot.NinMax);
        return slot;
    }

    private IntPtr OpenContext(RadeProtocol protocol)
    {
        IntPtr h;
        if (protocol == RadeProtocol.V2)
        {
            if (!_v2Available) return IntPtr.Zero;
            h = RadeNativeMethods.zeus_rade_open_protocol(2);
            if (h != IntPtr.Zero && RadeNativeMethods.zeus_rade_protocol(h) != 2)
            {
                RadeNativeMethods.zeus_rade_close(h);
                h = IntPtr.Zero;
            }
        }
        else
        {
            // zeus_rade_open() is V1 on every shim revision, old or new.
            h = RadeNativeMethods.zeus_rade_open();
        }
        if (h == IntPtr.Zero)
            _log?.LogError("RADE: opening a {Protocol} context failed — that protocol stays unavailable this session.", protocol);
        return h;
    }

    // Gate the RX hot path out, close every decoder the new set drops, reset
    // the shared RX state, publish the replacement (null = passthrough).
    private void PublishRx(RxSet? replacement)
    {
        var old = Volatile.Read(ref _rxSet);
        Volatile.Write(ref _rxSet, null);
        Thread.MemoryBarrier();
        SpinUntilIdle(ref _rxBusy);
        bool kept = false;
        if (old is not null)
        {
            foreach (var ch in old.Channels)
            {
                if (replacement is not null && replacement.Contains(ch)) { kept = true; continue; }
                RadeNativeMethods.zeus_rade_close(ch.Handle);
            }
        }
        // A kept decoder keeps its stream continuous (no decimator reset), so an
        // established lock survives the change; a wholly new set starts clean.
        if (!kept) _rxDown.Reset();
        ResetRxSharedState();
        Volatile.Write(ref _rxSet, replacement);
    }

    private void ResetRxSharedState()
    {
        _rxGate.Reset();
        _arbiter.Reset();
        _rxOutput = null;
        _rxLocked = RadeProtocol.None;
        _synced = false;
        Volatile.Write(ref _snrDb, 0d);
    }

    private void RetireRx() => PublishRx(null);

    // Gate the TX hot path out, close every encoder the new set drops, size the
    // shared scratch for the largest remaining encoder, publish the replacement.
    // An over whose latched encoder is dropped (only an explicit operator
    // selection change can do that) is abandoned cleanly.
    // A latched encoder no longer matches what the selection would pick (e.g.
    // V1 latched under monitor, then Auto chosen with a V2 preference).
    private bool LatchedIsStale()
    {
        var latched = Volatile.Read(ref _txOverSlot);
        return latched is not null && latched.Protocol != ResolveTxProtocol();
    }

    private void PublishTx(TxSet? replacement, bool selectionChange = false)
    {
        _txRingClearing = true;
        try
        {
            PublishTxCore(replacement, selectionChange);
        }
        finally
        {
            _txRingClearing = false;
        }
    }

    private void PublishTxCore(TxSet? replacement, bool selectionChange)
    {
        var old = GateOutTx();
        var latched = Volatile.Read(ref _txOverSlot);
        if (old is not null)
        {
            foreach (var slot in old.Slots)
            {
                if (replacement is not null && replacement.Contains(slot)) continue;
                RadeNativeMethods.zeus_rade_close(slot.Handle);
            }
        }
        bool latchLost = latched is not null
            && (replacement is null || !replacement.Contains(latched)
                || (selectionChange && latched.Protocol != ResolveTxProtocol()));
        if (latched is null || latchLost)
        {
            Volatile.Write(ref _txOverSlot, null);
            _tx16In.Clear(); _txOut48.Clear();
            _txDown.Reset(); _txUp.Reset();
            _micGate.Reset();
        }
        if (replacement is not null)
        {
            int nSpeech = 0, txMax = 0;
            foreach (var slot in replacement.Slots)
            {
                nSpeech = Math.Max(nSpeech, slot.NSpeech);
                txMax = Math.Max(txMax, Math.Max(slot.NTxOut, slot.NEooOut));
            }
            EnsureFloat(ref _tx16Float, nSpeech);
            EnsureShort(ref _tx16Short, nSpeech);
            EnsureFloat(ref _txIqOut, 2 * txMax);
            EnsureFloat(ref _txModemReal, txMax);
            EnsureFloat(ref _txInterp, FreeDvResampler.InterpolatedLength(txMax));
        }
        Volatile.Write(ref _txSet, replacement);
    }

    private void RetireTx() => PublishTx(null);

    // Publish null on the TX hot path and wait for any in-flight block to
    // finish. Returns the set that was published (null when TX is closed);
    // the caller republishes it (or a replacement) when done.
    private TxSet? GateOutTx()
    {
        var set = Volatile.Read(ref _txSet);
        Volatile.Write(ref _txSet, null);
        Thread.MemoryBarrier();
        SpinUntilIdle(ref _txBusy);
        return set;
    }

    private static void SpinUntilIdle(ref int busyFlag)
    {
        var sw = new SpinWait();
        while (Volatile.Read(ref busyFlag) == 1) sw.SpinOnce();
    }

    private static void EnsureFloat(ref float[] buf, int n)
    {
        if (buf.Length < n) buf = new float[Math.Max(n, buf.Length * 2)];
    }

    private static void EnsureShort(ref short[] buf, int n)
    {
        if (buf.Length < n) buf = new short[Math.Max(n, buf.Length * 2)];
    }

    public void Dispose()
    {
        _active = false;
        lock (_reconfigGate)
        {
            RetireRx();
            RetireTx();
        }
    }

    // -------- per-protocol state (built off the hot path) --------

    /// <summary>
    /// One RX decoder with its own input/output rings and scratch, sized from
    /// its own context. Fresh arrays are zeroed by the runtime, so the IQ
    /// buffer's imaginary lanes start (and stay) at 0.
    /// </summary>
    private sealed class RxChannel
    {
        internal RxChannel(IntPtr handle, RadeProtocol protocol, int ninMax, int maxPcm)
        {
            Handle = handle;
            Protocol = protocol;
            NinMax = ninMax;
            Block8 = new float[ninMax];
            Iq = new float[2 * ninMax];
            Pcm = new short[maxPcm];
            Speech = new float[maxPcm];
            Interp = new float[RadeResampler.InterpolatedLength(maxPcm)];
        }

        internal readonly IntPtr Handle;
        internal readonly RadeProtocol Protocol;
        internal readonly int NinMax;
        internal readonly FreeDvSampleRing In8 = new(8192);     // 8 kHz modem samples awaiting zeus_rade_rx
        internal readonly FreeDvSampleRing Out48 = new(16384);  // decoded speech, 48 kHz, awaiting playout
        // 16k -> 48k uses the wideband 3:1 interpolator so FARGAN's full
        // bandwidth survives.
        internal readonly RadeResampler.Interpolator Up = RadeResampler.NewInterpolator();
        internal readonly float[] Block8;
        internal readonly float[] Iq;
        internal readonly short[] Pcm;
        internal readonly float[] Speech;
        internal readonly float[] Interp;
        internal volatile bool Synced;
        internal double SnrDb;

        internal void Reset()
        {
            In8.Clear();
            Out48.Clear();
            Up.Reset();
            Synced = false;
            Volatile.Write(ref SnrDb, 0d);
        }
    }

    private sealed class RxSet(RxChannel? v1, RxChannel? v2)
    {
        internal RxChannel? V1 { get; } = v1;
        internal RxChannel? V2 { get; } = v2;

        internal IEnumerable<RxChannel> Channels
        {
            get
            {
                if (V1 is not null) yield return V1;
                if (V2 is not null) yield return V2;
            }
        }

        internal bool Contains(RxChannel ch) => ReferenceEquals(ch, V1) || ReferenceEquals(ch, V2);
    }

    private sealed record TxSlot(
        IntPtr Handle,
        RadeProtocol Protocol,
        int NSpeech,
        int NTxOut,
        int NEooOut,
        int NinMax);

    private sealed class TxSet(TxSlot? v1, TxSlot? v2)
    {
        internal TxSlot? V1 { get; } = v1;
        internal TxSlot? V2 { get; } = v2;

        internal IEnumerable<TxSlot> Slots
        {
            get
            {
                if (V1 is not null) yield return V1;
                if (V2 is not null) yield return V2;
            }
        }

        internal bool Contains(TxSlot slot) => ReferenceEquals(slot, V1) || ReferenceEquals(slot, V2);

        // Hot-path safe (no alloc). A V2 request may fall back to V1 if the V2
        // encoder failed to open; a V1 request NEVER falls back to V2: V1 is
        // what is asked for exactly when V2 would be wrong (an LSB
        // transmitter under Auto, or a manual V1 pick). No encoder = silence.
        internal TxSlot? Select(RadeProtocol protocol) =>
            protocol == RadeProtocol.V2 ? V2 ?? V1 : V1;
    }
}
