// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>A single key chord, mirroring zeus-web's Keybinding shape
/// (zeus-web/src/state/keybindings-store.ts). Hosting-only wire DTO — not
/// shared with any consumer besides GlobalHotkeyBindingsStore/Endpoints.</summary>
public sealed record HotkeyKeybindingDto(string Key, bool Alt, bool Ctrl, bool Shift, bool Meta);

/// <summary>
/// In-memory mirror of the hotkey bindings eligible for native OS-level
/// global-hotkey registration (tune/zoom/station-favorite recall — never
/// MOX/TUNE/chat-PTT; see zeus-web/src/util/use-global-hotkey-sync.ts for the
/// full eligibility rationale). Bindings stay authoritative in the browser's
/// localStorage; this store exists only so Zeus.Host's desktop-only
/// GlobalHotkeyManager can learn the operator's current chords without
/// reaching into the webview. Populated by a best-effort push from the
/// frontend on every launch and rebind — nothing here needs to survive a
/// process restart, so no LiteDB persistence.
/// </summary>
public sealed class GlobalHotkeyBindingsStore
{
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, HotkeyKeybindingDto?> _bindings =
        new Dictionary<string, HotkeyKeybindingDto?>();

    /// <summary>Raised after every SetBindings call, including no-op pushes,
    /// so GlobalHotkeyManager can debounce on its own side if it wants to.</summary>
    public event Action? Changed;

    public IReadOnlyDictionary<string, HotkeyKeybindingDto?> Current
    {
        get { lock (_gate) return _bindings; }
    }

    public void SetBindings(IReadOnlyDictionary<string, HotkeyKeybindingDto?> bindings)
    {
        lock (_gate) _bindings = bindings;
        Changed?.Invoke();
    }
}
