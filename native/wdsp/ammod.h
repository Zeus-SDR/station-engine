/*  ammod.h

This file is part of a program that implements a Software-Defined Radio.

Copyright (C) 2013 Warren Pratt, NR0V

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

// Zeus extension (broadcast AM): mode 0 (AM / SAM) gains an optional
// 4th-order Butterworth high-pass on the modulating signal and an optional
// "broadcast" chain -- NRSC-style pre-emphasis, polarity invert, asymmetric
// modulation (positive peaks up to pos_limit, negative peaks held at
// -neg_limit so the carrier never pinches off), a clipper followed by an
// 8th-order Butterworth brickwall low-pass, and a PEP-preserving carrier
// (c = 1 / (1 + pos_limit)). Modes 1 (DSB) and 2 (SSB w/carrier) are
// unchanged.

#ifndef _ammod_h
#define _ammod_h

#define AMMOD_HP_STAGES 2
#define AMMOD_LP_STAGES 4

typedef struct _ammod_bq {
  int run;
  double b0, b1, b2, a1, a2;
  double x1, x2, y1, y2;
} ammod_bq;

typedef struct _ammod {
  int run;
  int mode;
  int size;
  double* in;
  double* out;
  double c_level;
  double a_level;
  double mult;
  // ---- Zeus broadcast-AM extension ----
  int rate;
  double hpf_hz;                        // 0 => high-pass off
  double lpf_hz;                        // 0 => brickwall low-pass off
  int bcast_run;
  double pos_limit;                     // positive modulation peak (1.25 => +125%)
  double neg_limit;                     // negative modulation limit (0.97 => -97%)
  int preemph;
  int invert;
  ammod_bq hp[AMMOD_HP_STAGES];
  ammod_bq lp[AMMOD_LP_STAGES];
  double pe_b0, pe_b1, pe_a1;           // first-order pre-emphasis shelf
  double pe_x1, pe_y1;
  CRITICAL_SECTION cs_peak;
  double peak_pos;                      // max m since last GetTXAAMModPeaks
  double peak_neg;                      // min m since last GetTXAAMModPeaks
} ammod, *AMMOD;

extern AMMOD create_ammod (int run, int mode, int size, double* in, double* out, double c_level, int rate);

extern void destroy_ammod (AMMOD a);

extern void flush_ammod (AMMOD a);

extern void xammod (AMMOD a);

extern void setBuffers_ammod (AMMOD a, double* in, double* out);

extern void setSamplerate_ammod (AMMOD a, int rate);

extern void setSize_ammod (AMMOD a, int size);

extern void setBroadcast_ammod (AMMOD a, int run, double pos_limit, double neg_limit, int preemph, int invert);

extern void setFilter_ammod (AMMOD a, double hpf_hz, double lpf_hz);

extern void getModPeaks_ammod (AMMOD a, double* pos_pct, double* neg_pct);

// TXA Properties

#if !defined(WDSP_AMMOD_CORE_TEST)
extern __declspec (dllexport) void SetTXAAMCarrierLevel (int channel, double c_level);

extern __declspec (dllexport) void SetTXAAMBroadcast (int channel, int run, double posLimit, double negLimit, int preemph, int invert);

extern __declspec (dllexport) void SetTXAAMFilter (int channel, double hpfHz, double lpfHz);

extern __declspec (dllexport) void GetTXAAMModPeaks (int channel, double* posPct, double* negPct);
#endif

#endif
