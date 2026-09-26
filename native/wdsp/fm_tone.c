/*  fm_tone.c

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

// Zeus extension (FM tones). See fm_tone.h for the DCS codeword layout.
//
// DCS references (codeword construction and bit order):
//   * Golay (23,12) layout "P11..P1-100-C9..C1", DCS 023 =
//     11101100011-100-000/010/011, sent in reverse (C1 first), inverted =
//     one's complement: https://www.kb8zqz.org/onfreq_mirror/syntorx/dcs.html
//     (mirrored at https://wiki.w9cr.net/index.php/DPL/DCS_Information)
//   * Full 23-bit codewords (bit-reversed, per ETSI TS 103 236) in sdrtrunk:
//     https://github.com/DSheirer/sdrtrunk/blob/master/src/main/java/io/github/dsheirer/module/decode/squelch/dcs/DCSCode.java
//     (N023 = 6557239, N025 = 5508971, N411 = 4737911, N754 = 1822594)
//   * Encoder structure (code + 0x800, parity in bits 12..22, inverted = XOR
//     0x7FFFFF): https://github.com/DualTachyon/uv-k5-firmware/blob/main/dcs.c
// The generator polynomial 0xC75 reproduces every one of those codewords
// (pinned in native/wdsp/tests/fm_tests.c).

#include <math.h>
#include <string.h>
#include "fm_tone.h"

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#define FMTONE_GOLAY_POLY     0xC75u
#define FMTONE_CTCSS_MIN_AMP  0.008   // 40 Hz at 5 kHz deviation
#define FMTONE_CTCSS_RATIO    3.0     // best bin power / runner-up power
#define FMTONE_CTCSS_FRAC     0.20    // tone power / total low-passed power
#define FMTONE_DPLL_GAIN      0.2
#define FMTONE_DCS_RELEASE    (2 * 23 + 2)
#define FMTONE_RAMP_S         0.010

const double fmtone_ctcss_tones[FMTONE_NCTCSS] =
{
	 67.0,  69.3,  71.9,  74.4,  77.0,  79.7,  82.5,  85.4,  88.5,  91.5,  94.8,  97.4,
	100.0, 103.5, 107.2, 110.9, 114.8, 118.8, 123.0, 127.3, 131.8, 136.5,
	141.3, 146.2, 151.4, 156.7, 159.8, 162.2, 165.5, 167.9, 171.3, 173.8,
	177.3, 179.9, 183.5, 186.2, 189.9, 192.8, 199.5, 203.5, 206.5, 210.7,
	218.1, 225.7, 229.1, 233.6, 241.8, 250.3, 254.1
};

const int fmtone_dcs_codes[FMTONE_NDCS] =
{
	 23,  25,  26,  31,  32,  36,  43,  47,  51,  53,  54,  65,  71,  72,  73,  74,
	114, 115, 116, 122, 125, 131, 132, 134, 143, 145, 152, 155, 156, 162, 165, 172, 174,
	205, 212, 223, 225, 226, 243, 244, 245, 246, 251, 252, 255, 261, 263, 265, 266, 271, 274,
	306, 311, 315, 325, 331, 332, 343, 346, 351, 356, 364, 365, 371,
	411, 412, 413, 423, 431, 432, 445, 446, 452, 454, 455, 462, 464, 465, 466,
	503, 506, 516, 523, 526, 532, 546, 565,
	606, 612, 624, 627, 631, 632, 654, 662, 664,
	703, 712, 723, 731, 732, 734, 743, 754
};

/********************************************************************************************************
*																										*
*											Biquad helpers												*
*																										*
********************************************************************************************************/

static void fmtone_bq_lowpass (fmtone_bq* bq, double fc, double q, double rate)
{
	double w0 = 2.0 * M_PI * fc / rate;
	double cw = cos (w0);
	double alpha = sin (w0) / (2.0 * q);
	double a0 = 1.0 + alpha;
	bq->b0 = 0.5 * (1.0 - cw) / a0;
	bq->b1 = (1.0 - cw) / a0;
	bq->b2 = 0.5 * (1.0 - cw) / a0;
	bq->a1 = -2.0 * cw / a0;
	bq->a2 = (1.0 - alpha) / a0;
	bq->x1 = bq->x2 = bq->y1 = bq->y2 = 0.0;
}

static double fmtone_bq_run (fmtone_bq* s, double x)
{
	double y = s->b0 * x + s->b1 * s->x1 + s->b2 * s->x2 - s->a1 * s->y1 - s->a2 * s->y2;
	s->x2 = s->x1;
	s->x1 = x;
	s->y2 = s->y1;
	s->y1 = y;
	return y;
}

