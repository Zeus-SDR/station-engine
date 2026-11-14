/*  iqc.c

This file is part of a program that implements a Software-Defined Radio.

Copyright (C) 2013, 2016, 2026 Warren Pratt, NR0V

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

warren@pratt.one

*/

#include "comm.h"
#include "ps_memcorr.h"

void calc_iqc (IQC a)
{
	a->cset = 0;
	a->count = 0;
	a->state = 0;
	a->busy = 0;
	a->ntup = (int)(a->tup * a->rate);
	a->cup = (double *) malloc0 ((a->ntup + 1) * sizeof (double));
	double delta = PI / (double)a->ntup;
	double theta = 0.0;
	for (int i = 0; i <= a->ntup; i++)
	{
		a->cup[i] = 0.5 * (1.0 - cos (theta));
		theta += delta;
	}
}

IQC create_iqc (int run, int size, double* in, double* out, double rate, double tup)
{
	IQC a = (IQC) malloc0 (sizeof (iqc));
	a->run = run;
	a->size = size;
	a->in = in;
	a->out = out;
	a->rate = rate;
	a->tup = tup;
	a->m_spline[0] = a->m_spline[1] = NULL;
	a->c_spline[0] = a->c_spline[1] = NULL;
	a->s_spline[0] = a->s_spline[1] = NULL;
	calc_iqc (a);
	return a;
}

void destroy_iqc (IQC a)
{
	_aligned_free(a->cup);

	ns_free(a->m_spline[0]); ns_free(a->m_spline[1]);
	ns_free(a->c_spline[0]); ns_free(a->c_spline[1]);
	ns_free(a->s_spline[0]); ns_free(a->s_spline[1]);
	a->m_spline[0] = a->m_spline[1] = NULL;
	a->c_spline[0] = a->c_spline[1] = NULL;
	a->s_spline[0] = a->s_spline[1] = NULL;
	_aligned_free (a);
}

void flush_iqc (IQC a)
{
	a->mem_prev_x2 = 0.0;
}

enum _iqcstate
{
	RUN = 0,
	BEGIN,
	SWAP,
	END,
	DONE
};

#define IQC_OUT_MAX 0.999

