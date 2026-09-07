// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Microsoft.Extensions.Logging.EventSource;

namespace Zeus.Server.Diagnostics;

public static class AudioDiagnosticLogging
{
    // These categories run on capture, DSP or radio workers. Their complete
    // Information+ trace is retained by the queued ring/file provider. Built-in
    // providers must not format the same event again on the audio caller.
    internal static readonly string[] Categories =
    [
        "Zeus.Protocol1", "Zeus.Protocol2", "Zeus.Dsp",
        "Zeus.Server.NativeMicCapture", "Zeus.Server.MiniAudioInput",
        "Zeus.Server.TxAudioIngest", "Zeus.Server.TxTuneDriver",
        "Zeus.Server.SaturnSpeakerAudioSink", "Zeus.Server.DspPipelineService",
        "Zeus.Server.SignalJammerTxSource",
    ];

    public static void Configure(ILoggingBuilder logging)
    {
        foreach (var category in Categories)
        {
            logging.AddFilter<ConsoleLoggerProvider>(category, LogLevel.None);
            logging.AddFilter<DebugLoggerProvider>(category, LogLevel.None);
            logging.AddFilter<EventSourceLoggerProvider>(category, LogLevel.None);
        }
    }
}
