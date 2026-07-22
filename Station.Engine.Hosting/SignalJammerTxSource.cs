// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Buffers.Binary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

internal sealed class SignalJammerTxSource : BackgroundService
{
    public const int SampleRateHz = TxMicBlockResampler.OutputSampleRate;
    public const int BlockSamples = TxMicBlockResampler.OutputBlockSamples;
    public const int MaxTextSamples = SampleRateHz * 80;

    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan AutoTransmitReleaseGuard = TimeSpan.FromMilliseconds(40);
    private const double Tau = Math.PI * 2.0;

    private readonly object _sync = new();
    private readonly Action<ReadOnlyMemory<byte>> _emit;
    private readonly Func<bool> _isMoxOn;
    private readonly Func<string?>? _requestTextMox;
    private readonly Action? _releaseTextMox;
    private readonly Func<bool> _ownsTextMox;
    private readonly ILogger<SignalJammerTxSource> _log;
    private readonly SignalJammerGenerator _generator = new();
    private readonly float[] _block = new float[BlockSamples];
    private readonly byte[] _payload = new byte[BlockSamples * sizeof(float)];

    private SignalJammerTxSettings _settings = SignalJammerTxSettings.Disabled;
    private float[]? _textSamples;
    private int _textIndex;
    private bool _textAutoReleaseMox;
    private long _lastAppliedVersion = -1;

    public SignalJammerTxSource(
        TxAudioIngest txIngest,
        TxService tx,
        ILogger<SignalJammerTxSource> log)
        : this(
            payload => txIngest.OnMicPcmBytesFromWav(payload),
            () => tx.IsMoxOn,
            () => tx.TrySetMox(true, MoxSource.QrmText, out var err) ? null : err ?? "MOX refused",
            () =>
            {
                if (tx.MoxOwner == MoxSource.QrmText)
                    tx.TrySetMox(false, MoxSource.QrmText, out _);
            },
            () => tx.MoxOwner == MoxSource.QrmText,
            log)
    {
    }

    internal SignalJammerTxSource(
        Action<ReadOnlyMemory<byte>> emit,
        Func<bool> isMoxOn,
        ILogger<SignalJammerTxSource> log)
        : this(emit, isMoxOn, null, null, null, log)
    {
    }

    internal SignalJammerTxSource(
        Action<ReadOnlyMemory<byte>> emit,
        Func<bool> isMoxOn,
        Func<string?>? requestTextMox,
        Action? releaseTextMox,
        Func<bool>? ownsTextMox,
        ILogger<SignalJammerTxSource> log)
    {
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _isMoxOn = isMoxOn ?? throw new ArgumentNullException(nameof(isMoxOn));
        _requestTextMox = requestTextMox;
        _releaseTextMox = releaseTextMox;
        _ownsTextMox = ownsTextMox ?? (() => false);
        _log = log;
    }

    public SignalJammerTxSnapshot Configure(SignalJammerSetRequest req)
    {
        if (req is null) throw new ArgumentNullException(nameof(req));

        lock (_sync)
        {
            _settings = SignalJammerTxSettings.Normalize(req, _settings.Version + 1);
            return SnapshotLocked();
        }
    }

    public SignalJammerTxSnapshot EnqueueText(
        ReadOnlySpan<float> samples,
        int sampleRate,
        bool autoTransmit = false)
    {
        if (!TxMicBlockResampler.IsSupportedInputSampleRate(sampleRate))
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate,
                $"sampleRate must be {TxMicBlockResampler.MinInputSampleRate}..{TxMicBlockResampler.MaxInputSampleRate} Hz");
        if (samples.Length == 0)
            throw new ArgumentException("samples required", nameof(samples));
        if (samples.Length > MaxTextSamples)
            throw new ArgumentException($"samples may not exceed {MaxTextSamples}", nameof(samples));

        var output = new List<float>(EstimateOutputSamples(samples.Length, sampleRate));
        var resampler = new TxMicBlockResampler(block =>
        {
            for (int i = 0; i < block.Length; i++)
                output.Add(block[i]);
        });
        resampler.Accept(samples, sampleRate);
        resampler.FlushZeroPadded();

        bool autoReleaseMox = false;
        if (autoTransmit)
        {
            if (!_isMoxOn())
            {
                string? error = _requestTextMox is null
                    ? "auto TX unavailable"
                    : _requestTextMox();
                if (error is not null)
                    throw new InvalidOperationException(error);
            }
            autoReleaseMox = _ownsTextMox();
        }

