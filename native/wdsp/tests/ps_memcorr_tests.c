// SPDX-License-Identifier: GPL-2.0-or-later
//
// Deterministic tests for the PureSignal memory-correction estimator
// (ps_memcorr.c): stream continuity, the joint least-squares solve, the
// excitation and singularity guards, and the learning-rate / clamp rule.
// Compiled standalone (no FFTW, no WDSP channel state).
//
// Mutants these kill:
//   - drop a regressor or mis-conjugate an accumulation term:
//     joint_solve_recovers_memory_skew_and_trim fails.
//   - solve only for beta (no skew / trim regressors): same test fails,
//     because the skew leaks into beta.
//   - reset continuity on a skipped sample, or accumulate the unprimed first
//     sample: skipped_samples_keep_continuity fails.
//   - remove the envelope-spread guard: steady_carrier_is_rejected fails.
//   - remove the pivot check: degenerate_system_is_rejected fails (nan out).
//   - drop the clamp or change the rate: learn_rate_and_clamp fails.

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

#include "ps_memcorr.h"

#define RATE   192000.0
#define TWOPI_ 6.2831853071795864

typedef struct { double re, im; } cplx;

static cplx cmul (cplx a, cplx b) { cplx r = { a.re * b.re - a.im * b.im, a.re * b.im + a.im * b.re }; return r; }
static cplx cdiv (cplx a, cplx b) {
  double m = b.re * b.re + b.im * b.im;
  cplx r = { (a.re * b.re + a.im * b.im) / m, (a.im * b.re - a.re * b.im) / m };
  return r;
}

static cplx two_tone (long n, double amp) {
  double t = (double)n / RATE;
  cplx r = { amp * (cos (TWOPI_ * 700.0 * t) + cos (TWOPI_ * 1900.0 * t)),
             amp * (sin (TWOPI_ * 700.0 * t) + sin (TWOPI_ * 1900.0 * t)) };
  return r;
}

static void feed (ps_memcorr_acc* acc, cplx x, cplx t) {
  double mag, d, dtr, dti;
  if (ps_memcorr_advance (acc, ps_memcorr_dscale (RATE), x.re, x.im, t.re, t.im, &mag, &d, &dtr, &dti))
    // Identity curves, so the residual is e = T - X.
    ps_memcorr_accumulate (acc, x.re, x.im, t.re, t.im, d, dtr, dti, 1.0, 1.0, 0.0);
}

static void scales (void) {
  assert (ps_memcorr_window (RATE) == 96000);
  assert (ps_memcorr_window (48000.0) == 24000);
  assert (fabs (ps_memcorr_dscale (RATE) - RATE / (TWOPI_ * 1000.0)) < 1e-12);
}

static void joint_solve_recovers_memory_skew_and_trim (void) {
  // Build T so that, exactly, T - X = c1 * X * D(X) + c2 * dT/dt + c3 * T,
  // i.e. (1 - c3 - c2 * k) T[n] = X[n] (1 + c1 D[n]) - c2 * k * T[n-1].
  // The two-tone envelope crosses zero, so samples below X_MIN are skipped
  // throughout and continuity must hold across them.
  const cplx c1 = { -0.008, 0.003 };   // memory
  const cplx c2 = { -0.0063, 0.0011 }; // ~1 us of stream skew
  const cplx c3 = { 0.02, -0.01 };     // gain / phase trim
  const double k = ps_memcorr_dscale (RATE);
  const cplx den = { 1.0 - c3.re - c2.re * k, -c3.im - c2.im * k };
  ps_memcorr_acc acc;
  cplx t_prev = { 0.0, 0.0 };
  double x2_prev = 0.0;
  double c_re[PS_MEMCORR_NREG], c_im[PS_MEMCORR_NREG];
  long skipped_low = 0;
  ps_memcorr_reset (&acc);
  for (long n = 0; n < 96000; n++) {
    cplx x = two_tone (n, 0.4);
    double x2 = x.re * x.re + x.im * x.im;
    double d = (x2 - x2_prev) * k;
    cplx one_plus = { 1.0 + c1.re * d, c1.im * d };
    cplx rhs = cmul (x, one_plus);
    cplx carry = cmul (c2, t_prev);
    cplx t;
    rhs.re -= carry.re * k;
    rhs.im -= carry.im * k;
    t = cdiv (rhs, den);
    if (sqrt (x2) < PS_MEMCORR_X_MIN) skipped_low++;
    feed (&acc, x, t);
    t_prev = t;
    x2_prev = x2;
  }
  assert (skipped_low > 0);
  // Everything but the unprimed first sample and the low-envelope skips.
  assert (acc.n == 96000 - 1 - skipped_low);
  assert (ps_memcorr_solve (&acc, c_re, c_im));
  assert (fabs (c_re[0] - c1.re) < 1e-9 && fabs (c_im[0] - c1.im) < 1e-9);
  assert (fabs (c_re[1] - c2.re) < 1e-9 && fabs (c_im[1] - c2.im) < 1e-9);
  assert (fabs (c_re[2] - c3.re) < 1e-9 && fabs (c_im[2] - c3.im) < 1e-9);
}

