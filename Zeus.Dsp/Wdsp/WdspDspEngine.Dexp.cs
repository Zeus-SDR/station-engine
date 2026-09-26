// SPDX-License-Identifier: GPL-2.0-or-later
//
// DEXP (downward expander / noise gate) on the TX mic block — WDSP dexp.c.
//
// Thetis (ChannelMaster/cmaster.c) creates one dexp per transmitter and runs
// xdexp(tx) in place on the mic buffer immediately before fexchange0 into TXA.
// Zeus mirrors that seam in ProcessTxBlock: after the TX-audio plugin handler
// fills the mic block and before fexchange2. Zeus has no VOX, so run_vox=0 and
// the pushvox callback is null (every pushvox call in dexp.c is guarded by
// `a->run_vox &&`); DEXP only gates audio.
//
// Native constraints handled here:
//   * pdexp[4] is a process-global array indexed by id. Several WdspDspEngine
//     instances can coexist (engine swap, OfflinePreviewDspEngine), so ids come
//     from a static 0..3 allocator; when exhausted DEXP is skipped (logged) and
//     the TX path runs without it — never a colliding id.
//   * WDSP keeps the raw in/out pointer, so the complex-double buffer is
//     NativeMemory and is freed only after destroy_dexp.
//   * The side-channel filter builds create_fircore(size, ..., nc): nc must be a
//     power of two and >= size, so the block size must be a power of two.
//   * The look-ahead ring is sized to `rate` samples, so size < rate and the
//     look-ahead stays under 1 s (DexpConfig.Clamped caps it at 999 ms).
//   * Every setter and xdexp take dexp's own critical section, and destroy_dexp
//     frees the instance, so all native DEXP calls are serialized by _dexpLock
//     (lock order: _txaLock -> _dexpLock -> _nativeLifecycleLock). The audio
//     thread only takes _dexpLock, and only while DEXP is enabled.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Dsp.Wdsp;

public sealed partial class WdspDspEngine
{
    // native/wdsp/dexp.c: `DEXP pdexp[4];`
    private const int DexpMaxInstances = 4;
    // Thetis cmaster.c create_dexp nc. Raised to the block size when the TXA
    // input block is larger (fircore needs nc >= size).
    private const int DexpMinFilterTaps = 256;
    // Floor for the peak read-out so a silent mic never yields -Infinity.
    private const double DexpPeakFloorDbv = -160.0;

    private static readonly object s_dexpIdLock = new();
    private static readonly bool[] s_dexpIdsInUse = new bool[DexpMaxInstances];

    private static readonly string[] DexpRequiredExports =
    [
        nameof(NativeMethods.create_dexp),
        nameof(NativeMethods.destroy_dexp),
        nameof(NativeMethods.flush_dexp),
        nameof(NativeMethods.xdexp),
        nameof(NativeMethods.SetDEXPRun),
        nameof(NativeMethods.SetDEXPDetectorTau),
        nameof(NativeMethods.SetDEXPAttackTime),
        nameof(NativeMethods.SetDEXPReleaseTime),
        nameof(NativeMethods.SetDEXPHoldTime),
        nameof(NativeMethods.SetDEXPExpansionRatio),
        nameof(NativeMethods.SetDEXPHysteresisRatio),
        nameof(NativeMethods.SetDEXPAttackThreshold),
        nameof(NativeMethods.SetDEXPLowCut),
        nameof(NativeMethods.SetDEXPHighCut),
        nameof(NativeMethods.SetDEXPRunSideChannelFilter),
        nameof(NativeMethods.SetDEXPRunAudioDelay),
        nameof(NativeMethods.SetDEXPAudioDelay),
        nameof(NativeMethods.GetDEXPPeakSignal),
    ];

    /// <summary>The loaded libwdsp exports every dexp.c entry point Zeus calls.
    /// An older libwdsp without them leaves DEXP inert instead of throwing
    /// EntryPointNotFoundException on the TX path.</summary>
    public static bool DexpAvailable => AllNativeExportsAvailable(DexpRequiredExports);

