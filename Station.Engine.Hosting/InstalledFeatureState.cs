// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

namespace Zeus.Server;

internal interface IInstalledFeatureState
{
    bool IsActive(string pluginId);
}

internal interface IInstalledFeatureChangeSource
{
    event Action? Changed;
}

internal sealed class NoInstalledFeatureState : IInstalledFeatureState
{
    public bool IsActive(string pluginId) => false;
}
