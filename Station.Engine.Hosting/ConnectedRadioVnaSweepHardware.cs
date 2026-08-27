// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

public sealed class ConnectedRadioVnaSweepHardware : IVnaSweepHardware
{
    private readonly RadioService _radio;
    private readonly Hl2VnaSweepHardware _hl2;
    private readonly RadioBridgeVnaSweepHardware _bridge;

    public ConnectedRadioVnaSweepHardware(
        RadioService radio,
        Hl2VnaSweepHardware hl2,
        RadioBridgeVnaSweepHardware bridge)
    {
        _radio = radio;
        _hl2 = hl2;
        _bridge = bridge;
    }

    private IVnaSweepHardware Active =>
        _radio.ConnectedBoardKind == HpsdrBoardKind.HermesLite2 ? _hl2 : _bridge;

    public VnaCapabilityDto Capability => Active.Capability;

    public Task<VnaCaptureResult> CaptureAsync(
        long startHz, long endHz, int points, int driveLevel, bool fixedRxGainHigh,
        IProgress<int>? progress, CancellationToken cancellationToken) =>
        Active.CaptureAsync(startHz, endHz, points, driveLevel, fixedRxGainHigh,
            progress, cancellationToken);
}
