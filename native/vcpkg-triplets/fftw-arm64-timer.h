/* SPDX-License-Identifier: GPL-2.0-or-later */
/* Copyright (C) 2026 Douglas J. Cerrato (KB2UKA). */
#ifndef ZEUS_FFTW_ARM64_TIMER_H
#define ZEUS_FFTW_ARM64_TIMER_H

#if defined(_WIN32) && defined(_M_ARM64)
/* FFTW's cycle.h has no MSVC ARM64 counter. Supply the Windows monotonic
   performance counter without importing conflicting Windows SDK types. */
typedef unsigned long long ticks;
#ifdef __cplusplus
extern "C" {
#endif
ticks zeus_fftw_counter(void);
#ifdef __cplusplus
}
#endif
static __inline ticks getticks(void)
{
    return zeus_fftw_counter();
}
static __inline double elapsed(ticks end, ticks start)
{
    return (double)(end - start);
}
#define HAVE_TICK_COUNTER 1
#endif
#endif
