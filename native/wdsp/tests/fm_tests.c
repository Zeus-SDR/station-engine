// SPDX-License-Identifier: GPL-2.0-or-later
//
// Deterministic tests for the Zeus FM tone extension (fm_tone.c): DCS
// codeword construction against published vectors, DCS encode -> decode
// loopback, CTCSS Goertzel-bank discrimination of the closest tone pairs,
// false-lock immunity and the tone-squelch gate. Compiled standalone (no
// FFTW, no WDSP channel state).

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "fm_tone.h"

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#define RATE 48000.0
#define TONE_LEVEL (0.10 / 1.10)   /* WDSP ctcss_level 0.10 after tscale, 1.0 = full deviation */

/* ---------------------------------------------------------------------- */
/* deterministic noise                                                     */
/* ---------------------------------------------------------------------- */

static unsigned long long rng_state = 0x9E3779B97F4A7C15ull;

static double urand (void)
{
	rng_state ^= rng_state << 13;
	rng_state ^= rng_state >> 7;
	rng_state ^= rng_state << 17;
	return (double)(rng_state >> 11) / 9007199254740992.0;
}

static double grand (void)
{
	double u1 = urand (), u2 = urand ();
	if (u1 < 1e-300) u1 = 1e-300;
	return sqrt (-2.0 * log (u1)) * cos (2.0 * M_PI * u2);
}

typedef struct { double b0, b1, b2, a1, a2, x1, x2, y1, y2; } bq;

static void bq_design (bq* s, int hp, double fc, double q, double rate)
{
	double w0 = 2.0 * M_PI * fc / rate, cw = cos (w0), al = sin (w0) / (2.0 * q), a0 = 1.0 + al;
	if (hp) { s->b0 = 0.5 * (1 + cw) / a0; s->b1 = -(1 + cw) / a0; s->b2 = s->b0; }
	else    { s->b0 = 0.5 * (1 - cw) / a0; s->b1 = (1 - cw) / a0;  s->b2 = s->b0; }
	s->a1 = -2.0 * cw / a0;
	s->a2 = (1.0 - al) / a0;
	s->x1 = s->x2 = s->y1 = s->y2 = 0.0;
}

static double bq_run (bq* s, double x)
{
	double y = s->b0 * x + s->b1 * s->x1 + s->b2 * s->x2 - s->a1 * s->y1 - s->a2 * s->y2;
	s->x2 = s->x1; s->x1 = x; s->y2 = s->y1; s->y1 = y;
	return y;
}

/* Voice-like programme: noise band-limited 300..3000 Hz (4th-order HP as a
   transmitter's voice high-pass), syllabic 4 Hz envelope, ~0.3 rms (30 % of
   deviation) -- plus a gliding 110..190 Hz pitch component at 3 % so the
   sub-audible band is not empty. */
typedef struct { bq hp1, hp2, lp; double t; double scale; } voice;

static void voice_init (voice* v, double rate)
{
	bq_design (&v->hp1, 1, 300.0, 0.5412, rate);
	bq_design (&v->hp2, 1, 300.0, 1.3066, rate);
	bq_design (&v->lp, 0, 3000.0, 0.7071, rate);
	v->t = 0.0;
	v->scale = 1.0;
}

static double voice_next (voice* v, double rate)
{
	double x = bq_run (&v->lp, bq_run (&v->hp2, bq_run (&v->hp1, grand ())));
	double env = 0.55 + 0.45 * sin (2.0 * M_PI * 4.0 * v->t);
	double pitch = 150.0 + 40.0 * sin (2.0 * M_PI * 0.7 * v->t);
	double y = v->scale * (0.9 * env * x + 0.03 * sin (2.0 * M_PI * pitch * v->t));
	v->t += 1.0 / rate;
	return y;
}

/* ---------------------------------------------------------------------- */
/* 1. DCS codewords vs published data                                      */
/* ---------------------------------------------------------------------- */

static unsigned int rev23 (unsigned int v)
{
	unsigned int r = 0;
	int i;
	for (i = 0; i < 23; i++)
		if (v & (1u << i)) r |= 1u << (22 - i);
	return r;
}

static unsigned int bits_from_string (const char* s)
{
	unsigned int v = 0;
	for (; *s; s++)
		if (*s == '0' || *s == '1') v = (v << 1) | (unsigned int)(*s - '0');
	return v;
}

/* Independent reference: the UV-K5 firmware's encoder (DCS_CalculateGolay,
   https://github.com/DualTachyon/uv-k5-firmware/blob/main/dcs.c). */
