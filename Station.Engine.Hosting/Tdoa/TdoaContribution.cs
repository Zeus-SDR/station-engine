// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server.Tdoa;

public enum TdoaContributionSourceKind
{
    Hpsdr,
    KiwiSdr,
}

/// <summary>Immutable authorization and capture request supplied by a future
/// local relay adapter. Friendship has already been evaluated by the chat layer,
/// but is repeated here so capture cannot accidentally bypass that boundary.</summary>
public sealed record TdoaContributionRequest(
    string RequesterId,
    bool RequesterIsAcceptedFriend,
    bool RequesterIsMutualFriend,
    TdoaContributionSourceKind Source,
    long CenterFrequencyHz,
    int SampleCount,
    TimeSpan Timeout);

public sealed record TdoaContributionEligibility(
    bool Eligible,
    string? Source,
    string? SourceLabel,
    string? Reason);

/// <summary>Immutable, transport-ready GNSS sample-domain capture. IQ is
/// complex-float32 little-endian encoded as base64.</summary>
public sealed record TdoaContributionCapture(
    string Source,
    string StationId,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeMeters,
    string ReferenceTimeTaiNanoseconds,
    double SampleRateHz,
    double GroupDelayNanoseconds,
    double ClockUncertaintyNanoseconds,
    bool ClockLocked,
    long CenterFrequencyHz,
    int SampleCount,
    string IqBase64);

public sealed record TdoaContributionResult(
    bool Accepted,
    TdoaContributionCapture? Capture,
    string? Reason)
{
    public static TdoaContributionResult Declined(string reason) => new(false, null, reason);
    public static TdoaContributionResult Completed(TdoaContributionCapture capture) => new(true, capture, null);
}

/// <summary>Originless product-to-engine command. The product chat layer owns
/// the persisted participation preference and friendship decision; the engine
/// repeats both gates before touching a capture transport.</summary>
public sealed record TdoaContributionCaptureCommand(
    bool ParticipationEnabled,
    TdoaContributionRequest Request);

public sealed record TdoaPublicKiwiCaptureCommand(
    string Url,
    TdoaContributionRequest Request);

public interface ITdoaContributionSource
{
    TdoaContributionSourceKind Kind { get; }
    TdoaContributionEligibility GetEligibility();
    Task<TdoaContributionResult> CaptureAsync(TdoaContributionRequest request, CancellationToken cancellationToken);
}

/// <summary>Non-mutating contribution coordinator. It has no RadioService or
/// protocol client dependency and therefore cannot tune, key, or reconfigure an HPSDR.</summary>
public sealed class TdoaContributionCoordinator(IEnumerable<ITdoaContributionSource> sources)
{
    private readonly IReadOnlyDictionary<TdoaContributionSourceKind, ITdoaContributionSource> _sources =
        sources.ToDictionary(source => source.Kind);

    public TdoaContributionEligibility GetLocalEligibility()
    {
        if (_sources.TryGetValue(TdoaContributionSourceKind.KiwiSdr, out var kiwi))
            return kiwi.GetEligibility();
        return new(false, null, null, "No GNSS sample-domain capture source is configured.");
    }

    public TdoaContributionEligibility Evaluate(TdoaContributionRequest request, bool participationEnabled)
    {
        if (!participationEnabled)
            return new(false, null, null, "TDoA participation is off.");
        if (string.IsNullOrWhiteSpace(request.RequesterId)
            || !request.RequesterIsAcceptedFriend
            || !request.RequesterIsMutualFriend)
            return new(false, null, null, "Only accepted mutual friends may request a contribution.");
        if (request.SampleCount is < TdoaLimits.MinComplexSamplesPerStation or > TdoaLimits.MaxComplexSamplesPerStation)
            return new(false, null, null,
                $"sampleCount must be in [{TdoaLimits.MinComplexSamplesPerStation}, {TdoaLimits.MaxComplexSamplesPerStation}].");
        if (request.CenterFrequencyHz is <= 0 or > 60_000_000)
            return new(false, null, null, "centerFrequencyHz must be in (0, 60000000].");
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromSeconds(60))
            return new(false, null, null, "timeout must be in (0, 60] seconds.");
        if (request.Source == TdoaContributionSourceKind.Hpsdr)
            return new(false, "hpsdr", "HPSDR", "HPSDR contribution is unavailable until the ADC supplies locked GNSS/TAI sample timestamps.");
        if (request.Source == TdoaContributionSourceKind.KiwiSdr
            && request.CenterFrequencyHz > KiwiTdoaContributionSource.MaxCenterFrequencyHz)
            return new(false, "kiwi", "KiwiSDR GNSS IQ",
                $"KiwiSDR contribution centerFrequencyHz must not exceed {KiwiTdoaContributionSource.MaxCenterFrequencyHz}.");
        return _sources.TryGetValue(request.Source, out var source)
            ? source.GetEligibility()
            : new(false, null, null, "Requested capture source is unavailable.");
    }

    public async Task<TdoaContributionResult> CaptureAsync(TdoaContributionRequest request,
        bool participationEnabled, CancellationToken cancellationToken = default)
    {
        TdoaContributionEligibility eligibility = Evaluate(request, participationEnabled);
        if (!eligibility.Eligible) return TdoaContributionResult.Declined(eligibility.Reason ?? "Not eligible.");
        var source = _sources[request.Source];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try
        {
            return await source.CaptureAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TdoaContributionResult.Declined("Capture timed out without a complete GNSS-tagged IQ block.");
        }
    }

    public async Task<TdoaContributionResult> CapturePublicKiwiAsync(string url,
        TdoaContributionRequest request, CancellationToken cancellationToken = default)
    {
        if (!_sources.TryGetValue(TdoaContributionSourceKind.KiwiSdr, out var source)
            || source is not KiwiTdoaContributionSource kiwi)
            return TdoaContributionResult.Declined("Public KiwiSDR capture is unavailable.");
        if (request.SampleCount is < TdoaLimits.MinComplexSamplesPerStation or > TdoaLimits.MaxComplexSamplesPerStation
            || request.CenterFrequencyHz is <= 0 or > KiwiTdoaContributionSource.MaxCenterFrequencyHz
            || request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromSeconds(60))
            return TdoaContributionResult.Declined("Public KiwiSDR capture parameters are invalid.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try { return await kiwi.CapturePublicAsync(url, request, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return TdoaContributionResult.Declined("Public KiwiSDR capture timed out."); }
    }
}
