// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Display data supplied by an optional product-owned radio sidecar.</summary>
public sealed record ExternalDisplayFrame(
    byte RxId,
    DisplayBodyFlags BodyFlags,
    long CenterHz,
    float HzPerPixel,
    float[] PanDb,
    float[] WfDb);

/// <summary>Product-neutral data path for an optional external radio sidecar.</summary>
public interface IExternalRadioSidecar
{
    Task DisconnectAsync(CancellationToken ct);

    ValueTask<ExternalDisplayFrame?> FetchDisplayFrameAsync(
        int targetWidth,
        int zoomLevel,
        long centerHz,
        CancellationToken ct);

    /// <summary>
    /// Forwards one realtime TX-IQ block. Implementations must not allocate,
    /// block, log, or allow exceptions to escape.
    /// </summary>
    void ConfigureTxIqSafetyGate(Func<long, bool> gate);
    void ForwardTxIq(ReadOnlySpan<float> iqInterleaved, long safetyRevision);
    void RevokeTxIq();
}

/// <summary>Standalone sidecar default that supplies no frames or TX sink.</summary>
public sealed class NullExternalRadioSidecar : IExternalRadioSidecar
{
    public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask<ExternalDisplayFrame?> FetchDisplayFrameAsync(
        int targetWidth,
        int zoomLevel,
        long centerHz,
        CancellationToken ct) => new((ExternalDisplayFrame?)null);

    public void ConfigureTxIqSafetyGate(Func<long, bool> gate) { }
    public void ForwardTxIq(ReadOnlySpan<float> iqInterleaved, long safetyRevision) { }
    public void RevokeTxIq() { }
}
