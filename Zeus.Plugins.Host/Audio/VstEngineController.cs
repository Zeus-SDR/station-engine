// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// One plugin enumerated by the engine's scanner. A "shell" VST3 yields many of
/// these from a single <see cref="File"/>, disambiguated by <see cref="Uid"/>.
/// </summary>
public sealed record EngineScannedPlugin(
    string Uid,
    string Name,
    string Manufacturer,
    string File,
    string Format,
    string Category,
    bool IsInstrument,
    int NumInputs,
    int NumOutputs);

/// <summary>
/// Outcome of an engine-driven scan. Beyond the enumerated <see cref="Plugins"/>,
/// it carries the files the engine has BLACKLISTED (crashed or hung while the
/// engine probed them — JUCE KnownPluginList + dead-man's-pedal) so the caller
/// can surface them instead of letting them vanish silently, and whether the
/// engine process DIED mid-scan (a probing plugin took it down — the supervisor
/// relaunches it and the dead-man's pedal blacklists the offender, so a rescan
/// makes progress). The pinned 2026.06.14 engine predates upstream's
/// sacrificial-child <c>--probe</c> scanner (verified absent from the binary),
/// so mid-scan death is a real, expected failure mode the caller must handle.
/// </summary>
public sealed record EngineScanResult(
    IReadOnlyList<EngineScannedPlugin> Plugins,
    IReadOnlyList<string> BlacklistedFiles,
    bool EngineDied);

/// <summary>Outcome of a <see cref="VstEngineController.ActivateAsync"/> call.</summary>
public enum VstEngineStartResult
{
    /// <summary>The engine launched, handshook <c>ready</c>, and the realtime tap is live.</summary>
    Started,
    /// <summary>No <c>VSTHostEngine.exe</c> was found (caller surfaces "Get VSTHost").</summary>
    EngineNotFound,
    /// <summary>The engine process failed to start.</summary>
    LaunchFailed,
    /// <summary>The engine started but did not handshake <c>ready</c> within the budget.</summary>
    NotReady,
    /// <summary>Named shared memory + events are Windows-only.</summary>
    PlatformUnsupported,
}

/// <summary>Coarse lifecycle state of the supervised engine (diagnostics).</summary>
public enum VstEngineState
{
    /// <summary>Operator hasn't selected VST, or it was switched back to Native.</summary>
    Inactive,
    /// <summary>A launch / relaunch is in flight (process up, awaiting <c>ready</c>).</summary>
    Activating,
    /// <summary>Engine is live and the realtime tap routes audio through it.</summary>
    Active,
    /// <summary>Engine went down while wanted; the supervisor is backing off to relaunch.</summary>
    Backoff,
    /// <summary>Crash-loop cap hit (or engine unavailable); staying passthrough, slow-retrying.</summary>
    Faulted,
}

/// <summary>
/// Public lifecycle + realtime facade over the out-of-process VST engine
/// (the internal <see cref="VstEngineBridge"/> audio plane and
/// <see cref="VstEngineProcess"/> supervisor). This is the seam
/// <c>Zeus.Server.Hosting</c> drives.
///
/// <para><b>Robust-path lifetime model (load-bearing).</b> The shared-memory
/// bridge is created once, on first activation, and held for the controller's
/// whole lifetime — it is NEVER disposed on a mode toggle. Deactivation only
/// flips a single <c>volatile</c> gate and tears down the external <em>process</em>;
/// the realtime <see cref="Process"/> tap therefore never races a half-disposed
/// memory map. In Native mode (<see cref="IsActive"/> = false) the realtime path
/// is pure passthrough and never touches the engine at all — byte-identical to
/// having no VST mode. Windows-only (named SHM + events); on other platforms
/// activation returns <see cref="VstEngineStartResult.PlatformUnsupported"/>.</para>
///
/// <para><b>Self-heal supervisor.</b> Once the operator has selected VST
/// (<c>_desiredActive</c>), the controller keeps the engine up on its own:
/// an unexpected process exit triggers a rate-limited relaunch with exponential
/// backoff, a rolling crash-loop cap (after which it cools down rather than
/// spinning hot), and a degraded-block watchdog that force-recycles an
/// alive-but-unresponsive (hung) engine. Every successful relaunch fires
/// <see cref="Reconnected"/> so owners can replay their chain. Throughout, the
/// realtime tap stays a clean bounded-wait passthrough — the radio audio is
/// never blocked while the engine is down or recovering.</para>
/// </summary>
public sealed class VstEngineController : IAsyncDisposable
{
    private readonly int _maxFrames;
    private readonly int _rate;
    private readonly int _channels;
    private readonly string _shmName;
    private readonly object _lifecycleLock = new();
    private readonly object _failureLock = new();
    private readonly bool _supportsEngine;

    // Seams: production wires the real process / bridge / exe resolver; tests
    // inject fakes so the supervisor logic runs deterministically off-Windows.
    private readonly Func<string, IVstEngineProcess> _launch;
    private readonly Func<string?> _resolveExe;
    private readonly Func<IVstEngineBridge> _createBridge;

