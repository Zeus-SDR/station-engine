/*  ammod.h

This file is part of a program that implements a Software-Defined Radio.

Copyright (C) 2013, 2017 Warren Pratt, NR0V

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

The author can be reached by email at

warren@wpratt.com

*/

#if defined(WDSP_AMMOD_CORE_TEST)
#include <math.h>
#include <stdlib.h>
#include <string.h>
typedef int CRITICAL_SECTION;
#define InitializeCriticalSectionAndSpinCount(cs, n) ((void)(cs), (void)(n))
#define DeleteCriticalSection(cs) ((void)(cs))
#define EnterCriticalSection(cs) ((void)(cs))
#define LeaveCriticalSection(cs) ((void)(cs))
typedef double complex[2];
extern void *malloc0 (int size);
#define _aligned_free free
#include "ammod.h"
#else
#include "comm.h"
#endif

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

// NRSC-1 style pre-emphasis: zero at the 75 us time constant (~2122 Hz),
// pole at 8700 Hz so the boost shelves off (~+12 dB) instead of rising
// without bound into the brickwall low-pass.
#define AMMOD_PREEMPH_ZERO_HZ   2122.0
#define AMMOD_PREEMPH_POLE_HZ   8700.0

/********************************************************************************************************
*                                                   *
*                     Broadcast-AM helpers                  *
*                                                   *
********************************************************************************************************/

static void design_bq_ammod (ammod_bq* bq, int highpass, double fc, double q, int rate) {
  double w0 = 2.0 * M_PI * fc / (double)rate;
  double cw = cos (w0);
  double alpha = sin (w0) / (2.0 * q);
  double a0 = 1.0 + alpha;

  if (highpass) {
    bq->b0 = 0.5 * (1.0 + cw) / a0;
    bq->b1 = -(1.0 + cw) / a0;
    bq->b2 = 0.5 * (1.0 + cw) / a0;
  } else {
    bq->b0 = 0.5 * (1.0 - cw) / a0;
    bq->b1 = (1.0 - cw) / a0;
    bq->b2 = 0.5 * (1.0 - cw) / a0;
  }

  bq->a1 = -2.0 * cw / a0;
  bq->a2 = (1.0 - alpha) / a0;
}

// Butterworth section Q for stage k (0-based) of an order-n cascade.
static double butterworth_q_ammod (int n, int k) {
  double theta = (2.0 * (double)k + 1.0) * M_PI / (2.0 * (double)n);
  return 1.0 / (2.0 * cos (theta));
}

static void flush_bq_ammod (ammod_bq* bq, int count) {
  int i;

  for (i = 0; i < count; i++) {
    bq[i].x1 = bq[i].x2 = bq[i].y1 = bq[i].y2 = 0.0;
  }
}

static double run_bq_ammod (ammod_bq* bq, int count, double x) {
  int i;

  for (i = 0; i < count; i++) {
    ammod_bq* s = &bq[i];
    double y = s->b0 * x + s->b1 * s->x1 + s->b2 * s->x2 - s->a1 * s->y1 - s->a2 * s->y2;
    s->x2 = s->x1;
    s->x1 = x;
    s->y2 = s->y1;
    s->y1 = y;
    x = y;
  }

  return x;
}

static void calc_ammod (AMMOD a) {
  int i;
  double nyq_guard = 0.45 * (double)a->rate;
  int hp_run = a->rate > 0 && a->hpf_hz > 0.0 && a->hpf_hz < nyq_guard;
  int lp_run = a->rate > 0 && a->lpf_hz > 0.0 && a->lpf_hz < nyq_guard;

  for (i = 0; i < AMMOD_HP_STAGES; i++) {
    int was = a->hp[i].run;
    a->hp[i].run = hp_run;

    if (hp_run) {
      design_bq_ammod (&a->hp[i], 1, a->hpf_hz, butterworth_q_ammod (2 * AMMOD_HP_STAGES, i), a->rate);
    }

    if (!was && hp_run) {
      flush_bq_ammod (&a->hp[i], 1);
    }
  }

  for (i = 0; i < AMMOD_LP_STAGES; i++) {
    int was = a->lp[i].run;
    a->lp[i].run = lp_run;

    if (lp_run) {
      design_bq_ammod (&a->lp[i], 0, a->lpf_hz, butterworth_q_ammod (2 * AMMOD_LP_STAGES, i), a->rate);
    }

    if (!was && lp_run) {
      flush_bq_ammod (&a->lp[i], 1);
    }
  }

  // Pre-emphasis shelf via the bilinear transform with pre-warped corners:
  // H(s) = (1 + s/wz) / (1 + s/wp), unity gain at DC.
  if (a->rate > 0 && AMMOD_PREEMPH_POLE_HZ < nyq_guard) {
    double k = 2.0 * (double)a->rate;
    double wz = k * tan (M_PI * AMMOD_PREEMPH_ZERO_HZ / (double)a->rate);
    double wp = k * tan (M_PI * AMMOD_PREEMPH_POLE_HZ / (double)a->rate);
    double tz = k / wz;
    double tp = k / wp;
    a->pe_b0 = (1.0 + tz) / (1.0 + tp);
    a->pe_b1 = (1.0 - tz) / (1.0 + tp);
    a->pe_a1 = (1.0 - tp) / (1.0 + tp);
  } else {
    a->pe_b0 = 1.0;
    a->pe_b1 = 0.0;
    a->pe_a1 = 0.0;
  }
}