static unsigned int uvk5_golay (unsigned int codeword)
{
	unsigned int word = codeword;
	int i;
	for (i = 0; i < 12; i++)
	{
		word <<= 1;
		if (word & 0x1000u) word ^= 0x08EAu;
	}
	return codeword | ((word & 0x0FFEu) << 11);
}

static void test_dcs_codewords (void)
{
	int i;
	/* kb8zqz / W9CR: DCS 023 = 11101100011-100-000/010/011 (MSB = P11),
	   inverted = 00010011100-011-111/101/100. */
	assert (fmtone_dcs_codeword (fmtone_dcs_code_to_bin (23)) == bits_from_string ("11101100011-100-000/010/011"));
	assert ((~fmtone_dcs_codeword (fmtone_dcs_code_to_bin (23)) & FMTONE_DCS_MASK) == bits_from_string ("00010011100-011-111/101/100"));
	/* sdrtrunk DCSCode (ETSI TS 103 236), values are the transmitted-order
	   (bit-reversed) integers. */
	assert (rev23 (fmtone_dcs_codeword (fmtone_dcs_code_to_bin (23))) == 6557239u);
	assert (rev23 (fmtone_dcs_codeword (fmtone_dcs_code_to_bin (25))) == 5508971u);
	assert (rev23 (fmtone_dcs_codeword (fmtone_dcs_code_to_bin (411))) == 4737911u);
	assert (rev23 (fmtone_dcs_codeword (fmtone_dcs_code_to_bin (754))) == 1822594u);
	/* explicit hex pins */
	assert (fmtone_dcs_codeword (0x013) == 0x763813u);
	assert (fmtone_dcs_codeword (0x015) == 0x6B7815u);
	assert (fmtone_dcs_codeword (0x109) == 0x776909u);
	assert (fmtone_dcs_codeword (0x1EC) == 0x20F9ECu);
	/* all 104 standard codes agree with the independent UV-K5 encoder */
	for (i = 0; i < FMTONE_NDCS; i++)
	{
		int bin = fmtone_dcs_code_to_bin (fmtone_dcs_codes[i]);
		assert (bin >= 0);
		assert (fmtone_dcs_is_standard (bin));
		assert (fmtone_dcs_codeword (bin) == uvk5_golay ((unsigned int)bin + 0x800u));
	}
	assert (fmtone_dcs_code_to_bin (28) == -1);
	assert (fmtone_dcs_code_to_bin (800) == -1);
	assert (!fmtone_dcs_is_standard (fmtone_dcs_code_to_bin (340)));
	printf ("  dcs codewords: 4 published vectors + 104 codes vs reference encoder OK\n");
}

/* ---------------------------------------------------------------------- */
/* signal runner                                                           */
/* ---------------------------------------------------------------------- */

typedef struct
{
	double rate;
	double seconds;
	double ctcss_hz;          /* 0 = none */
	double ctcss_amp;
	int dcs_code;             /* 0 = none */
	int dcs_inv;
	double dcs_amp;
	int with_voice;
	double noise_rms;         /* white discriminator noise */
	double start_s;           /* tone/DCS begins at start_s */
} sig_cfg;

typedef struct
{
	double lock_s;            /* first time the expected readout appeared, -1 never */
	double ctcss_hz;          /* final readout */
	int dcs_code;
	int dcs_inv;
	int open;
	int ever_ctcss;           /* any CTCSS lock at all */
	int ever_dcs;
	int ever_wrong;           /* a readout other than the expected one */
} sig_res;

static sig_res run_signal (fmtone_det* d, const sig_cfg* c, double want_ctcss, int want_dcs, int want_inv)
{
	sig_res r;
	voice v;
	fmtone_dcsenc enc;
	long n, total = (long)(c->seconds * c->rate);
	double ph = 0.0;
	memset (&r, 0, sizeof (r));
	r.lock_s = -1.0;
	voice_init (&v, c->rate);
	fmtone_dcsenc_init (&enc, c->rate);
	if (c->dcs_code) fmtone_dcsenc_set (&enc, c->dcs_code, c->dcs_inv);
	for (n = 0; n < total; n++)
	{
		double t = (double)n / c->rate, x = 0.0;
		double hz; int code, inv, open;
		if (t >= c->start_s)
		{
			if (c->ctcss_hz > 0.0)
			{
				x += c->ctcss_amp * cos (ph);
				ph += 2.0 * M_PI * c->ctcss_hz / c->rate;
				if (ph > 2.0 * M_PI) ph -= 2.0 * M_PI;
			}
			if (c->dcs_code) x += c->dcs_amp * fmtone_dcsenc_next (&enc);
		}
		if (c->with_voice) x += voice_next (&v, c->rate);
		if (c->noise_rms > 0.0) x += c->noise_rms * grand ();
		fmtone_det_process (d, x);
		if ((n & 63) == 0)
		{
			fmtone_det_status (d, &hz, &code, &inv, &open);
			if (hz != 0.0) r.ever_ctcss = 1;
			if (code != 0) r.ever_dcs = 1;
			if ((hz != 0.0 && fabs (hz - want_ctcss) > 0.01) || (code != 0 && (code != want_dcs || inv != want_inv)))
				r.ever_wrong = 1;
			if (r.lock_s < 0.0 && ((want_ctcss > 0.0 && fabs (hz - want_ctcss) < 0.01) || (want_dcs && code == want_dcs && inv == want_inv)))
				r.lock_s = t - c->start_s;
		}
	}
	fmtone_det_status (d, &r.ctcss_hz, &r.dcs_code, &r.dcs_inv, &r.open);
	return r;
}

