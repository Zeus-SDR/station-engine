// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// <see cref="IAudioPlugin"/> implementation that hosts a single VST3
/// effect via <see cref="IVstBridgeNative"/>. Synthesised by the host
/// when a plugin's manifest declares <c>audio.vst3Path</c> — plugin
/// authors don't write this themselves.
/// </summary>
public sealed class VstHostAudioPlugin : IAudioPlugin, IAsyncDisposable
{
    public const string PersistedStateKey = "__zeus.vst.state";
    private const int MaxStateBytes = 64 * 1024 * 1024;

    private static int s_stateUnavailableLogged;

    private readonly IVstBridgeNative _bridge;
    private readonly string _loadIdentity;
    private readonly bool _isAudioUnit;
    private readonly string _pluginRootPath;
    private readonly string _slot;
    private readonly string? _pluginId;
    private readonly PluginSettingsStore? _settingsStore;
    private readonly ILogger? _log;
    private readonly bool _canRestoreAudioUnitState;
    private nint _handle;
    private int _latencySamples;

    public VstHostAudioPlugin(
        IVstBridgeNative bridge,
        AudioBlock manifestAudio,
        string pluginRootPath,
        string displayName,
        ILogger? log = null,
        string? pluginId = null,
        PluginSettingsStore? settingsStore = null,
        bool canRestoreAudioUnitState = true)
    {
        _bridge = bridge;
        _pluginRootPath = pluginRootPath;
        _log = log;
        _pluginId = string.IsNullOrWhiteSpace(pluginId) ? null : pluginId;
        _settingsStore = settingsStore;
        _canRestoreAudioUnitState = canRestoreAudioUnitState;
        _slot = manifestAudio.Slot;
        DisplayName = displayName;
        Requirements = new AudioPluginRequirements(
            SampleRate: manifestAudio.SampleRate,
            Channels: manifestAudio.Channels,
            BlockSize: manifestAudio.Slot.StartsWith("rx.", StringComparison.OrdinalIgnoreCase) ? 2048 : 1024);

        // Format selects the load identity. "au" loads a macOS Audio Unit by
        // its type:subtype:manufacturer triple (resolved from the OS registry,
        // not a file); anything else (default "vst3") loads from a VST3 path.
        // AudioPluginBridge picks the matching IVstBridgeNative backend; this
        // class stays backend-agnostic.
        _isAudioUnit = string.Equals(manifestAudio.Format, "au", StringComparison.OrdinalIgnoreCase);
        _loadIdentity = _isAudioUnit
            ? (manifestAudio.AuComponentId
                ?? throw new ArgumentException("audio.auComponentId is required when audio.format is \"au\""))
            : (manifestAudio.Vst3Path
                ?? throw new ArgumentException("audio.vst3Path is required for VstHostAudioPlugin"));
    }

    public string DisplayName { get; }
    public AudioPluginRequirements Requirements { get; }

    /// <summary>
    /// The plugin's reported processing latency in samples at its loaded
    /// geometry (0 if zero-latency, not natively loaded, or the bridge does
    /// not report it). Captured once at load.
    /// </summary>
    public int ReportedLatencySamples => _latencySamples;

    /// <summary>
    /// Load gate for in-process native plugin hosting (VST3 + Audio Unit). Both
    /// RX and TX native load default ON — TX was enabled by KB2UKA after
    /// bench-verifying AU TX hosting (audio confirmed changing under the editor)
    /// on 2026-06-26; it had previously been opt-in.
    ///
    /// Kill switches fall back to the crash-isolated out-of-process engine:
    /// <c>ZEUS_DISABLE_VST_LOAD=1</c> (all slots), <c>ZEUS_DISABLE_RX_VST_LOAD=1</c>
    /// (RX only), <c>ZEUS_DISABLE_TX_VST_LOAD=1</c> (TX only).
    /// <c>ZEUS_ENABLE_VST_LOAD=1</c> force-enables everything even if a per-side
    /// disable is set. Precedence: global disable &gt; force-enable &gt; per-side
    /// disable. See native/zeus-vst-bridge.
    /// </summary>
    /// <summary>Test override for <see cref="NativeLoadEnabled"/>; null = use the env var.</summary>
    internal static bool? NativeLoadEnabledOverride;

