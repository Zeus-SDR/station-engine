// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Buffers.Binary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Publishes the pre-MOX microphone peak from the engine's common TX ingest.
/// Native, browser, radio-jack, TCI, and playback sources all cross that seam,
/// so meter ownership does not depend on a particular product capture path.
/// </summary>
internal sealed class TxMicMeterService : IHostedService, IDisposable
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(100);

    private readonly TxAudioIngest _ingest;
    private readonly StreamingHub _hub;
    private readonly ILogger<TxMicMeterService> _log;
    private readonly object _sync = new();
    private CancellationTokenSource? _stopping;
    private Task? _publisher;
    private float _peakLinear;
    private MicBlockSource _peakSource = MicBlockSource.Host;
    private bool _started;
    private int _disposed;

    public TxMicMeterService(
        TxAudioIngest ingest,
        StreamingHub hub,
        ILogger<TxMicMeterService> log)
    {
        _ingest = ingest;
        _hub = hub;
        _log = log;
    }

    public Task StartAsync(CancellationToken _)
    {
        if (_started) return Task.CompletedTask;
        _started = true;
        _ingest.MicPcmTapped += ObserveMicPcm;
        _stopping = new CancellationTokenSource();
        _publisher = PublishLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken _)
    {
        if (!_started) return;
        _started = false;
        _ingest.MicPcmTapped -= ObserveMicPcm;
        if (_stopping is null || _publisher is null) return;

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _publisher.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ingest.MicPcmTapped -= ObserveMicPcm;
        _stopping?.Cancel();
        _stopping?.Dispose();
    }

    private void ObserveMicPcm(ReadOnlyMemory<byte> f32lePayload, MicBlockSource source)
    {
        var activeSource = _ingest.ActiveSource;
        if (source is MicBlockSource.Host or MicBlockSource.RadioMic
            && source != activeSource)
            return;

        var bytes = f32lePayload.Span;
        float peak = 0f;
        for (var offset = 0; offset + sizeof(float) <= bytes.Length; offset += sizeof(float))
        {
            var sample = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, sizeof(float)));
            if (!float.IsFinite(sample)) continue;
            var magnitude = MathF.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }

        lock (_sync)
        {
            if (_peakSource != activeSource)
            {
                _peakSource = activeSource;
                _peakLinear = 0f;
            }
            if (peak > _peakLinear) _peakLinear = peak;
        }
    }

    internal void PublishTick()
    {
        var source = _ingest.ActiveSource;
        float peak;
        lock (_sync)
        {
            peak = _peakSource == source ? _peakLinear : 0f;
            _peakSource = source;
            _peakLinear = 0f;
        }

        try
        {
            _hub.Broadcast(new MicPeakFrame(
                MicPeakFrame.LinearToDbfs(peak),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tx.mic-meter broadcast threw");
        }
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PublishInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                PublishTick();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
