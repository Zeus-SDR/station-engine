// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Zeus.Dsp.Wdsp;

public sealed partial class WdspDspEngine : IBandwidthSpectrumSource
{
    private int _bandwidthTxAnalyzer = -1;
    private long _bandwidthTxGeneration;
    private long _bandwidthTxDemandUntil;
    private long _bandwidthTxWarmupUntil;
    private bool _bandwidthTxSnapped;

    private void OpenBandwidthTxAnalyzer(int txa)
    {
        lock (_txDispLock)
        {
            // Channel IDs are reserved process-wide. RX's auxiliary analyzer uses
            // 32 + its RX channel; the TX channel owns this otherwise unused slot.
            int disp = MaxWdspNativeChannels + txa;
            try
            {
                int rc = 0;
                RunNativeLifecycleCriticalSection(() => NativeMethods.XCreateAnalyzer(disp, out rc, AnalyzerFftSize, 1, 1, null));
                if (rc != 0) return;
                _bandwidthTxAnalyzer = disp;
                ConfigureAnalyzer(disp, _txaDspRateHz, _txaDspSize, 0.0,
                    AnalyzerFftSize, AnalyzerFftSize, AnalyzerWindow, AnalyzerKaiserPi);
                NativeMethods.SetDisplayDetectorMode(disp, 0, 0);
                NativeMethods.SetDisplayAverageMode(disp, 0, 0);
                _bandwidthTxGeneration = Interlocked.Increment(ref s_snrAnalyzerGeneration);
                _bandwidthTxSnapped = false;
            }
            catch (Exception ex)
            {
                try { CloseBandwidthTxAnalyzer(); }
                catch (Exception cleanup) { _log.LogWarning(cleanup, "wdsp.bandwidthDisplay cleanup failed"); }
                _log.LogWarning(ex, "wdsp.bandwidthDisplay unavailable; normal TX remains active");
            }
        }
    }

    private void CloseBandwidthTxAnalyzer()
    {
        // Called under the same lock as the SIPHON feed and pixel drain.
        if (_bandwidthTxAnalyzer >= 0)
            RunNativeLifecycleCriticalSection(() => NativeMethods.DestroyAnalyzer(_bandwidthTxAnalyzer));
        _bandwidthTxAnalyzer = -1;
        _bandwidthTxDemandUntil = 0;
        _bandwidthTxSnapped = false;
    }

    public bool TryGetBandwidthSpectrum(int channelId, bool tx, Span<float> bins,
        long demandUntilTickMs, out RxSnrSpectrumInfo info)
    {
        info = default;
        if (_disposed != 0 || bins.Length < AnalyzerFftSize) return false;
        if (!tx)
        {
            if (!_channels.TryGetValue(channelId, out var state)) return false;
            lock (state.AnalyzerLock)
            {
                if (state.Stopped || !state.SnrAnalyzerHasSnapped) return false;
                info = new(AnalyzerFftSize, state.SampleRateHz, state.SnrAnalyzerGeneration);
                // Output 0 is a fast peak display. Output 2 retains the existing
                // 750 ms, one-hertz-normalized signal-quality measurement.
                NativeMethods.GetPixels(state.SnrAnalyzerId, 0,
                    ref MemoryMarshal.GetReference(bins), out int flag);
                return flag == 1;
            }
        }
        lock (_txDispLock)
        {
            if (_bandwidthTxAnalyzer < 0) return false;
            long now = Environment.TickCount64;
            if (_bandwidthTxDemandUntil <= now)
            {
                _bandwidthTxWarmupUntil = now + (long)Math.Ceiling(1000.0 * AnalyzerFftSize / _txaDspRateHz);
                _bandwidthTxSnapped = false;
                NativeMethods.ResetPixelBuffers(_bandwidthTxAnalyzer);
            }
            Volatile.Write(ref _bandwidthTxDemandUntil, demandUntilTickMs);
            info = new(AnalyzerFftSize, _txaDspRateHz, _bandwidthTxGeneration);
            if (!_bandwidthTxSnapped || now < _bandwidthTxWarmupUntil) return false;
            NativeMethods.GetPixels(_bandwidthTxAnalyzer, 0,
                ref MemoryMarshal.GetReference(bins), out int flag);
            return flag == 1;
        }
    }
}
