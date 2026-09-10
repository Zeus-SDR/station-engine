// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

public sealed record Hl2IoBoardStatus(
    bool Supported = false,
    bool Present = false,
    byte HardwareVersion = 0,
    byte FirmwareMajor = 0,
    byte FirmwareMinor = 0,
    byte Inputs = 0,
    byte Outputs = 0,
    byte ReservedOutputs = 0,
    byte Fault = 0,
    byte Tuner = 0,
    long? FrequencyHz = null,
    DateTimeOffset? LastReadUtc = null,
    string? Error = null);

/// <summary>Common Pico register protocol, independent of amplifier-specific firmware.</summary>
internal sealed class Hl2IoBoardSession(IHl2I2cTransport transport)
{
    private const byte Pico = 0x1D;
    private long? _frequency;
    private byte? _mode;
    private byte? _antenna;
    public RxMode? SynchronizedMode { get; private set; }
    public Hl2IoBoardStatus Status { get; private set; } = new(Supported: true);

    public bool NeedsSynchronization(long frequencyHz, RxMode mode, byte rxAntenna, byte txAntenna) =>
        _frequency != frequencyHz || _mode != EncodeMode(mode) || _antenna != (byte)((txAntenna << 4) | rxAntenna);

    public async Task<bool> DetectAsync(CancellationToken ct)
    {
        uint hardware = await transport.ReadAsync(0x41, 0, ct).ConfigureAwait(false);
        byte revision = (byte)(hardware & 15);
        if (revision != 1) return false;
        uint firmware = await transport.ReadAsync(Pico, 9, ct).ConfigureAwait(false);
        Status = Status with
        {
            Present = true,
            HardwareVersion = revision,
            FirmwareMajor = (byte)firmware,
            FirmwareMinor = (byte)(firmware >> 8),
        };
        _frequency = null;
        _mode = null;
        _antenna = null;
        return true;
    }

    public async Task SynchronizeAsync(long frequencyHz, RxMode mode, byte rxAntenna, byte txAntenna, CancellationToken ct)
    {
        if (!Status.Present) throw new InvalidOperationException("HL2 IO Board has not been detected.");
        if (frequencyHz is < 0 or > 0xFFFFFFFFFFL) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (rxAntenna > 15 || txAntenna > 15) throw new ArgumentOutOfRangeException(nameof(rxAntenna));
        byte modeCode = EncodeMode(mode);
        if (_mode != modeCode)
        {
            await transport.WriteAsync(Pico, 32, modeCode, ct).ConfigureAwait(false);
            _mode = modeCode;
        }
        if (_frequency != frequencyHz)
        {
            // Register 4 commits the complete 40-bit RF frequency in the Pico.
            // Cache only after all five acknowledged writes have succeeded.
            for (byte register = 0; register < 5; register++)
                await transport.WriteAsync(Pico, register, (byte)(frequencyHz >> ((4 - register) * 8)), ct).ConfigureAwait(false);
            _frequency = frequencyHz;
        }
        byte antenna = (byte)((txAntenna << 4) | rxAntenna);
        if (_antenna != antenna)
        {
            await transport.WriteAsync(Pico, 31, antenna, ct).ConfigureAwait(false);
            _antenna = antenna;
        }
        SynchronizedMode = mode;
        Status = Status with { FrequencyHz = _frequency };
    }

    public async Task PollAsync(CancellationToken ct)
    {
        uint status = await transport.ReadAsync(Pico, 6, ct).ConfigureAwait(false);
        Status = Status with
        {
            Inputs = (byte)(status & 0x3F),
            Tuner = (byte)(status >> 8),
            Fault = (byte)(status >> 16),
            FirmwareMajor = (byte)(status >> 24),
            LastReadUtc = DateTimeOffset.UtcNow,
        };
    }

    public async Task ReadOutputsAsync(CancellationToken ct)
    {
        uint data = await transport.ReadAsync(Pico, 168, ct).ConfigureAwait(false);
        // Input-register bits 6/7 identify UART/PWM pins that firmware owns.
        Status = Status with
        {
            Outputs = (byte)(data >> 8),
            ReservedOutputs = (byte)(((data & 0x40) != 0 ? 1 : 0) | ((data & 0x80) != 0 ? 128 : 0)),
        };
    }

    public async Task SetOutputsAsync(byte outputs, CancellationToken ct)
    {
        await ReadOutputsAsync(ct).ConfigureAwait(false);
        byte mask = Status.ReservedOutputs;
        byte value = (byte)((outputs & ~mask) | (Status.Outputs & mask));
        await transport.WriteAsync(Pico, 169, value, ct).ConfigureAwait(false);
        await ReadOutputsAsync(ct).ConfigureAwait(false);
    }

    public Task TunerCommandAsync(byte command, CancellationToken ct)
    {
        if (command > 2) throw new ArgumentOutOfRangeException(nameof(command));
        return transport.WriteAsync(Pico, 7, command, ct);
    }

    internal static byte EncodeMode(RxMode mode) => mode switch
    {
        RxMode.LSB => 0,
        RxMode.USB or RxMode.FreeDv => 1,
        RxMode.DSB => 2,
        RxMode.CWL => 3,
        RxMode.CWU => 4,
        RxMode.FM => 5,
        RxMode.AM => 6,
        RxMode.DIGU => 7,
        RxMode.DIGL => 9,
        RxMode.SAM => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
