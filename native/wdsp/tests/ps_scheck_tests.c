// SPDX-License-Identifier: GPL-2.0-or-later
//
// Deterministic tests for the PureSignal fit-to-fit solution check
// (ps_scheck.c), the comparison calcc's scheck() runs on every new fit.
// Compiled standalone (no FFTW, no WDSP channel state).
//
// Mutants these kill:
//   - drop the weighting (magnitude tolerance SCHECK_TOL everywhere):
//     low_envelope_magnitude_jitter_is_accepted fails.
//   - weight every point, or move the 0.20 boundary up:
//     magnitude_deviation_at_or_above_boundary_is_rejected fails.
//   - weight the phase curves too: phase_tolerance_is_unchanged fails.
//   - drop the 0.04 floor (divide by x = 0 at k = 0):
//     tolerance_profile fails (inf / nan instead of 0.50).

#include <assert.h>
#include <math.h>
#include <stdio.h>

#include "ps_scheck.h"

static double m_prev[SCHECK_PTS], m_new[SCHECK_PTS];
static double c_prev[SCHECK_PTS], c_new[SCHECK_PTS];
static double s_prev[SCHECK_PTS], s_new[SCHECK_PTS];

static void reset_solutions (void) {
  for (int k = 0; k < SCHECK_PTS; k++) {
    // A plausible converged solution: gain falling towards 1 at full scale,
    // a small phase rotation.
    double x = ps_scheck_x (k);
    m_prev[k] = m_new[k] = 1.3 - 0.3 * x;
    c_prev[k] = c_new[k] = cos (0.1 * x);
    s_prev[k] = s_new[k] = sin (0.1 * x);
  }
}

static int deviates (void) {
  return ps_scheck_deviates (m_prev, m_new, c_prev, c_new, s_prev, s_new);
}

static void tolerance_profile (void) {
  // 0.50 at k = 0 and k = 1 (x below the 0.04 floor), falling as 1/x to
  // 0.10 at x >= 0.20, and exactly SCHECK_TOL from there on.
  assert (fabs (ps_scheck_mag_tol (ps_scheck_x (0)) - 0.50) < 1e-12);
  assert (fabs (ps_scheck_mag_tol (ps_scheck_x (1)) - 0.50) < 1e-12);
  assert (fabs (ps_scheck_mag_tol (0.10) - 0.20) < 1e-12);
  assert (ps_scheck_mag_tol (0.20) == SCHECK_TOL);
  for (int k = 0; k < SCHECK_PTS; k++) {
    double x = ps_scheck_x (k);
    double tol = ps_scheck_mag_tol (x);
    assert (isfinite (tol));
    if (x >= SCHECK_MAG_WEIGHT_X) assert (tol == SCHECK_TOL);
    else assert (tol > SCHECK_TOL && tol <= 0.50 + 1e-12);
  }
}

static void identical_solution_is_accepted (void) {
  reset_solutions ();
  assert (!deviates ());
}

static void low_envelope_magnitude_jitter_is_accepted (void) {
  // The on-air failure: the fitted gain at the bottom of the envelope
  // (k = 1..3, x = 0.026..0.077) moves between fits on a warm amplifier
  // with memory. 0.3 at x <= 0.051 and 0.2 at x = 0.077 are each worth
  // under 0.016 of output and must not reject the fit.
  reset_solutions ();
  for (int k = 0; k <= 2; k++) m_new[k] += 0.3;
  m_new[3] -= 0.2;
  assert (!deviates ());
}

static void magnitude_deviation_at_or_above_boundary_is_rejected (void) {
  for (int k = 0; k < SCHECK_PTS; k++) {
    double x = ps_scheck_x (k);
    if (x < SCHECK_MAG_WEIGHT_X) continue;
    reset_solutions ();
    m_new[k] += 0.3;
    assert (deviates ());
    // Just inside the unchanged 0.10 tolerance is still accepted.
    reset_solutions ();
    m_new[k] -= 0.099;
    assert (!deviates ());
  }
  // The first weighted point below the boundary still rejects 0.3: its
  // tolerance is 0.10 * 0.20 / x, about 0.11 at x = 0.18.
  reset_solutions ();
  m_new[7] += 0.3;
  assert (ps_scheck_x (7) < SCHECK_MAG_WEIGHT_X);
  assert (deviates ());
}

static void phase_tolerance_is_unchanged (void) {
  for (int k = 0; k < SCHECK_PTS; k++) {
    reset_solutions ();
    c_new[k] += 0.15;
    assert (deviates ());
    reset_solutions ();
    s_new[k] -= 0.15;
    assert (deviates ());
    reset_solutions ();
    c_new[k] += 0.099;
    s_new[k] -= 0.099;
    assert (!deviates ());
  }
}

int main (void) {
  tolerance_profile ();
  identical_solution_is_accepted ();
  low_envelope_magnitude_jitter_is_accepted ();
  magnitude_deviation_at_or_above_boundary_is_rejected ();
  phase_tolerance_is_unchanged ();
  puts ("PS scheck: weighted magnitude tolerance, unchanged phase tolerance passed");
  return 0;
}
