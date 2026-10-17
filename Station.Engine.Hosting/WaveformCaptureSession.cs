// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;

namespace Zeus.Server;

public sealed record WaveformCaptureMetadata(string Id, string Status, int ReceivedFrames,
    int TargetFrames, int SampleRateHz, int Channels, string Kind, string Source,
    string SignalStage, string AmplitudeReference, int ReceiverIndex, long Generation,
    int DurationMs, string Mode, string? Error);

/// <summary>One bounded, forward-only capture. Storage is allocated by the HTTP reader.</summary>
internal sealed class WaveformCaptureSession
{
    public const long RetentionMs = 60000;
    public readonly string Id;
    public readonly object Context;
    public readonly int Receiver, Duration, Channels;
    public readonly bool Tx;
    public readonly string Mode;
    private readonly bool _microphone, _monitor;
    public readonly long Started;
    public long Generation;
    public long LastBlock, Finished;
    public int Rate, Received, Target;
    public string Status = "capturing";
    public string? Error;
    private float[]? _samples;
    private byte[]? _serialized;
    private readonly object _serializationSync = new();

    public WaveformCaptureSession(object context, int receiver, bool tx, int duration, string mode,
        long generation, long now, bool microphone = false, bool monitor = false)
    {
        _microphone = microphone; _monitor = monitor;
        Id = (monitor ? "p" : microphone ? "m" : tx ? "t" : "r" + receiver) + Guid.NewGuid().ToString("N");
        Context = context; Receiver = receiver; Tx = tx; Duration = duration;
        Channels = tx ? 2 : 1; Mode = mode; Generation = generation; Started = now;
        _samples = new float[checked(768000 * (duration / 1000) * Channels)];
    }

    public bool Expired(long now) => Status != "capturing" && now - Finished >= RetentionMs;
    public void Stop(string status, string? error, long now)
    {
        if (Status != "capturing") return;
        Error = error; Finished = now;
        Volatile.Write(ref Status, status);
        if (status != "complete") _samples = null;
    }
    public void Check(object? context, long now, bool missed = false)
    {
        if (Status != "capturing") return;
        if (!Equals(context, Context)) Stop("interrupted", "Signal source changed", now);
        else if (missed) Stop("interrupted", "A producer block was missed", now);
        else if (now < Started || now - Started > Duration + 2000
            || (Received > 0 && now - LastBlock > 400))
            Stop("interrupted", "Signal samples stopped", now);
    }
    public void Append(object? context, int rate, int channels, ReadOnlySpan<float> samples,
        long now, bool missed)
    {
        Check(context, now, missed);
        if (Status != "capturing") return;
        if (rate is < 8000 or > 768000 || channels != Channels || samples.Length == 0
            || samples.Length % channels != 0 || (Rate != 0 && Rate != rate))
        { Stop("interrupted", "Sample format changed", now); return; }
        double blockMs = samples.Length * 1000.0 / (rate * channels);
        if (Received > 0 && (now < LastBlock || now - LastBlock > Math.Max(60, blockMs * 2 + 20)))
        { Stop("interrupted", "A gap interrupted the signal", now); return; }
        for (int i = 0; i < samples.Length; i++)
            if (!float.IsFinite(samples[i])) { Stop("interrupted", "Invalid signal samples", now); return; }
        if (Rate == 0) { Rate = rate; Target = checked(rate * (Duration / 1000)); }
        int frames = Math.Min(samples.Length / channels, Target - Received);
        samples[..(frames * channels)].CopyTo(_samples!.AsSpan(Received * channels));
        Received += frames; LastBlock = now;
        if (Received == Target) Stop("complete", null, now);
    }
    public WaveformCaptureMetadata Metadata() => new(Id, Status, Received, Target, Rate,
        Channels, Tx ? "complex" : "real", _monitor ? "tx-monitor" : _microphone ? "mic-input" : Tx ? "digital-tx" : "rx-demodulated",
        _monitor ? "tx-post-demod" : _microphone ? "mic-pre-dsp" : Tx ? "tx-output" : "rx-post-demod", "relative", Receiver, Generation, Duration, Mode, Error);
    public byte[]? Bytes()
    {
        if (Status != "complete") return null;
        // Shared HTTP payload is immutable. Concurrent downloads allocate one encoding only.
        // This lock is never taken by the producer; completed samples cannot change.
        lock (_serializationSync)
        {
            if (_serialized is not null) return _serialized;
            var samples = _samples;
            if (samples is null) return null;
            var result = new byte[checked(Received * Channels * sizeof(float))];
            for (int i = 0; i < Received * Channels; i++)
                BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(i * sizeof(float), sizeof(float)), samples[i]);
            _serialized = result;
            _samples = null;
            return result;
        }
    }
}