static sig_cfg cfg_default (void)
{
	sig_cfg c;
	memset (&c, 0, sizeof (c));
	c.rate = RATE;
	c.seconds = 2.0;
	c.ctcss_amp = TONE_LEVEL;
	c.dcs_amp = TONE_LEVEL;
	return c;
}

/* ---------------------------------------------------------------------- */
/* 2. CTCSS                                                                */
/* ---------------------------------------------------------------------- */

static double worst_ctcss_lock = 0.0;

static void expect_ctcss (double tone, double not_tone, int with_voice, double rate)
{
	fmtone_det d;
	sig_cfg c = cfg_default ();
	sig_res r;
	c.rate = rate;
	c.ctcss_hz = tone;
	c.with_voice = with_voice;
	fmtone_det_init (&d, rate);
	r = run_signal (&d, &c, tone, 0, 0);
	if (fabs (r.ctcss_hz - tone) > 0.01 || r.ever_wrong || r.lock_s < 0.0 || r.lock_s >= 0.6)
	{
		printf ("  FAIL ctcss %.1f (voice=%d rate=%.0f): got %.1f lock=%.3f wrong=%d\n", tone, with_voice, rate, r.ctcss_hz, r.lock_s, r.ever_wrong);
		assert (0);
	}
	assert (fabs (r.ctcss_hz - not_tone) > 0.01);
	if (r.lock_s > worst_ctcss_lock) worst_ctcss_lock = r.lock_s;
}

static void test_ctcss_pairs (void)
{
	int k;
	expect_ctcss (67.0, 69.3, 1, RATE);
	expect_ctcss (69.3, 67.0, 1, RATE);
	expect_ctcss (159.8, 162.2, 1, RATE);
	expect_ctcss (162.2, 159.8, 1, RATE);
	expect_ctcss (254.1, 250.3, 1, RATE);
	for (k = 0; k < FMTONE_NCTCSS; k++)
	{
		double t = fmtone_ctcss_tones[k];
		double other = fmtone_ctcss_tones[k == 0 ? 1 : k - 1];
		expect_ctcss (t, other, 1, RATE);
	}
	/* other WDSP RX rates */
	expect_ctcss (67.0, 69.3, 1, 96000.0);
	expect_ctcss (162.2, 159.8, 1, 192000.0);
	expect_ctcss (100.0, 97.4, 1, 44100.0);
	printf ("  ctcss: 49 tones + close pairs with voice, 44.1/48/96/192 kHz OK (worst lock %.3f s)\n", worst_ctcss_lock);
}

static void test_ctcss_low_level (void)
{
	fmtone_det d;
	sig_cfg c = cfg_default ();
	sig_res r;
	c.ctcss_hz = 88.5;
	c.ctcss_amp = 0.02 / 1.02;          /* minimum Zeus CTCSS level, 2 % */
	c.noise_rms = 0.02;
	fmtone_det_init (&d, RATE);
	r = run_signal (&d, &c, 88.5, 0, 0);
	assert (fabs (r.ctcss_hz - 88.5) < 0.01 && !r.ever_wrong);
	printf ("  ctcss: 2 %% level tone in noise OK (lock %.3f s)\n", r.lock_s);
}

/* Tone arriving mid-stream (detector already running on voice): the hop
   grid is no longer aligned with the onset, so this is the realistic
   worst case; one hop (180 ms) above the aligned figure at most. */
