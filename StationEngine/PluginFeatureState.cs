// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Plugins.Host;
using Zeus.Server;

namespace Zeus.StationEngine;

internal sealed class PluginFeatureState : IInstalledFeatureState, IInstalledFeatureChangeSource, IDisposable
{
    private readonly PluginManager _plugins;

    public PluginFeatureState(PluginManager plugins)
    {
        _plugins = plugins;
        _plugins.PluginActivated += OnPluginChanged;
        _plugins.PluginDeactivated += OnPluginChanged;
    }

    public event Action? Changed;

    public bool IsActive(string pluginId) => _plugins.Find(pluginId) is not null;

    public void Dispose()
    {
        _plugins.PluginActivated -= OnPluginChanged;
        _plugins.PluginDeactivated -= OnPluginChanged;
    }

    private void OnPluginChanged(ActivatedPlugin plugin) => Changed?.Invoke();
}