        lock (_sync)
        {
            _textSamples = output.ToArray();
            _textIndex = 0;
            _textAutoReleaseMox = autoReleaseMox;
            return SnapshotLocked();
        }
    }

    public SignalJammerTxSnapshot Snapshot()
    {
        lock (_sync) return SnapshotLocked();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Factory.StartNew(
            () => PumpLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    internal bool TryPumpOnceForTests() => TryPumpOnce();

    private void PumpLoop(CancellationToken ct)
    {
        Zeus.Protocol2.RealtimeThreadPriority.PromoteCallingThreadToProAudio(_log);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long periodTicks = (long)(System.Diagnostics.Stopwatch.Frequency
            * BlockSamples / (double)SampleRateHz);
        long nextDeadlineTicks = 0;
        bool pacing = false;

        while (!ct.IsCancellationRequested)
        {
            if (!ShouldEmit())
            {
                pacing = false;
                if (ct.WaitHandle.WaitOne(IdlePoll)) return;
                continue;
            }

            TryPumpOnce();

            if (!pacing)
            {
                pacing = true;
                nextDeadlineTicks = clock.ElapsedTicks;
            }

            nextDeadlineTicks += periodTicks;
            long remainingTicks = nextDeadlineTicks - clock.ElapsedTicks;
            if (remainingTicks <= 0)
            {
                if (-remainingTicks > periodTicks * 8) nextDeadlineTicks = clock.ElapsedTicks;
                continue;
            }

            int delayMs = (int)(remainingTicks * 1000 / System.Diagnostics.Stopwatch.Frequency);
            if (delayMs > 0 && ct.WaitHandle.WaitOne(delayMs)) return;
        }
    }

    private bool ShouldEmit()
    {
        if (!_isMoxOn()) return false;
        lock (_sync) return _settings.Enabled || HasQueuedTextLocked();
    }

    private bool TryPumpOnce()
    {
        SignalJammerTxSettings settings;
        lock (_sync)
        {
            if (!_isMoxOn() || (!_settings.Enabled && !HasQueuedTextLocked())) return false;
            settings = _settings;
            if (_lastAppliedVersion != settings.Version)
            {
                _generator.Reset();
                _lastAppliedVersion = settings.Version;
            }
        }

        Array.Clear(_block, 0, _block.Length);
        if (settings.Enabled)
            _generator.Fill(_block, settings);
        bool releaseAutoTextAfterEmit = MixQueuedText(_block);
        Limit(_block);
        EncodeF32Le(_block, _payload);
        _emit(new ReadOnlyMemory<byte>(_payload, 0, _payload.Length));
        if (releaseAutoTextAfterEmit)
            ReleaseAutoTextMox();
        return true;
    }

    private bool MixQueuedText(Span<float> destination)
    {
        lock (_sync)
        {
            if (!HasQueuedTextLocked()) return false;
            var text = _textSamples!;
            int take = Math.Min(destination.Length, text.Length - _textIndex);
            for (int i = 0; i < take; i++)
                destination[i] += text[_textIndex + i];
            _textIndex += take;
            if (_textIndex >= text.Length)
            {
                bool releaseAutoMox = _textAutoReleaseMox;
                _textSamples = null;
                _textIndex = 0;
                _textAutoReleaseMox = false;
                return releaseAutoMox;
            }
            return false;
        }
    }

    private void ReleaseAutoTextMox()
    {
        if (_releaseTextMox is null) return;
        try
        {
            Thread.Sleep(AutoTransmitReleaseGuard);
            _releaseTextMox();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "qrm.text.autoTx.release failed");
        }
    }

    private bool HasQueuedTextLocked()
        => _textSamples is { Length: > 0 } && _textIndex < _textSamples.Length;

    private SignalJammerTxSnapshot SnapshotLocked()
        => new(
            Enabled: _settings.Enabled,
            Preset: _settings.Preset,
            Level: _settings.Level,
            ToneHz: _settings.ToneHz,
            DriftHz: _settings.DriftHz,
            PulseRateHz: _settings.PulseRateHz,
            TextQueued: HasQueuedTextLocked(),
            TextPendingSamples: _textSamples is null ? 0 : Math.Max(0, _textSamples.Length - _textIndex));

    private static int EstimateOutputSamples(int inputSamples, int sampleRate)
        => Math.Max(BlockSamples, (int)Math.Ceiling(inputSamples * (double)SampleRateHz / sampleRate));

    private static void EncodeF32Le(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(i * sizeof(float), sizeof(float)), samples[i]);
    }

    private static void Limit(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];
            if (!float.IsFinite(sample))
            {
                samples[i] = 0f;
                continue;
            }
            samples[i] = Math.Clamp(sample, -0.95f, 0.95f);
        }
    }

    private sealed class SignalJammerGenerator
    {
        private readonly Random _random = new(0x51524d);
        private readonly double[] _combPhases = new double[20];
        private readonly double[] _rakePhases = new double[17];
        private double _phase1;
        private double _phase2;
        private double _phase3;
        private double _driftPhase;
        private double _pulsePhase;
        private double _sweepPhase;
        private double _sweepPhase2;
        private double _chirpPhase;
        private float _noiseState;
        private float _fastNoiseState;
        private int _burstSamplesRemaining;
        private int _burstSamplesTotal;
        private float _burstPolarity = 1f;

        public void Reset()
        {
            Array.Clear(_combPhases, 0, _combPhases.Length);
            Array.Clear(_rakePhases, 0, _rakePhases.Length);
            _phase1 = 0;
            _phase2 = 0;
            _phase3 = 0;
            _driftPhase = 0;
            _pulsePhase = 0;
            _sweepPhase = 0;
            _sweepPhase2 = 0;
            _chirpPhase = 0;
            _noiseState = 0;
            _fastNoiseState = 0;
            _burstSamplesRemaining = 0;
            _burstSamplesTotal = 0;
            _burstPolarity = 1f;
        }

        public void Fill(Span<float> destination, SignalJammerTxSettings settings)
        {
            float levelGain = LevelToGain(settings.Level);
            for (int i = 0; i < destination.Length; i++)
            {
                float sample = settings.Preset switch
                {
                    "heterodyne" => CarrierRake(settings),
                    "pulse" => Burst(settings),
                    _ => Barrage(settings),
                };
                destination[i] += sample * levelGain;
            }
        }

        private float Barrage(SignalJammerTxSettings settings)
        {
            AdvancePhase(ref _driftPhase, 0.27);
            double drift = Math.Sin(_driftPhase) * settings.DriftHz;
            float noise = ColoredNoise(0.86f) * 0.76f + FastNoise() * 0.38f;
            float comb = FixedComb(drift) * 1.18f;
            float sweep = Tone(ref _phase1, SweptHz(ref _sweepPhase, 280.0, 3180.0, 0.33), "saw") * 0.34f;
            float sweep2 = Tone(ref _phase2, SweptHz(ref _sweepPhase2, 420.0, 2860.0, 0.71), "triangle") * 0.22f;
            float impulse = RandomBurst(0.0085, 0.88f);
            return noise + comb + sweep + sweep2 + impulse;
        }

        private float CarrierRake(SignalJammerTxSettings settings)
        {
            AdvancePhase(ref _driftPhase, 0.19);
            double drift = Math.Sin(_driftPhase) * settings.DriftHz;
            float rake = 0f;
            int middle = _rakePhases.Length / 2;
            for (int i = 0; i < _rakePhases.Length; i++)
            {
                double offset = (i - middle) * (i % 2 == 0 ? 133.0 : 91.0);
                double hz = Math.Clamp(settings.ToneHz + offset + drift * (0.20 + i * 0.055), 250.0, 3200.0);
                rake += Tone(ref _rakePhases[i], hz, i % 3 == 0 ? "triangle" : "sine");
            }

            float beat = Tone(ref _phase1, Math.Clamp(settings.ToneHz + 43.0 + drift * 0.35, 250.0, 3200.0), "square") * 0.32f;
            float counterBeat = Tone(ref _phase2, Math.Clamp(settings.ToneHz - 43.0 - drift * 0.28, 250.0, 3200.0), "square") * 0.22f;
            float sweep = Tone(ref _chirpPhase, SweptHz(ref _sweepPhase, 360.0, 3140.0, 0.21), "sine") * 0.22f;
            float sweep2 = Tone(ref _phase3, SweptHz(ref _sweepPhase2, 520.0, 2860.0, 0.56), "triangle") * 0.12f;
            float noise = ColoredNoise(0.70f) * 0.34f;
            return rake * 0.145f + beat + counterBeat + sweep + sweep2 + noise;
        }

        private float Burst(SignalJammerTxSettings settings)
        {
            AdvancePhase(ref _pulsePhase, settings.PulseRateHz);
            AdvancePhase(ref _driftPhase, 0.41);
            double drift = Math.Sin(_driftPhase) * settings.DriftHz;
            float gate = _pulsePhase < Math.PI * 1.16 ? 1.0f : 0.02f;
            float noise = (ColoredNoise(0.88f) * 1.08f + FastNoise() * 0.34f) * gate;
            float tone = Tone(ref _phase1, Math.Clamp(settings.ToneHz + drift, 250.0, 3200.0), "square") * 0.44f * gate;
            float chirp = Tone(ref _chirpPhase, SweptHz(ref _sweepPhase, 300.0, 3200.0, 0.92), "saw") * 0.36f * gate;
            float fastChirp = Tone(ref _phase2, SweptHz(ref _sweepPhase2, 460.0, 2800.0, 1.71), "square") * 0.20f * gate;
            float comb = FixedComb(drift * 0.35) * 0.42f * gate;
            float burst = RandomBurst(0.014, 1.0f);
            return noise + tone + chirp + fastChirp + comb + burst;
        }

        private float FixedComb(double drift)
        {
            float sum = 0f;
            for (int i = 0; i < _combPhases.Length; i++)
            {
                double hz = Math.Clamp(280.0 + i * 145.0 + drift * (0.08 + i * 0.016), 250.0, 3200.0);
                sum += Tone(ref _combPhases[i], hz, i % 4 == 0 ? "triangle" : "sine");
            }
            return sum * 0.064f;
        }

        private float ColoredNoise(float whiteMix)
        {
            float white = (float)(_random.NextDouble() * 2.0 - 1.0);
            _noiseState = white * whiteMix + _noiseState * (1f - whiteMix);
            return _noiseState;
        }

        private float FastNoise()
        {
            float white = (float)(_random.NextDouble() * 2.0 - 1.0);
            _fastNoiseState = white * 0.92f + _fastNoiseState * 0.08f;
            return _fastNoiseState;
        }

        private float RandomBurst(double probability, float magnitude)
        {
            if (_burstSamplesRemaining <= 0 && _random.NextDouble() < probability)
            {
                _burstSamplesTotal = _random.Next(18, 130);
                _burstSamplesRemaining = _burstSamplesTotal;
                _burstPolarity = _random.Next(2) == 0 ? -1f : 1f;
            }

            if (_burstSamplesRemaining <= 0 || _burstSamplesTotal <= 0) return 0f;
            float envelope = _burstSamplesRemaining / (float)_burstSamplesTotal;
            _burstSamplesRemaining--;
            return _burstPolarity * magnitude * envelope;
        }

        private double SweptHz(ref double phase, double lowHz, double highHz, double rateHz)
        {
            AdvancePhase(ref phase, rateHz);
            double position = (Math.Sin(phase) + 1.0) * 0.5;
            return lowHz + (highHz - lowHz) * position;
        }

        private static float Tone(ref double phase, double frequencyHz, string type)
        {
            AdvancePhase(ref phase, frequencyHz);
            return type switch
            {
                "square" => phase < Math.PI ? 1f : -1f,
                "saw" => (float)(phase / Math.PI - 1.0),
                "triangle" => (float)(2.0 / Math.PI * Math.Asin(Math.Sin(phase))),
                _ => (float)Math.Sin(phase),
            };
        }

        private static void AdvancePhase(ref double phase, double frequencyHz)
        {
            phase += Tau * Math.Max(0.0, frequencyHz) / SampleRateHz;
            if (phase >= Tau) phase %= Tau;
        }

        private static float LevelToGain(int level)
        {
            double normalized = Math.Clamp(level / 100.0, 0.0, 1.0);
            return (float)(Math.Pow(normalized, 1.18) * 0.46);
        }
    }
}

