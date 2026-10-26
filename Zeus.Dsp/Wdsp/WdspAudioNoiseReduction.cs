// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Contracts;

namespace Zeus.Dsp.Wdsp;

/// <summary>A private RXA for already-demodulated 48 kHz mono audio.</summary>
public sealed class WdspAudioNoiseReduction : IDisposable
{
    private readonly WdspDspEngine _engine = new();
    private readonly double[] _iq = new double[4_800 * 2];
    private readonly int _channel;
    private readonly string? _modelPath;
    private volatile float _inputGain = 1;

    public WdspAudioNoiseReduction(NrConfig config, string? modelPath = null, RxMode sourceMode = RxMode.DSB)
    {
        try
        {
            _modelPath = modelPath;
            EnsureModel(config);
            _channel = _engine.OpenChannel(48_000, 128);
            _engine.IsolateChannelFromOperatorSettings(_channel);
            _engine.SetMode(_channel, RxMode.DSB);
            _engine.SetFilter(_channel, 0, 23_500);
            _engine.SetAgc(_channel, new AgcConfig(AgcMode.Fixed, FixedGainDb: 0));
            _engine.SetSquelch(_channel, new SquelchConfig());
            _engine.SetReceiveEqualizer(_channel, false, 0, 0, 0, 0);
            Apply(config, sourceMode);
        }
        catch
        {
            _engine.Dispose();
            throw;
        }
    }

    /// <summary>Invalidates the private-channel cache after an installed model changes.</summary>
    public static void InvalidateModelCache() => WdspDspEngine.InvalidateNr3ModelCache();

    /// <summary>
    /// Applies the operator's settings. <paramref name="sourceMode"/> is the
    /// mode the radio demodulated this audio in; it only matters for NNR in CW.
    /// </summary>
    public void Apply(NrConfig config, RxMode sourceMode = RxMode.DSB)
    {
        EnsureModel(config);
        // Before SetNoiseReduction, so turning NNR on in CW selects the CW
        // network at once and turning it off leaves the voice network behind.
        _engine.SetMode(_channel, ChannelMode(config, sourceMode));
        _engine.SetNoiseReduction(_channel, AudioConfig(config));
        // This MUST follow SetNoiseReduction: its run-setters re-enable bp1.
        // SNB's bpsnba passes only positive frequencies in DSB, halving real
        // input before SNBA and every NR stage. Compensate at the input so
        // level-dependent NR sees the true level; bp1's post-NR gain cannot.
        NativeMethods.SetRXABandpassRun(_channel, 0);
        _inputGain = InputGain(config, sourceMode);
    }

    // The CW training-band filter passes one sideband of this real audio, so
    // it halves the input just as SNB's bpsnba does; SNB does not run then.
    internal static float InputGain(NrConfig config, RxMode sourceMode = RxMode.DSB) =>
        config.SnbEnabled || UsesCwNetwork(config, sourceMode) ? 2 : 1;

    /// <summary>
    /// True when WDSP 2.11's CW networks serve this audio: NNR on audio the
    /// radio demodulated in CWU or CWL. The channel then runs in that mode, so
    /// WDSP picks the CW network and its training-band path, as on an HPSDR
    /// receiver; SNB is bypassed there (WDSP Reference Manual §5.3.20).
    /// </summary>
    public static bool UsesCwNetwork(NrConfig config, RxMode sourceMode) =>
        config.NrMode == NrMode.Nnr && sourceMode is RxMode.CWU or RxMode.CWL;

    /// <summary>
    /// The mode this channel runs in: the source's CW mode while the CW network
    /// serves it. Every other setting keeps the transparent DSB channel the NR,
    /// SNB and NB parity measurements were made on, whatever the source mode.
    /// </summary>
    public static RxMode ChannelMode(NrConfig config, RxMode sourceMode) =>
        UsesCwNetwork(config, sourceMode) ? sourceMode : RxMode.DSB;

    internal int ChannelIdForTests => _channel;

    private void EnsureModel(NrConfig config)
    {
        if (config.NrMode == NrMode.Rnnr
            && _engine.EnsureNr3ModelLoaded(_modelPath) != Nr3ModelLoadResult.Loaded)
            throw new InvalidOperationException("Receive noise reduction model is unavailable.");
    }

    // The operator's spectral noise blanker and NB1/NB2 impulse blanker run on
    // this audio (the blanker ahead of the RXA in the worker, as on any
    // receiver); the auto-notch and the manual notches do not.
    internal static NrConfig AudioConfig(NrConfig config) => config with
    {
        AnfEnabled = false, NbpNotchesEnabled = false,
        NbThreshold = config.NbThreshold * RealAudioNbThresholdScale,
    };

    // NB1/NB2 blank a sample whose magnitude exceeds threshold x its running
    // average magnitude (native/wdsp/nob.c). An HPSDR receiver feeds complex IQ,
    // whose magnitude is the signal envelope; this channel feeds real audio
    // (Q = 0), whose magnitude swings with every cycle: a steady sine averages
    // 2/pi of its peak. Scaling by pi/2 puts a steady real signal at the same
    // trigger margin as its IQ envelope, so the operator's threshold means the
    // same thing on both radio families.
    internal const double RealAudioNbThresholdScale = Math.PI / 2;

    public void Feed(ReadOnlySpan<float> mono)
    {
        if (mono.Length > _iq.Length / 2) throw new ArgumentOutOfRangeException(nameof(mono));
        // Snapshot the applied gain once for the whole frame across threads.
        double inputGain = _inputGain;
        for (int i = 0; i < mono.Length; i++)
        {
            _iq[2 * i] = mono[i] * inputGain;
            _iq[2 * i + 1] = 0;
        }
        _engine.FeedIq(_channel, _iq.AsSpan(0, mono.Length * 2));
    }

    public int Read(Span<float> mono) => _engine.ReadAudio(_channel, mono);
    public void Dispose() => _engine.Dispose();
}
