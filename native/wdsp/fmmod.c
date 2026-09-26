/*  fmmod.c

This file is part of a program that implements a Software-Defined Radio.

Copyright (C) 2013, 2016, 2023 Warren Pratt, NR0V

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

#include "comm.h"

void calc_fmmod (FMMOD a)
{
	// ctcss gen
	a->tscale = 1.0 / (1.0 + a->ctcss_level);
	a->tphase = 0.0;
	a->tdelta = TWOPI * a->ctcss_freq / a->samplerate;
	// mod
	a->sphase = 0.0;
	a->sdelta = TWOPI * a->deviation / a->samplerate;
	// bandpass
	a->bp_fc = a->deviation + a->f_high;
}

FMMOD create_fmmod (int run, int size, double* in, double* out, int rate, double dev, double f_low, double f_high, 
	int ctcss_run, double ctcss_level, double ctcss_freq, int bp_run, int nc, int mp)
{
	FMMOD a = (FMMOD) malloc0 (sizeof (fmmod));
	double* impulse;
	a->run = run;
	a->size = size;
	a->in = in;
	a->out = out;
	a->samplerate = (double)rate;
	a->deviation = dev;
	a->f_low = f_low;
	a->f_high = f_high;
	a->ctcss_run = ctcss_run;
	a->ctcss_level = ctcss_level;
	a->ctcss_freq = ctcss_freq;
	a->bp_run = bp_run;
	a->nc = nc;
	a->mp = mp;
	calc_fmmod (a);
	// Zeus extension (FM tones)
	a->dcs_run = 0;
	a->dcs_code = 23;
	a->dcs_inverted = 0;
	fmtone_dcsenc_init (&a->dcs, rate);
	a->dev_peak = 0.0;
	InitializeCriticalSectionAndSpinCount (&a->cs_peak, 2500);
	impulse = fir_bandpass(a->nc, -a->bp_fc, +a->bp_fc, a->samplerate, 0, 1, 1.0 / (2 * a->size));
	a->p = create_fircore (a->size, a->out, a->out, a->nc, a->mp, 8, impulse);
	_aligned_free (impulse);
	return a;
}

void destroy_fmmod (FMMOD a)
{
	destroy_fircore (a->p);
	DeleteCriticalSection (&a->cs_peak);
	_aligned_free (a);
}

void flush_fmmod (FMMOD a)
{
	a->tphase = 0.0;
	a->sphase = 0.0;
	fmtone_dcsenc_reset (&a->dcs);
	EnterCriticalSection (&a->cs_peak);
	a->dev_peak = 0.0;
	LeaveCriticalSection (&a->cs_peak);
}

void xfmmod (FMMOD a)
{
	int i;
	double dp, magdp, peak, mag, modpeak;
	if (a->run)
	{
		peak = 0.0;
		modpeak = 0.0;
		for (i = 0; i < a->size; i++)
		{
			if (a->dcs_run)
			{
				// Zeus extension: DCS NRZ at the CTCSS level, same tscale
				// headroom; DCS wins over CTCSS when both are set.
				a->out[2 * i + 0] = a->tscale * (a->in[2 * i + 0] + a->ctcss_level * fmtone_dcsenc_next (&a->dcs));
			}
			else if (a->ctcss_run)
			{
				a->tphase += a->tdelta;
				if (a->tphase >= TWOPI) a->tphase -= TWOPI;
				a->out[2 * i + 0] = a->tscale * (a->in[2 * i + 0] + a->ctcss_level * cos (a->tphase));
			}
			if ((mag = a->out[2 * i + 0]) < 0.0) mag = - mag;
			if (mag > modpeak) modpeak = mag;
			dp = a->out[2 * i + 0] * a->sdelta;
			a->sphase += dp;
			if (a->sphase >= TWOPI) a->sphase -= TWOPI;
			if (a->sphase <   0.0 ) a->sphase += TWOPI;
			a->out[2 * i + 0] = 0.7071 * cos (a->sphase);
			a->out[2 * i + 1] = 0.7071 * sin (a->sphase);
			if ((magdp = dp) < 0.0) magdp = - magdp;
			if (magdp > peak) peak = magdp;
		}
		//print_deviation ("peakdev.txt", peak, a->samplerate);
		// Zeus extension: composite modulating peak -> deviation (Hz)
		modpeak *= a->deviation;
		EnterCriticalSection (&a->cs_peak);
		if (modpeak > a->dev_peak) a->dev_peak = modpeak;
		LeaveCriticalSection (&a->cs_peak);
		if (a->bp_run)
			xfircore (a->p);
	}
	else if (a->in != a->out)
		memcpy (a->out, a->in, a->size * sizeof (complex));
}

void setBuffers_fmmod (FMMOD a, double* in, double* out)
{
	a->in = in;
	a->out = out;
	calc_fmmod (a);
	setBuffers_fircore (a->p, a->out, a->out);
}

void setSamplerate_fmmod (FMMOD a, int rate)
{
	double* impulse;
	a->samplerate = rate;
	calc_fmmod (a);
	fmtone_dcsenc_init (&a->dcs, rate);
	fmtone_dcsenc_set (&a->dcs, a->dcs_code, a->dcs_inverted);
	impulse = fir_bandpass(a->nc, -a->bp_fc, +a->bp_fc, a->samplerate, 0, 1, 1.0 / (2 * a->size));
	setImpulse_fircore (a->p, impulse, 1);
	_aligned_free (impulse);
}

void setSize_fmmod (FMMOD a, int size)
{
	double* impulse;
	a->size = size;
	calc_fmmod (a);
	setSize_fircore (a->p, a->size);
	impulse = fir_bandpass(a->nc, -a->bp_fc, +a->bp_fc, a->samplerate, 0, 1, 1.0 / (2 * a->size));
	setImpulse_fircore (a->p, impulse, 1);
	_aligned_free (impulse);
}

/********************************************************************************************************
*																										*
*											TXA Properties												*
*																										*
********************************************************************************************************/

