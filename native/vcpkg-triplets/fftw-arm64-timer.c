/* SPDX-License-Identifier: GPL-2.0-or-later */
/* Copyright (C) 2026 Douglas J. Cerrato (KB2UKA). */
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

unsigned long long zeus_fftw_counter(void)
{
    LARGE_INTEGER value;
    QueryPerformanceCounter(&value);
    return (unsigned long long)value.QuadPart;
}