static void skipped_samples_keep_continuity (void) {
  ps_memcorr_acc acc;
  double x, d, dtr, dti;
  const double k = ps_memcorr_dscale (RATE);
  ps_memcorr_reset (&acc);
  // First sample after a reset has no predecessor: never accepted.
  assert (!ps_memcorr_advance (&acc, k, 0.5, 0.0, 0.5, 0.0, &x, &d, &dtr, &dti));
  // Below the floor and above full scale: skipped, but each one still
  // becomes the predecessor of the next sample.
  assert (!ps_memcorr_advance (&acc, k, 0.01, 0.0, 0.2, 0.0, &x, &d, &dtr, &dti));
  assert (!ps_memcorr_advance (&acc, k, 1.5, 0.0, 0.3, 0.0, &x, &d, &dtr, &dti));
  assert (ps_memcorr_advance (&acc, k, 0.0, 0.6, 0.1, 0.4, &x, &d, &dtr, &dti));
  assert (fabs (x - 0.6) < 1e-15);
  assert (fabs (d - (0.36 - 2.25) * k) < 1e-9);
  assert (fabs (dtr - (0.1 - 0.3) * k) < 1e-12);
  assert (fabs (dti - 0.4 * k) < 1e-12);
  // Clearing the window sums keeps continuity; a reset does not.
  ps_memcorr_accumulate (&acc, 0.0, 0.6, 0.1, 0.4, d, dtr, dti, 1.0, 1.0, 0.0);
  ps_memcorr_clear_sums (&acc);
  assert (acc.n == 0 && acc.g_re[0][0] == 0.0 && acc.r_re[0] == 0.0 && acc.sum_x2 == 0.0);
  assert (ps_memcorr_advance (&acc, k, 0.0, 0.5, 0.1, 0.4, &x, &d, &dtr, &dti));
  assert (fabs (d - (0.25 - 0.36) * k) < 1e-9);
  ps_memcorr_reset (&acc);
  assert (!ps_memcorr_advance (&acc, k, 0.0, 0.5, 0.1, 0.4, &x, &d, &dtr, &dti));
}

static void steady_carrier_is_rejected (void) {
  // TUN: constant envelope plus feedback noise. The memory regressor is
  // pure noise, so the window must not produce an estimate.
  ps_memcorr_acc acc;
  double c_re[PS_MEMCORR_NREG] = { 7.0, 7.0, 7.0 }, c_im[PS_MEMCORR_NREG] = { 7.0, 7.0, 7.0 };
  unsigned int seed = 12345u;
  ps_memcorr_reset (&acc);
  for (long n = 0; n < 96000; n++) {
    double ph = TWOPI_ * 1000.0 * (double)n / RATE;
    double noise;
    cplx x, t;
    seed = seed * 1103515245u + 12345u;
    noise = 3e-4 * ((double)((seed >> 8) & 0xffff) / 32768.0 - 1.0);
    x.re = (0.9 + noise) * cos (ph); x.im = (0.9 + noise) * sin (ph);
    t.re = 0.95 * cos (ph);          t.im = 0.95 * sin (ph);
    feed (&acc, x, t);
  }
  assert (acc.n > 0);
  assert (!ps_memcorr_solve (&acc, c_re, c_im));
  assert (c_re[0] == 7.0 && c_im[0] == 7.0);  // untouched on failure
}

static void degenerate_system_is_rejected (void) {
  ps_memcorr_acc acc;
  double c_re[PS_MEMCORR_NREG], c_im[PS_MEMCORR_NREG];
  ps_memcorr_reset (&acc);
  assert (!ps_memcorr_solve (&acc, c_re, c_im));   // empty window
  // Plenty of envelope spread but no TX reference: phi2 = phi3 = 0.
  for (long n = 0; n < 9600; n++) {
    cplx zero = { 0.0, 0.0 };
    feed (&acc, two_tone (n, 0.4), zero);
  }
  assert (acc.n > 0);
  assert (!ps_memcorr_solve (&acc, c_re, c_im));
}

static void learn_rate_and_clamp (void) {
  double br = 0.0, bi = 0.0;
  ps_memcorr_learn (&br, &bi, 0.01, -0.005);
  assert (fabs (br - 0.08 * 0.01) < 1e-15 && fabs (bi + 0.08 * 0.005) < 1e-15);
  ps_memcorr_learn (&br, &bi, 0.01, -0.005);
  assert (fabs (br - (0.0008 + 0.08 * (0.01 - 0.0008))) < 1e-15);
  // A runaway estimate is held to |beta| = BETA_MAX, direction kept.
  for (int i = 0; i < 50; i++) {
    ps_memcorr_learn (&br, &bi, 1.0, 1.0);
    assert (sqrt (br * br + bi * bi) <= PS_MEMCORR_BETA_MAX * (1.0 + 1e-12));
  }
  assert (fabs (sqrt (br * br + bi * bi) - PS_MEMCORR_BETA_MAX) < 1e-12);
  assert (fabs (br - bi) < 1e-12);
}

int main (void) {
  scales ();
  joint_solve_recovers_memory_skew_and_trim ();
  skipped_samples_keep_continuity ();
  steady_carrier_is_rejected ();
  degenerate_system_is_rejected ();
  learn_rate_and_clamp ();
  puts ("PS memory correction: continuity, joint solve, guards, learn/clamp passed");
  return 0;
}
