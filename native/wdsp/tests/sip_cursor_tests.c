// SPDX-License-Identifier: GPL-2.0-or-later
//
// Deterministic tests for the Zeus contiguous siphon reader (sip_cursor.c).
// The writer below mirrors xsiphon's mode-0 ring copy in siphon.c. Every
// sample carries its absolute index, so a repeated, skipped or reordered
// sample fails loudly. Compiled standalone (no FFTW, no WDSP channel state).

#include <assert.h>
#include <stdio.h>
#include <string.h>

#include "sip_cursor.h"

#define RING 64
#define BLOCK 8

typedef struct {
  double buff[2 * RING];
  int idx;
  long long next;   // absolute index of the next sample written
  sip_cursor cursor;
} ring_t;

static void ring_init (ring_t* r) {
  memset (r, 0, sizeof (*r));
  sip_cursor_reset (&r->cursor);
}

// Same arithmetic as xsiphon mode 0 for insize < sipsize.
static void ring_write (ring_t* r, int insize) {
  double in[2 * RING];
  int i, first, second;
  assert (insize < RING);

  for (i = 0; i < insize; i++) {
    in[2 * i + 0] = (double)(r->next + i);
    in[2 * i + 1] = -(double)(r->next + i);
  }

  if (insize > (RING - r->idx)) {
    first = RING - r->idx;
    second = insize - first;
  } else {
    first = insize;
    second = 0;
  }

  memcpy (r->buff + 2 * r->idx, in, first * 2 * sizeof (double));
  memcpy (r->buff, in + 2 * first, second * 2 * sizeof (double));

  if ((r->idx += insize) >= RING) { r->idx -= RING; }

  r->cursor.written += insize;
  r->next += insize;
}

static int ring_read (ring_t* r, float* out, int max_blocks) {
  return sip_cursor_read_blocks (&r->cursor, r->buff, RING, r->idx, out, BLOCK, max_blocks);
}

static void expect_run (const float* out, int blocks, long long first) {
  int i;

  for (i = 0; i < blocks * BLOCK; i++) {
    assert (out[2 * i + 0] == (float)(first + i));
    assert (out[2 * i + 1] == -(float)(first + i));
  }
}

// The bug: polling faster or slower than the writer must neither repeat nor
// skip a block. Interleave 0, 1 and 2 writes per read across many ring wraps.
static void test_every_block_once_in_order (void) {
  ring_t r;
  float out[2 * RING];
  long long expected = 0;
  int round, writes, w, got;
  static const int pattern[] = { 1, 0, 2, 1, 1, 0, 0, 2, 1, 2, 0, 1 };
  ring_init (&r);

  for (round = 0; round < 600; round++) {
    writes = pattern[round % 12];

    for (w = 0; w < writes; w++) { ring_write (&r, BLOCK); }

    got = ring_read (&r, out, 4);
    assert (got == writes);
    expect_run (out, got, expected);
    expected += (long long)got * BLOCK;
  }

  assert (expected == r.next);
  printf ("  every block once in order: ok\n");
}

static void test_nothing_new_returns_zero (void) {
  ring_t r;
  float out[2 * RING];
  ring_init (&r);
  ring_write (&r, BLOCK);
  assert (ring_read (&r, out, 4) == 1);
  assert (ring_read (&r, out, 4) == 0);
  assert (ring_read (&r, out, 4) == 0);
  printf ("  nothing new returns zero: ok\n");
}

// After an idle gap the reader resumes at the newest max_blocks, block aligned.
static void test_backlog_drops_oldest (void) {
  ring_t r;
  float out[2 * RING];
  int w;
  ring_init (&r);

  for (w = 0; w < 5; w++) { ring_write (&r, BLOCK); }

  assert (ring_read (&r, out, 2) == 2);
  expect_run (out, 2, 3 * BLOCK);
  ring_write (&r, BLOCK);
  assert (ring_read (&r, out, 2) == 1);
  expect_run (out, 1, 5 * BLOCK);
  printf ("  backlog drops oldest: ok\n");
}

