// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugins.Contracts.Registry;

namespace Zeus.Plugins.Host.Registry;

/// <summary>
/// Published plugin id renames. The host uses this table for startup migration
/// and registry browse filtering; future renames should be one-line additions.
/// </summary>
internal static class PluginIdMigrations
{
    public static IReadOnlyDictionary<string, string> Map { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["com.kb2uka.voyeur"] = "org.openhpsdr.voyeur",
            ["com.kb2uka.recorder"] = "org.openhpsdr.recorder",
            ["com.kb2uka.digital"] = "org.openhpsdr.digital",
            ["com.openhpsdr.zeus.plugins.remoteaccess"] = "com.zeussdr.plugins.remoteaccess",
        };

    public static RegistryCatalog FilterSupersededEntries(RegistryCatalog catalog)
    {
        var plugins = catalog.Plugins
            .Where(p => !Map.ContainsKey(p.Id))
            .ToArray();

        return catalog with { Plugins = plugins };
    }
}
