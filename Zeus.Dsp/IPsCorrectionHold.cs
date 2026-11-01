// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Dsp;

/// <summary>
/// Keeps PureSignal's running correction applied through a calibration reset
/// until the next fit replaces it, and lets a restored correction run while
/// calibration continues. Off unless a caller turns it on; disarming
/// PureSignal turns it off, so the correction still ends with the arm.
/// </summary>
public interface IPsCorrectionHold
{
    void SetPsHoldCorrection(bool hold);
}
