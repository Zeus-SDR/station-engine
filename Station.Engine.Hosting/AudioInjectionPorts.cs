// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>
/// Product-neutral access to the active RX/TX audio modem.
/// </summary>
/// <remarks>
/// All RX/TX callback members are realtime paths: implementations must not
/// allocate, block, log, or allow exceptions to escape.
/// </remarks>
public interface IAudioModemPort
{
    bool Available { get; }
    bool Active { get; }
    void SyncMode(byte rxModeByte);
    void ProcessRx(Span<float> block48k);
    /// <summary>
    /// Transforms live mic audio in place. After <see cref="FinishTx"/>, drains
    /// queued tail audio instead until <see cref="FlushTx"/> resets the over.
    /// </summary>
    void ProcessTx(Span<float> block48k);
    void FlushRx();
    void FlushTx();
    /// <summary>Queued tail samples remaining after the latest drain block.</summary>
    int PendingTxSamples { get; }
    int FinishTx();
}

/// <summary>Standalone audio-modem default that preserves the no-plugin path.</summary>
public sealed class NullAudioModemPort : IAudioModemPort
{
    public bool Available => false;
    public bool Active => false;
    public int PendingTxSamples => 0;
    public void SyncMode(byte rxModeByte) { }
    public void ProcessRx(Span<float> block48k) { }
    public void ProcessTx(Span<float> block48k) { }
    public void FlushRx() { }
    public void FlushTx() { }
    public int FinishTx() => 0;
}

/// <summary>
/// Product-neutral live-mic preview processor.
/// </summary>
/// <remarks>
/// This is a realtime callback: implementations must not allocate, block,
/// log, or allow exceptions to escape.
/// </remarks>
public interface ITxAudioPreviewProcessor
{
    void ProcessPreview(ReadOnlySpan<float> mic, int sampleRate);
}

/// <summary>Standalone preview default that leaves live mic audio untouched.</summary>
public sealed class NullTxAudioPreviewProcessor : ITxAudioPreviewProcessor
{
    public void ProcessPreview(ReadOnlySpan<float> mic, int sampleRate) { }
}

/// <summary>
/// Product-neutral external TX audio insert. Implementations transform blocks
/// in place and must fail open without affecting modem lifecycle semantics.
/// </summary>
/// <remarks>
/// This is a realtime callback: implementations must not allocate, take an
/// unbounded wait, log, or allow exceptions to escape.
/// </remarks>
public interface IProductTxAudioPort
{
    bool Active { get; }
    void ProcessTx(Span<float> block48k);
    void ProcessRx(Span<float> block48k) { }
}

/// <summary>Default product TX insert; always dead and always passthrough.</summary>
public sealed class NullProductTxAudioPort : IProductTxAudioPort
{
    public bool Active => false;
    public void ProcessTx(Span<float> block48k) { }
    public void ProcessRx(Span<float> block48k) { }
}
