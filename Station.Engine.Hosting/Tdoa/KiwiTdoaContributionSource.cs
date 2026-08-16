// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Zeus.Server.Tdoa;

public interface IKiwiTdoaCaptureTransport
{
    Task<TdoaContributionResult> CaptureAsync(
        KiwiSettings settings,
        TdoaContributionRequest request,
        CancellationToken cancellationToken);
}

public sealed class KiwiTdoaContributionSource : ITdoaContributionSource
{
    public const long MaxCenterFrequencyHz = 30_000_000;

    private readonly KiwiSettingsStore _settings;
    private readonly IKiwiTdoaCaptureTransport _transport;
    private readonly SemaphoreSlim _captureGate = new(6, 6);

    public KiwiTdoaContributionSource(KiwiSettingsStore settings, IKiwiTdoaCaptureTransport transport)
    {
        _settings = settings;
        _transport = transport;
    }

    public TdoaContributionSourceKind Kind => TdoaContributionSourceKind.KiwiSdr;

    public TdoaContributionEligibility GetEligibility()
    {
        KiwiSettings settings = _settings.Get();
        if (!settings.Enabled)
            return new(false, "kiwi", "KiwiSDR GNSS IQ", "KiwiSDR must be enabled first.");
        if (string.IsNullOrWhiteSpace(settings.Url)
            || !KiwiSdrService.TryParseEndpoint(settings.Url, out var host, out _, out _))
            return new(false, "kiwi", "KiwiSDR GNSS IQ", "A valid KiwiSDR URL must be configured first.");
        if (_captureGate.CurrentCount == 0)
            return new(false, "kiwi", $"KiwiSDR GNSS IQ ({host})", "The local Kiwi contribution channel is busy.");
        return new(true, "kiwi", $"KiwiSDR GNSS IQ ({host})", null);
    }