/********************************************************************************************************
*																										*
*											DCS codewords												*
*																										*
********************************************************************************************************/

int fmtone_dcs_code_to_bin (int code)
{
	int d0, d1, d2;
	if (code < 0 || code > 777) return -1;
	d0 = code % 10;
	d1 = (code / 10) % 10;
	d2 = code / 100;
	if (d0 > 7 || d1 > 7 || d2 > 7) return -1;
	return (d2 << 6) | (d1 << 3) | d0;
}

static int fmtone_dcs_bin_to_code (int bin)
{
	return ((bin >> 6) & 7) * 100 + ((bin >> 3) & 7) * 10 + (bin & 7);
}

unsigned int fmtone_dcs_codeword (int code_bin)
{
	unsigned int info = 0x800u | ((unsigned int)code_bin & 0x1FFu);
	unsigned int rem = info << 11;
	int i;
	for (i = 22; i >= 11; i--)
	{
		if (rem & (1u << i))
			rem ^= FMTONE_GOLAY_POLY << (i - 11);
	}
	return ((rem & 0x7FFu) << 12) | info;
}

int fmtone_dcs_is_standard (int code_bin)
{
	int i;
	for (i = 0; i < FMTONE_NDCS; i++)
		if (fmtone_dcs_code_to_bin (fmtone_dcs_codes[i]) == code_bin) return 1;
	return 0;
}

/********************************************************************************************************
*																										*
*											TX: DCS encoder												*
*																										*
********************************************************************************************************/

void fmtone_dcsenc_init (fmtone_dcsenc* e, double rate)
{
	memset (e, 0, sizeof (fmtone_dcsenc));
	e->rate = rate;
	e->inc = FMTONE_DCS_BAUD / rate;
	e->word = fmtone_dcs_codeword (fmtone_dcs_code_to_bin (23));
	fmtone_dcsenc_reset (e);
}

void fmtone_dcsenc_reset (fmtone_dcsenc* e)
{
	e->bit = 0;
	e->acc = 0.0;
	// 2nd-order Butterworth at 300 Hz shapes the NRZ edges.
	fmtone_bq_lowpass (&e->lp, 300.0, 0.70710678118654752, e->rate);
}

void fmtone_dcsenc_set (fmtone_dcsenc* e, int code, int inverted)
{
	int bin = fmtone_dcs_code_to_bin (code);
	unsigned int w;
	if (bin < 0) bin = fmtone_dcs_code_to_bin (23);
	w = fmtone_dcs_codeword (bin);
	if (inverted) w = ~w & FMTONE_DCS_MASK;
	if (w != e->word)
	{
		e->word = w;
		e->bit = 0;
		e->acc = 0.0;
	}
}

double fmtone_dcsenc_next (fmtone_dcsenc* e)
{
	double nrz = ((e->word >> e->bit) & 1u) ? 1.0 : -1.0;
	e->acc += e->inc;
	if (e->acc >= 1.0)
	{
		e->acc -= 1.0;
		if (++e->bit >= 23) e->bit = 0;
	}
	return fmtone_bq_run (&e->lp, nrz);
}

/********************************************************************************************************
*																										*
*										RX: detector and gate											*
*																										*
********************************************************************************************************/

static void fmtone_det_calc (fmtone_det* d)
{
	int k;
	d->decim = (int)floor (d->rate / FMTONE_TARGET_RATE);
	if (d->decim < 1) d->decim = 1;
	d->drate = d->rate / (double)d->decim;
	d->win = (int)(FMTONE_CTCSS_WIN_S * d->drate + 0.5);
	if (d->win > FMTONE_MAX_WIN) d->win = FMTONE_MAX_WIN;
	if (d->win < 8) d->win = 8;
	d->hop = d->win / 2;
	for (k = 0; k < FMTONE_NCTCSS; k++)
		d->coef[k] = 2.0 * cos (2.0 * M_PI * fmtone_ctcss_tones[k] / d->drate);
	d->dc_alpha = 1.0 - exp (-1.0 / (0.5 * d->drate));
	d->ph_inc = FMTONE_DCS_BAUD / d->drate;
	d->gain_step = 1.0 / (FMTONE_RAMP_S * d->rate);
	fmtone_det_reset (d);
}

void fmtone_det_init (fmtone_det* d, double rate)
{
	memset (d, 0, sizeof (fmtone_det));
	d->rate = rate > 0.0 ? rate : 48000.0;
	d->mode = 0;
	d->target_code_bin = -1;
	fmtone_det_calc (d);
}