// More pending than the ring holds: only what the ring still has is valid.
static void test_overrun_capped_to_ring (void) {
  ring_t r;
  float out[2 * RING];
  int w;
  ring_init (&r);

  for (w = 0; w < 3 * RING / BLOCK + 3; w++) { ring_write (&r, BLOCK); }

  assert (ring_read (&r, out, 100) == RING / BLOCK);
  expect_run (out, RING / BLOCK, r.next - RING);
  printf ("  overrun capped to ring: ok\n");
}

// A partial remainder must not let the oldest returned samples be ones the
// ring has already overwritten (ring 64, block 8, 68 written, nothing read).
static void test_overrun_with_partial_remainder (void) {
  ring_t r;
  float out[2 * RING];
  int w;
  ring_init (&r);

  for (w = 0; w < RING / BLOCK; w++) { ring_write (&r, BLOCK); }

  ring_write (&r, BLOCK / 2);
  assert (ring_read (&r, out, RING / BLOCK) == RING / BLOCK - 1);
  expect_run (out, RING / BLOCK - 1, BLOCK);
  ring_write (&r, BLOCK / 2);
  assert (ring_read (&r, out, RING / BLOCK) == 1);
  expect_run (out, 1, RING);
  printf ("  overrun with partial remainder: ok\n");
}

// A partial block stays pending until it completes.
static void test_partial_block_waits (void) {
  ring_t r;
  float out[2 * RING];
  ring_init (&r);
  ring_write (&r, BLOCK / 2);
  assert (ring_read (&r, out, 4) == 0);
  ring_write (&r, BLOCK / 2);
  assert (ring_read (&r, out, 4) == 1);
  expect_run (out, 1, 0);
  printf ("  partial block waits: ok\n");
}

// flush_siphon zeroes idx and resets the cursor; reads restart cleanly.
static void test_reset_restarts (void) {
  ring_t r;
  float out[2 * RING];
  ring_init (&r);
  ring_write (&r, BLOCK);
  ring_write (&r, BLOCK);
  assert (ring_read (&r, out, 1) == 1);
  memset (r.buff, 0, sizeof (r.buff));
  r.idx = 0;
  r.next = 0;
  sip_cursor_reset (&r.cursor);
  assert (ring_read (&r, out, 4) == 0);
  ring_write (&r, BLOCK);
  assert (ring_read (&r, out, 4) == 1);
  expect_run (out, 1, 0);
  // A cursor left ahead of a reset writer must not read stale data.
  r.cursor.read = r.cursor.written + 3 * BLOCK;
  assert (ring_read (&r, out, 4) == 0);
  printf ("  reset restarts: ok\n");
}

static void test_bad_arguments (void) {
  ring_t r;
  float out[2 * RING];
  ring_init (&r);
  ring_write (&r, BLOCK);
  assert (sip_cursor_read_blocks (&r.cursor, r.buff, RING, r.idx, out, 0, 4) == 0);
  assert (sip_cursor_read_blocks (&r.cursor, r.buff, RING, r.idx, out, BLOCK, 0) == 0);
  assert (sip_cursor_read_blocks (&r.cursor, r.buff, RING, r.idx, out, 2 * RING, 4) == 0);
  assert (ring_read (&r, out, 4) == 1);
  printf ("  bad arguments consume nothing: ok\n");
}

int main (void) {
  printf ("sip_cursor tests\n");
  test_every_block_once_in_order ();
  test_nothing_new_returns_zero ();
  test_backlog_drops_oldest ();
  test_overrun_capped_to_ring ();
  test_overrun_with_partial_remainder ();
  test_partial_block_waits ();
  test_reset_restarts ();
  test_bad_arguments ();
  printf ("all sip_cursor tests passed\n");
  return 0;
}