    public async Task<TdoaContributionResult> CaptureAsync(TdoaContributionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CenterFrequencyHz is <= 0 or > MaxCenterFrequencyHz)
            return TdoaContributionResult.Declined(
                $"KiwiSDR contribution centerFrequencyHz must be in (0, {MaxCenterFrequencyHz}].");
        if (!await _captureGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return TdoaContributionResult.Declined("The local Kiwi contribution channel is busy.");
        try
        {
            KiwiSettings settings = _settings.Get();
            if (!settings.Enabled)
                return TdoaContributionResult.Declined("KiwiSDR must be enabled first.");
            try
            {
                return await _transport.CaptureAsync(settings, request, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                return TdoaContributionResult.Declined($"KiwiSDR contribution connection failed: {ex.Message}");
            }
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async Task<TdoaContributionResult> CapturePublicAsync(string url,
        TdoaContributionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !KiwiSdrService.TryParseEndpoint(url, out _, out _, out _))
            return TdoaContributionResult.Declined("The public KiwiSDR URL is invalid.");
        if (request.CenterFrequencyHz is <= 0 or > MaxCenterFrequencyHz)
            return TdoaContributionResult.Declined(
                $"KiwiSDR centerFrequencyHz must be in (0, {MaxCenterFrequencyHz}].");
        if (!await _captureGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return TdoaContributionResult.Declined("All public KiwiSDR capture channels are busy.");
        try
        {
            return await _transport.CaptureAsync(
                new KiwiSettings(true, url.Trim(), null), request, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            return TdoaContributionResult.Declined($"Public KiwiSDR connection failed: {ex.Message}");
        }
        finally { _captureGate.Release(); }
    }
}

public sealed class KiwiTdoaCaptureTransport(ILogger<KiwiTdoaCaptureTransport> log)
    : IKiwiTdoaCaptureTransport
{
    private const double PeerCaptureSampleRateHz = 12_000;

    // The peer protocol carries a fixed sample count derived at a nominal 12 kHz,
    // so this gate only has to separate a 12 kHz firmware mode from a genuinely
    // different one (8.25 kHz, 20.25 kHz, ...). It must NOT try to police clock
    // accuracy: a Kiwi reports its GPS-corrected rate, which sits a millihertz-to-
    // hertz above nominal per unit, and the earlier 100 ppm window left only a few
    // ppm of real headroom — most healthy public receivers were declined outright.
    // Absolute timing comes from the GNSS anchor fit, never from this nominal, and
    // 0.5% of a 2 s capture is 10 ms of span difference, which the solver absorbs.
    private const double PeerCaptureSampleRateTolerancePpm = 5_000;
    private const int GpsWeekSeconds = 604_800;
    private const long GpsWeekNanoseconds = GpsWeekSeconds * 1_000_000_000L;
    private const long GpsEpochUnixSeconds = 315_964_800;
    private const int TaiMinusGpsSeconds = 19;
    private const int IqLowCutHz = -5_000;
    private const int IqHighCutHz = 5_000;
    private readonly ILogger<KiwiTdoaCaptureTransport> _log = log;

    public async Task<TdoaContributionResult> CaptureAsync(KiwiSettings settings,
        TdoaContributionRequest request, CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
            return TdoaContributionResult.Declined("KiwiSDR must be enabled first.");
        if (string.IsNullOrWhiteSpace(settings.Url)
            || !KiwiSdrService.TryParseEndpoint(settings.Url, out var host, out var port, out var secure))
            return TdoaContributionResult.Declined("A valid KiwiSDR URL is not configured.");

        _log.LogDebug("tdoa.kiwi.capture.start host={Host} samples={Samples} frequencyHz={FrequencyHz}",
            host, request.SampleCount, request.CenterFrequencyHz);

        // Every decline below is a station that silently drops out of the solve,
        // so name the receiver and the reason once, on the wire-facing side.
        TdoaContributionResult Decline(string reason)
        {
            _log.LogInformation("tdoa.kiwi.capture.declined host={Host} reason={Reason}", host, reason);
            return TdoaContributionResult.Declined(reason);
        }

        using var socket = await OpenAsync(host, port, secure, cancellationToken).ConfigureAwait(false);
        await SendAsync(socket, $"SET auth t=kiwi p={settings.Password ?? "#"}", cancellationToken).ConfigureAwait(false);
        await SendAsync(socket, "SET ident_user=ZeusSDR-TDoA", cancellationToken).ConfigureAwait(false);
        await SendAsync(socket, "SET geo=", cancellationToken).ConfigureAwait(false);
        await SendAsync(socket, "SET keepalive", cancellationToken).ConfigureAwait(false);
        long nextKeepaliveMilliseconds = Environment.TickCount64 + 1_000;

        var receiveBuffer = new byte[64 * 1024];
        var iqBytes = new byte[request.SampleCount * 8];
        int writtenSamples = 0;
        var session = new KiwiTdoaSessionState();
        bool discardedFirstIqFrame = false;
        KiwiGnssAnchorTracker? timingTracker = null;
        KiwiGnssTiming? timing = null;

        while (writtenSamples < request.SampleCount || timing is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long nowMilliseconds = Environment.TickCount64;
            if (IsKeepaliveDue(nowMilliseconds, ref nextKeepaliveMilliseconds))
                await SendAsync(socket, "SET keepalive", cancellationToken).ConfigureAwait(false);
            int count = await ReceiveMessageAsync(socket, receiveBuffer, cancellationToken).ConfigureAwait(false);
            if (count < 0) return Decline("The KiwiSDR closed the contribution channel.");
            if (count < 3) continue;
            var message = receiveBuffer.AsSpan(0, count);
            if (message[0] == (byte)'M' && message[1] == (byte)'S' && message[2] == (byte)'G')
            {
                string? rejection = await HandleMessageAsync(receiveBuffer.AsMemory(3, count - 3), socket,
                    request, session, cancellationToken).ConfigureAwait(false);
                if (rejection is not null) return Decline(rejection);
                continue;
            }
            if (message[0] != (byte)'S' || message[1] != (byte)'N' || message[2] != (byte)'D'
                || !session.Configured)
                continue;
            if (!TryDecodeIqFrame(message, out KiwiIqFrame frame, out string? error))
                return Decline(error ?? "Malformed KiwiSDR IQ frame.");
            if (!discardedFirstIqFrame)
            {
                // Kiwi channels can retain one stale buffer from their previous user.
                discardedFirstIqFrame = true;
                timingTracker = new KiwiGnssAnchorTracker(session.CaptureRateHz!.Value);
                timingTracker.PrimeSequence(frame.Sequence);
                continue;
            }
            if (!timingTracker!.Observe(frame, out error))
                return Decline(error ?? "KiwiSDR timing metadata is invalid.");
            int take = Math.Min(frame.InterleavedIq.Length / 2, request.SampleCount - writtenSamples);
            for (int i = 0; i < take; i++)
            {
                int destination = (writtenSamples + i) * 8;
                BinaryPrimitives.WriteInt32LittleEndian(iqBytes.AsSpan(destination, 4),
                    BitConverter.SingleToInt32Bits(frame.InterleavedIq[i * 2] / 32768f));
                BinaryPrimitives.WriteInt32LittleEndian(iqBytes.AsSpan(destination + 4, 4),
                    BitConverter.SingleToInt32Bits(frame.InterleavedIq[i * 2 + 1] / 32768f));
            }
            writtenSamples += take;
            if (timingTracker.TryGetCaptureTiming(out KiwiGnssTiming candidate)) timing = candidate;
        }

        if (timing is null)
            return Decline("KiwiSDR did not provide at least three compatible fresh GNSS sample-clock anchors.");
        if (!TryValidatePeerCaptureSampleRate(timing.Value.SampleRateHz, out string? rateRejection))
            return Decline($"KiwiSDR measured sample clock is unsupported. {rateRejection}");
        if (session.Latitude is null || session.Longitude is null
            || !double.IsFinite(session.Latitude.Value) || !double.IsFinite(session.Longitude.Value)
            || session.Latitude is < -90 or > 90 || session.Longitude is < -180 or > 180
            || (Math.Abs(session.Latitude.Value) < 1e-9 && Math.Abs(session.Longitude.Value) < 1e-9))
            return Decline("KiwiSDR did not report a valid non-placeholder station position.");

        long taiNanoseconds = ToTaiNanosecondsFromUnwrapped(
            timing.Value.FirstSampleGpsNanoseconds, DateTimeOffset.UtcNow);
        if (timing.Value.RejectedAnchorCount > 0)
            _log.LogDebug("tdoa.kiwi.capture.gnss-filtered host={Host} rejectedAnchors={RejectedAnchors}",
                host, timing.Value.RejectedAnchorCount);
        var capture = new TdoaContributionCapture(
            "kiwi",
            $"{host}:{port}",
            session.Latitude.Value,
            session.Longitude.Value,
            0,
            taiNanoseconds.ToString(CultureInfo.InvariantCulture),
            timing.Value.SampleRateHz,
            0,
            timing.Value.ClockUncertaintyNanoseconds,
            true,
            request.CenterFrequencyHz,
            request.SampleCount,
            Convert.ToBase64String(iqBytes));
        return TdoaContributionResult.Completed(capture);
    }

    private static async Task<ClientWebSocket> OpenAsync(string host, int port, bool secure, CancellationToken token)
    {
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", "ZeusSDR");
        try
        {
            await socket.ConnectAsync(KiwiSdrClient.BuildSocketUri(host, port, secure, stamp, "SND"), token)
                .ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException)
        {
            socket.Dispose();
            var legacy = new ClientWebSocket();
            legacy.Options.SetRequestHeader("User-Agent", "ZeusSDR");
            await legacy.ConnectAsync(KiwiSdrClient.BuildLegacySocketUri(host, port, secure, stamp, "SND"), token)
                .ConfigureAwait(false);
            return legacy;
        }
    }

    private static async Task<string?> HandleMessageAsync(ReadOnlyMemory<byte> payload,
        ClientWebSocket socket,
        TdoaContributionRequest request,
        KiwiTdoaSessionState session,
        CancellationToken token)
    {
        string text = Encoding.ASCII.GetString(payload.Span).Trim('\0', ' ');
        string? rejection = session.ApplyMessage(text);
        if (rejection is not null) return rejection;
        if (!session.AudioRateAcknowledged && session.AudioRateHz is { } audioRate)
        {
            await SendAsync(socket, BuildAudioRateAcknowledgement(audioRate), token).ConfigureAwait(false);
            session.AudioRateAcknowledged = true;
        }
        if (session.Configured || !session.ReadyToConfigure) return null;
        if (!TryValidatePeerCaptureSampleRate(session.CaptureRateHz!.Value, out rejection))
            return $"KiwiSDR reported sample rate is unsupported. {rejection}";
        if (!TryTranslateTune(request.CenterFrequencyHz, session.FrequencyOffsetKHz!.Value,
                IqLowCutHz, IqHighCutHz, out double basebandKHz, out rejection))
            return rejection;

        await SendAsync(socket, "SET squelch=0 max=0", token).ConfigureAwait(false);
        await SendAsync(socket, "SET compression=0", token).ConfigureAwait(false);
        await SendAsync(socket, "SET genattn=0", token).ConfigureAwait(false);
        await SendAsync(socket, "SET agc=1 hang=0 thresh=-100 slope=6 decay=1000 manGain=50", token)
            .ConfigureAwait(false);
        await SendAsync(socket, string.Create(CultureInfo.InvariantCulture,
            $"SET mod=iq low_cut={IqLowCutHz} high_cut={IqHighCutHz} freq={basebandKHz:F3}"), token)
            .ConfigureAwait(false);
        session.Configured = true;
        return null;
    }

    /// <summary>Echo the native rate as the output rate so the Kiwi never
    /// resamples on its side. A server-side resample would sever the sample
    /// index from the GNSS anchor timeline the whole solve depends on.</summary>
    internal static string BuildAudioRateAcknowledgement(double audioRateHz) =>
        string.Create(CultureInfo.InvariantCulture,
            $"SET AR OK in={audioRateHz:F0} out={audioRateHz:F0}");

    internal static bool TryValidatePeerCaptureSampleRate(double sampleRateHz, out string? error)
    {
        error = null;
        double errorPpm = Math.Abs(sampleRateHz / PeerCaptureSampleRateHz - 1) * 1_000_000;
        if (double.IsFinite(sampleRateHz) && sampleRateHz > 0
            && errorPpm <= PeerCaptureSampleRateTolerancePpm)
            return true;
        error = string.Create(CultureInfo.InvariantCulture,
            $"Live peer capture requires a 12000 Hz KiwiSDR mode (within {PeerCaptureSampleRateTolerancePpm:F0} ppm of nominal); other firmware rates are declined until the peer protocol carries duration instead of a fixed sample count.");
        return false;
    }

    internal static bool IsKeepaliveDue(long nowMilliseconds, ref long nextKeepaliveMilliseconds)
    {
        if (nowMilliseconds < nextKeepaliveMilliseconds) return false;
        nextKeepaliveMilliseconds = nowMilliseconds + 1_000;
        return true;
    }

    internal static bool TryTranslateTune(long rfFrequencyHz, double frequencyOffsetKHz,
        int lowCutHz, int highCutHz, out double basebandKHz, out string? error)
    {
        basebandKHz = 0;
        error = null;
        if (!double.IsFinite(frequencyOffsetKHz))
        {
            error = "KiwiSDR supplied an invalid frequency offset.";
            return false;
        }
        basebandKHz = rfFrequencyHz / 1000.0 - frequencyOffsetKHz;
        double lowerEdgeHz = basebandKHz * 1000 + lowCutHz;
        double upperEdgeHz = basebandKHz * 1000 + highCutHz;
        if (lowerEdgeHz < 0 || upperEdgeHz > KiwiTdoaContributionSource.MaxCenterFrequencyHz)
        {
            error = "The requested KiwiSDR IQ passband is outside the translated 0-30 MHz receive range.";
            return false;
        }
        return true;
    }

    internal static bool TryParsePosition(string encodedConfig, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        try
        {
            string json = Uri.UnescapeDataString(encodedConfig.Replace("+", " ", StringComparison.Ordinal));
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("rx_gps", out JsonElement gps)
                && KiwiDirectoryService.TryParseGps(gps.GetString(), out latitude, out longitude)
                && !(Math.Abs(latitude) < 1e-9 && Math.Abs(longitude) < 1e-9);
        }
        catch (JsonException) { return false; }
        catch (UriFormatException) { return false; }
    }

    internal static bool TryDecodeIqFrame(ReadOnlySpan<byte> message, out KiwiIqFrame frame, out string? error)
    {
        frame = default;
        error = null;
        if (message.Length < 20 || message[0] != (byte)'S' || message[1] != (byte)'N' || message[2] != (byte)'D')
        {
            error = "KiwiSDR IQ frame is truncated.";
            return false;
        }
        byte flags = message[3];
        if ((flags & 0x08) == 0)
        {
            error = "KiwiSDR returned mono audio instead of a GNSS-tagged IQ stream.";
            return false;
        }
        ReadOnlySpan<byte> gps = message.Slice(10, 10);
        ReadOnlySpan<byte> payload = message[20..];
        if (payload.Length == 0 || payload.Length % 4 != 0)
        {
            error = "KiwiSDR IQ payload has invalid complex-int16 geometry.";
            return false;
        }
        bool littleEndian = (flags & 0x80) != 0;
        var samples = new short[payload.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = littleEndian
                ? BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(i * 2, 2))
                : BinaryPrimitives.ReadInt16BigEndian(payload.Slice(i * 2, 2));
        frame = new KiwiIqFrame(
            BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(4, 4)),
            gps[0],
            BinaryPrimitives.ReadUInt32LittleEndian(gps.Slice(2, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(gps.Slice(6, 4)),
            samples);
        return true;
    }

    internal static long ToTaiNanoseconds(uint gpsSecondsOfWeek, uint gpsNanoseconds, DateTimeOffset nowUtc)
    {
        double approximateGpsSeconds = (nowUtc - DateTimeOffset.UnixEpoch).TotalSeconds
            - GpsEpochUnixSeconds + 18; // current leap offset selects the week only
        long week = (long)Math.Round((approximateGpsSeconds - gpsSecondsOfWeek) / GpsWeekSeconds);
        long taiSeconds = checked(GpsEpochUnixSeconds + week * GpsWeekSeconds
            + gpsSecondsOfWeek + TaiMinusGpsSeconds);
        return checked(taiSeconds * 1_000_000_000L + gpsNanoseconds);
    }

    internal static long ToTaiNanosecondsFromUnwrapped(long gpsNanoseconds, DateTimeOffset nowUtc)
    {
        double approximateGpsSeconds = (nowUtc - DateTimeOffset.UnixEpoch).TotalSeconds
            - GpsEpochUnixSeconds + 18;
        long week = checked((long)Math.Round(
            (approximateGpsSeconds - gpsNanoseconds / 1_000_000_000.0) / GpsWeekSeconds));
        long absoluteGpsNanoseconds = checked(week * GpsWeekNanoseconds + gpsNanoseconds);
        return checked((GpsEpochUnixSeconds + TaiMinusGpsSeconds) * 1_000_000_000L
            + absoluteGpsNanoseconds);
    }

    internal static bool TryAdvanceGpsTimestamp(uint gpsSecondsOfWeek, uint gpsNanoseconds,
        long? previousUnwrappedNanoseconds, out long unwrappedNanoseconds)
    {
        const long weekNanoseconds = GpsWeekSeconds * 1_000_000_000L;
        long raw = gpsSecondsOfWeek * 1_000_000_000L + gpsNanoseconds;
        unwrappedNanoseconds = raw;
        if (previousUnwrappedNanoseconds is not { } previous) return true;

        long weekBase = previous / weekNanoseconds * weekNanoseconds;
        unwrappedNanoseconds = weekBase + raw;
        if (unwrappedNanoseconds > previous) return true;

        long previousWithinWeek = previous % weekNanoseconds;
        bool plausibleWeekRollover = previousWithinWeek >= (GpsWeekSeconds - 60L) * 1_000_000_000L
            && raw <= 60L * 1_000_000_000L;
        if (!plausibleWeekRollover) return false;
        unwrappedNanoseconds += weekNanoseconds;
        return unwrappedNanoseconds > previous;
    }

    private static async Task SendAsync(ClientWebSocket socket, string command, CancellationToken token) =>
        await socket.SendAsync(Encoding.ASCII.GetBytes(command), WebSocketMessageType.Text, true, token)
            .ConfigureAwait(false);

    private static async Task<int> ReceiveMessageAsync(ClientWebSocket socket, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return -1;
            offset += result.Count;
            if (result.EndOfMessage) return offset;
        }
        return -1;
    }
}

internal readonly record struct KiwiIqFrame(
    uint Sequence,
    byte LastGpsSolution,
    uint GpsSeconds,
    uint GpsNanoseconds,
    short[] InterleavedIq);

internal sealed class KiwiTdoaSessionState
{
    public double? SampleRateHz { get; private set; }
    public double? AudioRateHz { get; private set; }
    public double? FrequencyOffsetKHz { get; private set; }
    public double? Latitude { get; private set; }
    public double? Longitude { get; private set; }
    public bool Configured { get; set; }
    public bool AudioRateAcknowledged { get; set; }

    /// <summary>
    /// The Kiwi echoes <c>sample_rate</c> (and the <c>wf_*</c> keys) even on a
    /// channel it ultimately denies, so treating it as the readiness cue tunes a
    /// channel we may never have been granted and then blocks until the deadline.
    /// <c>audio_rate</c> arrives on the SND socket only once audio is actually
    /// live. This mirrors the handshake cue KiwiSdrClient already relies on.
    /// </summary>
    public bool ReadyToConfigure => AudioRateHz.HasValue && FrequencyOffsetKHz.HasValue;

    /// <summary>Rate the SND stream actually delivers, preferred over the
    /// ADC/waterfall <c>sample_rate</c>. Seeds the GNSS anchor fit.</summary>
    public double? CaptureRateHz => AudioRateHz ?? SampleRateHz;

    public string? ApplyMessage(string text)
    {
        foreach (string item in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = item.IndexOf('=');
            if (equals <= 0) continue;
            string key = item[..equals].TrimStart('\0');
            string value = item[(equals + 1)..];
            if (key == "too_busy") return $"KiwiSDR is full ({value}); contribution declined.";
            if (key == "badp" && value != "0")
                return $"KiwiSDR refused the free channel (badp={value}); it may be full or password-protected.";
            if (key == "down") return "KiwiSDR reports that it is unavailable.";
            if (key == "sample_rate"
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate)
                && double.IsFinite(rate) && rate > 0)
                SampleRateHz = rate;
            else if (key == "audio_rate"
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double audioRate)
                && double.IsFinite(audioRate) && audioRate > 0)
                AudioRateHz = audioRate;
            else if (key == "freq_offset"
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double offset)
                && double.IsFinite(offset))
                FrequencyOffsetKHz = offset;
            else if (key == "load_cfg"
                && KiwiTdoaCaptureTransport.TryParsePosition(value, out double latitude, out double longitude))
            {
                Latitude = latitude;
                Longitude = longitude;
            }
        }
        return null;
    }
}

internal readonly record struct KiwiGnssTiming(
    long FirstSampleGpsNanoseconds,
    double SampleRateHz,
    double ClockUncertaintyNanoseconds,
    int AcceptedAnchorCount,
    int RejectedAnchorCount);

/// <summary>
/// Reconstructs the stream epoch from Kiwi GPS sample-clock anchors. The Kiwi
/// repeats GPS metadata between solutions; only a gpslast transition into zero
/// denotes a fresh anchor.
/// </summary>
internal sealed class KiwiGnssAnchorTracker(double reportedSampleRateHz)
{
    private const long GpsWeekNanoseconds = 604_800_000_000_000L;
    private const double MaximumRateErrorFraction = 0.02;
    private const int MaximumAnchors = 64;

    private readonly double _reportedSampleRateHz = reportedSampleRateHz;
    private readonly List<Anchor> _anchors = [];
    private uint? _previousSequence;
    private byte? _previousGpsLast;
    private long _nextSampleIndex;

    internal int FreshAnchorCount => _anchors.Count;

    public void PrimeSequence(uint sequence) => _previousSequence = sequence;

    public bool Observe(KiwiIqFrame frame, out string? error)
    {
        error = null;
        int complexSamples = frame.InterleavedIq.Length / 2;
        if (complexSamples <= 0 || frame.InterleavedIq.Length % 2 != 0)
        {
            error = "KiwiSDR IQ payload has invalid complex-int16 geometry.";
            return false;
        }
        if (_previousSequence is { } previous && frame.Sequence != unchecked(previous + 1))
        {
            error = "KiwiSDR IQ sequence gap; capture was not contiguous.";
            return false;
        }
        _previousSequence = frame.Sequence;

        bool freshAnchor = frame.LastGpsSolution == 0
            && _previousGpsLast is { } previousGpsLast
            && previousGpsLast != 0;
        _previousGpsLast = frame.LastGpsSolution;
        if (freshAnchor)
        {
            if (frame.GpsSeconds >= 604_800 || frame.GpsNanoseconds >= 1_000_000_000)
            {
                error = "KiwiSDR supplied an invalid fresh GNSS sample-clock anchor.";
                return false;
            }
            if (_anchors.Count < MaximumAnchors)
            {
                long raw = checked(frame.GpsSeconds * 1_000_000_000L + frame.GpsNanoseconds);
                long unwrapped = raw;
                if (_anchors.Count > 0)
                {
                    Anchor first = _anchors[0];
                    double expected = first.TimeNanoseconds
                        + (_nextSampleIndex - first.SampleIndex) * 1_000_000_000.0 / _reportedSampleRateHz;
                    long weekOffset = checked((long)Math.Round((expected - raw) / GpsWeekNanoseconds));
                    unwrapped = checked(raw + weekOffset * GpsWeekNanoseconds);
                }
                _anchors.Add(new Anchor(_nextSampleIndex, unwrapped));
            }
        }
        _nextSampleIndex = checked(_nextSampleIndex + complexSamples);
        return true;
    }

    public bool TryGetTiming(out KiwiGnssTiming timing)
    {
        timing = default;
        if (_anchors.Count < 2 || !double.IsFinite(_reportedSampleRateHz) || _reportedSampleRateHz <= 0)
            return false;

        List<int>? bestInliers = null;
        double bestRateError = double.PositiveInfinity;
        double bestResidualSum = double.PositiveInfinity;
        for (int i = 0; i < _anchors.Count - 1; i++)
        {
            for (int j = i + 1; j < _anchors.Count; j++)
            {
                Anchor first = _anchors[i];
                Anchor second = _anchors[j];
                long sampleDelta = second.SampleIndex - first.SampleIndex;
                long timeDelta = second.TimeNanoseconds - first.TimeNanoseconds;
                if (sampleDelta <= 0 || timeDelta <= 0) continue;
                double rate = sampleDelta * 1_000_000_000.0 / timeDelta;
                double rateError = Math.Abs(rate / _reportedSampleRateHz - 1);
                if (!double.IsFinite(rate) || rateError > MaximumRateErrorFraction) continue;

                double nanosecondsPerSample = 1_000_000_000.0 / rate;
                double residualGate = Math.Max(2 * nanosecondsPerSample, 2_500);
                var inliers = new List<int>(_anchors.Count);
                double residualSum = 0;
                for (int k = 0; k < _anchors.Count; k++)
                {
                    Anchor candidate = _anchors[k];
                    double predicted = first.TimeNanoseconds
                        + (candidate.SampleIndex - first.SampleIndex) * nanosecondsPerSample;
                    double residual = candidate.TimeNanoseconds - predicted;
                    if (Math.Abs(residual) > residualGate) continue;
                    inliers.Add(k);
                    residualSum += residual * residual;
                }
                if (inliers.Count < 2) continue;
                if (bestInliers is null
                    || inliers.Count > bestInliers.Count
                    || (inliers.Count == bestInliers.Count && rateError < bestRateError)
                    || (inliers.Count == bestInliers.Count && Math.Abs(rateError - bestRateError) < 1e-12
                        && residualSum < bestResidualSum))
                {
                    bestInliers = inliers;
                    bestRateError = rateError;
                    bestResidualSum = residualSum;
                }
            }
        }
        if (bestInliers is null) return false;

        Anchor reference = _anchors[bestInliers[0]];
        double meanX = 0, meanY = 0;
        foreach (int index in bestInliers)
        {
            meanX += _anchors[index].SampleIndex;
            meanY += _anchors[index].TimeNanoseconds - reference.TimeNanoseconds;
        }
        meanX /= bestInliers.Count;
        meanY /= bestInliers.Count;
        double covariance = 0, variance = 0;
        foreach (int index in bestInliers)
        {
            double x = _anchors[index].SampleIndex - meanX;
            double y = (_anchors[index].TimeNanoseconds - reference.TimeNanoseconds) - meanY;
            covariance += x * y;
            variance += x * x;
        }
        if (variance <= 0) return false;
        double slope = covariance / variance;
        double measuredRate = 1_000_000_000.0 / slope;
        if (!double.IsFinite(measuredRate) || measuredRate <= 0
            || Math.Abs(measuredRate / _reportedSampleRateHz - 1) > MaximumRateErrorFraction)
            return false;

        double interceptRelative = meanY - slope * meanX;
        double residualSquared = 0;
        foreach (int index in bestInliers)
        {
            Anchor anchor = _anchors[index];
            double predictedRelative = interceptRelative + slope * anchor.SampleIndex;
            double residual = (anchor.TimeNanoseconds - reference.TimeNanoseconds) - predictedRelative;
            residualSquared += residual * residual;
        }
        double residualRms = Math.Sqrt(residualSquared / bestInliers.Count);
        long firstSampleGpsNanoseconds = checked(reference.TimeNanoseconds
            + (long)Math.Round(interceptRelative));
        double clockUncertainty = Math.Max(slope / 2, residualRms + slope / 2);
        timing = new KiwiGnssTiming(
            firstSampleGpsNanoseconds,
            measuredRate,
            clockUncertainty,
            bestInliers.Count,
            _anchors.Count - bestInliers.Count);
        return true;
    }

    /// <summary>
    /// Live capture waits for at least three mutually compatible anchors. If
    /// one of the first three is bad, a fourth observation is needed to form an
    /// unambiguous three-anchor consensus.
    /// </summary>
    public bool TryGetCaptureTiming(out KiwiGnssTiming timing)
    {
        timing = default;
        return _anchors.Count >= 3
            && TryGetTiming(out timing)
            && timing.AcceptedAnchorCount >= 3;
    }

    private readonly record struct Anchor(long SampleIndex, long TimeNanoseconds);
}
