/* SPDX-License-Identifier: GPL-2.0-or-later */
/* Copyright (C) 2026 Douglas J. Cerrato (KB2UKA). */
#include <fftw3.h>
#include <math.h>
#include <stdio.h>

#ifdef TEST_FFTW_FLOAT
#define FFT(name) fftwf_##name
typedef float sample;
#else
#define FFT(name) fftw_##name
typedef double sample;
#endif

int main(void)
{
    const int sizes[] = { 64, 1024 };
    int measured = 0;
    FFT(forget_wisdom)();
    for (unsigned i = 0; i < sizeof(sizes) / sizeof(sizes[0]); ++i) {
        const int n = sizes[i];
        FFT(complex)* input = FFT(alloc_complex)(n);
        FFT(complex)* output = FFT(alloc_complex)(n);
        if (!input || !output) return 1;
        FFT(plan) forward = FFT(plan_dft_1d)(n, input, output, FFTW_FORWARD, FFTW_PATIENT);
        FFT(plan) backward = FFT(plan_dft_1d)(n, output, input, FFTW_BACKWARD, FFTW_PATIENT);
        if (!forward || !backward) return 1;
        double cost = FFT(cost)(forward);
        double estimate = FFT(estimate_cost)(forward);
        printf("fftw.planning size=%d cost=%.9g estimate=%.9g\n", n, cost, estimate);
        if (cost > 0 && isfinite(cost) && cost != estimate) measured = 1;
        for (int j = 0; j < n; ++j) {
            input[j][0] = (sample)(j == 1 ? 1 : 0);
            input[j][1] = 0;
        }
        FFT(execute)(forward);
        FFT(execute)(backward);
        for (int j = 0; j < n; ++j) {
            double real = input[j][0] / n;
            double imaginary = input[j][1] / n;
            if (!isfinite(real) || !isfinite(imaginary)
                || fabs(real - (j == 1 ? 1 : 0)) > 0.00001
                || fabs(imaginary) > 0.00001) return 1;
        }
        FFT(destroy_plan)(forward);
        FFT(destroy_plan)(backward);
        FFT(free)(input);
        FFT(free)(output);
    }
    if (!measured) {
        fprintf(stderr, "FFTW PATIENT planning used only estimated costs; timing is unavailable.\n");
        return 1;
    }
    return 0;
}
