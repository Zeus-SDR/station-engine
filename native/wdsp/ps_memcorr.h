/*  ps_memcorr.h

Zeus extension (PureSignal memory correction) -- not part of upstream WDSP.

This file is part of Zeus's vendored copy of WDSP, a program that implements
a Software-Defined Radio. WDSP is Copyright (C) Warren Pratt, NR0V.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

*/

// Zeus extension: the estimator behind the opt-in PureSignal memory term.
//
// calcc fits a memoryless predistorter: gain and phase as functions of the
// envelope. An amplifier with memory (gain that lags the envelope, for
// example from supply or thermal time constants) leaves an IMD3 component
// that differs between the two sides of a two-tone and that no memoryless
// curve can cancel. iqc can add one memory term,
//
//     out += in * beta * D(in),   D(v) = d|v|^2/dt normalised to a 1 kHz beat,
//
// and this file learns the complex coefficient beta from the live feedback.
// For every feedback sample X (normalised amplifier output) and T (normalised
// amplifier input) it regresses the residual of calcc's current memoryless
// inverse, e = T - ym(|X|) * X * (yc(|X|) + j ys(|X|)), on three regressors:
//
//     phi1 = X * D(X)          the memory term (its coefficient is beta)
//     phi2 = dT/dt             timing skew between the TX reference and the
//                              feedback stream, normalised like D
//     phi3 = T                 a residual gain/phase trim
//
// Solving all three jointly keeps beta independent of the amp-delay setting
// and of any residual gain error; only beta is used. The sums are the 3x3
// Hermitian normal equations, solved once per window with partial pivoting.
//
// No WDSP channel state, no FFTW, no heap and no complex.h (the library is
// also built with MSVC), so the helper compiles standalone for
// native/wdsp/tests/ps_memcorr_tests.c.

#ifndef _ps_memcorr_h
#define _ps_memcorr_h

#define PS_MEMCORR_NREG          3
// Feedback envelope range that feeds the estimator. Below the floor the
// curves are extrapolated (no fit data) and D is noise-dominated; above
// full scale the amplifier is past the calibrated range.
#define PS_MEMCORR_X_MIN         0.05
#define PS_MEMCORR_X_MAX         1.0
// Solve once per this much feedback (0.5 s: 96000 samples at 192 kHz).
#define PS_MEMCORR_WINDOW_SEC    0.5
// Learning rate per window. Measured on the air: 0.3 fights calcc's fast
// tracking and resets calibration repeatedly; 0.08 settles in ~10 s.
#define PS_MEMCORR_RATE          0.08
// Ceiling on |beta|, so a runaway estimate can only add a bounded term.
#define PS_MEMCORR_BETA_MAX      0.02
// D and dT/dt are derivatives per radian of a 1 kHz beat, so beta does not
// depend on the sample rate of the stream it is applied to.
#define PS_MEMCORR_NORM_HZ       1000.0
// Numerical near-singularity: pivot below this fraction of the largest
// diagonal term of the normal equations.
#define PS_MEMCORR_PIVOT_REL     1.0e-9
// A window whose envelope barely moves (a steady carrier, TUN) gives the
// memory regressor only noise, which correlates with the noise in the
// residual and would bias beta. Require the standard deviation of |X|^2 to
// be at least this fraction of its mean. A two-tone sits near 0.7, voice
// near 1.
#define PS_MEMCORR_MIN_SPREAD    0.05

typedef struct _ps_memcorr_acc {
  // Upper triangle of sum(conj(phi_r) * phi_c) and sum(conj(phi_r) * e).
  double g_re[PS_MEMCORR_NREG][PS_MEMCORR_NREG];
  double g_im[PS_MEMCORR_NREG][PS_MEMCORR_NREG];
  double r_re[PS_MEMCORR_NREG];
  double r_im[PS_MEMCORR_NREG];
  double sum_x2;      // sum(|X|^2) and sum(|X|^4): envelope spread guard
  double sum_x4;
  long   n;           // accepted samples in the window
  // Stream continuity: previous |X|^2 and T, kept across skipped samples
  // and window boundaries so every derivative spans exactly one sample.
  double prev_x2;
  double prev_t_re;
  double prev_t_im;
  int    primed;
} ps_memcorr_acc;

// Derivative scale for a stream at `rate` Hz: rate / (2 * pi * NORM_HZ).
extern double ps_memcorr_dscale (double rate);

// Accepted samples per solve window at `rate` Hz.
extern long ps_memcorr_window (double rate);

// Clears the window sums. The continuity state is kept.
extern void ps_memcorr_clear_sums (ps_memcorr_acc* a);

// Clears everything, including the continuity state (MOX drop, reset).
extern void ps_memcorr_reset (ps_memcorr_acc* a);

// Advances the continuity state by one sample (always) and returns 1 when the
// sample may be accumulated: the stream is primed and x = |X| lies in
// [X_MIN, X_MAX]. On 1, *x, *d (D(X)) and *dt_re/*dt_im (dT/dt) are set.
extern int ps_memcorr_advance (ps_memcorr_acc* a, double dscale,
                               double x_re, double x_im, double t_re, double t_im,
                               double* x, double* d, double* dt_re, double* dt_im);

// Accumulates one accepted sample. ym, yc, ys are calcc's current averaged
// magnitude, cosine and sine corrections at |X|.
extern void ps_memcorr_accumulate (ps_memcorr_acc* a,
                                   double x_re, double x_im, double t_re, double t_im,
                                   double d, double dt_re, double dt_im,
                                   double ym, double yc, double ys);

// Solves the window's normal equations. Returns 1 and the three coefficients
// (c[0] is beta-hat) when the window has envelope spread and the system is
// not near-singular; returns 0 otherwise and leaves c untouched.
extern int ps_memcorr_solve (const ps_memcorr_acc* a,
                             double c_re[PS_MEMCORR_NREG], double c_im[PS_MEMCORR_NREG]);

// beta += RATE * (hat - beta), then |beta| is clamped to BETA_MAX.
extern void ps_memcorr_learn (double* beta_re, double* beta_im, double hat_re, double hat_im);

#endif