    private readonly object _dexpLock = new();
    // Operator config (clamped). Cached even while no dexp instance exists so
    // it is applied when TXA opens. Under _dexpLock.
    private DexpConfig _dexpConfig = DexpConfig.Default;
    // Config the native instance currently holds; drives the per-field diff so
    // only changed parameters are pushed (each setter resets dexp's gate state).
    private DexpConfig? _dexpApplied;
    private int _dexpId = -1;
    private int _dexpSize;
    private unsafe double* _dexpBuffer;
    // Audio-thread fast path: an instance exists / the operator has DEXP
    // enabled. Written under _dexpLock, read lock-free.
    private volatile bool _dexpInstanceLive;
    private volatile bool _dexpActive;
    // Set whenever a block bypasses DEXP (disabled, digital/roger/injected
    // bypass) so stale detector / look-ahead state is flushed on resume.
    private volatile bool _dexpNeedsFlush = true;

    // ---- Live detector meter ------------------------------------------------
    // dexp.c exports the per-block detector peak (GetDEXPPeakSignal) but not its
    // gate state. The gate is derived here per block with dexp.c's own rules
    // (open above attack_thresh; below hold_thresh = attack_thresh x
    // hysteresis_ratio starts the hold count; back above attack_thresh cancels
    // it; hold expiry closes). That is block-granular (5-11 ms) rather than
    // per-sample, which is ample for a ~10 Hz UI indicator and needs no native
    // change to the six bundled libwdsp binaries.
    //
    // Meter-only mode: while DEXP is OFF, a meter poll within the last
    // DexpMeterArmMs keeps the detector running on a COPY of the mic block
    // (output discarded) so the operator can set the threshold before enabling.
    // With nobody metering and DEXP off, the audio path stays a volatile read
    // plus a tick read.
    private const long DexpMeterArmMs = 2000;
    private const long DexpMeterStaleMs = 500;
    private long _dexpMeterArmedUntilMs = long.MinValue;
    private double _dexpMeterPeakDbv = DexpPeakFloorDbv; // under _dexpLock
    private bool _dexpGateOpen;                          // under _dexpLock
    private int _dexpHoldRemaining = -1;                 // samples; -1 = not holding
    private long _dexpLastBlockMs = long.MinValue / 2;   // under _dexpLock

    public void SetDexpConfig(DexpConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (_disposed != 0) return;
        var next = cfg.Clamped();
        lock (_dexpLock)
        {
            _dexpConfig = next;
            if (_dexpId >= 0 && _dexpApplied is { } applied)
            {
                ApplyDexpDiffLocked(_dexpId, applied, next);
                _dexpApplied = next;
            }
            if (!_dexpActive && next.Enabled) _dexpNeedsFlush = true;
            _dexpActive = _dexpId >= 0 && next.Enabled;
        }
        _log.LogInformation(
            "wdsp.setDexp enabled={Enabled} thresh={Thresh:F1}dBV exp={Exp:F1}dB hyst={Hyst:F1}dB attack={Attack:F0}ms hold={Hold:F0}ms release={Release:F0}ms tau={Tau:F0}ms scf={Scf}({Low:F0}-{High:F0}Hz) lookAhead={La}({LaMs:F0}ms) instance={Id}",
            next.Enabled, next.ThresholdDbv, next.ExpansionDb, next.HysteresisDb,
            next.AttackMs, next.HoldMs, next.ReleaseMs, next.DetectorTauMs,
            next.SideChannelFilterEnabled, next.SideChannelLowHz, next.SideChannelHighHz,
            next.LookAheadEnabled, next.LookAheadMs, _dexpId);
    }

    /// <summary>Live DEXP detector reading. Polling also arms meter-only mode
    /// for <see cref="DexpMeterArmMs"/> so the detector runs while DEXP is OFF.
    /// <c>Active</c> is false when no TX block has been metered recently (TXA
    /// closed / neither keyed nor monitoring / digital bypass / no dexp
    /// exports).</summary>
    public DexpMeterDto GetDexpMeter()
    {
        if (_disposed != 0) return DexpMeterDto.Inactive;
        long now = Environment.TickCount64;
        Volatile.Write(ref _dexpMeterArmedUntilMs, now + DexpMeterArmMs);
        lock (_dexpLock)
        {
            bool active = _dexpId >= 0 && now - _dexpLastBlockMs <= DexpMeterStaleMs;
            return active
                ? new DexpMeterDto(_dexpMeterPeakDbv, _dexpGateOpen, true)
                : DexpMeterDto.Inactive;
        }
    }

