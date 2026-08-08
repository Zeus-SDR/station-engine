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

using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace Zeus.Server;

/// <summary>
/// A single KiwiSDR streaming connection. KiwiSDR speaks a dual-WebSocket
/// protocol: one socket for demodulated audio (<c>/SND</c>) and one for the
/// wide waterfall FFT (<c>/W/F</c>), both carrying a plain-text <c>SET ...</c>
/// command channel. The Kiwi performs demodulation server-side, so Zeus drives
/// it like a remote front-end: tuning / mode / passband are pushed as SET
/// commands and the decoded audio + waterfall stream back.
///
/// <para>This client speaks the cleartext, uncompressed dialect:
/// <c>SET compression=0</c> so the SND payload is raw signed-16 PCM (no IMA
/// ADPCM running decoder needed) and <c>SET wf_comp=0</c> so waterfall rows are
/// one unsigned byte per FFT bin. Reference implementation:
/// <c>jks-prv/kiwiclient</c> (kiwi/client.py).</para>
///
/// <para>All decode callbacks fire on the receive-loop tasks — keep them cheap
/// and non-blocking (the consumer copies into its own buffers).</para>
/// </summary>
public sealed class KiwiSdrClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _secure;
    private readonly string? _password;
    private readonly string _identUser;
    private readonly ILogger _log;
    // ClientWebSocket supports one send and one receive concurrently, but not
    // overlapping sends. Commands originate from handshake, tune coalescing,
    // zoom and keepalive paths, so serialize them across both Kiwi sockets.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _snd;
    private ClientWebSocket? _wf;
    private CancellationTokenSource? _cts;
    private Task? _sndLoop;
    private Task? _wfLoop;
    private Task? _keepalive;

    // Live tuning, guarded by _sync. The receive loops re-issue the current
    // tune once the channel is authenticated (sample_rate known), and Tune()
    // pushes deltas live.
    private readonly object _sync = new();
    private long _freqHz = 14_200_000;        // demod (dial) frequency → SET freq
    private long _centerHz = 14_200_000;      // waterfall centre → SET cf (frozen under CTUN)
    private string _mode = "usb";
    private int _lowCutHz = 100;
    private int _highCutHz = 2_850;
    // Kiwi-native waterfall zoom (z0..server zoom_cap). Each integer step
    // halves the span; this is deliberately not Zeus's linear 1..32 DDC zoom.
    private int _waterfallZoom = 7;

    // Captured from the server handshake.
    private volatile int _audioRateHz; // native SND rate, ~12000
    private double _wfFullSpanHz = 30_000_000; // negotiated bandwidth, span at z0
    private int _wfFftSize = 1024;
    private int _wfZoomMax = 14;
    private int _wfZoomCap = 14;
    private bool _wfStarted;
    private bool _handshakeReady;
    private int _lastWfZoom = -1;       // last SET zoom step pushed to the W/F socket
    private double _lastWfCfKHz = -1;   // last SET cf (kHz) pushed; de-dupes rebases
    private const int PushThrottleMs = 70; // min gap between remote re-tunes
    private int _pushBusy;                  // 0/1 — a coalescing push loop is running
    private int _pushDirty;                 // 0/1 — newer tune fields await sending
    private long _lastPushMs;              // TickCount64 of the last re-tune sent
    private bool _loggedFirstSnd;
    private bool _loggedFirstWf;
    private int _msgLogCount;
    // Waterfall drop diagnostics. A "connected, audio fine, panadapter blank"
    // session means every W/F row is being rejected in DecodeWaterfallRow, and
    // rows arrive up to ~23/s — so count every drop but emit at most one
    // warning per reason per WfDropLogIntervalMs (first drop logs at once).
    private const long WfDropLogIntervalMs = 10_000;
    private long _wfDropGeometryCount;
    private long _wfDropGeometryLastLogMs = -1;
    private long _wfDropAdpcmCount;
    private long _wfDropAdpcmLastLogMs = -1;
    // Per-socket operation timeouts so an unresponsive Kiwi (firewall drop,
    // proxy hang, server restart) fails fast instead of freezing the UI on
    // "connecting" indefinitely. Linked to the caller's token so shutdown still
    // aborts promptly.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Decoded audio: mono float samples in -1..1 (first arg) at the
    /// native Kiwi rate in Hz (second arg). Fires once per SND frame.</summary>
    public Action<float[], int>? AudioReceived;

    /// <summary>One waterfall row: per-bin power in dBm, plus the row's center
    /// frequency and Hz-per-bin so the consumer can build a DisplayFrame.</summary>
    public Action<float[], long, double, int>? WaterfallReceived;

    /// <summary>Fires when the remote reports its native zoom cap.</summary>
    public Action<int>? ZoomCapChanged;

    /// <summary>The server-negotiated maximum native waterfall zoom.</summary>
    public int ZoomCap => Volatile.Read(ref _wfZoomCap);

    /// <summary>The server-negotiated full waterfall bandwidth in Hz.</summary>
    public double WaterfallBandwidthHz
    {
        get { lock (_sync) return _wfFullSpanHz; }
    }

    /// <summary>RX signal level in dBm, from the SND frame S-meter.</summary>
    public Action<double>? SignalLevel;

    /// <summary>Connection state word ("connecting" / "connected" / "error" /
    /// "closed") plus an optional human-readable detail.</summary>
    public Action<string, string?>? StatusChanged;

    public KiwiSdrClient(string host, int port, bool secure, string? password, string identUser, ILogger log)
    {
        _host = host;
        _port = port;
        _secure = secure;
        _password = string.IsNullOrEmpty(password) ? null : password;
        _identUser = string.IsNullOrWhiteSpace(identUser) ? "ZeusSDR" : identUser;
        _log = log;
    }

    /// <summary>Open both sockets and start the receive + keepalive loops. The
    /// returned task completes once the loops are running; streaming continues
    /// until <see cref="StopAsync"/> / disposal.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        StatusChanged?.Invoke("connecting", $"{_host}:{_port}");

        // The supported browser route uses a millisecond connection token and
        // shares it between the paired SND/W/F sockets. Legacy external-API
        // firmware treats the token as an arbitrary cache-buster too.
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _snd = await OpenSocketAsync(stamp, "SND", token).ConfigureAwait(false);
        _wf = await OpenSocketAsync(stamp, "W/F", token).ConfigureAwait(false);

        _sndLoop = Task.Run(() => SndLoopAsync(token), token);
        _wfLoop = Task.Run(() => WfLoopAsync(token), token);
        _keepalive = Task.Run(() => KeepaliveLoopAsync(token), token);
    }

    private async Task<ClientWebSocket> OpenSocketAsync(long stamp, string which, CancellationToken ct)
    {
        var uri = BuildSocketUri(_host, _port, _secure, stamp, which);
        ClientWebSocket ws;
        try
        {
            ws = await ConnectSocketAsync(uri, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex) when (!ct.IsCancellationRequested)
        {
            // Pre-/ws/kiwi firmware only knows the external-API route. A
            // current Kiwi returns the browser route immediately; an older
            // one rejects it during the WebSocket handshake, so fall back
            // without changing the route used by other platforms.
            var legacyUri = BuildLegacySocketUri(_host, _port, _secure, stamp, which);
            _log.LogDebug(
                "kiwi.ws modern route rejected stream={Stream} err={Err}; retrying legacy route={Route}",
                which, ex.Message, legacyUri.AbsolutePath);
            ws = await ConnectSocketAsync(legacyUri, ct).ConfigureAwait(false);
        }

        try
        {
            // Auth first ("#" password = public receiver), then etiquette identity.
            await SendAsync(ws, $"SET auth t=kiwi p={_password ?? "#"}", ct).ConfigureAwait(false);
            await SendAsync(ws, $"SET ident_user={_identUser}", ct).ConfigureAwait(false);
            await SendAsync(ws, "SET geo=", ct).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    internal static Uri BuildSocketUri(string host, int port, bool secure, long stamp, string which)
    {
        var scheme = secure ? "wss" : "ws";
        return new Uri($"{scheme}://{host}:{port}/ws/kiwi/{stamp}/{which}");
    }

    internal static Uri BuildLegacySocketUri(string host, int port, bool secure, long stamp, string which)
    {
        var scheme = secure ? "wss" : "ws";
        return new Uri($"{scheme}://{host}:{port}/{stamp / 1000}/{which}");
    }

    private async Task<ClientWebSocket> ConnectSocketAsync(Uri uri, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        try
        {
            ws.Options.SetRequestHeader("User-Agent", "ZeusSDR");
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private async Task SendAsync(ClientWebSocket ws, string command, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = Encoding.ASCII.GetBytes(command);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    // -------------------------------------------------------------------------
    // SND (audio) receive loop.
    // -------------------------------------------------------------------------
    private async Task SndLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        string? error = null;
        try
        {
            while (!ct.IsCancellationRequested && _snd is { State: WebSocketState.Open })
            {
                var (count, isText, endReason) = await ReceiveMessageAsync(_snd, buf, ct).ConfigureAwait(false);
                if (count < 0) { error = endReason; break; }
                // KiwiSDR delivers MSG/SND/W/F as BINARY frames led by a 3-byte
                // ASCII tag (never WebSocket text frames). The "MSG" frames carry
                // the audio_rate/sample_rate handshake that flips us to connected.
                if (isText) { await HandleTextAsync(buf.AsMemory(0, count), ct).ConfigureAwait(false); continue; }
                if (count < 3) continue;
                if (IsTag(buf, 'M', 'S', 'G')) await HandleTextAsync(buf.AsMemory(3, count - 3), ct).ConfigureAwait(false);
                else if (IsTag(buf, 'S', 'N', 'D')) DecodeSndFrame(buf.AsSpan(0, count));
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        // Issue #1114 (SND twin): surface every unsolicited exit so KiwiSdrService
        // can reconnect — and name the real cause. A 15 s receive starvation used
        // to log nothing and surface as "audio socket closed", pointing debugging
        // at a socket problem that didn't exist.
        if (!ct.IsCancellationRequested)
        {
            var reason = error ?? "audio socket closed";
            _log.LogWarning("kiwi.snd.loop ended host={Host} reason={Reason}", _host, reason);
            StatusChanged?.Invoke("dropped", reason);
        }
    }

    // SND binary frame layout (compression=0):
    //   [0..3)  ASCII tag "SND"
    //   [3]     flags (bit 0x80 = little-endian PCM, 0x08 = IQ/stereo)
    //   [4..8)  seq (uint32 LE)
    //   [8..10) S-meter (uint16 BE)
    //   [10..]  signed 16-bit PCM payload
    private void DecodeSndFrame(ReadOnlySpan<byte> msg)
    {
        if (msg.Length < 10) return;
        byte flags = msg[3];
        ushort smeter = BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(8, 2));
        // KiwiSDR S-meter encodes (dBm + 127) * 10.
        double dbm = smeter * 0.1 - 127.0;
        SignalLevel?.Invoke(dbm);

        var pcm = msg.Slice(10);
        int n = pcm.Length / 2;
        if (n <= 0) return;
        bool little = (flags & 0x80) != 0;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = little
                ? BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2))
                : BinaryPrimitives.ReadInt16BigEndian(pcm.Slice(i * 2, 2));
            samples[i] = s / 32768f;
        }
        int rate = _audioRateHz > 0 ? _audioRateHz : 12_000;
        if (!_loggedFirstSnd) { _loggedFirstSnd = true; _log.LogDebug("kiwi.snd.first samples={N} rate={Rate}", n, rate); }
        MarkStreamStarted();
        AudioReceived?.Invoke(samples, rate);
    }

    // -------------------------------------------------------------------------
    // W/F (waterfall) receive loop.
    // -------------------------------------------------------------------------
    private async Task WfLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        string? error = null;
        try
        {
            while (!ct.IsCancellationRequested && _wf is { State: WebSocketState.Open })
            {
                var (count, isText, endReason) = await ReceiveMessageAsync(_wf, buf, ct).ConfigureAwait(false);
                if (count < 0) { error = endReason; break; }
                if (isText) { await HandleTextAsync(buf.AsMemory(0, count), ct).ConfigureAwait(false); continue; }
                if (count < 3) continue;
                if (IsTag(buf, 'M', 'S', 'G')) await HandleTextAsync(buf.AsMemory(3, count - 3), ct).ConfigureAwait(false);
                else if (IsTag(buf, 'W', (char)'/', 'F')) DecodeWfFrame(buf.AsSpan(0, count));
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        // Issue #1114: the KiwiSDR's wide-FFT (/W/F) socket closes mid-session
        // (server bug / proxy timeout / channel limit) while the audio (/SND)
        // socket stays up. Without this signal the pan/waterfall went blank and
        // Zeus never reconnected — the operator had to toggle the slice manually.
        // Name the real cause: a 15 s receive starvation used to log nothing and
        // surface as "waterfall socket closed", which reads as a socket problem
        // when the socket is fine and the remote simply stopped sending.
        if (!ct.IsCancellationRequested)
        {
            var reason = error ?? "waterfall socket closed";
            _log.LogWarning("kiwi.wf.loop ended host={Host} reason={Reason}", _host, reason);
            StatusChanged?.Invoke("dropped", reason);
        }
    }

    // W/F binary frame layout (wf_comp=0):
    //   [0..4)   ASCII tag "W/F "
    //   [4..16)  three uint32 LE: x-bin start, packed flags/zoom, seq
    //   [16..]   one unsigned byte per FFT bin; dBm = byte - 255
    private void DecodeWfFrame(ReadOnlySpan<byte> msg)
    {
        double bandwidth;
        int fftSize, zoomMax;
        lock (_sync)
        {
            bandwidth = _wfFullSpanHz;
            fftSize = _wfFftSize;
            zoomMax = _wfZoomMax;
        }
        var row = DecodeWaterfallRow(msg, bandwidth, fftSize, zoomMax, out var drop);
        if (row is null)
        {
            LogWaterfallDrop(drop, msg.Length, fftSize, bandwidth);
            return;
        }
        // Information, not Debug: shipped log sinks are capped at Information,
        // and this single line is the positive proof the W/F decode path is
        // alive (its absence, paired with kiwi.wf.drop warnings, is what
        // diagnoses a blank-panadapter-but-audio session).
        if (!_loggedFirstWf) { _loggedFirstWf = true; _log.LogInformation("kiwi.wf.first bins={N} zoom={Zoom} hzPerBin={Hpb:F1} centerHz={C}", row.Value.BinsDb.Length, row.Value.Zoom, row.Value.HzPerBin, row.Value.CenterHz); }
        MarkStreamStarted();
        WaterfallReceived?.Invoke(row.Value.BinsDb, row.Value.CenterHz, row.Value.HzPerBin, row.Value.Zoom);
    }

    // DecodeWaterfallRow rejected a row. Without this line a silent drop is
    // indistinguishable from "server sent nothing" — the failure mode behind a
    // blank panadapter with healthy audio. Throttled per reason; the count
    // keeps the drop cadence visible without flooding the log at ~23 rows/s.
    private void LogWaterfallDrop(WaterfallRowDrop drop, int msgLength, int fftSize, double bandwidthHz)
    {
        long now = Environment.TickCount64;
        if (drop == WaterfallRowDrop.Compressed)
        {
            long count = Interlocked.Increment(ref _wfDropAdpcmCount);
            long last = Interlocked.Read(ref _wfDropAdpcmLastLogMs);
            if (last >= 0 && now - last < WfDropLogIntervalMs) return;
            Interlocked.Exchange(ref _wfDropAdpcmLastLogMs, now);
            // The server/proxy ignored SET wf_comp=0 and is streaming
            // ADPCM-compressed rows this client does not decode.
            _log.LogWarning(
                "kiwi.wf.drop reason=adpcm host={Host} rows={Rows} (compressed waterfall rows despite SET wf_comp=0)",
                _host, count);
            return;
        }
        long sizeCount = Interlocked.Increment(ref _wfDropGeometryCount);
        long sizeLast = Interlocked.Read(ref _wfDropGeometryLastLogMs);
        if (sizeLast >= 0 && now - sizeLast < WfDropLogIntervalMs) return;
        Interlocked.Exchange(ref _wfDropGeometryLastLogMs, now);
        // Row length != 16-byte header + negotiated fftSize (or the wf_fft_size
        // handshake never landed). The logged values identify which.
        _log.LogWarning(
            "kiwi.wf.drop reason=geometry host={Host} rows={Rows} msgLen={MsgLen} fftSize={FftSize} bandwidthHz={Bandwidth}",
            _host, sizeCount, msgLength, fftSize, bandwidthHz);
    }

    internal readonly record struct WaterfallRow(float[] BinsDb, long CenterHz, double HzPerBin, int Zoom);

    /// <summary>Why <see cref="DecodeWaterfallRow"/> rejected a W/F row.</summary>
    internal enum WaterfallRowDrop
    {
        /// <summary>Row decoded cleanly.</summary>
        None,
        /// <summary>The row is not exactly 16-byte header + fftSize payload (or
        /// the negotiated geometry is non-positive — e.g. the wf_fft_size
        /// handshake has not landed or the sender disagrees with it).</summary>
        Geometry,
        /// <summary>The flags word carries the ADPCM bit: the sender ignored
        /// <c>SET wf_comp=0</c> and is streaming compressed rows.</summary>
        Compressed,
    }

    internal static WaterfallRow? DecodeWaterfallRow(
        ReadOnlySpan<byte> msg,
        double bandwidthHz,
        int fftSize,
        int zoomMax)
        => DecodeWaterfallRow(msg, bandwidthHz, fftSize, zoomMax, out _);

    internal static WaterfallRow? DecodeWaterfallRow(
        ReadOnlySpan<byte> msg,
        double bandwidthHz,
        int fftSize,
        int zoomMax,
        out WaterfallRowDrop drop)
    {
        const int header = 16;
        if (msg.Length != header + fftSize || !(bandwidthHz > 0) || fftSize <= 0)
        {
            drop = WaterfallRowDrop.Geometry;
            return null;
        }

        uint xBin = BinaryPrimitives.ReadUInt32LittleEndian(msg.Slice(4, 4));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(msg.Slice(8, 4));
        if ((flags & 0x0001_0000) != 0) // ADPCM-compressed row
        {
            drop = WaterfallRowDrop.Compressed;
            return null;
        }
        int actualZoom = Math.Clamp((int)(flags & 0xffff), 0, Math.Max(0, zoomMax));
        var bins = msg.Slice(header);
        var db = new float[bins.Length];
        for (int i = 0; i < bins.Length; i++) db[i] = bins[i] - 255f;

        double spanHz = bandwidthHz / Math.Pow(2, actualZoom);
        double hzPerStart = bandwidthHz / (fftSize * Math.Pow(2, Math.Max(0, zoomMax)));
        long centerHz = (long)Math.Round(xBin * hzPerStart + spanHz / 2.0);
        drop = WaterfallRowDrop.None;
        return new WaterfallRow(db, centerHz, spanHz / bins.Length, actualZoom);
    }

    // -------------------------------------------------------------------------
    // Text (MSG) handling + handshake completion.
    // -------------------------------------------------------------------------
    private async Task HandleTextAsync(ReadOnlyMemory<byte> msg, CancellationToken ct)
    {
        var text = Encoding.ASCII.GetString(msg.Span);
        // Log the wire during bring-up: the exact handshake-key spellings differ
        // across KiwiSDR firmware/proxy versions, so log the first messages
        // verbatim rather than guessing.
        if (_msgLogCount < 40)
        {
            _msgLogCount++;
            _log.LogDebug("kiwi.msg #{N} {Text}", _msgLogCount,
                text.Length > 300 ? text[..300] : text);
        }
        // Frames look like "MSG audio_rate=12000.000 sample_rate=12000.000 ...".
        foreach (var tok in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = tok.IndexOf('=');
            if (eq <= 0) continue;
            var key = tok.AsSpan(0, eq);
            var val = tok.AsSpan(eq + 1);

            if (key.SequenceEqual("audio_rate"))
            {
                if (TryParseDouble(val, out var r) && r > 0)
                {
                    _audioRateHz = (int)Math.Round(r);
                    // Acknowledge the rate so the server starts the audio stream.
                    // Echo the native rate as the output rate too: the Kiwi always
                    // delivers SND at its native audio_rate (~12 kHz) and we
                    // resample to 48 kHz ourselves, so we never want the server to
                    // resample on its side regardless of how it reads `out`.
                    if (_snd is not null)
                        await SendAsync(_snd, $"SET AR OK in={_audioRateHz} out={_audioRateHz}", ct).ConfigureAwait(false);
                    // audio_rate arrives on the SND socket itself, so this is the
                    // reliable cue that the audio path is live — drive the rest of
                    // the audio handshake (squelch/compression/agc/tune) from here,
                    // not from sample_rate (which is also echoed on the W/F socket
                    // and would race ahead with no audio rate known yet).
                    await OnHandshakeReadyAsync(ct).ConfigureAwait(false);
                }
            }
            else if (key.SequenceEqual("bandwidth"))
            {
                if (TryParseDouble(val, out var bandwidth) && bandwidth > 0)
                    lock (_sync) _wfFullSpanHz = bandwidth;
            }
            else if (key.SequenceEqual("wf_fft_size"))
            {
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fftSize) && fftSize > 0)
                    lock (_sync) _wfFftSize = fftSize;
            }
            else if (key.SequenceEqual("zoom_max"))
            {
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoomMax))
                    lock (_sync) _wfZoomMax = Math.Clamp(zoomMax, 0, 30);
            }
            else if (key.SequenceEqual("zoom_cap"))
            {
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoomCap))
                {
                    zoomCap = Math.Clamp(zoomCap, 0, 30);
                    Volatile.Write(ref _wfZoomCap, zoomCap);
                    lock (_sync) _waterfallZoom = Math.Min(_waterfallZoom, zoomCap);
                    ZoomCapChanged?.Invoke(zoomCap);
                }
            }
            else if (key.SequenceEqual("sample_rate") || key.SequenceEqual("wf_setup") || key.SequenceEqual("wf_fps"))
            {
                // sample_rate / wf_* arrive even when the RX channel is ultimately
                // denied, so they only start the waterfall — NOT the "connected"
                // signal. We mark connected on audio_rate (the audio-granted cue).
                await EnsureWaterfallStartedAsync(ct).ConfigureAwait(false);
            }
            else if (key.SequenceEqual("badp"))
            {
                // Auth/availability verdict. 0 = granted; anything else means the
                // receiver refused the channel (full, password required, or
                // registration-only). Surface it instead of hanging on "connecting".
                if (int.TryParse(val, out var bp) && bp != 0)
                {
                    _log.LogWarning("kiwi.rejected host={Host} badp={Badp}", _host, bp);
                    StatusChanged?.Invoke("error",
                        $"receiver refused the connection (badp={bp}) — it is likely full or requires a password");
                }
            }
        }
    }

    // Once the SND channel reports its sample rate it is ready for tuning +
    // stream config. Idempotent — the server may repeat handshake MSGs.
    private async Task OnHandshakeReadyAsync(CancellationToken ct)
    {
        if (_snd is null || _handshakeReady) return;
        _handshakeReady = true;
        await SendAsync(_snd, "SET squelch=0 max=0", ct).ConfigureAwait(false);
        await SendAsync(_snd, "SET compression=0", ct).ConfigureAwait(false);
        await SendAsync(_snd, "SET agc=1 hang=0 thresh=-100 slope=6 decay=1000 manGain=50", ct).ConfigureAwait(false);
        await PushTuneAsync(ct).ConfigureAwait(false);
        await EnsureWaterfallStartedAsync(ct).ConfigureAwait(false);
        _log.LogInformation("kiwi.handshake.ready host={Host} audioRate={Rate}", _host, _audioRateHz);
        // NOT "connected": a bare handshake proves only that the server answered
        // MSGs. Dead/zombie endpoints (e.g. a wedged receiver behind a
        // kiwisdr.com proxy) complete the handshake and then stream nothing —
        // reporting "connected" here showed CONNECTED on a permanently blank
        // pan/waterfall. The status flips in MarkStreamStarted() when the first
        // real SND/W/F frame decodes.
        StatusChanged?.Invoke("handshake", null);
    }

    // First decoded stream frame (audio OR waterfall) — the only honest proof
    // the remote is actually streaming. Idempotent.
    private bool _streamStarted;
    private void MarkStreamStarted()
    {
        if (_streamStarted) return;
        _streamStarted = true;
        _log.LogInformation("kiwi.stream.started host={Host}", _host);
        StatusChanged?.Invoke("connected", null);
    }

    private async Task EnsureWaterfallStartedAsync(CancellationToken ct)
    {
        if (_wf is null || _wfStarted) return;
        _wfStarted = true;
        await SendAsync(_wf, "SET wf_comp=0", ct).ConfigureAwait(false);
        // wf_speed selects the KiwiSDR waterfall cadence: 1=1 fps (slowest),
        // 4=fast (~max ~23 fps). The panadapter trace is driven off this same
        // stream, so 1 fps made both crawl while the hardware RX runs ~20 fps.
        // Use fast so the Kiwi updates smoothly like every other receiver.
        await SendAsync(_wf, "SET wf_speed=4", ct).ConfigureAwait(false);
        await SendAsync(_wf, "SET maxdb=0 mindb=-120", ct).ConfigureAwait(false);
        await PushWaterfallZoomAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Tuning.
    // -------------------------------------------------------------------------
    /// <summary>Retune the Kiwi. <paramref name="freqHz"/> is the demod (dial)
    /// frequency; <paramref name="centerHz"/> is the waterfall centre — they
    /// differ under CTUN (centre frozen, dial roams). <paramref name="mode"/> is a
    /// Kiwi mode word (usb/lsb/am/cw/cwn/nbfm/iq/sam). Cuts are passband edges in
    /// Hz relative to the carrier. <paramref name="waterfallZoom"/> is Kiwi's
    /// native integer z-level (z0..the negotiated zoom cap).</summary>
    public void Tune(long freqHz, long centerHz, string mode, int lowCutHz, int highCutHz, int waterfallZoom)
    {
        lock (_sync)
        {
            _freqHz = freqHz;
            _centerHz = centerHz > 0 ? centerHz : freqHz;
            _mode = string.IsNullOrWhiteSpace(mode) ? _mode : mode;
            _lowCutHz = lowCutHz;
            _highCutHz = highCutHz;
            _waterfallZoom = Math.Clamp(waterfallZoom, 0, _wfZoomCap);
        }
        SchedulePush();
    }

    // Throttle the remote re-tune. A filter / VFO drag calls Tune() up to ~60×/s;
    // re-tuning the remote KiwiSDR that fast spams it and the audio can't keep up
    // (the "filter isn't smooth" symptom). Coalesce into at most one push per
    // PushThrottleMs window, always carrying the LATEST fields (PushTuneAsync
    // reads them under _sync at send time), with a trailing push so the final
    // drag position always lands.
    private void SchedulePush()
    {
        // Mark work pending. If a push loop is already running it will pick up the
        // latest fields on its next iteration; only start a new loop otherwise.
        Interlocked.Exchange(ref _pushDirty, 1);
        if (Interlocked.CompareExchange(ref _pushBusy, 1, 0) != 0) return;
        _ = RunPushLoopAsync();
    }

    private async Task RunPushLoopAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            // Drain pending tunes, never faster than one per PushThrottleMs. Each
            // pass sends the LATEST fields (PushTuneAsync reads under _sync), so a
            // drag collapses to ~14 re-tunes/s and the final position always lands.
            while (Interlocked.Exchange(ref _pushDirty, 0) == 1)
            {
                long since = Environment.TickCount64 - Interlocked.Read(ref _lastPushMs);
                int wait = (int)Math.Max(0, PushThrottleMs - since);
                if (wait > 0) await Task.Delay(wait, ct).ConfigureAwait(false);
                await PushTuneAsync(ct).ConfigureAwait(false);
                await PushWaterfallZoomAsync(ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastPushMs, Environment.TickCount64);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug("kiwi.tune.push err={Err}", ex.Message); }
        finally
        {
            Interlocked.Exchange(ref _pushBusy, 0);
            // A tune that arrived between the loop's last dirty-check and here would
            // otherwise be lost — re-arm if so and we can re-acquire the loop slot.
            if (Volatile.Read(ref _pushDirty) == 1 && Interlocked.CompareExchange(ref _pushBusy, 1, 0) == 0)
                _ = RunPushLoopAsync();
        }
    }

    private async Task PushTuneAsync(CancellationToken ct)
    {
        if (_snd is null) return;
        long freq;
        string mode;
        int lo, hi;
        lock (_sync) { freq = _freqHz; mode = _mode; lo = _lowCutHz; hi = _highCutHz; }
        // freq is the dial frequency in kHz with 3 decimals.
        double freqKHz = freq / 1000.0;
        var cmd = string.Create(CultureInfo.InvariantCulture,
            $"SET mod={mode} low_cut={lo} high_cut={hi} freq={freqKHz:F3}");
        _log.LogDebug("kiwi.tune {Cmd}", cmd);
        await SendAsync(_snd, cmd, ct).ConfigureAwait(false);
    }

    private async Task PushWaterfallZoomAsync(CancellationToken ct)
    {
        if (_wf is null || !_wfStarted) return;
        long center;
        int zoom;
        lock (_sync)
        {
            center = _centerHz;
            zoom = CurrentZoom();
        }
        double cfKHz = center / 1000.0;
        // De-dupe: the server REBASES its whole waterfall history on every
        // `SET zoom/cf`, so re-sending an unchanged value (e.g. on a filter-only
        // tune, which leaves zoom + centre alone) makes the waterfall flicker /
        // stutter. Only push when the zoom step or centre actually moved.
        if (zoom == _lastWfZoom && Math.Abs(cfKHz - _lastWfCfKHz) < 0.0005) return;
        _lastWfZoom = zoom;
        _lastWfCfKHz = cfKHz;
        var cmd = string.Create(CultureInfo.InvariantCulture, $"SET zoom={zoom} cf={cfKHz:F3}");
        _log.LogDebug("kiwi.wf {Cmd}", cmd);
        await SendAsync(_wf, cmd, ct).ConfigureAwait(false);
    }

    // Caller holds _sync.
    private int CurrentZoom() => Math.Clamp(_waterfallZoom, 0, _wfZoomCap);

    // -------------------------------------------------------------------------
    // Keepalive + receive plumbing + teardown.
    // -------------------------------------------------------------------------
    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                if (_snd is { State: WebSocketState.Open }) await SendAsync(_snd, "SET keepalive", ct).ConfigureAwait(false);
                if (_wf is { State: WebSocketState.Open }) await SendAsync(_wf, "SET keepalive", ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug("kiwi.keepalive ended err={Err}", ex.Message); }
    }

    // Reads one full WebSocket message into buf. Returns (byteCount, isText,
    // endReason); byteCount < 0 signals the stream ended, with endReason naming
    // why — receive-timeout starvation, a remote close, or a transport error —
    // so the loops surface the real cause instead of a generic "socket closed".
    private static async Task<(int Count, bool IsText, string? EndReason)> ReceiveMessageAsync(
        ClientWebSocket ws, byte[] buf, CancellationToken ct)
    {
        int offset = 0;
        while (true)
        {
            var seg = new ArraySegment<byte>(buf, offset, buf.Length - offset);
            WebSocketReceiveResult res;
            try
            {
                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                receiveCts.CancelAfter(ReceiveTimeout);
                res = await ws.ReceiveAsync(seg, receiveCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The socket never closed — nothing arrived for the whole
                // ReceiveTimeout window (wedged server, dead proxy, black-holed
                // route). Distinct from a close: the remote is starving us.
                return (-1, false,
                    $"stream starved — no data for {(int)ReceiveTimeout.TotalSeconds} s");
            }
            catch (WebSocketException ex)
            {
                return (-1, false, $"transport error: {ex.Message}");
            }
            if (res.MessageType == WebSocketMessageType.Close)
            {
                return (-1, false, string.IsNullOrEmpty(res.CloseStatusDescription)
                    ? "closed by remote"
                    : $"closed by remote — {res.CloseStatusDescription}");
            }
            offset += res.Count;
            if (res.EndOfMessage)
                return (offset, res.MessageType == WebSocketMessageType.Text, null);
            if (offset >= buf.Length) return (offset, res.MessageType == WebSocketMessageType.Text, null); // drop overflow
        }
    }

    private static bool TryParseDouble(ReadOnlySpan<char> s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool IsTag(byte[] b, char a, char c, char d) =>
        b[0] == (byte)a && b[1] == (byte)c && b[2] == (byte)d;

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        await CloseSocketAsync(_snd).ConfigureAwait(false);
        await CloseSocketAsync(_wf).ConfigureAwait(false);
        StatusChanged?.Invoke("closed", null);
    }

    private static async Task CloseSocketAsync(ClientWebSocket? ws)
    {
        if (ws is null) return;
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch { /* best effort */ }
        finally { ws.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
