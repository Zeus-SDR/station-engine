// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// SPE Expert 1.5K Taurus amplifier support. This file is GPL-3.0-or-later
// (see Station.Engine.Hosting/SpeTaurus/SOURCE.md); the rest of the engine is
// GPL-2.0-or-later, whose "or later" option permits the combination. The
// resulting engine binary is distributed as GPL-3.0-or-later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Globalization;
using System.Text;

namespace Zeus.Server.SpeTaurus;

internal enum SpeCommand : byte
{
    Input = 0x01,
    Antenna = 0x04,
    Tune = 0x09,
    PowerLevel = 0x0B,
    Operate = 0x0D,
    Status = 0x90,
}

internal sealed record SpeFrame(byte[] Data, bool IsStatus);

internal sealed record SpeAmplifierStatus(
    string ModelCode,
    string ModelName,
    bool IsTaurus,
    bool Operate,
    bool Transmitting,
    string MemoryBank,
    int Input,
    string Band,
    int BandIndex,
    int TxAntenna,
    string AtuState,
    string RxAntenna,
    string PowerLevel,
    double? OutputPowerWatts,
    double? SwrAtu,
    double? SwrAntenna,
    double? PaVoltage,
    double? PaCurrent,
    int? UpperTemperature,
    int? LowerTemperature,
    int? CombinerTemperature,
    string WarningCode,
    string Warning,
    string AlarmCode,
    string Alarm);

internal static class SpeProtocol
{
    internal const byte HostSync = 0x55;
    internal const byte AmplifierSync = 0xAA;

    private static readonly string[] Bands =
    [
        "160m", "80m", "60m", "40m", "30m", "20m",
        "17m", "15m", "12m", "10m", "6m", "4m",
    ];

    internal static byte[] EncodeCommand(SpeCommand command) =>
    [
        HostSync,
        HostSync,
        HostSync,
        0x01,
        (byte)command,
        (byte)command,
    ];

    internal static SpeAmplifierStatus? TryParseStatus(ReadOnlySpan<byte> payload)
    {
        var text = Encoding.ASCII.GetString(payload);
        var fields = text.Split(',');
        if (fields.Length != 19) return null;

        var modelCode = fields[0].Trim();
        if (fields[0].Length != 3 || modelCode is not ("13K" or "15K" or "15T" or "20K"))
            return null;
        var mode = fields[1].Trim();
        var rxTx = fields[2].Trim();
        var memory = fields[3].Trim();
        var powerLevel = fields[8].Trim();
        if (fields[1].Length != 1 || mode is not ("S" or "O")
            || fields[2].Length != 1 || rxTx is not ("R" or "T")
            || fields[3].Length != 1 || memory is not ("A" or "B" or "x")
            || fields[4].Length != 1
            || fields[5].Length != 2
            || fields[6].Length != 2
            || fields[7].Length != 2
            || fields[8].Length != 1 || powerLevel is not ("L" or "M" or "H")
            || fields[9].Length != 4
            || fields[10].Length != 5
            || fields[11].Length != 5
            || fields[12].Length != 4
            || fields[13].Length != 4
            || fields[14].Length != 3
            || fields[15].Length != 3
            || fields[16].Length != 3)
            return null;
        if (!TryInt(fields[4], out var input)
            || !TryInt(fields[5], out var bandIndex)
            || !TryAntenna(fields[6], out var antenna, out var atu)
            || !TryDouble(fields[9], out var power)
            || !TryDouble(fields[10], out var swrAtu)
            || !TryDouble(fields[11], out var swrAntenna)
            || !TryDouble(fields[12], out var voltage)
            || !TryDouble(fields[13], out var current)
            || !TryInt(fields[14], out var upper)
            || !TryInt(fields[15], out var lower)
            || !TryInt(fields[16], out var combiner))
            return null;
        if (input is not (1 or 2)
            || bandIndex < 0 || bandIndex > (modelCode == "20K" ? 10 : 11)
            || antenna < 0 || antenna > (modelCode == "20K" ? 6 : 4)
            || !TryRxAntenna(fields[7])
            || power is < 0 or > 3000
            || swrAtu is < 0 or > 99.99
            || swrAntenna is < 0 or > 99.99
            || voltage is < 0 or > 100
            || current is < 0 or > 100
            || upper is < -99 or > 999
            || lower is < -99 or > 999
            || combiner is < -99 or > 999)
            return null;

        var warningCode = fields[17].Trim();
        var alarmCode = fields[18].Trim();
        if (warningCode.Length != 1 || alarmCode.Length != 1
            || !"MASBPOYWKR TCN".Replace(" ", "", StringComparison.Ordinal).Contains(warningCode[0])
            || !"SADHCN".Contains(alarmCode[0]))
            return null;

        return new SpeAmplifierStatus(
            modelCode,
            ModelName(modelCode),
            IsTaurus: string.Equals(modelCode, "15T", StringComparison.Ordinal),
            mode == "O",
            rxTx == "T",
            memory,
            input,
            bandIndex >= 0 && bandIndex < Bands.Length ? Bands[bandIndex] : $"Band {bandIndex:D2}",
            bandIndex,
            antenna,
            atu,
            fields[7].Trim(),
            powerLevel,
            power,
            swrAtu,
            swrAntenna,
            voltage,
            current,
            upper,
            lower,
            combiner,
            warningCode,
            WarningText(warningCode[0]),
            alarmCode,
            AlarmText(alarmCode[0]));
    }