    // Realtime path. Called from ProcessTxBlock with the mic block that
    // fexchange2 is about to consume. No allocation. Enabled: xdexp in place
    // and the gated block replaces the mic. Meter-only: xdexp on the native
    // copy and the mic is never written (bit-identical). Otherwise it returns
    // after a volatile read (plus a tick read while an instance exists).
    private unsafe void ProcessDexpBlock(Span<float> mic, bool bypass)
    {
        if (bypass || !_dexpInstanceLive)
        {
            _dexpNeedsFlush = true;
            return;
        }
        if (!_dexpActive
            && Environment.TickCount64 > Volatile.Read(ref _dexpMeterArmedUntilMs))
        {
            _dexpNeedsFlush = true;
            return;
        }
        lock (_dexpLock)
        {
            if (_dexpId < 0 || mic.Length != _dexpSize)
            {
                _dexpNeedsFlush = true;
                return;
            }
            if (_dexpNeedsFlush)
            {
                NativeMethods.flush_dexp(_dexpId);
                _dexpNeedsFlush = false;
                _dexpGateOpen = false;
                _dexpHoldRemaining = -1;
            }
            double* buf = _dexpBuffer;
            for (int i = 0; i < mic.Length; i++)
            {
                buf[2 * i] = mic[i];
                buf[2 * i + 1] = 0.0;
            }
            NativeMethods.xdexp(_dexpId);
            if (_dexpConfig.Enabled)
            {
                for (int i = 0; i < mic.Length; i++)
                    mic[i] = (float)buf[2 * i];
            }
            NativeMethods.GetDEXPPeakSignal(_dexpId, out double peak);
            UpdateDexpMeterLocked(peak, mic.Length);
        }
    }

    // Caller holds _dexpLock. Mirrors dexp.c's LOW/ATTACK/HIGH/HOLD/DECAY
    // transitions at block granularity; decay counts as closed.
    private void UpdateDexpMeterLocked(double peak, int blockSamples)
    {
        var cfg = _dexpConfig;
        double attack = DexpAttackThreshold(cfg);
        double hold = attack * DexpHysteresisRatio(cfg);
        if (!double.IsFinite(peak) || peak < 0.0) peak = 0.0;
        if (peak > attack)
        {
            _dexpGateOpen = true;
            _dexpHoldRemaining = -1;
        }
        else if (_dexpGateOpen)
        {
            if (_dexpHoldRemaining < 0)
            {
                if (peak < hold)
                    _dexpHoldRemaining = (int)(cfg.HoldMs / 1000.0 * _txaInputRateHz);
            }
            else
            {
                _dexpHoldRemaining -= blockSamples;
                if (_dexpHoldRemaining <= 0)
                {
                    _dexpGateOpen = false;
                    _dexpHoldRemaining = -1;
                }
            }
        }
        _dexpMeterPeakDbv = peak > 0.0
            ? Math.Max(DexpPeakFloorDbv, 20.0 * Math.Log10(peak))
            : DexpPeakFloorDbv;
        _dexpLastBlockMs = Environment.TickCount64;
    }

    internal void ProcessDexpBlockForTests(Span<float> mic, bool bypass = false) =>
        ProcessDexpBlock(mic, bypass);

    internal bool HasDexpInstanceForTests
    {
        get { lock (_dexpLock) return _dexpId >= 0; }
    }

