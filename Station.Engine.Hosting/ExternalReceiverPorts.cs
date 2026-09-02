// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Product-neutral source for one optional external receiver.</summary>
public interface IExternalReceiverSource
{
    ReceiverDto? GetReceiver();
    event Action? ReceiverChanged;
}

/// <summary>Standalone external-receiver default with no projected receiver.</summary>
public sealed class NullExternalReceiverSource : IExternalReceiverSource
{
    public ReceiverDto? GetReceiver() => null;

    public event Action? ReceiverChanged
    {
        add { }
        remove { }
    }
}

/// <summary>
/// Product-neutral control seam for the optional external receiver projected
/// into the engine's reserved receiver slot.
/// </summary>
public interface IExternalReceiverControlPort
{
    void SetTuning(
        long? vfoHz,
        RxMode? mode,
        int? filterLowHz,
        int? filterHighHz,
        bool ctun);

    void SetCenter(long centerHz);
    void SetAfGainDb(double db);
    void SetMuted(bool muted);
    void SetZoom(int level);
}

/// <summary>Standalone external-receiver control default with no side effects.</summary>
public sealed class NullExternalReceiverControlPort : IExternalReceiverControlPort
{
    public void SetTuning(
        long? vfoHz,
        RxMode? mode,
        int? filterLowHz,
        int? filterHighHz,
        bool ctun) { }

    public void SetCenter(long centerHz) { }
    public void SetAfGainDb(double db) { }
    public void SetMuted(bool muted) { }
    public void SetZoom(int level) { }
}

/// <summary>
/// Product-neutral pull source for one external 48 kHz mono RX stream.
/// </summary>
/// <remarks>
/// <see cref="Read"/> is a realtime callback: implementations must not
/// allocate, block, log, or allow exceptions to escape.
/// </remarks>
public interface IExternalRxAudioSource
{
    bool Active { get; }
    int Read(Span<float> destination);
}

/// <summary>Standalone external-audio default that produces no samples.</summary>
public sealed class NullExternalRxAudioSource : IExternalRxAudioSource
{
    public bool Active => false;
    public int Read(Span<float> destination) => 0;
}