static void test_onset_latency (void)
{
	fmtone_det d;
	sig_cfg c = cfg_default ();
	sig_res r;
	double worst = 0.0;
	int i;
	for (i = 0; i < 8; i++)
	{
		c.seconds = 3.0;
		c.start_s = 1.0 + 0.0237 * i;
		c.with_voice = 1;
		c.ctcss_hz = 131.8;
		c.dcs_code = 0;
		fmtone_det_init (&d, RATE);
		r = run_signal (&d, &c, 131.8, 0, 0);
		assert (fabs (r.ctcss_hz - 131.8) < 0.01 && !r.ever_wrong && r.lock_s > 0.0 && r.lock_s < 0.75);
		if (r.lock_s > worst) worst = r.lock_s;
		c.ctcss_hz = 0.0;
		c.dcs_code = 244;
		fmtone_det_init (&d, RATE);
		r = run_signal (&d, &c, 0.0, 244, 0);
		assert (r.dcs_code == 244 && !r.ever_wrong && r.lock_s > 0.0 && r.lock_s < 0.6);
	}
	printf ("  onset mid-stream: CTCSS worst %.3f s, DCS < 0.6 s OK\n", worst);
}

/* ---------------------------------------------------------------------- */
/* 3. false-lock immunity                                                  */
/* ---------------------------------------------------------------------- */

static void test_no_false_lock (void)
{
	fmtone_det d;
	sig_cfg c = cfg_default ();
	sig_res r;
	/* no carrier: loud discriminator noise */
	c.seconds = 20.0;
	c.noise_rms = 0.6;
	fmtone_det_init (&d, RATE);
	r = run_signal (&d, &c, 0.0, 0, 0);
	assert (!r.ever_ctcss && !r.ever_dcs);
	/* quiet carrier */
	c.noise_rms = 0.01;
	fmtone_det_init (&d, RATE);
	r = run_signal (&d, &c, 0.0, 0, 0);
	assert (!r.ever_ctcss && !r.ever_dcs);
	/* voice only */
	c.noise_rms = 0.0;
	c.with_voice = 1;
	fmtone_det_init (&d, RATE);
	r = run_signal (&d, &c, 0.0, 0, 0);
	assert (!r.ever_ctcss && !r.ever_dcs);
	printf ("  no false lock: 20 s each of no-carrier noise, quiet carrier, voice OK\n");
}

/* ---------------------------------------------------------------------- */
/* 4. DCS loopback                                                         */
/* ---------------------------------------------------------------------- */

static double worst_dcs_lock = 0.0;

static void expect_dcs (int code, int inv, int target, int want_code, int want_inv, int with_voice, double rate)
{
	fmtone_det d;
	sig_cfg c = cfg_default ();
	sig_res r;
	c.rate = rate;
	c.dcs_code = code;
	c.dcs_inv = inv;
	c.with_voice = with_voice;
	fmtone_det_init (&d, rate);
	if (target) fmtone_det_set_squelch (&d, 2, 0.0, code, inv);
	r = run_signal (&d, &c, 0.0, want_code, want_inv);
	if (r.dcs_code != want_code || r.dcs_inv != want_inv || r.lock_s < 0.0 || r.lock_s >= 0.6 || r.ever_ctcss)
	{
		printf ("  FAIL dcs %03d%s target=%d: got %03d inv=%d lock=%.3f ctcss=%d\n", code, inv ? "I" : "N", target, r.dcs_code, r.dcs_inv, r.lock_s, r.ever_ctcss);
		assert (0);
	}
	if (target) assert (r.open);
	if (r.lock_s > worst_dcs_lock) worst_dcs_lock = r.lock_s;
}

static void test_dcs_loopback (void)
{
	int i;
	expect_dcs (23, 0, 0, 23, 0, 1, RATE);
	expect_dcs (754, 0, 0, 754, 0, 1, RATE);
	/* inverted 023 is the same bit stream as normal 047: a free scan reads
	   047 N, a squelch targeting 023 I opens and reports 023 I */
	expect_dcs (23, 1, 0, 47, 0, 1, RATE);
	expect_dcs (23, 1, 1, 23, 1, 1, RATE);
	expect_dcs (754, 1, 1, 754, 1, 1, RATE);
	expect_dcs (23, 0, 1, 23, 0, 1, 192000.0);
	for (i = 0; i < FMTONE_NDCS; i++)
		expect_dcs (fmtone_dcs_codes[i], 0, 0, fmtone_dcs_codes[i], 0, 0, RATE);
	printf ("  dcs: 023/754 N+I with voice, all 104 codes, 192 kHz OK (worst lock %.3f s)\n", worst_dcs_lock);
}