void xiqc (IQC a)
{
	if (_InterlockedAnd(&a->run, 1))
	{
		int cset, altset;
		double  I, Q, env, ym, yc, ys, PRE0, PRE1;
		// Zeus memory term. The caller holds csDSP for the whole buffer, the
		// same lock SetTXAiqcMemoryBeta writes under, so the pair is read once
		// and consistently here.
		const double mem_br = a->mem_beta_re;
		const double mem_bi = a->mem_beta_im;
		const int mem_on = (mem_br != 0.0 || mem_bi != 0.0);
		const double mem_dscale = ps_memcorr_dscale (a->rate);
		for (int i = 0; i < a->size; i++)
		{
			double x2;
			I = a->in[2 * i + 0];
			Q = a->in[2 * i + 1];
			x2 = I * I + Q * Q;
			env = sqrt (x2);
			cset = a->cset;
			ym = get_mag_correction_ema  (&a->m_calavg[cset], env, &a->m_prev_y[cset]);
			yc = get_phase_correction_ema(&a->c_calavg[cset], env, &a->c_prev_y[cset]);
			ys = get_phase_correction_ema(&a->s_calavg[cset], env, &a->s_prev_y[cset]);
			PRE0 = ym * (I * yc - Q * ys);
			PRE1 = ym * (I * ys + Q * yc);

			switch (a->state)
			{
			case RUN:
			
				break;
			case BEGIN:
				PRE0 = (1.0 - a->cup[a->count]) * I + a->cup[a->count] * PRE0;
				PRE1 = (1.0 - a->cup[a->count]) * Q + a->cup[a->count] * PRE1;
				if (a->count++ == a->ntup)
				{
					a->state = RUN;
					a->count = 0;
					InterlockedBitTestAndReset (&a->busy, 0);
				}
				break;
			case SWAP:
				altset = 1 - cset;
				ym = get_mag_correction_ema(&a->m_calavg[altset], env, &a->m_prev_y[altset]);
				yc = get_phase_correction_ema(&a->c_calavg[altset], env, &a->c_prev_y[altset]);
				ys = get_phase_correction_ema(&a->s_calavg[altset], env, &a->s_prev_y[altset]);
				PRE0 = a->cup[a->count] * ym * (I * yc - Q * ys) + (1.0 - a->cup[a->count]) * PRE0;
				PRE1 = a->cup[a->count] * ym * (I * ys + Q * yc) + (1.0 - a->cup[a->count]) * PRE1;
				if (a->count++ == a->ntup)
				{
					a->state = RUN;
					a->count = 0;
					a->cset = altset;
					InterlockedBitTestAndReset (&a->busy, 0);
				}
				break;
			case END:
				PRE0 = (1.0 - a->cup[a->count]) * PRE0 + a->cup[a->count] * I;
				PRE1 = (1.0 - a->cup[a->count]) * PRE1 + a->cup[a->count] * Q;
				if (a->count++ == a->ntup)
				{
					a->state = DONE;
					a->count = 0;
					InterlockedBitTestAndReset (&a->busy, 0);
				}
				break;
			case DONE:
				PRE0 = I;
				PRE1 = Q;
				break;
			}

			if (mem_on && a->state == RUN)
			{
				// in * beta * D(in), D(in) = d|in|^2/dt per radian of 1 kHz
				const double d = (x2 - a->mem_prev_x2) * mem_dscale;
				PRE0 += d * (I * mem_br - Q * mem_bi);
				PRE1 += d * (I * mem_bi + Q * mem_br);
			}
			a->mem_prev_x2 = x2;

			{
				double omag2 = PRE0 * PRE0 + PRE1 * PRE1;
				if (omag2 > IQC_OUT_MAX * IQC_OUT_MAX) 
				{
					double sc = IQC_OUT_MAX / sqrt(omag2);
					PRE0 *= sc;
					PRE1 *= sc;
				}
			}

			a->out[2 * i + 0] = PRE0;
			a->out[2 * i + 1] = PRE1;
		}
	}
	else if (a->out != a->in)
		memcpy (a->out, a->in, a->size * sizeof (complex));
}

void setBuffers_iqc (IQC a, double* in, double* out)
{
	a->in = in;
	a->out = out;
}

void setSamplerate_iqc (IQC a, int rate)
{
	_aligned_free(a->cup);
	a->rate = rate;
	calc_iqc (a);
}

void setSize_iqc (IQC a, int size)
{
	a->size = size;
}

/********************************************************************************************************
*																										*
*											TXA Properties												*
*																										*
********************************************************************************************************/

void GetTXAiqcValues (int channel, NS_Spline** m_spline, CurveEMA* m_calavg, double *m_prev_y,
	                               NS_Spline** c_spline, CurveEMA* c_calavg, double *c_prev_y,
	                               NS_Spline** s_spline, CurveEMA* s_calavg, double *s_prev_y)
{
	IQC a = txa[channel].iqc.p;
	EnterCriticalSection (&ch[channel].csDSP);
	ns_free(*m_spline);
	*m_spline = ns_copy(a->m_spline[a->cset]);
	memcpy(m_calavg, &a->m_calavg[a->cset], sizeof(CurveEMA));
	*m_prev_y = a->m_prev_y[a->cset];

	ns_free(*c_spline);
	*c_spline = ns_copy(a->c_spline[a->cset]);
	memcpy(c_calavg, &a->c_calavg[a->cset], sizeof(CurveEMA));
	*c_prev_y = a->c_prev_y[a->cset];

	ns_free(*s_spline);
	*s_spline = ns_copy(a->s_spline[a->cset]);
	memcpy(s_calavg, &a->s_calavg[a->cset], sizeof(CurveEMA));
	*s_prev_y = a->s_prev_y[a->cset];

	LeaveCriticalSection (&ch[channel].csDSP);
}

