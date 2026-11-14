/*  ps_scheck.h

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

// Zeus extension: the fit-to-fit comparison behind calcc's scheck().
//
// scheck() rejects a new fit when a correction curve moved more than
// SCHECK_TOL from the previous fit at any of SCHECK_PTS envelope points, and
// three rejections in a row reset calibration. For the magnitude (gain)
// curve that absolute tolerance is far too strict at low envelope: a gain
// deviation dg at envelope x changes the output by only x * dg, so 0.10 at
// x = 0.03 is about 30 times stricter, in output terms, than 0.10 at full
// scale. On a warm amplifier with memory the low-envelope gain data is
// two-valued and the fitted gain there jitters by 0.15..0.8 between fits,
// which reset calibration every ~2 s on the air. Below SCHECK_MAG_WEIGHT_X
// the magnitude tolerance is therefore output-referred,
// SCHECK_TOL * SCHECK_MAG_WEIGHT_X / x, held constant below
// SCHECK_MAG_WEIGHT_XMIN because the magnitude fit has no data there.
// The phase curves and every point at or above SCHECK_MAG_WEIGHT_X keep
// SCHECK_TOL exactly.
//
// No WDSP channel state, so the helper compiles standalone for
// native/wdsp/tests/ps_scheck_tests.c.

#ifndef _ps_scheck_h
#define _ps_scheck_h

#define SCHECK_PTS                40
#define SCHECK_TOL                0.10
// Envelope below which the magnitude tolerance is output-referred. Same
// boundary as calcc's low-envelope curve EMA (PS_NS_EMA_X_BND).
#define SCHECK_MAG_WEIGHT_X       0.20
// No magnitude fit data below calcc's pre-filter minimum
// (PS_NF_MAG_PREFILT_XMIN), so the tolerance stops growing there.
#define SCHECK_MAG_WEIGHT_XMIN    0.04

// Envelope of check point k, 0..SCHECK_PTS-1, spread over [0, 1].
extern double ps_scheck_x (int k);

// Allowed fit-to-fit change of the magnitude curve at envelope x.
extern double ps_scheck_mag_tol (double x);

// Returns 1 when the new SCHECK_PTS-point solution deviates from the previous
// one: magnitude beyond ps_scheck_mag_tol, or cosine/sine beyond SCHECK_TOL.
extern int ps_scheck_deviates (const double* m_prev, const double* m_new,
                               const double* c_prev, const double* c_new,
                               const double* s_prev, const double* s_new);

#endif