    // Created lazily on first ActivateAsync, then held for the controller's
    // lifetime. Read on the realtime thread; only ever assigned once (non-null
    // thereafter) under _lifecycleLock.
    private IVstEngineBridge? _bridge;
    private IVstEngineProcess? _proc;
    private volatile bool _active;
    private bool _desiredActive;
    private bool _activationInProgress;
    private bool _relaunchRunning;
    private bool _disposed;
    private VstEngineState _state = VstEngineState.Inactive;
    private CancellationTokenSource? _supervisorCts;
    private Timer? _watchdog;

    // Crash-loop accounting (rolling window of failure timestamps).
    private readonly Queue<long> _failureTicks = new();
    private volatile int _consecutiveFailures;
    private long _restartCount;

    // Hang watchdog state (guarded by _lifecycleLock).
    private long _lastDegradedSnapshot;
    private int _hangStreak;

    /// <summary>Forwarded engine stderr lines + supervisor diagnostics — control thread.</summary>
    public event Action<string>? StdErr;

    /// <summary>Forwarded parsed engine events (e.g. <c>chain</c>, <c>error</c>) — control thread.</summary>
    public event Action<JsonElement>? EngineEvent;

    /// <summary>
    /// Raised after a self-heal RELAUNCH re-arms the tap (not the first manual
    /// activation). Owners replay their <c>load_chain</c> here so the recovered
    /// engine comes back with the operator's plugins + saved state, not empty.
    /// </summary>
    public event Action? Reconnected;

    /// <summary>
    /// Raised when a downstream watchdog finds the engine has gone "alive but TX
    /// output dead" and asks owners to re-push their <c>load_chain</c> into the
    /// STILL-RUNNING engine (re-instantiating plugins) — the same recovery a manual
    /// audio-profile change performs, without a process recycle. Tier-1 self-heal
    /// for the post-long-TX wedge the seq/exit health model can't see. See
    /// <see cref="RequestChainReload"/>. (zeus-umt6)
    /// </summary>
    public event Action? ChainReloadRequested;

    /// <summary>
    /// Raised when the supervisor gives up hot-relaunching (crash-loop cap hit or
    /// engine unavailable) and falls back to slow-retry passthrough. Carries a
    /// human-readable reason for the operator log.
    /// </summary>
    public event Action<string>? Faulted;

    /// <param name="concealOnMiss">
    /// RX-only: replay the last good output block on a late/timed-out block
    /// instead of dry passthrough. Leave false for TX — see
    /// <see cref="VstEngineBridge.Create"/>.
    /// </param>
    public VstEngineController(int maxFrames = 4096, int rate = 48000, int channels = 1, bool concealOnMiss = false)
        : this(maxFrames, rate, channels, launch: null, resolveExe: null, createBridge: null, concealOnMiss: concealOnMiss)
    {
    }