PORT
void SetTXAFMDeviation (int channel, double deviation)
{
	FMMOD a = txa[channel].fmmod.p;
	double bp_fc = a->f_high + deviation;
	double* impulse = fir_bandpass (a->nc, -bp_fc, +bp_fc, a->samplerate, 0, 1, 1.0 / (2 * a->size));
	setImpulse_fircore (a->p, impulse, 0);
	_aligned_free (impulse);
	EnterCriticalSection (&ch[channel].csDSP);
	a->deviation = deviation;
	// mod
	a->sphase = 0.0;
	a->sdelta = TWOPI * a->deviation / a->samplerate;
	// bandpass
	a->bp_fc = bp_fc;
	setUpdate_fircore (a->p);
	LeaveCriticalSection (&ch[channel].csDSP);
}

PORT
void SetTXACTCSSFreq (int channel, double freq)
{
	FMMOD a;
	EnterCriticalSection (&ch[channel].csDSP);
	a = txa[channel].fmmod.p;
	a->ctcss_freq = freq;
	a->tphase = 0.0;
	a->tdelta = TWOPI * a->ctcss_freq / a->samplerate;
	LeaveCriticalSection (&ch[channel].csDSP);
}

PORT
void SetTXACTCSSRun (int channel, int run)
{
	EnterCriticalSection (&ch[channel].csDSP);
	txa[channel].fmmod.p->ctcss_run = run;
	LeaveCriticalSection (&ch[channel].csDSP);
}

PORT
void SetTXAFMNC (int channel, int nc)
{
	FMMOD a;
	double* impulse;
	EnterCriticalSection (&ch[channel].csDSP);
	a = txa[channel].fmmod.p;
	if (a->nc != nc)
	{
		a->nc = nc;
		impulse = fir_bandpass (a->nc, -a->bp_fc, +a->bp_fc, a->samplerate, 0, 1, 1.0 / (2 * a->size));
		setNc_fircore (a->p, a->nc, impulse);
		_aligned_free (impulse);
	}
	LeaveCriticalSection (&ch[channel].csDSP);
}

PORT 
void SetTXAFMMP (int channel, int mp)
{
	FMMOD a;
	a = txa[channel].fmmod.p;
	if (a->mp != mp)
	{
		a->mp = mp;
		setMp_fircore (a->p, a->mp);
	}
}

PORT
void SetTXAFMAFFreqs (int channel, double low, double high)
{
	FMMOD a;
	double* impulse;
	EnterCriticalSection(&ch[channel].csDSP);
	a = txa[channel].fmmod.p;
	if (a->f_low != low || a->f_high != high)
	{
		a->f_low = low;
		a->f_high = high;
		a->bp_fc = a->deviation + a->f_high;
		impulse = fir_bandpass (a->nc, -a->bp_fc, +a->bp_fc, a->samplerate, 0, 1, 1.0 / (2 * a->size));
		setImpulse_fircore (a->p, impulse, 1);
		_aligned_free (impulse);
	}
	LeaveCriticalSection(&ch[channel].csDSP);
}

/********************************************************************************************************
*																										*
*								Zeus extension (FM tones): TXA Properties								*
*																										*
********************************************************************************************************/

// CTCSS / DCS injection as WDSP ctcss_level (tone amplitude relative to
// full-scale audio; WDSP hard-codes 0.10). The audio is scaled by
// tscale = 1 / (1 + level) so audio + tone never exceeds the deviation.
PORT
void SetTXACTCSSLevel (int channel, double level)
{
	FMMOD a;
	if (level < 0.0) level = 0.0;
	if (level > 0.5) level = 0.5;
	EnterCriticalSection (&ch[channel].csDSP);
	a = txa[channel].fmmod.p;
	a->ctcss_level = level;
	a->tscale = 1.0 / (1.0 + a->ctcss_level);
	LeaveCriticalSection (&ch[channel].csDSP);
}

PORT
void SetTXADCSRun (int channel, int run)
{
	FMMOD a;
	EnterCriticalSection (&ch[channel].csDSP);
	a = txa[channel].fmmod.p;
	if (run && !a->dcs_run)
		fmtone_dcsenc_reset (&a->dcs);
	a->dcs_run = run ? 1 : 0;
	LeaveCriticalSection (&ch[channel].csDSP);
}

// code: octal DCS code written as decimal digits (023 -> 23); an invalid
// code falls back to 023.
PORT
void SetTXADCSCode (int channel, int code, int inverted)
{
	FMMOD a;
	if (fmtone_dcs_code_to_bin (code) < 0) code = 23;
	EnterCriticalSection (&ch[channel].csDSP);
	a = txa[channel].fmmod.p;
	a->dcs_code = code;
	a->dcs_inverted = inverted ? 1 : 0;
	fmtone_dcsenc_set (&a->dcs, a->dcs_code, a->dcs_inverted);
	LeaveCriticalSection (&ch[channel].csDSP);
}

// Peak |instantaneous deviation| (Hz) of the composite modulating signal
// (audio + CTCSS/DCS after tscale) since the previous call, then reset.
// Only the modulator's own lock is taken, never csDSP.
PORT
void GetTXAFMDeviationPeak (int channel, double* peakHz)
{
	FMMOD a = txa[channel].fmmod.p;
	if (a == 0)
	{
		*peakHz = 0.0;
		return;
	}
	EnterCriticalSection (&a->cs_peak);
	*peakHz = a->dev_peak;
	a->dev_peak = 0.0;
	LeaveCriticalSection (&a->cs_peak);
}
