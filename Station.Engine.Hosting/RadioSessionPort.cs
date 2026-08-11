// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>The radio architectures that can own a live Zeus session.</summary>
public enum RadioFamily
{
    Hpsdr,
    Flex,
}

/// <summary>Family-neutral lifecycle state for one radio session.</summary>
public enum RadioSessionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted,
    /// <summary>An unrecognized underlying state; never a healthy disconnect.</summary>
    Unknown,
}

/// <summary>
/// Family-scoped identity for a connected radio. Family-specific identity
/// types, such as an HPSDR board enum, deliberately do not cross this seam.
/// <see cref="Model"/> is display text only and must never be used as a
/// dispatch key; family implementations may deliberately use different
/// unknown-model labels.
/// </summary>
public sealed record RadioIdentity(
    RadioFamily Family,
    string? SerialNumber,
    string Model,
    string DisplayName,
    string? FirmwareVersion = null,
    string? Endpoint = null);

/// <summary>
/// Immutable snapshot of behaviours a session can provide. Every member is
/// fail-closed by default. In particular, <see cref="OwnsRxAudio"/> belongs to
/// the session: consumers must never infer audio ownership from
/// <see cref="IRadioSession.Family"/>.
/// </summary>
public sealed record SessionCapabilities(
    bool CanTune = false,
    bool CanSetMode = false,
    bool CanSetFilter = false,
    bool CanSetAgc = false,
    bool CanTransmit = false,
    bool OwnsRxAudio = false,
    bool ProvidesSpectrum = false,
    bool ProvidesMetering = false,
    bool HasAtu = false,
    int MaxReceivers = 0,
    int MaxRxSampleRateHz = 0);

/// <summary>
/// An already-authorized request to enter or leave transmit. Instances are
/// issued above the radio-session seam; a session translates this intent but
/// never decides whether RF transmission is allowed.
/// </summary>
public abstract class AuthorizedTransmitIntent
{
    private protected AuthorizedTransmitIntent(bool transmit) => Transmit = transmit;

    public bool Transmit { get; }
}

/// <summary>
/// The engine-internal authorization boundary. The safety/interlock owner
/// above a session is the only intended caller; session implementations must
/// not issue their own intents. Minting is available by convention inside this
/// assembly and its six friend assemblies; it is compiler-enforced only outside
/// that set. Closing that assembly boundary belongs to the Phase 4 TX-gate
/// refactor.
/// </summary>
internal static class TransmitIntentAuthorizer
{
    public static AuthorizedTransmitIntent Authorize(bool transmit) => new Intent(transmit);

    private sealed class Intent(bool transmit) : AuthorizedTransmitIntent(transmit);
}

/// <summary>
/// Marker for typed, per-domain truth flowing up from a session. Consumers
/// dispatch on the concrete record type, so later domains can be added without
/// changing this interface or the session event signature. Delivery ordering
/// is best-effort and unsequenced; consumers must tolerate out-of-order or
/// regressed observations instead of assuming monotonic delivery.
/// </summary>
public interface IRadioSessionDelta;

/// <summary>
/// Reported operating-context truth. A null field was not reported and must not
/// clear prior state. Steady-state deltas contain only changed fields; a
/// connect-time or explicit resync delta contains every current baseline value
/// for which the session has source truth. It never fills an absent value with
/// a default.
/// <see cref="Mode"/> takes precedence when both mode channels are present. If
/// only <see cref="ModeRaw"/> is present, consumers render it verbatim and gate
/// mode-dependent DSP controls off.
/// </summary>
public sealed record OperatingContextDelta(
    long? FrequencyHz = null,
    RxMode? Mode = null,
    int? FilterLowHz = null,
    int? FilterHighHz = null,
    AgcConfig? Agc = null) : IRadioSessionDelta
{
    /// <summary>A vendor mode name Zeus cannot represent with <see cref="RxMode"/>.</summary>
    public string? ModeRaw { get; init; }
}

/// <summary>
/// Reported transmit and interlock truth. Null means not reported. For an
/// HPSDR session, transmit truth is the connected state combined with the
/// RadioService MOX latch, not hardware or wire confirmation. HPSDR never
/// reports either interlock field because admission belongs to TxService;
/// absence therefore does not mean that an interlock denied transmission.
/// </summary>
public sealed record TransmitStateDelta(
    bool? IsTransmitting = null,
    bool? InterlockAllowsTransmit = null,
    string? InterlockReason = null) : IRadioSessionDelta;

/// <summary>Reported connection changes. Null means not reported.</summary>
public sealed record ConnectionStateDelta(
    RadioSessionState? State = null,
    RadioIdentity? Identity = null,
    SessionCapabilities? Capabilities = null) : IRadioSessionDelta;

/// <summary>Reported receive metering changes. Null means not reported.</summary>
public sealed record MeteringDelta(
    int? ReceiverIndex = null,
    double? SignalDbm = null) : IRadioSessionDelta;

// Phase N: add pan/display, meter catalog, memory, profile, GPS, ATU and
// transverter domains only when a session can emit truthful values for them.

/// <summary>
/// Shared present-only application rule for every delta decoder. An absent or
/// invalid report leaves the previous value untouched; it must never be
/// interpreted as the type's default value.
/// </summary>
public static class RadioSessionDeltaApply
{
    public static bool TryApplyPresent<T>(
        T? reported,
        Func<T, bool> isValid,
        ref T current)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(isValid);
        if (reported is not T value || !isValid(value)) return false;

        current = value;
        return true;
    }

    public static bool TryApplyPresent<T>(
        T? reported,
        Func<T, bool> isValid,
        ref T current)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(isValid);
        if (reported is null || !isValid(reported)) return false;

        current = reported;
        return true;
    }
}

/// <summary>A fault reported by session lifecycle or its underlying transport.</summary>
public sealed record RadioSessionFault(string Operation, string Message, Exception Exception);

/// <summary>
/// One live radio session, independent of where DSP executes. An HPSDR session
/// runs host DSP while another implementation may decode radio-owned streams;
/// both expose the same request/down and truth/up boundary.
/// <para>
/// Every verb is explicitly a request and returns no applied state. Callers
/// must wait for a typed <see cref="DeltaReceived"/> notification before
/// treating a requested value as truth.
/// </para>
/// </summary>
public interface IRadioSession : IDisposable
{
    RadioFamily Family { get; }
    RadioIdentity Identity { get; }
    SessionCapabilities Capabilities { get; }
    RadioSessionState State { get; }

    event Action<IRadioSessionDelta>? DeltaReceived;
    event Action<RadioSessionFault>? Faulted;

    /// <summary>
    /// Idempotently republishes current operating, transmit and connection
    /// truth, omitting any field the session cannot currently report. Consumers
    /// call this immediately after attaching event handlers so wrapping an
    /// already-connected session cannot lose its initial state.
    /// </summary>
    void RequestResync();

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    ValueTask RequestFrequencyAsync(long frequencyHz, CancellationToken ct = default);
    ValueTask RequestModeAsync(RxMode mode, CancellationToken ct = default);
    ValueTask RequestFilterAsync(int lowHz, int highHz, CancellationToken ct = default);
    ValueTask RequestAgcAsync(AgcConfig agc, CancellationToken ct = default);

    /// <summary>
    /// Translates an intent already authorized above this seam. The required
    /// capability object has no public constructor, so ordinary callers cannot
    /// key a session by passing a bare Boolean.
    /// </summary>
    ValueTask RequestTransmitAsync(
        AuthorizedTransmitIntent intent,
        CancellationToken ct = default);
}
