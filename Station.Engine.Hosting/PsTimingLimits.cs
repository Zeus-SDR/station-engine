// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

namespace Zeus.Server;

internal static class PsTimingLimits
{
    // WDSP delay.c allocates enough whole-sample delay positions for the
    // Thetis AMP-delay option range at the highest Zeus PS feedback rate.
    internal const int WdspDelayWholeSamplePositions = 9601;

    internal const double DefaultMoxDelaySec = 0.2;
    internal const double MinMoxDelaySec = 0.1;
    internal const double MaxMoxDelaySec = 1.0;
    internal const double DefaultLoopDelaySec = 0.0;
    internal const double MinLoopDelaySec = 0.0;
    internal const double MaxLoopDelaySec = 100.0;
    internal const double DefaultAmpDelayNs = 150.0;
    internal const double MaxAmpDelayNs = 25_000_000.0;

    internal static double ClampMoxDelaySec(double moxDelaySec)
    {
        if (double.IsNaN(moxDelaySec))
            return DefaultMoxDelaySec;
        if (moxDelaySec < MinMoxDelaySec)
            return MinMoxDelaySec;
        if (moxDelaySec > MaxMoxDelaySec)
            return MaxMoxDelaySec;
        return moxDelaySec;
    }

    internal static double ClampLoopDelaySec(double loopDelaySec)
    {
        if (double.IsNaN(loopDelaySec))
            return DefaultLoopDelaySec;
        if (loopDelaySec < MinLoopDelaySec)
            return MinLoopDelaySec;
        if (loopDelaySec > MaxLoopDelaySec)
            return MaxLoopDelaySec;
        return loopDelaySec;
    }

    internal static double ClampAmpDelayNs(double ampDelayNs)
    {
        if (double.IsNaN(ampDelayNs))
            return DefaultAmpDelayNs;
        if (ampDelayNs < 0.0)
            return 0.0;
        if (ampDelayNs > MaxAmpDelayNs)
            return MaxAmpDelayNs;
        return ampDelayNs;
    }
}
