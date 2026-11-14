// SPDX-License-Identifier: GPL-2.0-or-later
#include <assert.h>
#include <stdio.h>
#include "wdsp.h"

extern void GetPSFastTrackingAlphasForTest(int channel, double* values);

static void check_alphas(int channel, int fast)
{
    double values[7];
    GetPSFastTrackingAlphasForTest(channel, values);
    assert(GetPSFastTracking(channel) == fast);
    assert(values[0] == (fast ? 0.50 : 0.10));
    for (int i = 1; i < 7; ++i)
        assert(values[i] == (fast ? 0.60 : 0.30));
}

int main(void)
{
    const int channel = 0;
    double zeros[128] = {0};
    for (int lifetime = 0; lifetime < 2; ++lifetime)
    {
        OpenChannel(channel, 64, 64, 48000, 48000, 48000, 1, 0, 0, 0, 0, 0, 0);
        check_alphas(channel, 0);
        SetPSFastTracking(channel, 1);
        check_alphas(channel, 1);
        // Consume LRESET through the real feedback entry point.
        SetPSRunCal(channel, 1);
        SetPSControl(channel, 1, 0, 0, 0);
        pscc(channel, 64, zeros, zeros);
        check_alphas(channel, 1);
        SetPSFastTracking(channel, 0);
        check_alphas(channel, 0);
        pscc(channel, 64, zeros, zeros);
        check_alphas(channel, 0);
        SetPSFastTracking(channel, 1);
        CloseChannel(channel);
    }
    puts("PS fast tracking: default, live alphas, reset, restore, recreate passed");
    return 0;
}