    // Caller holds _txaLock. Called once the TXA channel is open (size / rate
    // latched). Never throws: a DEXP failure must not fail TX bring-up.
    private unsafe void CreateDexpForTxaLocked()
    {
        int size = _txaInSize;
        int rate = _txaInputRateHz;
        try
        {
            if (!DexpAvailable)
            {
                _log.LogInformation("wdsp.dexp unavailable — loaded libwdsp lacks the dexp exports; DEXP disabled");
                return;
            }
            if (size <= 0 || (size & (size - 1)) != 0 || size >= rate)
            {
                _log.LogWarning(
                    "wdsp.dexp skipped — TXA input block {Size} must be a power of two below the {Rate} Hz rate",
                    size, rate);
                return;
            }

            lock (_dexpLock)
            {
                if (_dexpId >= 0) return;
                int id = ReserveDexpId();
                if (id < 0)
                {
                    _log.LogWarning(
                        "wdsp.dexp skipped — all {Max} native DEXP instances are in use by other engines",
                        DexpMaxInstances);
                    return;
                }

                nuint bytes = (nuint)(2 * size * sizeof(double));
                double* buf = (double*)NativeMemory.AlignedAlloc(bytes, 64);
                NativeMemory.Clear(buf, bytes);
                var cfg = _dexpConfig;
                int nc = Math.Max(DexpMinFilterTaps, size);
                try
                {
                    // create_dexp builds the side-channel FIR (FFTW plans), so it
                    // shares the process-wide native lifecycle lock.
                    lock (_nativeLifecycleLock)
                    {
                        NativeMethods.create_dexp(
                            id,
                            run_dexp: cfg.Enabled ? 1 : 0,
                            size: size,
                            @in: buf,
                            @out: buf,
                            rate: rate,
                            dettau: cfg.DetectorTauMs / 1000.0,
                            tattack: cfg.AttackMs / 1000.0,
                            tdecay: cfg.ReleaseMs / 1000.0,
                            thold: cfg.HoldMs / 1000.0,
                            exp_ratio: DexpExpansionRatio(cfg),
                            hyst_ratio: DexpHysteresisRatio(cfg),
                            attack_thresh: DexpAttackThreshold(cfg),
                            nc: nc,
                            wtype: 0,
                            lowcut: cfg.SideChannelLowHz,
                            highcut: cfg.SideChannelHighHz,
                            run_filt: cfg.SideChannelFilterEnabled ? 1 : 0,
                            run_vox: 0,
                            run_audelay: DexpRunAudioDelay(cfg),
                            audelay: cfg.LookAheadMs / 1000.0,
                            pushvox: IntPtr.Zero,
                            antivox_run: 0,
                            antivox_size: size,
                            antivox_rate: rate,
                            antivox_gain: 0.01,
                            antivox_tau: 0.01);
                    }
                }
                catch
                {
                    NativeMemory.AlignedFree(buf);
                    ReleaseDexpId(id);
                    throw;
                }

                _dexpId = id;
                _dexpSize = size;
                _dexpBuffer = buf;
                _dexpApplied = cfg;
                _dexpNeedsFlush = true;
                _dexpActive = cfg.Enabled;
                _dexpInstanceLive = true;
                _log.LogInformation(
                    "wdsp.dexp created instance={Id} size={Size} rate={Rate} nc={Nc} enabled={Enabled}",
                    id, size, rate, nc, cfg.Enabled);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "wdsp.dexp create failed — TX continues without DEXP");
        }
    }

