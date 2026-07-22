// SPDX-License-Identifier: GPL-2.0-or-later

using Station.AudioIpc.Spike;

namespace Zeus.Server;

/// <summary>
/// Measurement-only cross-process audio adapter. It is never registered by
/// normal engine startup and can only be constructed when ZEUS_SPIKE_IPC is
/// explicitly set to shm or udp.
/// </summary>
public sealed class PluginHostAudioSpikeAdapter : IAudioModemPort, IDisposable
{
    private readonly IAudioBlockRoundTripClient _transport;
    private readonly TimeSpan _timeout;
    private readonly float[] _processed = new float[AudioIpcProtocol.SamplesPerBlock];
    private long _attempts;
    private long _completed;
    private long _bypassed;
    private int _attached;

    public PluginHostAudioSpikeAdapter(IAudioBlockRoundTripClient transport, TimeSpan? timeout = null)
    {
        _transport = transport;
        _timeout = timeout ?? TimeSpan.FromMilliseconds(15);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMilliseconds(AudioIpcProtocol.BlockPeriodMilliseconds))
            throw new ArgumentOutOfRangeException(nameof(timeout), "The spike timeout must fit inside one 20 ms block.");
    }

    public bool Available => true;
    public bool Active { get; set; } = true;
    public int PendingTxSamples => 0;
    public bool Attached => Volatile.Read(ref _attached) != 0;

    public PluginHostAudioSpikeSnapshot Snapshot => new(
        Interlocked.Read(ref _attempts),
        Interlocked.Read(ref _completed),
        Interlocked.Read(ref _bypassed),
        Attached);

    public static bool IsEnabledFromEnvironment(out AudioIpcTransportKind kind)
    {
        var value = Environment.GetEnvironmentVariable("ZEUS_SPIKE_IPC");
        if (string.Equals(value, "shm", StringComparison.OrdinalIgnoreCase))
        {
            kind = AudioIpcTransportKind.SharedMemory;
            return true;
        }

        if (string.Equals(value, "udp", StringComparison.OrdinalIgnoreCase))
        {
            kind = AudioIpcTransportKind.Udp;
            return true;
        }

        kind = default;
        return false;
    }

    public bool ProcessBlock(Span<float> block48k, out double roundTripMilliseconds)
    {
        if (!Active || block48k.Length != AudioIpcProtocol.SamplesPerBlock)
        {
            roundTripMilliseconds = 0;
            return false;
        }

        Interlocked.Increment(ref _attempts);
        try
        {
            if (_transport.TryRoundTrip(block48k, _processed, _timeout, out var ticks))
            {
                _processed.AsSpan().CopyTo(block48k);
                roundTripMilliseconds = AudioIpcProtocol.TicksToMilliseconds(ticks);
                Interlocked.Increment(ref _completed);
                Volatile.Write(ref _attached, 1);
                return true;
            }
        }
        catch (Exception)
        {
            // Spike fail-safe contract: transport faults never cross the audio
            // seam. Since the caller's block is only overwritten after a full,
            // matching response, every failure is clean passthrough.
        }

        roundTripMilliseconds = 0;
        Interlocked.Increment(ref _bypassed);
        Volatile.Write(ref _attached, 0);
        return false;
    }

    void IAudioModemPort.SyncMode(byte rxModeByte) { }
    void IAudioModemPort.ProcessRx(Span<float> block48k) { }
    void IAudioModemPort.ProcessTx(Span<float> block48k) => ProcessBlock(block48k, out _);
    void IAudioModemPort.FlushRx() { }
    void IAudioModemPort.FlushTx() { }
    int IAudioModemPort.FinishTx() => 0;

    public void Dispose() => _transport.Dispose();
}

public readonly record struct PluginHostAudioSpikeSnapshot(
    long Attempts,
    long Completed,
    long Bypassed,
    bool Attached);