// NaN-safe: a NaN compares false against both bounds and maps to lo.
static double clamp_ammod (double x, double lo, double hi) {
  if (!(x >= lo)) return lo;
  if (!(x <= hi)) return hi;
  return x;
}

// Finite test without relying on C99 isfinite (inf - inf and NaN - NaN are NaN).
static int finite_ammod (double x) {
  return (x - x) == 0.0;
}

// A non-finite sample or filter state would feed back through the IIR states
// and poison AM for good: flush every filter and substitute silence (carrier
// only) for this sample.
static void recover_ammod (AMMOD a) {
  flush_bq_ammod (a->hp, AMMOD_HP_STAGES);
  flush_bq_ammod (a->lp, AMMOD_LP_STAGES);
  a->pe_x1 = a->pe_y1 = 0.0;
}

/********************************************************************************************************
*                                                   *
*                         AMMOD                         *
*                                                   *
********************************************************************************************************/

AMMOD create_ammod (int run, int mode, int size, double* in, double* out, double c_level, int rate) {
  AMMOD a = (AMMOD) malloc0 (sizeof (ammod));
  a->run = run;
  a->mode = mode;
  a->size = size;
  a->in = in;
  a->out = out;
  a->c_level = c_level;
  a->a_level = 1.0 - a->c_level;
  a->mult = 1.0 / sqrt (2.0);
  a->rate = rate;
  a->hpf_hz = 0.0;
  a->lpf_hz = 0.0;
  a->bcast_run = 0;
  a->pos_limit = 1.25;
  a->neg_limit = 0.97;
  a->preemph = 0;
  a->invert = 0;
  InitializeCriticalSectionAndSpinCount (&a->cs_peak, 2500);
  calc_ammod (a);
  flush_ammod (a);
  return a;
}

void destroy_ammod (AMMOD a) {
  DeleteCriticalSection (&a->cs_peak);
  _aligned_free (a);
}

void flush_ammod (AMMOD a) {
  flush_bq_ammod (a->hp, AMMOD_HP_STAGES);
  flush_bq_ammod (a->lp, AMMOD_LP_STAGES);
  a->pe_x1 = a->pe_y1 = 0.0;
  EnterCriticalSection (&a->cs_peak);
  a->peak_pos = 0.0;
  a->peak_neg = 0.0;
  LeaveCriticalSection (&a->cs_peak);
}

void xammod (AMMOD a) {
  if (a->run) {
    int i;

    switch (a->mode) {
    case 0: { // AM (also SAM)
      double blk_pos = 0.0;
      double blk_neg = 0.0;
      int hp_run = a->hp[0].run;

      if (a->bcast_run) {
        // Envelope = mult * c * (1 + m), c = 1 / (1 + pos_limit): the peak
        // envelope at m = +pos_limit equals the legacy peak (mult * 1.0), so PEP
        // is unchanged and the carrier drops automatically as pos_limit grows.
        double pos = a->pos_limit;
        double neg = -a->neg_limit;
        double env = a->mult / (1.0 + pos);
        int lp_run = a->lp[0].run;

        for (i = 0; i < a->size; i++) {
          double x = a->in[2 * i + 0];
          double m;

          if (!finite_ammod (x)) { recover_ammod (a); x = 0.0; }

          if (hp_run) x = run_bq_ammod (a->hp, AMMOD_HP_STAGES, x);

          if (a->preemph) {
            double y = a->pe_b0 * x + a->pe_b1 * a->pe_x1 - a->pe_a1 * a->pe_y1;
            a->pe_x1 = x;
            a->pe_y1 = y;
            x = y;
          }

          if (!finite_ammod (x)) { recover_ammod (a); x = 0.0; }

          if (a->invert) x = -x;

          m = clamp_ammod (x * pos, neg, pos);         // clipper

          if (lp_run) m = run_bq_ammod (a->lp, AMMOD_LP_STAGES, m);

          if (!finite_ammod (m)) { recover_ammod (a); m = 0.0; }

          m = clamp_ammod (m, neg, pos);               // overshoot safety

          if (m > blk_pos) blk_pos = m;
          if (m < blk_neg) blk_neg = m;
          a->out[2 * i + 0] = a->out[2 * i + 1] = env * (1.0 + m);
        }
      } else {
        double inv_c = a->c_level > 0.0 ? a->a_level / a->c_level : 0.0;

        for (i = 0; i < a->size; i++) {
          double x = a->in[2 * i + 0];
          double m;

          if (!finite_ammod (x)) { recover_ammod (a); x = 0.0; }

          if (hp_run) x = run_bq_ammod (a->hp, AMMOD_HP_STAGES, x);

          if (!finite_ammod (x)) { recover_ammod (a); x = 0.0; }

          m = x * inv_c;
          if (m > blk_pos) blk_pos = m;
          if (m < blk_neg) blk_neg = m;
          a->out[2 * i + 0] = a->out[2 * i + 1] = a->mult * (a->c_level + a->a_level * x);
        }
      }

      EnterCriticalSection (&a->cs_peak);
      if (blk_pos > a->peak_pos) a->peak_pos = blk_pos;
      if (blk_neg < a->peak_neg) a->peak_neg = blk_neg;
      LeaveCriticalSection (&a->cs_peak);
      break;
    }

    case 1: // DSB
      for (i = 0; i < a->size; i++) {
        a->out[2 * i + 0] = a->out[2 * i + 1] = a->mult * a->in[2 * i + 0];
      }

      break;

    case 2: // SSB w/Carrier
      for (i = 0; i < a->size; i++) {
        a->out[2 * i + 0] = a->mult * a->c_level + a->a_level * a->in[2 * i + 0];
        a->out[2 * i + 1] = a->mult * a->c_level + a->a_level * a->in[2 * i + 1];
      }

      break;
    }
  } else if (a->in != a->out) {
    memcpy (a->out, a->in, a->size * sizeof (complex));
  }
}

