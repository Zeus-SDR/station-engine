// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>The external consumer receiver's single modem, before listening gain or receive DSP.</summary>
internal sealed class ExternalReceiverModem(IAudioModemPort modem) : IDisposable
{
    private readonly object _gate = new();
    private readonly float[] _decoded = new float[ExternalReceiverPort.MaxSamplesPerBlock];
    private readonly float[] _pending = new float[ExternalReceiverPort.MaxSamplesPerBlock];
    private ExternalReceiverContext? _context;
    private int _count;
    private bool _disposed;

    internal void Configure(ExternalReceiverContext context)
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool wasFreeDv = _context?.Mode == RxMode.FreeDv;
            bool freeDv = context.Mode == RxMode.FreeDv;
            if (_context is not { } prior || prior.Mode != context.Mode || prior.VfoHz != context.VfoHz
                || prior.Transmitting != context.Transmitting || prior.ProcessedAudio != context.ProcessedAudio)
            {
                _count = 0;
                if (wasFreeDv) modem.FlushRx();
            }
            if (wasFreeDv && _context is { Transmitting: true, ModemTransmit: true } && !context.Transmitting)
                modem.FlushTx();
            if (freeDv) modem.SyncMode((byte)RxMode.FreeDv);
            else modem.SyncMode((byte)context.Mode);
            _context = context;
        }
    }

    // True consumes the block even while unavailable: raw modem noise must never reach playback.
    internal bool Feed(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (_disposed || _context is not { Mode: RxMode.FreeDv } context) return false;
            if (context.Transmitting) return true;
            var decoded = _decoded.AsSpan(0, samples.Length);
            samples.CopyTo(decoded);
            if (modem.Active) modem.ProcessRx(decoded);
            else decoded.Clear();
            _count = context.ProcessedAudio
                ? ExternalReceiverPort.Source.HoldReturn(_pending, _count, decoded) : 0;
            return true;
        }
    }

    internal (bool Active, NrMode Mode, int? LatencyMs, int Samples)? Read(Span<float> output)
    {
        lock (_gate)
        {
            if (_disposed || _context?.Mode != RxMode.FreeDv) return null;
            int count = Math.Min(output.Length, _count);
            _pending.AsSpan(0, count).CopyTo(output);
            _pending.AsSpan(count, _count - count).CopyTo(_pending);
            _count -= count;
            return (true, NrMode.Off, 100, count);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _count = 0;
            if (_context?.Mode == RxMode.FreeDv)
            {
                modem.FlushRx();
                modem.FlushTx();
                modem.SyncMode((byte)RxMode.USB);
            }
            _context = null;
        }
    }
}
