// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Plugins.Contracts;

/// <summary>
/// Capability flags a plugin declares in its manifest. ABI 1 automatically
/// grants every declared capability. These flags describe and route access;
/// they are not a security sandbox or an operator-consent boundary. Services
/// that were not declared, or are unavailable on the host, surface as null on
/// <see cref="IPluginContext"/>.
/// </summary>
[Flags]
public enum PluginCapabilities
{
    None             = 0,

    /// <summary>Subscribe to frequency / mode / band / MOX events.</summary>
    ReadRadioState   = 1 << 0,

    /// <summary>Call into RadioController to change VFO / mode / MOX.</summary>
    ControlRadio     = 1 << 1,

    /// <summary>Process RX or TX audio blocks (requires IAudioPlugin).</summary>
    AudioStream      = 1 << 2,

    /// <summary>Open outbound network sockets / HTTP clients.</summary>
    NetworkAccess    = 1 << 3,

    /// <summary>Read arbitrary files outside the plugin's own directory.</summary>
    FileSystemRead   = 1 << 4,

    /// <summary>Write arbitrary files outside the plugin's own directory.</summary>
    FileSystemWrite  = 1 << 5,

    /// <summary>
    /// Persist plugin-scoped settings via IPluginContext.Settings.
    /// Granted by default to every plugin; declared in manifest only
    /// for documentation symmetry.
    /// </summary>
    PersistSettings  = 1 << 6,
}
