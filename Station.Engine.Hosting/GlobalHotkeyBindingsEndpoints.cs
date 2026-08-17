// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>Maps the desktop-shell-only global-hotkey-bindings mirror route.
/// See GlobalHotkeyBindingsStore for why this exists.</summary>
public static class GlobalHotkeyBindingsEndpoints
{
    // Defensive allow-list, mirroring GLOBAL_HOTKEY_ACTION_IDS in
    // zeus-web/src/util/use-global-hotkey-sync.ts. mox/tune/chatFriendPtt/
    // chatNetPtt must never reach the native global-hotkey path (RF safety —
    // a keystroke in another app must never key the transmitter) and
    // mapZoomIn/mapZoomOut have no backend concept to replicate, even though
    // the frontend already filters to this same set before pushing.
    private static readonly HashSet<string> EligibleActionIds = new(StringComparer.Ordinal)
    {
        "tuneDown", "tuneUp", "zoomIn", "zoomOut", "mute",
        "stationFavorite1", "stationFavorite2", "stationFavorite3",
        "stationFavorite4", "stationFavorite5",
        "previousStationFavorite", "nextStationFavorite",
    };

    public static IEndpointRouteBuilder MapGlobalHotkeyBindingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/hotkeys/global-bindings", (
            Dictionary<string, HotkeyKeybindingDto?> request,
            GlobalHotkeyBindingsStore store) =>
        {
            var filtered = new Dictionary<string, HotkeyKeybindingDto?>(StringComparer.Ordinal);
            foreach (var (id, chord) in request)
            {
                if (EligibleActionIds.Contains(id)) filtered[id] = chord;
            }
            store.SetBindings(filtered);
            return Results.Ok(new { ok = true });
        });

        return endpoints;
    }
}
