/*  sip_cursor.c

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

#include "sip_cursor.h"

void sip_cursor_reset (sip_cursor* c) {
  c->written = 0;
  c->read = 0;
}

int sip_cursor_read_blocks (sip_cursor* c, const double* ring, int ring_size, int idx,
                            float* out, int block_size, int max_blocks) {
  long long pending, blocks, cap, back;
  int mask, j, i, n;

  if (block_size <= 0 || max_blocks <= 0 || ring_size < block_size) { return 0; }

  // A reset writer (flush) or a cursor ahead of it restarts at the writer.
  if (c->read > c->written) { c->read = c->written; }

  // The ring keeps only the newest ring_size samples. Skip whole blocks until
  // the read position is still in the ring, so a pending partial block can
  // never make the oldest returned samples overwritten ones.
  if (c->written - c->read > ring_size) {
    long long lost = c->written - ring_size - c->read;
    c->read += ((lost + block_size - 1) / block_size) * block_size;
  }

  pending = c->written - c->read;
  blocks = pending / block_size;
  cap = max_blocks;

  if (blocks > cap) {
    c->read += (blocks - cap) * block_size;
    blocks = cap;
  }

  n = (int)(blocks * block_size);
  mask = ring_size - 1;
  back = c->written - c->read;   // samples between the read position and the writer
  j = (int)((idx - back) & mask);

  for (i = 0; i < n; i++) {
    out[2 * i + 0] = (float)ring[2 * j + 0];
    out[2 * i + 1] = (float)ring[2 * j + 1];
    j = (j + 1) & mask;
  }

  c->read += n;
  return (int)blocks;
}
