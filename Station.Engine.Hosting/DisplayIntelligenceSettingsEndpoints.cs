// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

public static class DisplayIntelligenceSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDisplayIntelligenceSettingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        // Signal Intelligence weak-signal display policy. The frontend owns the
        // live CFAR/noise-floor math; the backend persists the active profile
        // and tuning so multiple clients and diagnostics agree on the policy.
        endpoints.MapGet("/api/dsp/display-intelligence", (DisplayIntelligenceSettingsStore store) =>
            Results.Ok(store.Get()));

        endpoints.MapPut("/api/dsp/display-intelligence", (
            DisplayIntelligenceSettingsDto req,
            DisplayIntelligenceSettingsStore store) =>
        {
            if (!TryValidateDisplayIntelligenceSettings(req, out var err))
                return Results.BadRequest(new { error = err });
            return Results.Ok(store.Save(req));
        });

        return endpoints;
    }

    static bool TryValidateDisplayIntelligenceSettings(DisplayIntelligenceSettingsDto settings, out string error)
    {
        var profileId = settings.ProfileId?.Trim().ToLowerInvariant();
        if (profileId is not ("balanced" or "dx" or "cw" or "digital" or "voice" or "contest" or "custom"))
        {
            error = "profileId must be one of balanced, dx, cw, digital, voice, contest, custom";
            return false;
        }
        if (!double.IsFinite(settings.PopFloorDb) || settings.PopFloorDb < 0.0 || settings.PopFloorDb > 12.0)
        {
            error = "popFloorDb must be 0..12 dB";
            return false;
        }
        if (!double.IsFinite(settings.PopSpanDb) || settings.PopSpanDb < 12.0 || settings.PopSpanDb > 60.0)
        {
            error = "popSpanDb must be 12..60 dB";
            return false;
        }
        if (!double.IsFinite(settings.PopGamma) || settings.PopGamma < 0.3 || settings.PopGamma > 1.2)
        {
            error = "popGamma must be 0.3..1.2";
            return false;
        }
        if (settings.PopRenderIntensity < 0 || settings.PopRenderIntensity > 100)
        {
            error = "popRenderIntensity must be 0..100";
            return false;
        }
        if (settings.WaterfallPopRenderIntensity is < 0 or > 100)
        {
            error = "waterfallPopRenderIntensity must be 0..100";
            return false;
        }
        if (settings.WaterfallReliefDepth < 0 || settings.WaterfallReliefDepth > 100)
        {
            error = "waterfallReliefDepth must be 0..100";
            return false;
        }
        if (settings.WaterfallSmoothness < 0 || settings.WaterfallSmoothness > 100)
        {
            error = "waterfallSmoothness must be 0..100";
            return false;
        }
        if (!double.IsFinite(settings.CoherenceHoldGate) || settings.CoherenceHoldGate < 0.2 || settings.CoherenceHoldGate > 0.8)
        {
            error = "coherenceHoldGate must be 0.2..0.8";
            return false;
        }
        if (!double.IsFinite(settings.CoherenceBoostDb) || settings.CoherenceBoostDb < 0.0 || settings.CoherenceBoostDb > 8.0)
        {
            error = "coherenceBoostDb must be 0..8 dB";
            return false;
        }
        if (!double.IsFinite(settings.RidgeBoost) || settings.RidgeBoost < 0.0 || settings.RidgeBoost > 0.8)
        {
            error = "ridgeBoost must be 0..0.8";
            return false;
        }
        if (!double.IsFinite(settings.RidgeMaxBoostDb) || settings.RidgeMaxBoostDb < 0.0 || settings.RidgeMaxBoostDb > 12.0)
        {
            error = "ridgeMaxBoostDb must be 0..12 dB";
            return false;
        }
        if (settings.VisualAgcStrength < 0 || settings.VisualAgcStrength > 100)
        {
            error = "visualAgcStrength must be 0..100";
            return false;
        }
        if (settings.ImpulseRejectDb < 8 || settings.ImpulseRejectDb > 32)
        {
            error = "impulseRejectDb must be 8..32 dB";
            return false;
        }
        if (settings.SnapRadiusHz < 500 || settings.SnapRadiusHz > 12_000)
        {
            error = "snapRadiusHz must be 500..12000 Hz";
            return false;
        }
        if (!double.IsFinite(settings.SnapMinSnrDb) || settings.SnapMinSnrDb < 3.0 || settings.SnapMinSnrDb > 16.0)
        {
            error = "snapMinSnrDb must be 3..16 dB";
            return false;
        }
        if (!double.IsFinite(settings.PeakMinSnrDb) || settings.PeakMinSnrDb < 4.0 || settings.PeakMinSnrDb > 20.0)
        {
            error = "peakMinSnrDb must be 4..20 dB";
            return false;
        }
        error = "";
        return true;
    }

}
