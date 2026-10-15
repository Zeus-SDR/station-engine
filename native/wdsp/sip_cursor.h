/*  sip_cursor.h

Zeus extension (contiguous siphon reads) -- not part of upstream WDSP.

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

// Zeus extension: a read cursor over a siphon's complex sample ring, so a
// reader on another thread receives every written block exactly once.
//
// TXAGetaSipF1 copies "the newest N samples". Called from the fexchange
// thread, which is not synchronised with the DSP thread that writes the
// siphon inside xtxa, it returned the same block twice or skipped one, and
// the TX analyzer drew those splices as a broadband floor outside the TX
// filter, rippled at the block rate (96 kHz / 512 = 187.5 Hz on the G2).
//
// No WDSP channel state, no FFTW, no heap, so the helper compiles standalone
// for native/wdsp/tests/sip_cursor_tests.c.

#ifndef _sip_cursor_h
#define _sip_cursor_h

typedef struct _sip_cursor {
  long long written;  // complex samples ever written to the ring
  long long read;     // complex samples consumed by the cursor reader
} sip_cursor;

extern void sip_cursor_reset (sip_cursor* c);

// Copies up to max_blocks whole blocks of block_size complex samples, written
// since the previous call and oldest first, from `ring` (interleaved I/Q
// doubles, ring_size complex samples, a power of two, next write at `idx`)
// into `out` as interleaved floats. Blocks the ring has already overwritten
// are skipped, and when more than max_blocks are pending the oldest are
// dropped, so the reader resumes at the newest data. A partial block stays
// pending. Returns the number of blocks copied.
extern int sip_cursor_read_blocks (sip_cursor* c, const double* ring, int ring_size, int idx,
                                   float* out, int block_size, int max_blocks);

#endif