void fmtone_det_setrate (fmtone_det* d, double rate)
{
	if (rate <= 0.0 || rate == d->rate) return;
	d->rate = rate;
	fmtone_det_calc (d);
}

void fmtone_det_reset (fmtone_det* d)
{
	// 4th-order Butterworth low-pass at 300 Hz: anti-alias for the decimator
	// and removes voice above the sub-audible band.
	fmtone_bq_lowpass (&d->aa[0], 300.0, 0.54119610014619698, d->rate);
	fmtone_bq_lowpass (&d->aa[1], 300.0, 1.30656296487637653, d->rate);
	d->dcount = 0;
	d->dc = 0.0;
	memset (d->ring, 0, sizeof (d->ring));
	d->wpos = 0;
	d->filled = 0;
	d->since_hop = 0;
	d->pend_idx = -1;
	d->pend_count = 0;
	d->lock_idx = -1;
	d->miss = 0;
	d->ph = 0.0;
	d->integ = 0.0;
	d->prev_sign = 0;
	d->reg = 0;
	d->nbits = 0;
	d->n_code = -1;
	d->n_count = 0;
	d->n_since = 1000;
	d->n_lock = -1;
	d->t_count = 0;
	d->t_since = 1000;
	d->t_lock = 0;
	d->gain = fmtone_det_open (d) ? 1.0 : 0.0;
}

void fmtone_det_set_squelch (fmtone_det* d, int mode, double ctcss_hz, int dcs_code, int dcs_inverted)
{
	int bin = fmtone_dcs_code_to_bin (dcs_code);
	unsigned int w = 0;
	if (mode < 0 || mode > 2) mode = 0;
	if (bin >= 0)
	{
		w = fmtone_dcs_codeword (bin);
		if (dcs_inverted) w = ~w & FMTONE_DCS_MASK;
	}
	if (w != d->target_word)
	{
		d->t_count = 0;
		d->t_since = 1000;
		d->t_lock = 0;
	}
	d->mode = mode;
	d->target_hz = ctcss_hz;
	d->target_code_bin = bin;
	d->target_inv = dcs_inverted ? 1 : 0;
	d->target_word = w;
}

static void fmtone_ctcss_eval (fmtone_det* d)
{
	int k, n, idx;
	int best = -1;
	double pbest = 0.0, psecond = 0.0, energy = 0.0, amp, frac;
	for (n = 0; n < d->win; n++)
	{
		double x = d->ring[n];
		energy += x * x;
	}
	for (k = 0; k < FMTONE_NCTCSS; k++)
	{
		double s1 = 0.0, s2 = 0.0, s0, p;
		double c = d->coef[k];
		idx = d->wpos;                     // oldest sample
		for (n = 0; n < d->win; n++)
		{
			s0 = d->ring[idx] + c * s1 - s2;
			s2 = s1;
			s1 = s0;
			if (++idx >= d->win) idx = 0;
		}
		p = s1 * s1 + s2 * s2 - c * s1 * s2;
		if (p > pbest)
		{
			psecond = pbest;
			pbest = p;
			best = k;
		}
		else if (p > psecond)
			psecond = p;
	}
	amp = 2.0 * sqrt (pbest) / (double)d->win;
	frac = energy > 0.0 ? (2.0 * pbest / (double)d->win) / energy : 0.0;
	if (best >= 0 && (amp < FMTONE_CTCSS_MIN_AMP || pbest < FMTONE_CTCSS_RATIO * psecond || frac < FMTONE_CTCSS_FRAC))
		best = -1;

	if (best == d->pend_idx)
		d->pend_count++;
	else
	{
		d->pend_idx = best;
		d->pend_count = 1;
	}
	if (d->lock_idx >= 0)
	{
		if (best == d->lock_idx)
			d->miss = 0;
		else if (++d->miss >= 2)
			d->lock_idx = -1;
	}
	if (d->lock_idx < 0 && best >= 0 && d->pend_count >= 2)
	{
		d->lock_idx = best;
		d->miss = 0;
	}
}

