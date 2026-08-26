/*  osctrl.c

This file is part of a program that implements a Software-Defined Radio.

Copyright (C) 2014, 2017 Warren Pratt, NR0V

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

// This file is part of the implementation of the Overshoot Controller from
// "Controlled Envelope Single Sideband" by David L. Hershberger, W9GR, in
// the November/December 2014 issue of QEX.

#if defined(WDSP_OSCTRL_CORE_TEST)
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include "osctrl.h"
typedef double complex[2];
extern void *malloc0 (int size);
#define _aligned_free free
#else
#include "comm.h"
#endif

static int peak_window_osctrl (int rate, double bandwidth) {
  double target = (0.3 / bandwidth) * rate;
  int legacy;
  int lower = (int)floor (target);
  int upper;

  // Preserve Thetis's exact 3 kHz waveform geometry. The nearest-odd rule is
  // the WDSP 2.0 extension for the new 4 kHz profile, where round-then-up
  // would otherwise make 3 kHz and 4 kHz identical at 48 kHz.
  if (bandwidth == 3000.0) {
    legacy = (int)(target + 0.5);
    if ((legacy & 1) == 0) { legacy += 1; }
    return legacy < 3 ? 3 : legacy;
  }

  if ((lower & 1) == 0) { lower -= 1; }
  if (lower < 3) { lower = 3; }
  upper = lower + 2;

  return target - lower <= upper - target ? lower : upper;
}

void calc_osctrl (OSCTRL a) {
  a->pn = peak_window_osctrl (a->rate, a->bw);

  a->dl_len = a->pn >> 1;
  a->dl  = (double *) malloc0 (a->pn * sizeof (complex));
  a->dlenv = (double *) malloc0 (a->pn * sizeof (double));
  a->in_idx = 0;
  a->out_idx = a->in_idx + a->dl_len;
  a->max_env = 0.0;
  a->env_out = 0.0;
}

void decalc_osctrl (OSCTRL a) {
  _aligned_free (a->dlenv);
  _aligned_free (a->dl);
}

OSCTRL create_osctrl (
  int run,
  int size,
  double* inbuff,
  double* outbuff,
  int rate,
  double osgain ) {
  OSCTRL a = (OSCTRL) malloc0 (sizeof (osctrl));
  a->run = run;
  a->size = size;
  a->inbuff = inbuff;
  a->outbuff = outbuff;
  a->rate = rate;
  a->osgain = osgain;
  a->bw = 3000.0;
  calc_osctrl (a);
  return a;
}

void destroy_osctrl (OSCTRL a) {
  decalc_osctrl (a);
  _aligned_free (a);
}

void flush_osctrl (OSCTRL a) {
  memset (a->dl,    0, a->pn     * sizeof (complex));
  memset (a->dlenv, 0, a->pn     * sizeof (double));
  a->in_idx = 0;
  a->out_idx = a->dl_len;
  a->max_env = 0.0;
  a->env_out = 0.0;
}

void xosctrl (OSCTRL a) {
  if (a->run) {
    int i, j;
    double divisor, in_i, in_q, env;

    for (i = 0; i < a->size; i++) {
      in_i = a->inbuff[2 * i + 0];
      in_q = a->inbuff[2 * i + 1];

      // A non-finite sample must not poison the peak window indefinitely.
      if (!isfinite (in_i) || !isfinite (in_q)) {
        in_i = 0.0;
        in_q = 0.0;
      }

      env = hypot (in_i, in_q);

      if (!isfinite (env)) {
        in_i = 0.0;
        in_q = 0.0;
        env = 0.0;
      }

      a->dl[2 * a->in_idx + 0] = in_i;                              // put sample in delay line
      a->dl[2 * a->in_idx + 1] = in_q;
      a->env_out = a->dlenv[a->in_idx];                     // take env out of delay line
      a->dlenv[a->in_idx] = env;                                    // put env in delay line

      if (a->dlenv[a->in_idx]  >  a->max_env) { a->max_env = a->dlenv[a->in_idx]; }

      if (a->env_out >= a->max_env && a->env_out > 0.0) {           // run the buffer
        a->max_env = 0.0;

        for (j = 0; j < a->pn; j++)
          if (a->dlenv[j] > a->max_env) { a->max_env = a->dlenv[j]; }
      }

      if (a->max_env > 1.0) { divisor = 1.0 + a->osgain * (a->max_env - 1.0); }
      else { divisor = 1.0; }

      a->outbuff[2 * i + 0] = a->dl[2 * a->out_idx + 0] / divisor;        // output sample
      a->outbuff[2 * i + 1] = a->dl[2 * a->out_idx + 1] / divisor;

      if (--a->in_idx  < 0) { a->in_idx  += a->pn; }

      if (--a->out_idx < 0) { a->out_idx += a->pn; }
    }
  } else if (a->inbuff != a->outbuff) {
    memcpy (a->outbuff, a->inbuff, a->size * sizeof (complex));
  }
}

void setBuffers_osctrl (OSCTRL a, double* in, double* out) {
  a->inbuff = in;
  a->outbuff = out;
}

void setSamplerate_osctrl (OSCTRL a, int rate) {
  decalc_osctrl (a);
  a->rate = rate;
  calc_osctrl (a);
}

int setBandwidth_osctrl (OSCTRL a, double bandwidth) {
  int new_pn;
  double* new_dl;
  double* new_dlenv;

  if (!isfinite (bandwidth) || bandwidth <= 0.0) { return 0; }
  if (a->bw == bandwidth) { return 1; }

  new_pn = peak_window_osctrl (a->rate, bandwidth);

  // Allocate first so a transient allocation failure leaves the running
  // controller and its current bandwidth untouched.
  new_dl = (double *) malloc0 (new_pn * sizeof (complex));
  new_dlenv = (double *) malloc0 (new_pn * sizeof (double));

  if (new_dl == 0 || new_dlenv == 0) {
    if (new_dlenv != 0) { _aligned_free (new_dlenv); }
    if (new_dl != 0) { _aligned_free (new_dl); }
    return 0;
  }

  decalc_osctrl (a);
  a->bw = bandwidth;
  a->pn = new_pn;
  a->dl_len = new_pn >> 1;
  a->dl = new_dl;
  a->dlenv = new_dlenv;
  a->in_idx = 0;
  a->out_idx = a->dl_len;
  a->max_env = 0.0;
  a->env_out = 0.0;

  return 1;
}

void setSize_osctrl (OSCTRL a, int size) {
  a->size = size;
  flush_osctrl (a);
}

/********************************************************************************************************
*                                                   *
*                     TXA Properties                        *
*                                                   *
********************************************************************************************************/

#if !defined(WDSP_OSCTRL_CORE_TEST)
PORT
void SetTXAosctrlRun (int channel, int run) {
  if (txa[channel].osctrl.p->run != run) {
    EnterCriticalSection (&ch[channel].csDSP);

    if (run) {
      flush_osctrl (txa[channel].osctrl.p);
    }

    txa[channel].osctrl.p->run = run;
    TXASetupBPFilters (channel);
    LeaveCriticalSection (&ch[channel].csDSP);
  }
}

PORT
void SetTXAosctrlBandwidth (int channel, double bandwidth) {
  if (bandwidth == 3000.0 || bandwidth == 4000.0) {
    EnterCriticalSection (&ch[channel].csDSP);

    if (txa[channel].osctrl.p->bw != bandwidth) {
      setBandwidth_osctrl (txa[channel].osctrl.p, bandwidth);
    }

    LeaveCriticalSection (&ch[channel].csDSP);
  }
}
#endif