void SetTXAiqcSwap (int channel, NS_Spline* m_spline, CurveEMA* m_calavg, double m_prev_y,
		                         NS_Spline* c_spline, CurveEMA* c_calavg, double c_prev_y,
	                             NS_Spline* s_spline, CurveEMA* s_calavg, double s_prev_y)
{
	IQC a = txa[channel].iqc.p;
	EnterCriticalSection (&ch[channel].csDSP);
	int altset = 1 - a->cset;

	ns_free(a->m_spline[altset]);
	a->m_spline[altset] = m_spline;
	memcpy(&a->m_calavg[altset], m_calavg, sizeof(CurveEMA));
	a->m_prev_y[altset] = m_prev_y;

	ns_free(a->c_spline[altset]);
	a->c_spline[altset] = c_spline;
	memcpy(&a->c_calavg[altset], c_calavg, sizeof(CurveEMA));
	a->c_prev_y[altset] = c_prev_y;

	ns_free(a->s_spline[altset]);
	a->s_spline[altset] = s_spline;
	memcpy(&a->s_calavg[altset], s_calavg, sizeof(CurveEMA));
	a->s_prev_y[altset] = s_prev_y;

	InterlockedBitTestAndSet (&a->busy, 0);
	a->state = SWAP;
	a->count = 0;
	LeaveCriticalSection (&ch[channel].csDSP);

	while (_InterlockedAnd (&a->busy, 1)) Sleep(1);
}


void SetTXAiqcStart (int channel, NS_Spline* m_spline, CurveEMA* m_calavg, double m_prev_y,
	                              NS_Spline* c_spline, CurveEMA* c_calavg, double c_prev_y,
	                              NS_Spline* s_spline, CurveEMA* s_calavg, double s_prev_y)
{
	IQC a = txa[channel].iqc.p;
	EnterCriticalSection (&ch[channel].csDSP);
	a->cset = 0;

	ns_free(a->m_spline[a->cset]);
	a->m_spline[a->cset] = m_spline;
	memcpy(&a->m_calavg[a->cset], m_calavg, sizeof(CurveEMA));
	a->m_prev_y[a->cset] = m_prev_y;

	ns_free(a->c_spline[a->cset]);
	a->c_spline[a->cset] = c_spline;
	memcpy(&a->c_calavg[a->cset], c_calavg, sizeof(CurveEMA));
	a->c_prev_y[a->cset] = c_prev_y;

	ns_free(a->s_spline[a->cset]);
	a->s_spline[a->cset] = s_spline;
	memcpy(&a->s_calavg[a->cset], s_calavg, sizeof(CurveEMA));
	a->s_prev_y[a->cset] = s_prev_y;
	InterlockedBitTestAndSet (&a->busy, 0);
	a->state = BEGIN;
	a->count = 0;
	LeaveCriticalSection (&ch[channel].csDSP);
	InterlockedBitTestAndSet   (&txa[channel].iqc.p->run, 0);
	while (_InterlockedAnd (&a->busy, 1)) Sleep(1);
}

// Zeus: memory-correction hand-off from calcc (feedback thread, holding
// calcc.cs_update). xiqc runs inside the channel's csDSP (wdspmain holds it
// around xtxa), so writing both halves under csDSP means a TX buffer always
// sees a matched re/im pair, the same idiom SetTXAiqcSwap/Start use. Nothing
// takes calcc.cs_update while holding csDSP, so the order cannot deadlock.
void SetTXAiqcMemoryBeta (int channel, double beta_re, double beta_im)
{
	IQC a = txa[channel].iqc.p;
	EnterCriticalSection (&ch[channel].csDSP);
	a->mem_beta_re = beta_re;
	a->mem_beta_im = beta_im;
	LeaveCriticalSection (&ch[channel].csDSP);
}

void SetTXAiqcEnd (int channel)
{
	IQC a = txa[channel].iqc.p;
	EnterCriticalSection (&ch[channel].csDSP);
	InterlockedBitTestAndSet (&a->busy, 0);
	a->state = END;
	a->count = 0;
	LeaveCriticalSection (&ch[channel].csDSP);
	while (_InterlockedAnd (&a->busy, 1)) Sleep(1);
	InterlockedBitTestAndReset (&txa[channel].iqc.p->run, 0);
}

