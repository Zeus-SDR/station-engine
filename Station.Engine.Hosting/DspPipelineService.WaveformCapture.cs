// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Dsp;

namespace Zeus.Server;

public partial class DspPipelineService
{
    private long _txCaptureTapUntil;

    public WaveformCaptureMetadata? StartWaveformCapture(int receiver, bool tx, int durationMs, out string? error)
    {
        if (receiver is < 0 or > 7 || durationMs is not (1000 or 3000 or 5000 or 10000))
        { error = "Invalid receiver or duration"; return null; }
        var engine = Volatile.Read(ref _engine);
        var state = _radio.Snapshot();
        var context = WaveformSourceContext(engine, state, receiver, tx);
        long now = Environment.TickCount64;
        var cache = tx ? _txWaveform : _rxWaveforms[receiver];
        var result = cache.StartCapture(context, now, receiver, tx, durationMs,
            context?.Mode.ToString() ?? "", out error);
        if (result is null) return null;
        if (tx)
        {
            Volatile.Write(ref _requestedTxWaveform, context);
            Volatile.Write(ref _txCaptureTapUntil, now + durationMs + 2000);
            // Uses the existing read-only completed-output observer. No monitor or DSP setting is changed.
            GetWaveformSnapshot(receiver, true, 50);
        }
        else Volatile.Write(ref _requestedRxWaveforms[receiver], context);
        return result;
    }

    private WaveformSnapshotCache? FindWaveformCapture(string id, out WaveformCaptureMetadata? metadata)
    {
        // Route opaque IDs to one lane; status reads must not contend with unrelated producers.
        WaveformSnapshotCache? cache = id.Length == 33 && id[0] == 't' ? _txWaveform
            : id.Length == 34 && id[0] == 'r' && id[1] is >= '0' and <= '7'
                ? _rxWaveforms[id[1] - '0'] : null;
        var found = cache?.FindCapture(id);
        if (found is not null)
        {
            metadata = cache!.GetCapture(id, WaveformSourceContext(Volatile.Read(ref _engine),
                _radio.Snapshot(), found.ReceiverIndex, found.Kind == "complex"), Environment.TickCount64);
            return metadata is null ? null : cache;
        }
        metadata = null; return null;
    }
    public WaveformCaptureMetadata? GetWaveformCapture(string id)
    { FindWaveformCapture(id, out var metadata); return metadata; }
    public byte[]? GetWaveformCaptureSamples(string id)
    { return FindWaveformCapture(id, out _)?.GetCaptureBytes(id); }
    public bool CancelWaveformCapture(string id)
    { return FindWaveformCapture(id, out _)?.CancelCapture(id, Environment.TickCount64) == true; }
}