    private static bool TryAntenna(string value, out int antenna, out string atu)
    {
        value = value.Trim();
        antenna = 0;
        atu = "Unknown";
        if (value.Length != 2 || !char.IsAsciiDigit(value[0])) return false;
        antenna = value[0] - '0';
        atu = char.ToLowerInvariant(value[1]) switch
        {
            't' => "Tunable",
            'b' => "Bypassed",
            'a' => "Enabled",
            _ => "Unknown",
        };
        return atu != "Unknown";
    }

    private static bool TryRxAntenna(string value)
    {
        value = value.Trim();
        return value.Length == 2
            && char.IsAsciiDigit(value[0])
            && char.ToLowerInvariant(value[1]) == 'r';
    }

    private static bool TryInt(string value, out int parsed) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static bool TryDouble(string value, out double parsed) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
        && double.IsFinite(parsed);

    private static string ModelName(string code) => code switch
    {
        "20K" => "Expert 2K-FA",
        "13K" => "Expert 1.3K-FA",
        "15K" => "Expert 1.5K-FA",
        "15T" => "Expert 1.5K Taurus",
        _ => $"SPE Expert ({code})",
    };

    internal static string WarningText(char code) => char.ToUpperInvariant(code) switch
    {
        'M' => "Amplifier alarm",
        'A' => "No selected antenna",
        'S' => "Antenna SWR",
        'B' => "No valid band",
        'P' => "Power limit exceeded",
        'O' => "Overheating",
        'Y' => "ATU not available",
        'W' => "Tuning with no power",
        'K' => "ATU bypassed",
        'R' => "Power switch held by remote",
        'T' => "Combiner overheating",
        'C' => "Combiner fault",
        'N' => "",
        _ => $"Unknown warning ({code})",
    };

    internal static string AlarmText(char code) => char.ToUpperInvariant(code) switch
    {
        'S' => "SWR exceeding limits",
        'A' => "Amplifier protection",
        'D' => "Input overdriving",
        'H' => "Excess overheating",
        'C' => "Combiner fault",
        'N' => "",
        _ => $"Unknown alarm ({code})",
    };
}

internal sealed class SpeFrameParser
{
    private readonly List<byte> _buffer = [];

    public long RejectedFrames { get; private set; }

    public IReadOnlyList<SpeFrame> Push(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) _buffer.Add(value);
        var frames = new List<SpeFrame>();
        while (TryReadOne(out var frame))
        {
            if (frame is not null) frames.Add(frame);
        }
        return frames;
    }

    public void Reset()
    {
        _buffer.Clear();
        RejectedFrames = 0;
    }

    private bool TryReadOne(out SpeFrame? frame)
    {
        frame = null;
        var sync = FindSync();
        if (sync < 0)
        {
            if (_buffer.Count > 2) _buffer.RemoveRange(0, _buffer.Count - 2);
            return false;
        }
        if (sync > 0) _buffer.RemoveRange(0, sync);
        if (_buffer.Count < 4) return false;

        var length = _buffer[3];
        if (length == 0)
        {
            RejectCandidate();
            return true;
        }

        if (length == 1)
        {
            var total = 4 + length + 1;
            if (_buffer.Count < total) return false;
            var data = _buffer.Skip(4).Take(length).ToArray();
            if (_buffer[total - 1] != data.Aggregate(0, (sum, item) => (sum + item) & 0xFF))
            {
                RejectCandidate();
                return true;
            }
            _buffer.RemoveRange(0, total);
            frame = new SpeFrame(data, IsStatus: false);
            return true;
        }

        var statusTotal = 4 + length + 2 + 2;
        if (_buffer.Count < statusTotal) return false;
        var checksumIndex = 4 + length;
        if (_buffer[statusTotal - 2] != 13 || _buffer[statusTotal - 1] != 10)
        {
            RejectCandidate();
            return true;
        }

        var payload = _buffer.Skip(4).Take(length).ToArray();
        var sum = payload.Sum(item => (int)item);
        if (_buffer[checksumIndex] != (byte)sum
            || _buffer[checksumIndex + 1] != (byte)(sum >> 8))
        {
            RejectCandidate();
            return true;
        }

        _buffer.RemoveRange(0, statusTotal);
        frame = new SpeFrame(payload, IsStatus: true);
        return true;
    }

    private int FindSync()
    {
        for (var index = 0; index <= _buffer.Count - 3; index++)
            if (_buffer[index] == SpeProtocol.AmplifierSync
                && _buffer[index + 1] == SpeProtocol.AmplifierSync
                && _buffer[index + 2] == SpeProtocol.AmplifierSync)
                return index;
        return -1;
    }

    private void RejectCandidate()
    {
        RejectedFrames++;
        _buffer.RemoveAt(0);
    }
}
