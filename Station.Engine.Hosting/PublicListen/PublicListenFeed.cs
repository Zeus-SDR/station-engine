// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;

namespace Zeus.Server.PublicListen;

/// <summary>
/// Engine side of Public Listening (ADR-0010). Receives RX1 display rows and
/// RX1 audio through <see cref="IListenerFeedTap"/> and emits ONLY serialized
/// 0x41 PublicSpectrum, 0x42 PublicStationStatus and 0x43 PublicAudioPcm
/// frames to attached <see cref="IPublicListenFeedSink"/>s.
///
/// Nothing is produced for a stream nobody asked for (<see cref="SetDemand"/>),
/// and while <see cref="PublicTransmitHold"/> is held audio is replaced by
/// silence and spectrum frames carry no data (TransmitHold flag). The tap
/// methods run on producer threads: they gate, quantize and fan out the shared
/// immutable byte[] without blocking. Sinks are never operator clients.
/// </summary>
public sealed class PublicListenFeed : IPublicListenFeed, IListenerFeedTap, IDisposable
{
    public const int MinSpectrumFps = 1;
    public const int MaxSpectrumFps = 15;
    public const int DefaultSpectrumFps = 10;
    public const int DefaultSpectrumBins = 1024;

    /// <summary>Bin count of the wideband overview (stream 0xF0), preserving every analyzer output cell.</summary>
    public const int WidebandOverviewBins = WidebandSpectrumAnalyzer.DisplayWidth;

    // Status is emitted on change, at most 2 Hz, with a slow keepalive so a
    // cached copy on the host never goes stale by more than this.
    internal static readonly TimeSpan StatusMinInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan StatusKeepalive = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StatusPollPeriod = TimeSpan.FromMilliseconds(250);

    private readonly PublicTransmitHold _hold;
    private readonly Func<PublicStationStatusDto?> _statusSource;
    private readonly Func<bool> _widebandCapable;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ITimer? _statusTimer;

    private readonly object _sinkGate = new();
    private IPublicListenFeedSink[] _sinks = [];

    private int _demand;
    private int _bandSurvey;
    private int _spectrumFps = DefaultSpectrumFps;
    private int _spectrumBins = DefaultSpectrumBins;

    private readonly object _spectrumGate = new();
    private long _lastSpectrumTimestamp;
    private bool _spectrumEverEmitted;

    private readonly object _guestSpectrumGate = new();
    private readonly long[] _guestSpectrumTimestamps = new long[PublicStreamId.MaxGuestSlots];
    private readonly bool[] _guestSpectrumEverEmitted = new bool[PublicStreamId.MaxGuestSlots];

    private readonly object _statusGate = new();
    private string? _lastStatusKey;
    private long _lastStatusTimestamp;
    private bool _statusEverEmitted;

    private uint _spectrumSeq;
    private uint _statusSeq;
    private uint _audioSeq;
    private uint _widebandSeq;
    private long _sinkFailures;

    public PublicListenFeed(
        PublicTransmitHold hold,
        Func<PublicStationStatusDto?> statusSource,
        TimeProvider? time = null,
        ILogger<PublicListenFeed>? log = null,
        bool startStatusTimer = true,
        Func<bool>? widebandCapable = null)
    {
        _hold = hold ?? throw new ArgumentNullException(nameof(hold));
        _statusSource = statusSource ?? throw new ArgumentNullException(nameof(statusSource));
        _widebandCapable = widebandCapable ?? (() => false);
        _time = time ?? TimeProvider.System;
        _log = (ILogger?)log ?? NullLogger.Instance;
        if (startStatusTimer)
            _statusTimer = _time.CreateTimer(_ => PumpStatus(), null, StatusPollPeriod, StatusPollPeriod);
    }

    public ListenerFeedDemandFlags Demand => (ListenerFeedDemandFlags)Volatile.Read(ref _demand);

    public bool WantsOperatorView => HasDemand(ListenerFeedDemandFlags.OperatorView);

    public bool WantsListenAlong => HasDemand(ListenerFeedDemandFlags.ListenAlong);

    public bool WantsWideband => HasDemand(ListenerFeedDemandFlags.Wideband);