    /// <summary>
    /// Test/seam constructor. When any seam is supplied the OS gate is bypassed so
    /// the supervisor can be exercised off-Windows against fakes.
    /// </summary>
    internal VstEngineController(
        int maxFrames, int rate, int channels,
        Func<string, IVstEngineProcess>? launch,
        Func<string?>? resolveExe,
        Func<IVstEngineBridge>? createBridge,
        bool concealOnMiss = false)
    {
        if (maxFrames <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _maxFrames = maxFrames;
        _rate = rate;
        _channels = channels;
        _shmName = "zeus-vst-" + Guid.NewGuid().ToString("N");

        var seamed = launch is not null || resolveExe is not null || createBridge is not null;
        _supportsEngine = seamed || OperatingSystem.IsWindows();
        _resolveExe = resolveExe ?? VstEngineProcess.FindEngineExe;
        _launch = launch ?? (path => VstEngineProcess.Launch(path, _shmName, _maxFrames, _rate, _channels));
        _createBridge = createBridge ?? (() => VstEngineBridge.Create(_shmName, _maxFrames, _rate, _channels, concealOnMiss));
    }

    /// <summary>
    /// Resolve the engine exe without launching anything (override → default
    /// install → PATH). Null = not installed; the caller surfaces "Get VSTHost".
    /// </summary>
    public static string? FindEngineExe() => VstEngineProcess.FindEngineExe();

    /// <summary>
    /// The Zeus-managed engine location
    /// (<c>%LOCALAPPDATA%\Zeus\vst-engine\VSTHostEngine.exe</c> on Windows), or
    /// null on non-Windows. Where the in-app "Get VST Engine" provisioning flow
    /// stages a downloaded engine so VST mode works without a manual install.
    /// </summary>
    public static string? ManagedEnginePath() => VstEngineProcess.ManagedEnginePath();

    /// <summary>True while the realtime tap routes audio through the engine.</summary>
    public bool IsActive => _active;

    /// <summary>Coarse supervisor state (diagnostics).</summary>
    public VstEngineState State { get { lock (_lifecycleLock) return _state; } }

    /// <summary>Successful self-heal relaunches since construction (diagnostics).</summary>
    public long RestartCount => Interlocked.Read(ref _restartCount);

    /// <summary>Consecutive failed launch attempts in the current recovery run.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>Last fault reason surfaced to <see cref="Faulted"/>, if any.</summary>
    public string? LastFault { get; private set; }

    /// <summary>
    /// CUMULATIVE blocks that fell through to passthrough (timeout / stale-seq)
    /// since the bridge was created — it is never reset, including across self-heal
    /// relaunches. The hang watchdog uses per-interval DELTAS of this, not the
    /// total; treat the public value as a lifetime diagnostic, not a live health bar.
    /// </summary>
    public long DegradedBlocks => _bridge?.DegradedBlocks ?? 0;

    /// <summary>
    /// Engine-written lifecycle state from the SHM header (0=init, 1=running,
    /// 2=draining), or 0 when no bridge exists yet. Diagnostics only.
    /// </summary>
    public uint EngineState => _bridge?.EngineState ?? 0;

    /// <summary>
    /// Engine-written flags from the SHM header (bit0 = bypassed / empty chain),
    /// or 0 when no bridge exists yet. Diagnostics only.
    /// </summary>
    public uint EngineFlags => _bridge?.EngineFlags ?? 0;

    /// <summary>
    /// One engine telemetry snapshot, streamed by the engine's ~30 Hz
    /// <c>levels</c> event: <see cref="CpuLoad"/> is the JUCE audio-callback
    /// load (0–1; sustained ≈1 means the chain can't keep up in realtime),
    /// <see cref="InputLevel"/>/<see cref="OutputLevel"/> the chain's
    /// input/output peaks, and <see cref="SlotLevels"/> one output level per
    /// chain slot. Immutable; swapped atomically per event.
    /// </summary>
    public sealed record VstEngineLevels(
        double CpuLoad,
        double InputLevel,
        double OutputLevel,
        IReadOnlyList<float> SlotLevels,
        long Sequence);

    // Written on the engine reader thread only; read lock-free from anywhere.
    private VstEngineLevels? _levels;
    private long _levelsSeq;

    /// <summary>
    /// Latest engine telemetry snapshot, or null until the engine streams its
    /// first <c>levels</c> event (engine down, or an old engine build).
    /// </summary>
    public VstEngineLevels? LatestLevels => Volatile.Read(ref _levels);

    /// <summary>Engine audio-callback CPU load (0–1), or null before the first telemetry event.</summary>
    public double? EngineCpuLoad => LatestLevels?.CpuLoad;

    /// <summary>Blocks the engine serviced within budget since the last (re)launch (bridge telemetry).</summary>
    public long EngineServicedBlocks => _bridge?.ServicedBlocks ?? 0;

    /// <summary>Mean engine round-trip time (µs) this session (bridge telemetry).</summary>
    public double EngineAverageRoundTripMicros => _bridge?.AverageRoundTripMicros ?? 0;

    /// <summary>Worst engine round-trip time (µs) this session (bridge telemetry).</summary>
    public double EngineMaxRoundTripMicros => _bridge?.MaxRoundTripMicros ?? 0;

    /// <summary>Most recent engine round-trip time (µs) (bridge telemetry).</summary>
    public double EngineLastRoundTripMicros => _bridge?.LastRoundTripMicros ?? 0;

    /// <summary>The engine exe resolved on the last activation attempt, if any.</summary>
    public string? ResolvedEnginePath { get; private set; }

    /// <summary>
    /// Bounded per-block wait ceiling handed to the bridge (ms). Kept under the
    /// TX block period (960 frames @ 48 kHz ≈ 20 ms) so a wedged plugin costs at
    /// most one passthrough block. 12 ms leaves ~8 ms of headroom while reliably
    /// completing the cross-process event round-trip.
    /// </summary>
    public int WaitBudgetMs { get; set; } = 12;

    // ── Self-heal tuning (settable so tests can shrink delays) ────────────────
    /// <summary>First relaunch backoff (ms); doubles per consecutive failure.</summary>
    public int RelaunchBaseDelayMs { get; set; } = 250;
    /// <summary>Backoff ceiling (ms).</summary>
    public int RelaunchMaxDelayMs { get; set; } = 10_000;
    /// <summary>Rolling crash-loop window (s).</summary>
    public int RelaunchWindowSeconds { get; set; } = 60;
    /// <summary>Max relaunch attempts within the window before cooling down.</summary>
    public int MaxRelaunchesPerWindow { get; set; } = 5;
    /// <summary>Cooldown after the cap is hit / engine unavailable (ms) before retrying.</summary>
    public int FaultCooldownMs { get; set; } = 30_000;
    /// <summary>Ready-handshake budget for a supervised relaunch.</summary>
    public TimeSpan RelaunchReadyTimeout { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Hang-watchdog poll interval (ms).</summary>
    public int HangWatchdogIntervalMs { get; set; } = 1000;
    /// <summary>Degraded blocks per interval that count as "engine not servicing".</summary>
    public int HangDegradedThreshold { get; set; } = 40;
    /// <summary>Consecutive high-degrade intervals before force-recycling a hung engine.</summary>
    public int HangConsecutiveIntervals { get; set; } = 3;

    /// <summary>
    /// Launch the engine and bring the realtime tap online, and arm self-heal so
    /// the engine is kept up until <see cref="Deactivate"/>. Idempotent: calling
    /// while already active is a no-op returning <see cref="VstEngineStartResult.Started"/>.
    /// Runs on the control thread (never the realtime thread).
    /// </summary>
    public async Task<VstEngineStartResult> ActivateAsync(TimeSpan readyTimeout, CancellationToken ct = default)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_supportsEngine) return VstEngineStartResult.PlatformUnsupported;
            if (_active) return VstEngineStartResult.Started;
            _desiredActive = true;
            _supervisorCts ??= new CancellationTokenSource();
            EnsureWatchdogLocked();
        }

