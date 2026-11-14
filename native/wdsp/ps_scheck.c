/*  ps_scheck.c

Zeus extension (PureSignal solution check tolerance) -- not part of upstream WDSP.

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

#include <math.h>

#include "ps_scheck.h"

double ps_scheck_x (int k) {
  return (double)k / (double)(SCHECK_PTS - 1);
}

double ps_scheck_mag_tol (double x) {
  if (x >= SCHECK_MAG_WEIGHT_X) { return SCHECK_TOL; }
  if (x < SCHECK_MAG_WEIGHT_XMIN) { x = SCHECK_MAG_WEIGHT_XMIN; }
  return SCHECK_TOL * SCHECK_MAG_WEIGHT_X / x;
}

int ps_scheck_deviates (const double* m_prev, const double* m_new,
                        const double* c_prev, const double* c_new,
                        const double* s_prev, const double* s_new) {
  int k;
  for (k = 0; k < SCHECK_PTS; k++) {
    if (fabs (m_new[k] - m_prev[k]) > ps_scheck_mag_tol (ps_scheck_x (k))) { return 1; }
    if (fabs (c_new[k] - c_prev[k]) > SCHECK_TOL) { return 1; }
    if (fabs (s_new[k] - s_prev[k]) > SCHECK_TOL) { return 1; }
  }
  return 0;
}