    private static bool NativeLoadEnabled(string slot)
    {
        if (NativeLoadEnabledOverride is { } forced) return forced;
        if (Environment.GetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD") == "1") return false;
        if (Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD") == "1") return true;
        if (slot.StartsWith("rx.", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable("ZEUS_DISABLE_RX_VST_LOAD") != "1";
        // TX native load now defaults on (KB2UKA-approved 2026-06-26);
        // ZEUS_DISABLE_TX_VST_LOAD=1 is the TX-side kill switch.
        return Environment.GetEnvironmentVariable("ZEUS_DISABLE_TX_VST_LOAD") != "1";
    }

    /// <summary>
    /// Editor-guard signal ONLY — whether the TX editor-open fallback should
    /// treat TX as in-process-hostable when a plugin is <em>not</em> already
    /// natively loaded. Deliberately <b>decoupled</b> from the TX <em>load</em>
    /// default (<see cref="NativeLoadEnabled"/>, which now defaults TX on as of
    /// 2026-06-26): it stays pinned to the pre-flip explicit <c>ZEUS_ENABLE_VST_LOAD</c>
    /// opt-in so flipping the load default does not move the cross-platform
    /// editor engine-redirect UX (Windows in-process TX editor hosting is
    /// unverified). A TX plugin that actually loaded in-process is detected via
    /// <c>AudioPluginBridge.HostsPlugin</c> upstream and bypasses the guard
    /// regardless; this governs only the not-hosted fallback message. Mirrors the
    /// pre-flip <c>TxNativeLoadEnabled</c> semantics exactly.
    /// </summary>
    public static bool TxNativeEditorOptIn
    {
        get
        {
            if (NativeLoadEnabledOverride is { } forced) return forced;
            if (Environment.GetEnvironmentVariable("ZEUS_DISABLE_VST_LOAD") == "1") return false;
            return Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD") == "1";
        }
    }

    public async Task InitializeAudioAsync(IAudioHost host, CancellationToken ct)
    {
        if (!NativeLoadEnabled(_slot))
        {
            _log?.LogInformation(
                "VST host '{Name}' registered but native load is disabled "
                + "(clear ZEUS_DISABLE_VST_LOAD / ZEUS_DISABLE_TX_VST_LOAD / "
                + "ZEUS_DISABLE_RX_VST_LOAD to re-enable); passing audio through.",
                DisplayName);
            return; // _handle stays 0 → Process passes through
        }

        // Bridge init is idempotent — the native side ref-counts. (The AU
        // bridge ignores the abi argument and validates against its own
        // ZAU_ABI; VstBridgeAbi.Current is the VST3 ABI but the AU backend's
        // Init clamps to AuBridgeAbi.Current internally.)
        var initStatus = _bridge.Init(VstBridgeAbi.Current);
        if (initStatus != VstBridgeStatus.Ok)
            throw new PluginLoadException(
                $"audio bridge init failed (status={initStatus}); is the native bridge installed?");

        // For an Audio Unit the load identity is a registry triple, not a
        // filesystem path — pass it through unchanged and skip the file check.
        string loadIdentity;
        if (_isAudioUnit)
        {
            loadIdentity = _loadIdentity;
        }
        else
        {
            loadIdentity = Path.IsPathRooted(_loadIdentity)
                ? _loadIdentity
                : Path.Combine(_pluginRootPath, _loadIdentity);

            if (!File.Exists(loadIdentity) && !Directory.Exists(loadIdentity))
                throw new PluginLoadException($"VST3 path not found: {loadIdentity}");
        }

        var blockSize = Math.Max(1, host.CurrentBlockSize);
        // Load at the host's ACTUAL processing rate, not the manifest's
        // declared rate — the plugin must run at the stream rate it will be
        // fed, or its time-based DSP (filters, modulation) is detuned. Falls
        // back to the manifest rate only if the host reports nothing usable.
        var sampleRate = host.CurrentSampleRate > 0 ? host.CurrentSampleRate : Requirements.SampleRate;
        var status = _bridge.LoadVst3(
            loadIdentity,
            Requirements.Channels,
            sampleRate,
            blockSize,
            out _handle);

        if (status != VstBridgeStatus.Ok || _handle == 0)
            throw new PluginLoadException(
                $"{(_isAudioUnit ? "Audio Unit" : "VST3")} load failed for {loadIdentity} (status={status})");

        // Restore before editor open and before later parameter edits. For VST3
        // the native bridge applies component state, then component-state into
        // the controller, then controller state; subsequent set_param calls land
        // after that restore in the processor's parameter queue.
        try
        {
            await RestorePersistedStateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // A failed boot-time restore leaves the plugin unavailable, not a
            // half-initialized native instance that later editor/profile calls
            // can still reach. Clear the managed handle before unloading so even
            // an unload failure cannot advertise this instance as usable.
            var failedHandle = _handle;
            _handle = 0;
            try { _bridge.Unload(failedHandle); }
            catch (Exception ex)
            {
                _log?.LogWarning(ex,
                    "{Kind} '{Name}' cleanup after state restore failure also failed.",
                    _isAudioUnit ? "Audio Unit" : "VST", DisplayName);
            }
            throw;
        }

        // Capture the plugin's reported processing latency (ABI v3). 0 for
        // zero-latency effects; the host sums these to report total insert
        // latency. Defaulted bridges (test fakes) return 0.
        _latencySamples = _bridge.GetLatencySamples(_handle);

        _log?.LogInformation(
            "{Kind} host loaded {Id} (channels={Channels} sr={SampleRate} block={Block} latency={Latency}smp)",
            _isAudioUnit ? "AU" : "VST", loadIdentity,
            Requirements.Channels, sampleRate, blockSize, _latencySamples);
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        if (_handle == 0)
        {
            input.CopyTo(output); // safety: pass through if not initialised
            return;
        }

        var status = _bridge.Process(_handle, input, output, ctx.Frames);
        if (status != VstBridgeStatus.Ok)
        {
            // Realtime path: NEVER throw, NEVER log here (allocation).
            // Pass through on bridge failure — the operator will see a
            // status surface up via the next non-realtime poll.
            input.CopyTo(output);
        }
    }

    /// <summary>
    /// True once the VST is natively loaded (the editor can only open
    /// when there's a real plugin instance behind the handle). False when
    /// native load is gated off (<see cref="NativeLoadEnabled(string)"/>) or the
    /// load failed — in those states the slot passes audio through and
    /// has no GUI to show.
    /// </summary>
    public bool IsNativelyLoaded => _handle != 0;

    /// <summary>Whether the plugin's native editor window is currently open.</summary>
    public bool IsEditorOpen => _handle != 0 && _bridge.EditorIsOpen(_handle);

    /// <summary>
    /// Open the plugin's native editor (its real GUI) in a bridge-owned
    /// OS window. Returns false if the VST isn't natively loaded or the
    /// bridge reports failure (e.g. editor unsupported on this platform).
    /// </summary>
    public bool OpenEditor()
    {
        if (_handle == 0)
        {
            _log?.LogInformation(
                "VST '{Name}' editor open requested but no native handle "
                + "(native load disabled or load failed).", DisplayName);
            return false;
        }
        var status = _bridge.EditorOpen(_handle, DisplayName);
        if (status != VstBridgeStatus.Ok)
            _log?.LogWarning("VST '{Name}' editor open failed (status={Status}).", DisplayName, status);
        return status == VstBridgeStatus.Ok;
    }

    /// <summary>Close the plugin's native editor window if open.</summary>
    public void CloseEditor()
    {
        if (_handle == 0) return;
        _bridge.EditorClose(_handle);
        // Persistence failure must never propagate out of an editor-close
        // (typically a UI-thread call) — losing one capture beats crashing.
        try { CaptureAndPersistState(); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "VST '{Name}' state capture on editor close failed.", DisplayName);
        }
    }

    public Task ShutdownAudioAsync(CancellationToken ct)
    {
        if (_handle != 0)
        {
            // The native unload below must run even when persistence fails,
            // or a store error would leak the native instance at shutdown.
            try { CaptureAndPersistState(ct); }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "VST '{Name}' state capture on shutdown failed.", DisplayName);
            }
            _bridge.Unload(_handle);
            _handle = 0;
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_handle != 0)
        {
            try { await CaptureAndPersistStateAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* swallow */ }
            try { _bridge.Unload(_handle); } catch { /* swallow */ }
            _handle = 0;
        }
    }

    /// <summary>
    /// Capture the current native VST state as base64. Also writes it into this
    /// plugin's <see cref="PluginSettingsStore"/> collection when available, so
    /// restart/reload and TX Audio Profile native dumps share one persistence
    /// surface.
    /// </summary>
    public string? CaptureAndPersistState(CancellationToken ct = default)
        => CaptureAndPersistStateAsync(ct).GetAwaiter().GetResult();

    public async Task<string?> CaptureAndPersistStateAsync(CancellationToken ct = default)
    {
        var base64 = CaptureStateBase64();
        if (base64 is not null)
            await PersistStateBase64Async(base64, ct).ConfigureAwait(false);
        return base64;
    }

    /// <summary>
    /// Capture the current native state without writing the settings store. This
    /// lets editor autosave compare with the last persisted blob before doing I/O.
    /// </summary>
    public string? CaptureStateBase64()
    {
        if (_handle == 0) return null;

        var status = _bridge.GetState(_handle, Span<byte>.Empty, out var required);
        if (status == VstBridgeStatus.NotImplemented)
        {
            LogStateUnavailableOnce("capture");
            return null;
        }
        if (status != VstBridgeStatus.Ok || required <= 0)
        {
            if (status != VstBridgeStatus.Ok)
                _log?.LogDebug("VST '{Name}' state capture failed (status={Status}).", DisplayName, status);
            return null;
        }
        if (required > MaxStateBytes)
        {
            _log?.LogWarning(
                "VST '{Name}' state capture skipped: native bridge reported {Bytes} bytes, above the {Max} byte safety cap.",
                DisplayName, required, MaxStateBytes);
            return null;
        }

        var buffer = new byte[required];
        status = _bridge.GetState(_handle, buffer, out var written);
        if (status == VstBridgeStatus.NotImplemented)
        {
            LogStateUnavailableOnce("capture");
            return null;
        }
        if (status != VstBridgeStatus.Ok || written > buffer.Length)
        {
            _log?.LogDebug(
                "VST '{Name}' state capture failed after allocation (status={Status}, required={Required}, buffer={Buffer}).",
                DisplayName, status, written, buffer.Length);
            return null;
        }

        return written == buffer.Length
            ? Convert.ToBase64String(buffer)
            : Convert.ToBase64String(buffer, 0, written);
    }

    /// <summary>Persist a state blob previously returned by <see cref="CaptureStateBase64"/>.</summary>
    public Task PersistStateBase64Async(string base64, CancellationToken ct = default) =>
        _settingsStore is not null && _pluginId is not null
            ? _settingsStore.ForPlugin(_pluginId).SetAsync(PersistedStateKey, base64, ct)
            : Task.CompletedTask;

    /// <summary>Restore a base64 state blob into the loaded native VST instance.</summary>
    public void RestoreStateBase64(string? base64, bool persist = true, CancellationToken ct = default)
        => RestoreStateBase64Async(base64, persist, ct).GetAwaiter().GetResult();

    public async Task<bool> RestoreStateBase64Async(
        string? base64,
        bool persist = true,
        CancellationToken ct = default)
    {
        if (_handle == 0 || string.IsNullOrWhiteSpace(base64)) return false;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException)
        {
            _log?.LogWarning("VST '{Name}' ignored invalid persisted state blob.", DisplayName);
            return false;
        }

        var status = _bridge.SetState(_handle, bytes);
        if (status == VstBridgeStatus.NotImplemented)
        {
            LogStateUnavailableOnce("restore");
            return false;
        }
        if (status != VstBridgeStatus.Ok)
        {
            _log?.LogWarning("VST '{Name}' state restore failed (status={Status}).", DisplayName, status);
            return false;
        }

        if (persist && _settingsStore is not null && _pluginId is not null)
            await _settingsStore.ForPlugin(_pluginId).SetAsync(PersistedStateKey, base64, ct).ConfigureAwait(false);
        return true;
    }

    private async Task RestorePersistedStateAsync(CancellationToken ct)
    {
        if (_settingsStore is null || _pluginId is null || _handle == 0) return;
        var saved = await _settingsStore.ForPlugin(_pluginId)
            .GetAsync<string>(PersistedStateKey, ct)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(saved))
        {
            if (_isAudioUnit && !_canRestoreAudioUnitState)
            {
                throw new PluginLoadException(
                    $"Audio Unit '{DisplayName}' has saved state but cannot restore it in "
                    + "headless macOS service mode because no main UI run loop is available; "
                    + "the plugin was left unavailable instead of blocking server startup.");
            }
            await RestoreStateBase64Async(saved, persist: false, ct).ConfigureAwait(false);
        }
    }

    private void LogStateUnavailableOnce(string operation)
    {
        if (Interlocked.Exchange(ref s_stateUnavailableLogged, 1) != 0) return;
        _log?.LogWarning(
            "Native audio bridge does not expose state persistence; plugin state {Operation} is disabled until the native bridge is refreshed.",
            operation);
    }
}