/* ---------------------------------------------------------------------- */
/* 5. tone-squelch gate                                                    */
/* ---------------------------------------------------------------------- */

static double gate_run (fmtone_det* d, double tone, int dcs, double seconds, double* max_step)
{
	/* returns the gain of the last block; audio = constant 1.0 */
	enum { BLOCK = 256 };
	double buf[2 * BLOCK];
	double ph = 0.0, prev = -1.0, last = 0.0;
	long n = 0, total = (long)(seconds * RATE);
	int i;
	fmtone_dcsenc enc;
	fmtone_dcsenc_init (&enc, RATE);
	if (dcs) fmtone_dcsenc_set (&enc, dcs, 0);
	*max_step = 0.0;
	while (n < total)
	{
		for (i = 0; i < BLOCK; i++, n++)
		{
			double x = 0.0;
			if (tone > 0.0) { x += TONE_LEVEL * cos (ph); ph += 2.0 * M_PI * tone / RATE; }
			if (dcs) x += TONE_LEVEL * fmtone_dcsenc_next (&enc);
			fmtone_det_process (d, x);
			buf[2 * i] = buf[2 * i + 1] = 1.0;
		}
		fmtone_det_gate (d, buf, BLOCK);
		for (i = 0; i < BLOCK; i++)
		{
			if (prev >= 0.0 && fabs (buf[2 * i] - prev) > *max_step) *max_step = fabs (buf[2 * i] - prev);
			prev = buf[2 * i];
		}
		last = buf[2 * (BLOCK - 1)];
	}
	return last;
}

static void test_squelch_gate (void)
{
	fmtone_det d;
	double step, g;
	double ramp_step = 1.0 / (0.010 * RATE) + 1e-12;
	/* off: always open */
	fmtone_det_init (&d, RATE);
	g = gate_run (&d, 0.0, 0, 1.0, &step);
	assert (g == 1.0);
	/* CTCSS 100.0 target: wrong tone keeps it muted */
	fmtone_det_init (&d, RATE);
	fmtone_det_set_squelch (&d, 1, 100.0, 23, 0);
	g = gate_run (&d, 103.5, 0, 2.0, &step);
	assert (g == 0.0);
	/* right tone opens with a click-free ramp */
	g = gate_run (&d, 100.0, 0, 2.0, &step);
	assert (g == 1.0);
	assert (step <= ramp_step);
	/* tone removed: closes again with a ramp */
	g = gate_run (&d, 0.0, 0, 1.5, &step);
	assert (g == 0.0);
	assert (step <= ramp_step);
	/* DCS 023 N target: 025 keeps it muted, 023 opens */
	fmtone_det_init (&d, RATE);
	fmtone_det_set_squelch (&d, 2, 0.0, 23, 0);
	g = gate_run (&d, 0.0, 25, 2.0, &step);
	assert (g == 0.0);
	g = gate_run (&d, 0.0, 23, 2.0, &step);
	assert (g == 1.0 && step <= ramp_step);
	/* a CTCSS target does not open on DCS and vice versa */
	fmtone_det_init (&d, RATE);
	fmtone_det_set_squelch (&d, 2, 0.0, 23, 0);
	g = gate_run (&d, 100.0, 0, 2.0, &step);
	assert (g == 0.0);
	printf ("  squelch gate: CTCSS / DCS open+close with 10 ms ramps OK\n");
}

/* ---------------------------------------------------------------------- */
/* 6. DCS encoder waveform                                                 */
/* ---------------------------------------------------------------------- */

static void test_dcs_encoder (void)
{
	fmtone_dcsenc e;
	long n;
	double peak = 0.0;
	fmtone_dcsenc_init (&e, RATE);
	fmtone_dcsenc_set (&e, 23, 0);
	for (n = 0; n < (long)RATE; n++)
	{
		double y = fmtone_dcsenc_next (&e);
		if (fabs (y) > peak) peak = fabs (y);
	}
	/* 2nd-order Butterworth step overshoot is ~4 %; bit patterns stack to ~9 % */
	assert (peak > 0.99 && peak < 1.10);
	printf ("  dcs encoder: shaped NRZ peak %.3f OK\n", peak);
}

int main (void)
{
	setvbuf (stdout, NULL, _IONBF, 0);
	printf ("fm_tone tests\n");
	test_dcs_codewords ();
	test_dcs_encoder ();
	test_ctcss_pairs ();
	test_ctcss_low_level ();
	test_onset_latency ();
	test_no_false_lock ();
	test_dcs_loopback ();
	test_squelch_gate ();
	printf ("all fm_tone tests passed\n");
	return 0;
}