static void fmtone_dcs_bit (fmtone_det* d, int bit)
{
	unsigned int r;
	int code = -1;
	d->reg = (d->reg >> 1) | ((unsigned int)(bit ? 1 : 0) << 22);
	if (d->nbits < 23) d->nbits++;
	if (d->n_since < 1000) d->n_since++;
	if (d->t_since < 1000) d->t_since++;
	if (d->nbits < 23) return;
	r = d->reg;
	if (((r >> 9) & 7u) == 4u && fmtone_dcs_codeword ((int)(r & 0x1FFu)) == r && fmtone_dcs_is_standard ((int)(r & 0x1FFu)))
		code = (int)(r & 0x1FFu);
	if (code >= 0)
	{
		if (code == d->n_code && d->n_since == 23)
			d->n_count++;
		else
			d->n_count = 1;
		d->n_code = code;
		d->n_since = 0;
		if (d->n_count >= 2) d->n_lock = code;
	}
	if (d->n_lock >= 0 && d->n_since > FMTONE_DCS_RELEASE)
	{
		d->n_lock = -1;
		d->n_count = 0;
	}
	if (d->target_word != 0 && r == d->target_word)
	{
		if (d->t_since == 23)
			d->t_count++;
		else
			d->t_count = 1;
		d->t_since = 0;
		if (d->t_count >= 2) d->t_lock = 1;
	}
	if (d->t_lock && d->t_since > FMTONE_DCS_RELEASE)
	{
		d->t_lock = 0;
		d->t_count = 0;
	}
}

static void fmtone_det_decimated (fmtone_det* d, double x)
{
	int sign;
	// slow DC tracker (0.5 s) removes the tuning offset without drooping DCS runs
	d->dc += d->dc_alpha * (x - d->dc);
	x -= d->dc;
	// CTCSS ring + hop scheduling
	d->ring[d->wpos] = x;
	if (++d->wpos >= d->win) d->wpos = 0;
	if (d->filled < d->win) d->filled++;
	if (++d->since_hop >= d->hop && d->filled >= d->win)
	{
		d->since_hop = 0;
		fmtone_ctcss_eval (d);
	}
	// DCS: integrate-and-dump with a zero-crossing DPLL
	d->ph += d->ph_inc;
	if (d->ph >= 1.0)
	{
		d->ph -= 1.0;
		fmtone_dcs_bit (d, d->integ > 0.0);
		d->integ = 0.0;
	}
	d->integ += x;
	sign = x > 0.0 ? 1 : -1;
	if (d->prev_sign != 0 && sign != d->prev_sign)
	{
		double e = d->ph < 0.5 ? d->ph : d->ph - 1.0;
		d->ph -= FMTONE_DPLL_GAIN * e;
	}
	d->prev_sign = sign;
}

void fmtone_det_process (fmtone_det* d, double x)
{
	x = fmtone_bq_run (&d->aa[0], x);
	x = fmtone_bq_run (&d->aa[1], x);
	if (++d->dcount >= d->decim)
	{
		d->dcount = 0;
		fmtone_det_decimated (d, x);
	}
}

int fmtone_det_open (const fmtone_det* d)
{
	switch (d->mode)
	{
	case 1:
		return d->lock_idx >= 0 && fabs (fmtone_ctcss_tones[d->lock_idx] - d->target_hz) < 0.5;
	case 2:
		return d->t_lock;
	default:
		return 1;
	}
}

void fmtone_det_status (const fmtone_det* d, double* ctcss_hz, int* dcs_code, int* dcs_inverted, int* squelch_open)
{
	*ctcss_hz = d->lock_idx >= 0 ? fmtone_ctcss_tones[d->lock_idx] : 0.0;
	// An inverted-polarity stream is bit-identical to a normal-polarity
	// stream of another standard code (023 inverted == 047 normal), so a
	// free-running scan reports the normal reading; the configured target's
	// own code and polarity are reported when that target is locked.
	if (d->t_lock && d->target_code_bin >= 0)
	{
		*dcs_code = fmtone_dcs_bin_to_code (d->target_code_bin);
		*dcs_inverted = d->target_inv;
	}
	else if (d->n_lock >= 0)
	{
		*dcs_code = fmtone_dcs_bin_to_code (d->n_lock);
		*dcs_inverted = 0;
	}
	else
	{
		*dcs_code = 0;
		*dcs_inverted = 0;
	}
	*squelch_open = fmtone_det_open (d);
}

void fmtone_det_gate (fmtone_det* d, double* buff, int size)
{
	int i;
	double target = fmtone_det_open (d) ? 1.0 : 0.0;
	if (d->gain == target)
	{
		if (target == 0.0)
			memset (buff, 0, (size_t)size * 2 * sizeof (double));
		return;
	}
	for (i = 0; i < size; i++)
	{
		if (d->gain < target)
		{
			d->gain += d->gain_step;
			if (d->gain > target) d->gain = target;
		}
		else if (d->gain > target)
		{
			d->gain -= d->gain_step;
			if (d->gain < target) d->gain = target;
		}
		buff[2 * i + 0] *= d->gain;
		buff[2 * i + 1] *= d->gain;
	}
}