    public bool IsTransmitHeld => _hold.IsHeld;

    public bool BandSurveyEnabled => Volatile.Read(ref _bandSurvey) != 0;

    public void SetBandSurvey(bool enabled) => Volatile.Write(ref _bandSurvey, enabled ? 1 : 0);

    public bool WidebandCapable
    {
        get
        {
            try { return _widebandCapable(); }
            catch { return false; }
        }
    }

    internal int SpectrumFps => Volatile.Read(ref _spectrumFps);

    internal int SpectrumBins => Volatile.Read(ref _spectrumBins);

    internal long SinkFailures => Interlocked.Read(ref _sinkFailures);

    public void SetDemand(ListenerFeedDemandFlags demand)
    {
        int next = (int)(demand & (ListenerFeedDemandFlags.OperatorView
            | ListenerFeedDemandFlags.ListenAlong
            | ListenerFeedDemandFlags.Wideband));
        int prev = Interlocked.Exchange(ref _demand, next);
        if (prev == 0 && next != 0)
        {
            // First listener demand: let the next pump emit status immediately.
            lock (_statusGate) _lastStatusKey = null;
        }
    }

    public void Configure(int spectrumFps, int spectrumBins)
    {
        Volatile.Write(ref _spectrumFps, Math.Clamp(spectrumFps, MinSpectrumFps, MaxSpectrumFps));
        Volatile.Write(ref _spectrumBins, spectrumBins switch
        {
            <= 512 => 512,
            <= 1024 => 1024,
            _ => 2048,
        });
    }

