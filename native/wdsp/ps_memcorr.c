/*  ps_memcorr.c

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

#include <math.h>
#include <string.h>

#include "ps_memcorr.h"

#define PS_MEMCORR_TWOPI 6.2831853071795864

double ps_memcorr_dscale (double rate) {
  return rate / (PS_MEMCORR_TWOPI * PS_MEMCORR_NORM_HZ);
}

long ps_memcorr_window (double rate) {
  long n = (long)(rate * PS_MEMCORR_WINDOW_SEC);
  return n > 0 ? n : 1;
}

void ps_memcorr_clear_sums (ps_memcorr_acc* a) {
  memset (a->g_re, 0, sizeof (a->g_re));
  memset (a->g_im, 0, sizeof (a->g_im));
  memset (a->r_re, 0, sizeof (a->r_re));
  memset (a->r_im, 0, sizeof (a->r_im));
  a->sum_x2 = 0.0;
  a->sum_x4 = 0.0;
  a->n = 0;
}

void ps_memcorr_reset (ps_memcorr_acc* a) {
  ps_memcorr_clear_sums (a);
  a->prev_x2 = 0.0;
  a->prev_t_re = 0.0;
  a->prev_t_im = 0.0;
  a->primed = 0;
}

int ps_memcorr_advance (ps_memcorr_acc* a, double dscale,
                        double x_re, double x_im, double t_re, double t_im,
                        double* x, double* d, double* dt_re, double* dt_im) {
  double x2 = x_re * x_re + x_im * x_im;
  double dx2 = x2 - a->prev_x2;
  double dtr = t_re - a->prev_t_re;
  double dti = t_im - a->prev_t_im;
  int primed = a->primed;
  double mag;
  a->prev_x2 = x2;
  a->prev_t_re = t_re;
  a->prev_t_im = t_im;
  a->primed = 1;
  // The first sample after a reset has no predecessor.
  if (!primed) { return 0; }
  mag = sqrt (x2);
  if (mag < PS_MEMCORR_X_MIN || mag > PS_MEMCORR_X_MAX) { return 0; }
  *x = mag;
  *d = dx2 * dscale;
  *dt_re = dtr * dscale;
  *dt_im = dti * dscale;
  return 1;
}

void ps_memcorr_accumulate (ps_memcorr_acc* a,
                            double x_re, double x_im, double t_re, double t_im,
                            double d, double dt_re, double dt_im,
                            double ym, double yc, double ys) {
  double p_re[PS_MEMCORR_NREG], p_im[PS_MEMCORR_NREG];
  // e = T - ym * X * (yc + j ys)
  double e_re = t_re - ym * (x_re * yc - x_im * ys);
  double e_im = t_im - ym * (x_re * ys + x_im * yc);
  double x2 = x_re * x_re + x_im * x_im;
  int r, c;
  p_re[0] = x_re * d;  p_im[0] = x_im * d;
  p_re[1] = dt_re;     p_im[1] = dt_im;
  p_re[2] = t_re;      p_im[2] = t_im;
  for (r = 0; r < PS_MEMCORR_NREG; r++) {
    // conj(phi_r) * z = (pr*zr + pi*zi) + j (pr*zi - pi*zr)
    for (c = r; c < PS_MEMCORR_NREG; c++) {
      a->g_re[r][c] += p_re[r] * p_re[c] + p_im[r] * p_im[c];
      a->g_im[r][c] += p_re[r] * p_im[c] - p_im[r] * p_re[c];
    }
    a->r_re[r] += p_re[r] * e_re + p_im[r] * e_im;
    a->r_im[r] += p_re[r] * e_im - p_im[r] * e_re;
  }
  a->sum_x2 += x2;
  a->sum_x4 += x2 * x2;
  a->n++;
}

static double cabs2 (double re, double im) { return re * re + im * im; }

int ps_memcorr_solve (const ps_memcorr_acc* a,
                      double c_re[PS_MEMCORR_NREG], double c_im[PS_MEMCORR_NREG]) {
  enum { N = PS_MEMCORR_NREG, W = PS_MEMCORR_NREG + 1 };
  double m_re[PS_MEMCORR_NREG][PS_MEMCORR_NREG + 1];
  double m_im[PS_MEMCORR_NREG][PS_MEMCORR_NREG + 1];
  double mean, var, scale = 0.0, tol2;
  int r, c, k;

  if (a->n <= 0) { return 0; }
  mean = a->sum_x2 / (double)a->n;
  var = a->sum_x4 / (double)a->n - mean * mean;
  if (mean <= 0.0 || var < (PS_MEMCORR_MIN_SPREAD * mean) * (PS_MEMCORR_MIN_SPREAD * mean)) { return 0; }

  // Expand the Hermitian upper triangle and append the right-hand side.
  for (r = 0; r < N; r++) {
    for (c = 0; c < N; c++) {
      if (c >= r) { m_re[r][c] = a->g_re[r][c]; m_im[r][c] = a->g_im[r][c]; }
      else        { m_re[r][c] = a->g_re[c][r]; m_im[r][c] = -a->g_im[c][r]; }
    }
    m_re[r][N] = a->r_re[r];
    m_im[r][N] = a->r_im[r];
    if (a->g_re[r][r] > scale) { scale = a->g_re[r][r]; }
  }
  if (scale <= 0.0) { return 0; }
  tol2 = (PS_MEMCORR_PIVOT_REL * scale) * (PS_MEMCORR_PIVOT_REL * scale);

  // Gauss-Jordan elimination with partial pivoting.
  for (k = 0; k < N; k++) {
    int piv = k;
    double pr, pi, pm;
    for (r = k + 1; r < N; r++) {
      if (cabs2 (m_re[r][k], m_im[r][k]) > cabs2 (m_re[piv][k], m_im[piv][k])) { piv = r; }
    }
    if (cabs2 (m_re[piv][k], m_im[piv][k]) < tol2) { return 0; }
    if (piv != k) {
      for (c = 0; c < W; c++) {
        double tr = m_re[k][c], ti = m_im[k][c];
        m_re[k][c] = m_re[piv][c]; m_im[k][c] = m_im[piv][c];
        m_re[piv][c] = tr;         m_im[piv][c] = ti;
      }
    }
    pr = m_re[k][k]; pi = m_im[k][k];
    pm = cabs2 (pr, pi);
    for (r = 0; r < N; r++) {
      double fr, fi;
      if (r == k) { continue; }
      // f = m[r][k] / m[k][k]
      fr = (m_re[r][k] * pr + m_im[r][k] * pi) / pm;
      fi = (m_im[r][k] * pr - m_re[r][k] * pi) / pm;
      for (c = k; c < W; c++) {
        double sr = fr * m_re[k][c] - fi * m_im[k][c];
        double si = fr * m_im[k][c] + fi * m_re[k][c];
        m_re[r][c] -= sr;
        m_im[r][c] -= si;
      }
    }
  }
  for (k = 0; k < N; k++) {
    double pr = m_re[k][k], pi = m_im[k][k], pm = cabs2 (pr, pi);
    c_re[k] = (m_re[k][N] * pr + m_im[k][N] * pi) / pm;
    c_im[k] = (m_im[k][N] * pr - m_re[k][N] * pi) / pm;
  }
  return 1;
}

void ps_memcorr_learn (double* beta_re, double* beta_im, double hat_re, double hat_im) {
  double mag;
  *beta_re += PS_MEMCORR_RATE * (hat_re - *beta_re);
  *beta_im += PS_MEMCORR_RATE * (hat_im - *beta_im);
  mag = sqrt (*beta_re * *beta_re + *beta_im * *beta_im);
  if (mag > PS_MEMCORR_BETA_MAX) {
    *beta_re *= PS_MEMCORR_BETA_MAX / mag;
    *beta_im *= PS_MEMCORR_BETA_MAX / mag;
  }
}