void setBuffers_ammod (AMMOD a, double* in, double* out) {
  a->in = in;
  a->out = out;
}

void setSamplerate_ammod (AMMOD a, int rate) {
  a->rate = rate;
  calc_ammod (a);
  flush_ammod (a);
}

void setSize_ammod (AMMOD a, int size) {
  a->size = size;
}

void setBroadcast_ammod (AMMOD a, int run, double pos_limit, double neg_limit, int preemph, int invert) {
  int restart = (run && !a->bcast_run) || (preemph && !a->preemph);

  if (!(pos_limit == pos_limit)) pos_limit = 1.25;   // NaN guard
  if (!(neg_limit == neg_limit)) neg_limit = 0.97;

  a->pos_limit = clamp_ammod (pos_limit, 1.0, 1.5);
  a->neg_limit = clamp_ammod (neg_limit, 0.8, 1.0);
  a->bcast_run = run ? 1 : 0;
  a->preemph = preemph ? 1 : 0;
  a->invert = invert ? 1 : 0;

  if (restart) {
    flush_bq_ammod (a->lp, AMMOD_LP_STAGES);
    a->pe_x1 = a->pe_y1 = 0.0;
  }
}

void setFilter_ammod (AMMOD a, double hpf_hz, double lpf_hz) {
  if (!(hpf_hz == hpf_hz) || hpf_hz < 0.0) hpf_hz = 0.0;
  if (!(lpf_hz == lpf_hz) || lpf_hz < 0.0) lpf_hz = 0.0;
  a->hpf_hz = hpf_hz;
  a->lpf_hz = lpf_hz;
  calc_ammod (a);
}

void getModPeaks_ammod (AMMOD a, double* pos_pct, double* neg_pct) {
  EnterCriticalSection (&a->cs_peak);
  *pos_pct = 100.0 * a->peak_pos;
  *neg_pct = -100.0 * a->peak_neg;
  a->peak_pos = 0.0;
  a->peak_neg = 0.0;
  LeaveCriticalSection (&a->cs_peak);
}

/********************************************************************************************************
*                                                   *
*                     TXA Properties                        *
*                                                   *
********************************************************************************************************/

#if !defined(WDSP_AMMOD_CORE_TEST)
PORT void
SetTXAAMCarrierLevel (int channel, double c_level) {
  EnterCriticalSection (&ch[channel].csDSP);
  txa[channel].ammod.p->c_level = c_level;
  txa[channel].ammod.p->a_level = 1.0 - c_level;
  LeaveCriticalSection (&ch[channel].csDSP);
}

PORT void
SetTXAAMBroadcast (int channel, int run, double posLimit, double negLimit, int preemph, int invert) {
  EnterCriticalSection (&ch[channel].csDSP);
  setBroadcast_ammod (txa[channel].ammod.p, run, posLimit, negLimit, preemph, invert);
  LeaveCriticalSection (&ch[channel].csDSP);
}

PORT void
SetTXAAMFilter (int channel, double hpfHz, double lpfHz) {
  EnterCriticalSection (&ch[channel].csDSP);
  setFilter_ammod (txa[channel].ammod.p, hpfHz, lpfHz);
  LeaveCriticalSection (&ch[channel].csDSP);
}

// Modulation peaks (percent) since the previous call, then reset. posPct is
// the largest positive modulation index; negPct is the magnitude of the most
// negative one (97 => -97%). Only AM / SAM (ammod mode 0) accumulates.
PORT void
GetTXAAMModPeaks (int channel, double* posPct, double* negPct) {
  getModPeaks_ammod (txa[channel].ammod.p, posPct, negPct);
}
#endif