internal sealed record SignalJammerTxSnapshot(
    bool Enabled,
    string Preset,
    int Level,
    int ToneHz,
    int DriftHz,
    double PulseRateHz,
    bool TextQueued,
    int TextPendingSamples);

internal readonly record struct SignalJammerTxSettings(
    bool Enabled,
    string Preset,
    int Level,
    int ToneHz,
    int DriftHz,
    double PulseRateHz,
    long Version)
{
    public static readonly SignalJammerTxSettings Disabled = new(
        Enabled: false,
        Preset: "hash",
        Level: 28,
        ToneHz: 980,
        DriftHz: 80,
        PulseRateHz: 3.5,
        Version: 0);

    public static SignalJammerTxSettings Normalize(SignalJammerSetRequest req, long version)
    {
        string preset = req.Preset?.Trim().ToLowerInvariant() switch
        {
            "heterodyne" => "heterodyne",
            "pulse" => "pulse",
            _ => "hash",
        };
        double pulse = double.IsFinite(req.PulseRateHz) ? req.PulseRateHz : Disabled.PulseRateHz;
        return new SignalJammerTxSettings(
            Enabled: req.Enabled,
            Preset: preset,
            Level: Math.Clamp(req.Level, 0, 100),
            ToneHz: Math.Clamp(req.ToneHz, 250, 3200),
            DriftHz: Math.Clamp(req.DriftHz, 0, 500),
            PulseRateHz: Math.Clamp(pulse, 0.5, 12.0),
            Version: version);
    }
}
