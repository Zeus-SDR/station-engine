// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Text.Json;
using Zeus.Server;

namespace Zeus.StationEngine;

/// <summary>
/// Exposes first-party Zeus Link feature toggles to Station Engine hardware
/// services. The mirrored product document remains opaque everywhere else;
/// this adapter reads only the entitlement-effective boolean needed by the
/// Taurus service and preserves the legacy identifier accepted by its safety gates.
/// </summary>
internal sealed class ProductFeatureState :
    IInstalledFeatureState,
    IInstalledFeatureChangeSource,
    IDisposable
{
    internal const string SpeTaurusFeatureId = "spe-taurus";
    internal const string LegacySpeTaurusPluginId = "org.openhpsdr.speexperttaurus";

    private readonly ProductBundleSettingsStore _store;

    public ProductFeatureState(ProductBundleSettingsStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
    }

    public event Action? Changed;

    public bool IsActive(string featureId)
    {
        if (!string.Equals(featureId, SpeTaurusFeatureId, StringComparison.Ordinal)
            && !string.Equals(featureId, LegacySpeTaurusPluginId, StringComparison.Ordinal))
            return false;

        var entry = _store.Get();
        if (entry is null || string.IsNullOrWhiteSpace(entry.Json)) return false;
        try
        {
            using var document = JsonDocument.Parse(entry.Json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("effectiveFeaturesExpiresAtUnixSeconds", out var expiry)
                && expiry.ValueKind == JsonValueKind.Number
                && expiry.TryGetInt64(out var expiresAt)
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt)
                return false;
            return root.TryGetProperty("effectiveFeatures", out var features)
                && features.ValueKind == JsonValueKind.Object
                && features.TryGetProperty(SpeTaurusFeatureId, out var enabled)
                && enabled.ValueKind is JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose() => _store.Changed -= OnStoreChanged;

    private void OnStoreChanged() => Changed?.Invoke();
}
