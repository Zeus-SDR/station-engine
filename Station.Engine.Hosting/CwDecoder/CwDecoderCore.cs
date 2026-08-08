// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>Allocation-stable Goertzel → threshold → timing → Morse pipeline.</summary>
internal sealed class CwDecoderCore
{
    private readonly GoertzelDetector _detector;
    private readonly AdaptiveThreshold _threshold = new();
    private readonly MorseTimingEstimator _timing = new();
    private readonly MorseFsm _fsm;
    private readonly float[] _block = new float[GoertzelDetector.BlockSize];
    private int _blockFill;

    public CwDecoderCore(int sampleRateHz, double centerFrequencyHz)
    {
        SampleRateHz = sampleRateHz;
        _detector = new GoertzelDetector(sampleRateHz, centerFrequencyHz);
        _fsm = new MorseFsm(_timing);
    }

    public int SampleRateHz { get; }
    public double Wpm => _timing.Wpm;
    public double SnrDb => _threshold.SnrDb;

    public void Retune(double centerFrequencyHz) => _detector.Retune(centerFrequencyHz);

    public void Process(ReadOnlySpan<float> samples, Action<MorseDecodedSymbol> onDecoded)
    {
        int offset = 0;
        while (offset < samples.Length)
        {
            int copy = Math.Min(_block.Length - _blockFill, samples.Length - offset);
            samples.Slice(offset, copy).CopyTo(_block.AsSpan(_blockFill, copy));
            _blockFill += copy;
            offset += copy;
            if (_blockFill != _block.Length) continue;

            double power = _detector.DetectPower(_block);
            bool tone = _threshold.Update(power);
            if (_fsm.Process(tone, 1000.0 * _block.Length / SampleRateHz, out MorseDecodedSymbol symbol))
            {
                double signalFit = Math.Clamp((_threshold.SnrDb - 2.0) / 10.0, 0.2, 1.0);
                onDecoded(symbol with { Confidence = (float)(symbol.Confidence * signalFit) });
            }
            _blockFill = 0;
        }
    }

    public void Reset()
    {
        _blockFill = 0;
        _threshold.Reset();
        _timing.Reset();
        _fsm.Reset();
    }
}