        var result = await TryLaunchAndArmAsync(readyTimeout, isRelaunch: false, ct).ConfigureAwait(false);

        // The engine is installed but didn't come up this time — hand off to the
        // supervisor so a slow/contended start still recovers without operator action.
        if (result is VstEngineStartResult.LaunchFailed or VstEngineStartResult.NotReady)
        {
            bool start;
            lock (_lifecycleLock) start = _desiredActive && !_disposed;
            if (start) ScheduleRelaunch();
        }
        return result;
    }

    /// <summary>
    /// Take the realtime tap offline, disarm self-heal, and stop the external
    /// process. The bridge (shared memory) is intentionally left mapped so an
    /// in-flight realtime block can't fault on a torn-down map. Safe to call
    /// repeatedly.
    /// </summary>
    public void Deactivate()
    {
        IVstEngineProcess? proc;
        CancellationTokenSource? cts;
        lock (_lifecycleLock)
        {
            // Order matters: stop the realtime path FIRST so no block enters the
            // engine round-trip after this point, THEN drop the process. Clearing
            // _desiredActive makes OnProcessExited treat the teardown as intentional.
            _desiredActive = false;
            _active = false;
            _activationInProgress = false;
            if (_bridge is { } b) b.EngineReady = false;
            proc = _proc;
            _proc = null;
            _state = VstEngineState.Inactive;
            cts = _supervisorCts;
            _supervisorCts = null;
        }
        cts?.Cancel();
        cts?.Dispose();
        if (proc is not null) DisposeProcess(proc);
    }

    /// <summary>
    /// Tier-1 recovery for the "engine alive but TX output dead" wedge: ask owners
    /// to re-push their chain into the running engine (re-instantiating every
    /// plugin), exactly as a manual audio-profile change does — but WITHOUT killing
    /// the process. No-op unless the engine is the live route. Invoked off the
    /// realtime path by the TX-liveness watchdog; escalate via
    /// <see cref="ForceRecycle"/> if it doesn't take. (zeus-umt6)
    /// </summary>
    public void RequestChainReload(string reason)
    {
        bool active;
        lock (_lifecycleLock) active = _active && !_disposed && _proc is not null;
        if (!active) return;
        OnStdErr("vst-engine: " + reason);
        try { ChainReloadRequested?.Invoke(); } catch { /* owner replay is best-effort */ }
    }

    /// <summary>
    /// Tier-2 recovery: force-recycle the engine process (Kill → Exited → relaunch →
    /// <see cref="Reconnected"/> → owners replay) when a Tier-1
    /// <see cref="RequestChainReload"/> failed to clear a TX-output wedge. Routes
    /// through the normal exit/relaunch path, so the crash-loop cap and backoff
    /// still apply (a genuinely broken plugin ends in clean passthrough, not a kill
    /// loop). No-op unless the engine is live. (zeus-umt6)
    /// </summary>
    public void ForceRecycle(string reason)
    {
        IVstEngineProcess? victim;
        lock (_lifecycleLock)
        {
            if (_disposed || !_active || _proc is null) return;
            victim = _proc;
        }
        LastFault = reason;
        OnStdErr("vst-engine: " + reason);
        victim.Kill(); // → Exited → OnProcessExited → ScheduleRelaunch → Reconnected
    }

    /// <summary>
    /// The single launch routine, shared by manual activation and supervised
    /// relaunch. On a relaunch it resets the bridge to a clean sequence first.
    /// </summary>
    private async Task<VstEngineStartResult> TryLaunchAndArmAsync(
        TimeSpan readyTimeout, bool isRelaunch, CancellationToken ct)
    {
        IVstEngineProcess proc;
        var ownsActivation = false;
        lock (_lifecycleLock)
        {
            if (_disposed) return VstEngineStartResult.NotReady;
            if (_active) return VstEngineStartResult.Started;
            if (_activationInProgress) return VstEngineStartResult.NotReady;
            _activationInProgress = true;
            ownsActivation = true;

            var path = _resolveExe();
            if (path is null)
            {
                _activationInProgress = false;
                return VstEngineStartResult.EngineNotFound;
            }
            ResolvedEnginePath = path;

            // Create the shared-memory plane once and keep it for our lifetime.
            _bridge ??= _createBridge();
            _bridge.WaitBudgetMs = WaitBudgetMs;
            if (isRelaunch) _bridge.ResetForRelaunch();

            try
            {
                proc = _launch(path);
            }
            catch
            {
                _activationInProgress = false;
                return VstEngineStartResult.LaunchFailed;
            }
            proc.StdErrLine += OnStdErr;
            proc.EngineEvent += OnEngineEvent;
            proc.Exited += OnProcessExited;
            _proc = proc;
            _state = VstEngineState.Activating;
        }

        JsonElement ready;
        try
        {
            ready = await proc.Ready.WaitAsync(readyTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Engine never handshook — tear THIS process back down, stay passthrough.
            StopProcess(proc);
            ClearActivationInProgress(ownsActivation);
            return VstEngineStartResult.NotReady;
        }

        // Don't trust the audio plane until the engine echoes our launch contract.
        if (!ValidateHandshake(ready, out var why))
        {
            OnStdErr($"vst-engine: handshake rejected ({why})");
            StopProcess(proc);
            ClearActivationInProgress(ownsActivation);
            return VstEngineStartResult.NotReady;
        }

        // Engine's audio thread is now blocked on .in; arm the realtime tap.
        var armed = false;
        lock (_lifecycleLock)
        {
            if (!_disposed && ReferenceEquals(_proc, proc) && _desiredActive)
            {
                if (_bridge is { } b) b.EngineReady = true;
                _active = true;
                _state = VstEngineState.Active;
                ResetHangBaselineLocked();
                armed = true;
            }
            if (ownsActivation) _activationInProgress = false;
        }

        if (!armed)
        {
            // Disposed / deactivated / superseded while we waited.
            StopProcess(proc);
            return VstEngineStartResult.NotReady;
        }

        if (isRelaunch)
        {
            Interlocked.Increment(ref _restartCount);
            try { Reconnected?.Invoke(); } catch { /* owner replay is best-effort */ }
        }
        return VstEngineStartResult.Started;
    }

    private void OnProcessExited(IVstEngineProcess proc)
    {
        var relaunch = false;
        lock (_lifecycleLock)
        {
            if (!ReferenceEquals(_proc, proc)) return; // intentional teardown / superseded
            _active = false;
            _activationInProgress = false;
            if (_bridge is { } b) b.EngineReady = false;
            _proc = null;
            if (_desiredActive && !_disposed)
            {
                relaunch = true;
                _state = VstEngineState.Backoff;
            }
            else
            {
                _state = VstEngineState.Inactive;
            }
        }

        DisposeProcess(proc);
        if (relaunch) ScheduleRelaunch();
    }

    /// <summary>Start the single supervisor task if one isn't already running.</summary>
    private void ScheduleRelaunch()
    {
        CancellationToken token;
        lock (_lifecycleLock)
        {
            if (_disposed || !_desiredActive || _supervisorCts is null) return;
            if (_relaunchRunning) return;
            _relaunchRunning = true;
            token = _supervisorCts.Token;
        }
        _ = SuperviseAsync(token);
    }

    /// <summary>
    /// Rate-limited relaunch loop. Records each recovery attempt in a rolling
    /// window; once the cap is hit it cools down (rather than spinning hot) then
    /// tries again, so a transient bad state self-heals but a persistently
    /// crashing plugin can't burn the CPU. Returns once the engine is armed; a
    /// later crash re-enters via <see cref="OnProcessExited"/>.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested) return;
                lock (_lifecycleLock) { if (_disposed || !_desiredActive) return; }

                int failures = RecordFailureAndCount();
                if (failures > MaxRelaunchesPerWindow)
                {
                    var reason = $"VST engine crashed {failures}× within {RelaunchWindowSeconds}s; " +
                                 $"cooling down {FaultCooldownMs / 1000}s before retrying (TX audio passes through clean).";
                    SetFaulted(reason);
                    if (!await DelayAsync(FaultCooldownMs, ct).ConfigureAwait(false)) return;
                    ResetFailureWindow();
                    _consecutiveFailures = 0;
                    continue;
                }

                if (!await DelayAsync(BackoffDelayMs(_consecutiveFailures), ct).ConfigureAwait(false)) return;
                lock (_lifecycleLock) { if (_disposed || !_desiredActive) return; }

                VstEngineStartResult result;
                try
                {
                    result = await TryLaunchAndArmAsync(RelaunchReadyTimeout, isRelaunch: true, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    OnStdErr($"vst-engine: relaunch threw ({ex.Message})");
                    result = VstEngineStartResult.LaunchFailed;
                }

                switch (result)
                {
                    case VstEngineStartResult.Started:
                        _consecutiveFailures = 0;
                        return; // armed; next crash re-triggers supervision

                    case VstEngineStartResult.EngineNotFound:
                    case VstEngineStartResult.PlatformUnsupported:
                        // Nothing to relaunch onto — don't hot-loop; slow-retry in
                        // case the operator provisions the engine.
                        SetFaulted($"VST engine unavailable ({result}); staying in passthrough.");
                        if (!await DelayAsync(FaultCooldownMs, ct).ConfigureAwait(false)) return;
                        ResetFailureWindow();
                        _consecutiveFailures = 0;
                        continue;

                    default: // LaunchFailed / NotReady — count and back off harder
                        _consecutiveFailures++;
                        continue;
                }
            }
        }
        finally
        {
            // Close the success-return race: if the engine crashed again in the
            // tiny window between our Started-return and here, its OnProcessExited
            // saw _relaunchRunning still true and skipped — so re-arm supervision.
            bool reschedule;
            lock (_lifecycleLock)
            {
                _relaunchRunning = false;
                reschedule = !_disposed && _desiredActive && _proc is null && _supervisorCts is not null;
            }
            if (reschedule) ScheduleRelaunch();
        }
    }

    /// <summary>Returns false if cancelled (caller should stop the supervisor).</summary>
    private static async Task<bool> DelayAsync(int ms, CancellationToken ct)
    {
        try { await Task.Delay(Math.Max(0, ms), ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
        // Deactivate()/DisposeAsync() cancels THEN disposes the supervisor CTS; a
        // Task.Delay racing that disposal surfaces ODE instead of OCE — same intent.
        catch (ObjectDisposedException) { return false; }
    }

    private void SetFaulted(string reason)
    {
        lock (_lifecycleLock) _state = VstEngineState.Faulted;
        LastFault = reason;
        OnStdErr("vst-engine: " + reason);
        try { Faulted?.Invoke(reason); } catch { /* best-effort */ }
    }

    private int BackoffDelayMs(int consecutive)
    {
        int shift = Math.Min(consecutive, 16);
        long d = (long)RelaunchBaseDelayMs << shift;
        return (int)Math.Min(d, RelaunchMaxDelayMs);
    }

    private int RecordFailureAndCount()
    {
        long now = Stopwatch.GetTimestamp();
        long windowTicks = (long)(RelaunchWindowSeconds * (double)Stopwatch.Frequency);
        lock (_failureLock)
        {
            _failureTicks.Enqueue(now);
            while (_failureTicks.Count > 0 && now - _failureTicks.Peek() > windowTicks)
                _failureTicks.Dequeue();
            return _failureTicks.Count;
        }
    }

    private void ResetFailureWindow()
    {
        lock (_failureLock) _failureTicks.Clear();
    }

    // ── Hang watchdog ─────────────────────────────────────────────────────────
    private void EnsureWatchdogLocked()
    {
        _watchdog ??= new Timer(
            _ => { try { HangWatchdogTick(); } catch { /* never let a tick crash */ } },
            null, HangWatchdogIntervalMs, HangWatchdogIntervalMs);
    }

    private void ResetHangBaselineLocked()
    {
        _lastDegradedSnapshot = _bridge?.DegradedBlocks ?? 0;
        _hangStreak = 0;
    }

    /// <summary>
    /// A hung engine never exits, so <see cref="OnProcessExited"/> never fires —
    /// but it stops servicing blocks, so DegradedBlocks climbs fast under TX. When
    /// that persists across several intervals we force-recycle the process, which
    /// routes through the normal exit → relaunch path. No false positives when
    /// idle (no audio ⇒ no degrade) or healthy (engine answers ⇒ no degrade).
    /// </summary>
    private void HangWatchdogTick()
    {
        IVstEngineProcess? victim = null;
        string? reason = null;
        lock (_lifecycleLock)
        {
            if (_disposed || !_active || _proc is null || _bridge is null)
            {
                _hangStreak = 0;
                return;
            }
            long now = _bridge.DegradedBlocks;
            long delta = now - _lastDegradedSnapshot;
            _lastDegradedSnapshot = now;

            if (delta >= HangDegradedThreshold)
            {
                _hangStreak++;
                if (_hangStreak >= HangConsecutiveIntervals)
                {
                    victim = _proc;
                    _hangStreak = 0;
                    reason = $"VST engine unresponsive ({delta} degraded blocks/interval " +
                             $"×{HangConsecutiveIntervals}); recycling.";
                }
            }
            else
            {
                _hangStreak = 0;
            }
        }

        if (victim is not null)
        {
            LastFault = reason;
            OnStdErr("vst-engine: " + reason);
            victim.Kill(); // → Exited → OnProcessExited → ScheduleRelaunch
        }
    }

    private bool ValidateHandshake(JsonElement ready, out string? error)
    {
        error = null;
        if (ready.ValueKind != JsonValueKind.Object)
        {
            error = "ready payload is not an object";
            return false;
        }
        if (!ready.TryGetProperty("event", out var evt)
            || evt.ValueKind != JsonValueKind.String
            || !string.Equals(evt.GetString(), "ready", StringComparison.Ordinal))
        {
            error = "event is missing or is not 'ready'";
            return false;
        }

        // These fields are the audio-plane launch contract, not optional
        // metadata. A missing or malformed echo is as unsafe as a mismatch:
        // arming SHM against an unknown layout can corrupt or misroute audio.
        if (!RequiredEchoMatches(ready, "protocol", checked((int)VstEngineProtocol.Version), ref error)) return false;
        if (!RequiredEchoMatches(ready, "frames", _maxFrames, ref error)) return false;
        if (!RequiredEchoMatches(ready, "rate", _rate, ref error)) return false;
        if (!RequiredEchoMatches(ready, "channels", _channels, ref error)) return false;
        return true;
    }

    private static bool RequiredEchoMatches(JsonElement o, string key, int expected, ref string? error)
    {
        if (!o.TryGetProperty(key, out var v)
            || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out var got))
        {
            error = $"{key} is missing or is not an integer";
            return false;
        }
        if (got != expected)
        {
            error = $"{key} {got} != launched {expected}";
            return false;
        }
        return true;
    }

    private void ClearActivationInProgress(bool owns)
    {
        if (!owns) return;
        lock (_lifecycleLock) _activationInProgress = false;
    }

    private void StopProcess(IVstEngineProcess proc)
    {
        lock (_lifecycleLock)
        {
            if (ReferenceEquals(_proc, proc))
            {
                _active = false;
                if (_bridge is { } b) b.EngineReady = false;
                _proc = null;
            }
        }
        DisposeProcess(proc);
    }

    private void DisposeProcess(IVstEngineProcess proc)
    {
        proc.StdErrLine -= OnStdErr;
        proc.EngineEvent -= OnEngineEvent;
        proc.Exited -= OnProcessExited;
        proc.Dispose();
    }

    /// <summary>
    /// Send a control-plane command to the engine (e.g. <c>add_plugin</c>,
    /// <c>set_slot_gain</c>). No-op when not running. Control thread only.
    /// </summary>
    public void SendCommand(object command)
    {
        IVstEngineProcess? proc;
        lock (_lifecycleLock) proc = _proc;
        proc?.Send(command);
    }

    /// <summary>
    /// Drive the engine's <c>scan_plugins</c> and await its <c>plugins_scanned</c>
    /// reply, returning every plugin the engine's scanner enumerated. For a
    /// "shell" VST3 (e.g. Waves WaveShell) this yields one entry PER hosted
    /// sub-plugin, each with its own <see cref="EngineScannedPlugin.Uid"/> — the
    /// identifier Zeus must pass back in <c>load_chain</c> to load that exact one.
    /// Returns an empty result when the engine isn't active. Control thread only.
    ///
    /// <para>The reply's <c>blacklist</c> (files that crashed/hung while the
    /// engine probed them) is captured into <see cref="EngineScanResult.BlacklistedFiles"/>
    /// so callers can surface them — a blacklisted plugin silently absent from
    /// the list is exactly the "permanent failure that never says anything"
    /// failure mode the engine-health RCA forbids. The scan also RACES the
    /// engine process: if a probing plugin kills the engine mid-scan (possible
    /// on the pinned 2026.06.14 build, which predates upstream's sacrificial-child
    /// <c>--probe</c> scanner), the call returns promptly with
    /// <see cref="EngineScanResult.EngineDied"/> instead of hanging out the full
    /// timeout against a dead process.</para>
    /// </summary>
    public async Task<EngineScanResult> ScanPluginsAsync(
        IReadOnlyList<string> paths, TimeSpan timeout, CancellationToken ct = default)
    {
        IVstEngineProcess? proc;
        lock (_lifecycleLock) proc = _active ? _proc : null;
        if (proc is null)
            return new EngineScanResult([], [], EngineDied: false);

        var tcs = new TaskCompletionSource<(IReadOnlyList<EngineScannedPlugin>, IReadOnlyList<string>)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var died = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnProcExited(IVstEngineProcess p)
        {
            if (ReferenceEquals(p, proc)) died.TrySetResult();
        }

        void Handler(JsonElement e)
        {
            try
            {
                if (!e.TryGetProperty("event", out var ev)
                    || ev.ValueKind != JsonValueKind.String
                    || ev.GetString() != "plugins_scanned") return;

                var list = new List<EngineScannedPlugin>();
                if (e.TryGetProperty("plugins", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in arr.EnumerateArray())
                    {
                        static string Str(JsonElement o, string k) =>
                            o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                                ? v.GetString() ?? "" : "";
                        // JUCE serialises a bool var as the NUMBER 0/1, not a JSON
                        // true/false — accept both so the instrument flag isn't
                        // silently dropped (which let Waves instruments through).
                        static bool Bool(JsonElement o, string k) =>
                            o.TryGetProperty(k, out var v) && v.ValueKind switch
                            {
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
                                _ => false,
                            };
                        static int Int(JsonElement o, string k) =>
                            o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
                            && v.TryGetInt32(out var n) ? n : -1;

                        list.Add(new EngineScannedPlugin(
                            Uid: Str(p, "uid"),
                            Name: Str(p, "name"),
                            Manufacturer: Str(p, "manufacturer"),
                            File: Str(p, "file"),
                            Format: Str(p, "format"),
                            Category: Str(p, "category"),
                            IsInstrument: Bool(p, "isInstrument"),
                            NumInputs: Int(p, "numInputs"),
                            NumOutputs: Int(p, "numOutputs")));
                    }
                }

                var blacklist = new List<string>();
                if (e.TryGetProperty("blacklist", out var bl) && bl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in bl.EnumerateArray())
                    {
                        if (f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } file)
                            blacklist.Add(file);
                    }
                }
                tcs.TrySetResult((list, blacklist));
            }
            catch { tcs.TrySetResult((Array.Empty<EngineScannedPlugin>(), (IReadOnlyList<string>)[])); }
        }

        EngineEvent += Handler;
        proc.Exited += OnProcExited;
        if (proc.HasExited) died.TrySetResult(); // close the exit-before-subscribe race
        try
        {
            SendCommand(new { cmd = "scan_plugins", paths });
            var completed = await Task.WhenAny(tcs.Task, died.Task)
                .WaitAsync(timeout, ct).ConfigureAwait(false);
            if (ReferenceEquals(completed, died.Task))
                return new EngineScanResult([], [], EngineDied: true);
            var (plugins, blacklist) = await tcs.Task.ConfigureAwait(false);
            return new EngineScanResult(plugins, blacklist, EngineDied: false);
        }
        catch (TimeoutException)
        {
            // A timeout against an exited process means the scan died with the
            // engine — report that precisely instead of a bare empty result.
            return new EngineScanResult([], [], EngineDied: proc.HasExited);
        }
        finally
        {
            EngineEvent -= Handler;
            proc.Exited -= OnProcExited;
        }
    }

    /// <summary>
    /// Realtime tap. In Native mode (not active) this is a bit-identical copy and
    /// never touches the engine. In VST mode it hands the block to the bridge,
    /// which round-trips it through the engine or passes through on any failure.
    /// Returns true only when the output came from the engine. No allocation,
    /// no managed lock.
    /// </summary>
    public bool TryProcess(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        // Single volatile read; _bridge is assigned once and never nulled before
        // disposal, so reading it here without a lock is safe.
        var bridge = _bridge;
        if (!_active || bridge is null)
        {
            input.CopyTo(output);
            return false;
        }
        return bridge.Process(input, output, ctx);
    }

    /// <summary>
    /// Realtime tap for callers that only need robust passthrough semantics.
    /// Prefer <see cref="TryProcess"/> when metering needs to distinguish a real
    /// VST output block from a degraded passthrough block.
    /// </summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
        => _ = TryProcess(input, output, ctx);

    private void OnStdErr(string line) => StdErr?.Invoke(line);

    private void OnEngineEvent(JsonElement e)
    {
        // Intercept the engine's ~30 Hz telemetry before forwarding: swap one
        // small immutable snapshot per event (reader/control thread only — the
        // realtime path never touches this).
        if (e.TryGetProperty("event", out var evt)
            && evt.ValueKind == JsonValueKind.String
            && evt.GetString() == "levels")
        {
            UpdateLevels(e);
        }
        EngineEvent?.Invoke(e);
    }

    /// <summary>
    /// Parse a <c>levels</c> event into <see cref="LatestLevels"/>. Defensive:
    /// every field defaults to 0/empty, and a malformed event is dropped rather
    /// than breaking event forwarding — telemetry must never take down the
    /// control plane. JUCE serialises levels as numbers (0–1 floats).
    /// </summary>
    private void UpdateLevels(JsonElement e)
    {
        try
        {
            // A levels event without a numeric cpu reading is not telemetry —
            // drop it WHOLE rather than overwrite a good snapshot with zeros.
            if (!e.TryGetProperty("cpu", out var cpu)
                || cpu.ValueKind != JsonValueKind.Number
                || !cpu.TryGetDouble(out var cpuLoad))
                return;

            static double Num(JsonElement o, string k) =>
                o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
                    && v.TryGetDouble(out var d) ? d : 0;

            float[] slots = [];
            if (e.TryGetProperty("slots", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<float>(arr.GetArrayLength());
                foreach (var s in arr.EnumerateArray())
                {
                    if (s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var d))
                        list.Add((float)d);
                }
                slots = list.ToArray();
            }

            Volatile.Write(ref _levels, new VstEngineLevels(
                CpuLoad: cpuLoad,
                InputLevel: Num(e, "input"),
                OutputLevel: Num(e, "output"),
                SlotLevels: slots,
                Sequence: Interlocked.Increment(ref _levelsSeq)));
        }
        catch { /* malformed telemetry — keep the previous snapshot */ }
    }

    public ValueTask DisposeAsync()
    {
        Timer? watchdog;
        lock (_lifecycleLock)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            watchdog = _watchdog;
            _watchdog = null;
        }
        watchdog?.Dispose();
        Deactivate();
        IVstEngineBridge? bridge;
        lock (_lifecycleLock)
        {
            bridge = _bridge;
            _bridge = null;
        }
        bridge?.Dispose();
        return ValueTask.CompletedTask;
    }
}
