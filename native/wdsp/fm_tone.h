/*  fm_tone.h

Zeus extension (FM sub-audible signalling) -- not part of upstream WDSP.

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

// Zeus extension (FM tones): self-contained DSP helpers used by fmmod.c (TX
// DCS encode) and fmd.c (RX CTCSS / DCS decode and tone squelch). No WDSP
// channel state, no FFTW, no heap: every object is a plain struct, so the
// helpers compile standalone for native/wdsp/tests/fm_tests.c.
//
// DCS codeword (Golay (23,12), generator x^11+x^10+x^6+x^5+x^4+x^2+1 = 0xC75):
//   bits  0.. 8  the 9-bit code (octal 023 -> 0x013)
//   bits  9..11  the fixed "100" signature (bit 11 set: 0x800)
//   bits 12..22  11 parity bits = ((0x800 | code) * x^11) mod 0xC75
// Transmitted continuously, bit 0 first, NRZ at 134.4 bit/s; inverted
// polarity sends the one's complement. Logic 1 = positive deviation.
// Construction verified against published codewords -- see fm_tests.c.

#ifndef _fm_tone_h
#define _fm_tone_h

#define FMTONE_NCTCSS          49
#define FMTONE_NDCS            104
#define FMTONE_DCS_BAUD        134.4
#define FMTONE_DCS_MASK        0x7FFFFFu
#define FMTONE_CTCSS_WIN_S     0.36     // Goertzel window (s): resolves the 2.3 Hz pairs
#define FMTONE_MAX_WIN         2048     // ring capacity (decimated samples)
#define FMTONE_TARGET_RATE     2000.0   // nominal decimated detector rate (Hz)

extern const double fmtone_ctcss_tones[FMTONE_NCTCSS];
extern const int fmtone_dcs_codes[FMTONE_NDCS];   // octal written as decimal (23 = 023)

typedef struct _fmtone_bq
{
	double b0, b1, b2, a1, a2;
	double x1, x2, y1, y2;
} fmtone_bq;

// ---- DCS codeword helpers ----
extern int fmtone_dcs_code_to_bin (int code_octal_as_decimal);   // 23 -> 0x13, -1 if not octal 0..777
extern unsigned int fmtone_dcs_codeword (int code_bin);           // 23-bit codeword, normal polarity
extern int fmtone_dcs_is_standard (int code_bin);

// ---- TX: DCS NRZ encoder (low-passed, output in about -1..+1) ----
typedef struct _fmtone_dcsenc
{
	double rate;
	unsigned int word;
	int bit;
	double acc;
	double inc;
	fmtone_bq lp;
} fmtone_dcsenc;

extern void fmtone_dcsenc_init (fmtone_dcsenc* e, double rate);
extern void fmtone_dcsenc_set (fmtone_dcsenc* e, int code_octal_as_decimal, int inverted);
extern void fmtone_dcsenc_reset (fmtone_dcsenc* e);
extern double fmtone_dcsenc_next (fmtone_dcsenc* e);

// ---- RX: CTCSS / DCS detector + tone-squelch gate ----
typedef struct _fmtone_det
{
	double rate;                   // input (discriminator) rate
	int decim;
	double drate;                  // decimated rate
	int dcount;
	fmtone_bq aa[2];               // 4th-order Butterworth anti-alias / tone low-pass
	double dc;                     // slow DC tracker (tuning offset)
	double dc_alpha;
	// CTCSS (Goertzel bank over a ring of decimated samples)
	double coef[FMTONE_NCTCSS];
	double ring[FMTONE_MAX_WIN];
	int win;
	int hop;
	int wpos;
	int filled;
	int since_hop;
	int pend_idx;
	int pend_count;
	int lock_idx;
	int miss;
	// DCS (zero-crossing DPLL + integrate-and-dump slicer)
	double ph;
	double ph_inc;
	double integ;
	int prev_sign;
	unsigned int reg;
	int nbits;
	int n_code;                    // normal-polarity standard-code tracker
	int n_count;
	int n_since;
	int n_lock;                    // locked code_bin, -1 none
	int t_count;                   // target-word tracker
	int t_since;
	int t_lock;
	// squelch
	int mode;                      // 0 off, 1 CTCSS, 2 DCS
	double target_hz;
	int target_code_bin;
	int target_inv;
	unsigned int target_word;      // 0 = none
	double gain;
	double gain_step;
} fmtone_det;

extern void fmtone_det_init (fmtone_det* d, double rate);
extern void fmtone_det_setrate (fmtone_det* d, double rate);   // no-op when unchanged
extern void fmtone_det_reset (fmtone_det* d);
extern void fmtone_det_set_squelch (fmtone_det* d, int mode, double ctcss_hz, int dcs_code_octal_as_decimal, int dcs_inverted);
extern void fmtone_det_process (fmtone_det* d, double x);     // x: discriminator output, 1.0 = full deviation
extern int fmtone_det_open (const fmtone_det* d);
extern void fmtone_det_status (const fmtone_det* d, double* ctcss_hz, int* dcs_code, int* dcs_inverted, int* squelch_open);
// Ramp the gate (10 ms) and apply it to size interleaved complex samples.
extern void fmtone_det_gate (fmtone_det* d, double* buff, int size);

#endif