    // Caller holds _txaLock (TXA teardown). Destroys the native instance before
    // freeing the buffer WDSP points at, then returns the id to the pool.
    private unsafe void DestroyDexpLocked()
    {
        lock (_dexpLock)
        {
            _dexpActive = false;
            _dexpInstanceLive = false;
            if (_dexpId < 0) return;
            int id = _dexpId;
            try
            {
                lock (_nativeLifecycleLock)
                    NativeMethods.destroy_dexp(id);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "wdsp.dexp destroy failed instance={Id}", id);
            }
            finally
            {
                NativeMemory.AlignedFree(_dexpBuffer);
                _dexpBuffer = null;
                _dexpId = -1;
                _dexpSize = 0;
                _dexpApplied = null;
                ReleaseDexpId(id);
            }
        }
    }

    // Caller holds _dexpLock. Parameters first, run flags last, so enabling
    // lands on a fully configured stage. Each WDSP setter re-runs calc_dexp
    // (resetting the gate), so unchanged values are skipped.
    private static void ApplyDexpDiffLocked(int id, DexpConfig a, DexpConfig n)
    {
        if (n.DetectorTauMs != a.DetectorTauMs) NativeMethods.SetDEXPDetectorTau(id, n.DetectorTauMs / 1000.0);
        if (n.AttackMs != a.AttackMs) NativeMethods.SetDEXPAttackTime(id, n.AttackMs / 1000.0);
        if (n.HoldMs != a.HoldMs) NativeMethods.SetDEXPHoldTime(id, n.HoldMs / 1000.0);
        if (n.ReleaseMs != a.ReleaseMs) NativeMethods.SetDEXPReleaseTime(id, n.ReleaseMs / 1000.0);
        if (n.ExpansionDb != a.ExpansionDb) NativeMethods.SetDEXPExpansionRatio(id, DexpExpansionRatio(n));
        if (n.HysteresisDb != a.HysteresisDb) NativeMethods.SetDEXPHysteresisRatio(id, DexpHysteresisRatio(n));
        if (n.ThresholdDbv != a.ThresholdDbv) NativeMethods.SetDEXPAttackThreshold(id, DexpAttackThreshold(n));
        if (n.LookAheadMs != a.LookAheadMs) NativeMethods.SetDEXPAudioDelay(id, n.LookAheadMs / 1000.0);
        if (n.SideChannelLowHz != a.SideChannelLowHz || n.SideChannelHighHz != a.SideChannelHighHz)
        {
            // Each cut re-plans the side-channel FIR. Order the two calls so
            // the intermediate band never inverts (low above high).
            lock (_nativeLifecycleLock)
            {
                if (n.SideChannelLowHz >= a.SideChannelHighHz)
                {
                    NativeMethods.SetDEXPHighCut(id, n.SideChannelHighHz);
                    NativeMethods.SetDEXPLowCut(id, n.SideChannelLowHz);
                }
                else
                {
                    NativeMethods.SetDEXPLowCut(id, n.SideChannelLowHz);
                    NativeMethods.SetDEXPHighCut(id, n.SideChannelHighHz);
                }
            }
        }
        if (n.SideChannelFilterEnabled != a.SideChannelFilterEnabled)
            NativeMethods.SetDEXPRunSideChannelFilter(id, n.SideChannelFilterEnabled ? 1 : 0);
        if (DexpRunAudioDelay(n) != DexpRunAudioDelay(a))
            NativeMethods.SetDEXPRunAudioDelay(id, DexpRunAudioDelay(n));
        if (n.Enabled != a.Enabled)
            NativeMethods.SetDEXPRun(id, n.Enabled ? 1 : 0);
    }

    // Thetis setup.cs unit conversions.
    // Threshold: 10^(dBV/20). Thetis also multiplies by VOXGain when MicBoost is
    // on; Zeus applies mic gain in the TXA panel stage (after DEXP, like Thetis
    // SetTXAPanelGain1) and has no MicBoost/VOX gain, so no multiplier here.
    private static double DexpAttackThreshold(DexpConfig c) => Math.Pow(10.0, c.ThresholdDbv / 20.0);
    // Thetis "Dexp_Attenuate": 10^(dB/20); low gain = 1/ratio.
    private static double DexpExpansionRatio(DexpConfig c) => Math.Pow(10.0, c.ExpansionDb / 20.0);
    // Hold threshold = ratio x attack threshold, ratio < 1: 10^(-dB/20).
    private static double DexpHysteresisRatio(DexpConfig c) => Math.Pow(10.0, -c.HysteresisDb / 20.0);
    // Thetis: look-ahead && (VOX || DEXP). Zeus has no VOX.
    private static int DexpRunAudioDelay(DexpConfig c) => c.LookAheadEnabled && c.Enabled ? 1 : 0;

    private static int ReserveDexpId()
    {
        lock (s_dexpIdLock)
        {
            for (int i = 0; i < DexpMaxInstances; i++)
            {
                if (!s_dexpIdsInUse[i])
                {
                    s_dexpIdsInUse[i] = true;
                    return i;
                }
            }
            return -1;
        }
    }

    private static void ReleaseDexpId(int id)
    {
        lock (s_dexpIdLock)
            s_dexpIdsInUse[id] = false;
    }
}