    public IDisposable AttachSink(IPublicListenFeedSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_sinkGate)
            _sinks = [.. _sinks, sink];
        return new SinkRegistration(this, sink);
    }

    private void DetachSink(IPublicListenFeedSink sink)
    {
        lock (_sinkGate)
            _sinks = _sinks.Where(existing => !ReferenceEquals(existing, sink)).ToArray();
    }

    // ---- IListenerFeedTap ------------------------------------------------

    public void OfferDisplayFrame(in DisplayFrame frame)
    {
        if (frame.RxId != 0 || !WantsOperatorView) return;
        var row = SelectRow(frame);
        if (row.IsEmpty) return;
        if (!TryBeginSpectrum()) return;
        EmitSpectrum(row.Span, frame.TsUnixMs, frame.CenterHz, frame.HzPerPixel);
    }

    public void OfferDisplayFrameBytes(ReadOnlySpan<byte> payload)
    {
        if (!WantsOperatorView) return;
        if (payload.Length <= WireFormat.HeaderSize + DisplayFrame.BodyHeaderSize
            || payload[0] != (byte)MsgType.DisplayFrame
            || payload[WireFormat.HeaderSize] != 0)
            return;
        var bodyFlags = (DisplayBodyFlags)payload[WireFormat.HeaderSize + 1];
        if ((bodyFlags & DisplayBodyFlags.WfValid) == 0) return;
        if (!TryBeginSpectrum()) return;

        DisplayFrame frame;
        try { frame = DisplayFrame.Deserialize(payload); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            _log.LogDebug(ex, "public-listen: ignored malformed product display frame");
            return;
        }
        var row = SelectRow(frame);
        if (row.IsEmpty) return;
        EmitSpectrum(row.Span, frame.TsUnixMs, frame.CenterHz, frame.HzPerPixel);
    }

    /// <summary>
    /// Largest per-channel sample count one 0x43 frame can carry: its u16
    /// payload length must hold the body header plus mono f32 samples. Larger
    /// blocks are split into consecutive frames.
    /// </summary>
    internal const int MaxSamplesPerFrame = (ushort.MaxValue - AudioFrame.BodyHeaderSize) / sizeof(float);

    public void OfferRx1Audio(
        ReadOnlySpan<float> interleaved,
        int channels,
        int sampleRateHz,
        double appliedAfGainDb,
        bool silence,
        double captureUnixMs = double.NaN)
    {
        if (!WantsListenAlong) return;
        if (channels < 1) channels = 1;
        int count = interleaved.Length / channels;
        if (count <= 0 || sampleRateHz <= 0) return;

        // Read the hold first, even when the caller already forces silence, so
        // an ongoing transmission keeps stamping LastKeyedUnixMs. Then the
        // capture-time guard: a block captured while keyed (or within the tail)
        // stays silent even if it arrives after the station un-keyed.
        bool held = _hold.IsHeld;
        bool mute = silence || held || _hold.WasCapturedWhileHeld(captureUnixMs);

        // The operator's AF gain was applied upstream (WDSP panel gain / P3
        // sidecar). Divide it out so listener level ignores the volume knob.
        float gain = double.IsFinite(appliedAfGainDb)
            ? (float)Math.Pow(10.0, -appliedAfGainDb / 20.0)
            : 1f;
        for (int start = 0; start < count; start += MaxSamplesPerFrame)
        {
            int n = Math.Min(MaxSamplesPerFrame, count - start);
            var mono = new float[n];
            if (!mute)
            {
                for (int i = 0; i < n; i++)
                {
                    float sum = 0f;
                    int offset = (start + i) * channels;
                    for (int c = 0; c < channels; c++) sum += interleaved[offset + c];
                    float v = sum / channels * gain;
                    mono[i] = float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f;
                }
            }
            EmitAudio(mono, sampleRateHz);
        }
    }

    public void NoteTransmitting() => _hold.NoteKeyed();

    // ---- guest receivers (Phase 3) -----------------------------------------

    public bool TryBeginGuestSpectrum(int slot)
    {
        if (slot is < 0 or >= PublicStreamId.MaxGuestSlots || Volatile.Read(ref _sinks).Length == 0) return false;
        long now = _time.GetTimestamp();
        var period = TimeSpan.FromSeconds(1.0 / Math.Clamp(Volatile.Read(ref _spectrumFps), 1, 7));
        lock (_guestSpectrumGate)
        {
            if (_guestSpectrumEverEmitted[slot] && _time.GetElapsedTime(_guestSpectrumTimestamps[slot], now) < period)
                return false;
            _guestSpectrumEverEmitted[slot] = true;
            _guestSpectrumTimestamps[slot] = now;
            return true;
        }
    }

    public void OfferGuestSpectrum(int slot, ReadOnlySpan<float> rowDb, int width, double tsUnixMs, long centerHz, float hzPerPixel)
    {
        if (slot is < 0 or >= PublicStreamId.MaxGuestSlots || Volatile.Read(ref _sinks).Length == 0) return;
        byte streamId = (byte)(PublicStreamId.GuestBase + slot);
        uint seq = Interlocked.Increment(ref _spectrumSeq);
        PublicSpectrumFrame frame;
        if (_hold.IsHeld)
        {
            int bins = PublicSpectrumEncoder.ResolveBins(width, PublicSpectrumFrame.MaxBins);
            float hzPerBin = bins > 0 ? (float)((double)hzPerPixel * width / bins) : hzPerPixel;
            frame = PublicSpectrumEncoder.TransmitHold(seq, tsUnixMs, streamId, centerHz, hzPerBin);
        }
        else if (!PublicSpectrumEncoder.TryEncodeRow(
                     rowDb, PublicSpectrumFrame.MaxBins, seq, tsUnixMs,
                     streamId, centerHz, hzPerPixel, out frame))
        {
            return;
        }
        Emit(frame.ToArray());
    }

    public void OfferGuestAudio(int slot, ReadOnlySpan<float> mono, int sampleRateHz, double captureUnixMs = double.NaN)
    {
        if (slot is < 0 or >= PublicStreamId.MaxGuestSlots) return;
        EmitReceiverAudio((byte)(PublicStreamId.GuestBase + slot), mono, sampleRateHz, captureUnixMs);
    }

    // ---- virtual receivers (Phase 4) -----------------------------------------

    public void OfferVrxAudio(int slot, ReadOnlySpan<float> mono, int sampleRateHz, double captureUnixMs = double.NaN)
    {
        if (slot is < 0 or >= PublicStreamId.MaxVrxSlots) return;
        EmitReceiverAudio((byte)(PublicStreamId.VrxBase + slot), mono, sampleRateHz, captureUnixMs);
    }

    /// <summary>A listener receiver's own mono audio (guest or virtual), under the transmit hold.</summary>
    private void EmitReceiverAudio(byte streamId, ReadOnlySpan<float> mono, int sampleRateHz, double captureUnixMs)
    {
        if (Volatile.Read(ref _sinks).Length == 0) return;
        int count = mono.Length;
        if (count <= 0 || sampleRateHz <= 0) return;
        // Same order as listen-along: poll the hold first (keeps the keyed stamp
        // fresh), then the capture-time guard for audio captured while keyed.
        bool mute = _hold.IsHeld || _hold.WasCapturedWhileHeld(captureUnixMs);
        for (int start = 0; start < count; start += MaxSamplesPerFrame)
        {
            int n = Math.Min(MaxSamplesPerFrame, count - start);
            var samples = new float[n];
            if (!mute)
            {
                for (int i = 0; i < n; i++)
                {
                    float v = mono[start + i];
                    samples[i] = float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f;
                }
            }
            EmitAudio(samples, sampleRateHz, streamId);
        }
    }

    public void OfferWidebandOverview(ReadOnlySpan<float> rowDb, double tsUnixMs, long centerHz, float hzPerPixel)
    {
        if (!WantsWideband) return;
        uint seq = Interlocked.Increment(ref _widebandSeq);
        PublicSpectrumFrame frame;
        if (_hold.IsHeld)
        {
            int bins = PublicSpectrumEncoder.ResolveBins(rowDb.Length, WidebandOverviewBins);
            float hzPerBin = bins > 0 ? (float)((double)hzPerPixel * rowDb.Length / bins) : hzPerPixel;
            frame = PublicSpectrumEncoder.TransmitHold(seq, tsUnixMs, PublicStreamId.WidebandOverview, centerHz, hzPerBin);
        }
        else if (!PublicSpectrumEncoder.TryEncodeRow(
                     rowDb, WidebandOverviewBins, seq, tsUnixMs,
                     PublicStreamId.WidebandOverview, centerHz, hzPerPixel, out frame))
        {
            return;
        }
        Emit(frame.ToArray());
    }

    private void EmitAudio(float[] mono, int sampleRateHz, byte streamId = PublicStreamId.OperatorView)
    {
        var audio = new AudioFrame(
            Seq: Interlocked.Increment(ref _audioSeq),
            TsUnixMs: UnixNowMs(),
            RxId: streamId,
            Channels: 1,
            SampleRateHz: (uint)sampleRateHz,
            SampleCount: (ushort)mono.Length,
            Samples: mono);
        var payload = new byte[audio.TotalByteLength];
        audio.Serialize(new FixedWriter(payload));
        // Same body as AudioPcm; the distinct type byte keeps an operator
        // browser from ever playing it and the listener fan-out from ever
        // accepting an operator 0x02 frame.
        payload[0] = (byte)MsgType.PublicAudioPcm;
        Emit(payload);
    }

    // ---- status (0x42) ---------------------------------------------------

    /// <summary>
    /// Poll station state and emit 0x42 when it changed (≤ 2 Hz) or the
    /// keepalive elapsed. Driven by a 4 Hz timer; tests call it directly.
    /// </summary>
    internal void PumpStatus()
    {
        try
        {
            if (Volatile.Read(ref _demand) == 0 || Volatile.Read(ref _sinks).Length == 0) return;
            var source = _statusSource();
            if (source is null) return;
            bool held = _hold.IsHeld;
            var status = source with
            {
                V = PublicStationStatusCodec.CurrentVersion,
                Tx = held,
                // A reading taken while keyed shows the operator's own
                // transmission: none leaves the station until the hold releases.
                Op = source.Op is { } op
                    ? op with { Dbm = !held && op.Dbm is { } dbm && double.IsFinite(dbm) ? Math.Round(dbm) : null }
                    : null,
                // "The overview can be served now"; the host also masks it
                // with its ShareWideband switch before forwarding.
                Wideband = WidebandCapable,
                Listeners = 0,
                MaxListeners = 0,
                VrxFree = null,
            };
            string key = StatusKey(status);
            long now = _time.GetTimestamp();
            byte[] frame;
            lock (_statusGate)
            {
                bool changed = !string.Equals(key, _lastStatusKey, StringComparison.Ordinal);
                if (_statusEverEmitted)
                {
                    var since = _time.GetElapsedTime(_lastStatusTimestamp, now);
                    if (since < StatusMinInterval) return;
                    if (!changed && since < StatusKeepalive) return;
                }
                _lastStatusKey = key;
                _lastStatusTimestamp = now;
                _statusEverEmitted = true;
                frame = PublicStationStatusCodec.Encode(status, ++_statusSeq, UnixNowMs());
            }
            Emit(frame);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "public-listen: status pump failed");
        }
    }

    private static string StatusKey(PublicStationStatusDto s) =>
        $"{s.Tx}|{s.Protocol}|{s.Op?.Hz}|{s.Op?.Mode}|{s.Op?.FilterLo}|{s.Op?.FilterHi}|{s.Op?.Dbm}|{s.Wideband}";

    // ---- internals -------------------------------------------------------

    private bool HasDemand(ListenerFeedDemandFlags flag) =>
        (Volatile.Read(ref _demand) & (int)flag) != 0 && Volatile.Read(ref _sinks).Length > 0;

    private static ReadOnlyMemory<float> SelectRow(in DisplayFrame frame)
    {
        // Only genuine waterfall rows may enter this history. Panadapter-only
        // ticks must not substitute a trace or consume the listener row cadence.
        if ((frame.BodyFlags & DisplayBodyFlags.WfValid) != 0 && frame.WfDb.Length == frame.Width)
            return frame.WfDb;
        return ReadOnlyMemory<float>.Empty;
    }

    /// <summary>Per-stream rate gate at the configured listener fps.</summary>
    private bool TryBeginSpectrum()
    {
        long now = _time.GetTimestamp();
        var period = TimeSpan.FromSeconds(1.0 / Math.Max(1, Volatile.Read(ref _spectrumFps)));
        lock (_spectrumGate)
        {
            if (_spectrumEverEmitted && _time.GetElapsedTime(_lastSpectrumTimestamp, now) < period)
                return false;
            _spectrumEverEmitted = true;
            _lastSpectrumTimestamp = now;
            return true;
        }
    }

    private void EmitSpectrum(ReadOnlySpan<float> row, double tsUnixMs, long centerHz, float hzPerPixel)
    {
        uint seq = Interlocked.Increment(ref _spectrumSeq);
        PublicSpectrumFrame frame;
        if (_hold.IsHeld)
        {
            int bins = PublicSpectrumEncoder.ResolveBins(row.Length, Volatile.Read(ref _spectrumBins));
            float hzPerBin = bins > 0 ? (float)((double)hzPerPixel * row.Length / bins) : hzPerPixel;
            frame = PublicSpectrumEncoder.TransmitHold(seq, tsUnixMs, PublicStreamId.OperatorView, centerHz, hzPerBin);
        }
        else if (!PublicSpectrumEncoder.TryEncodeRow(
                     row, Volatile.Read(ref _spectrumBins), seq, tsUnixMs,
                     PublicStreamId.OperatorView, centerHz, hzPerPixel, out frame))
        {
            return;
        }
        Emit(frame.ToArray());
    }

    private void Emit(byte[] frame)
    {
        var sinks = Volatile.Read(ref _sinks);
        var memory = new ReadOnlyMemory<byte>(frame);
        foreach (var sink in sinks)
        {
            try { sink.OnFeedFrame(memory); }
            catch (Exception ex)
            {
                // A faulty sink must never disturb the producer thread (DSP tick).
                if (Interlocked.Increment(ref _sinkFailures) == 1)
                    _log.LogWarning(ex, "public-listen: feed sink threw");
            }
        }
    }

    private double UnixNowMs() => (_time.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalMilliseconds;

    public void Dispose()
    {
        _statusTimer?.Dispose();
        lock (_sinkGate) _sinks = [];
    }

    private sealed class SinkRegistration(PublicListenFeed owner, IPublicListenFeedSink sink) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.DetachSink(sink);
        }
    }

    private sealed class FixedWriter(byte[] buffer) : IBufferWriter<byte>
    {
        private int _written;

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => buffer.AsMemory(_written);

        public Span<byte> GetSpan(int sizeHint = 0) => buffer.AsSpan(_written);
    }
}
