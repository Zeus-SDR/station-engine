// SPDX-License-Identifier: GPL-2.0-or-later

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "osctrl.h"

void *malloc0 (int size) {
  return calloc (1, (size_t)size);
}

static void fill_iq (double* buffer, int size, double i_value, double q_value) {
  int i;

  for (i = 0; i < size; i++) {
    buffer[2 * i + 0] = i_value;
    buffer[2 * i + 1] = q_value;
  }
}

static double magnitude_at (const double* buffer, int index) {
  return hypot (buffer[2 * index + 0], buffer[2 * index + 1]);
}

static void assert_finite_and_bounded (const double* buffer, int size) {
  int i;

  for (i = 0; i < size; i++) {
    assert (isfinite (buffer[2 * i + 0]));
    assert (isfinite (buffer[2 * i + 1]));
    assert (magnitude_at (buffer, i) <= 1.0 + 1.0e-12);
  }
}

static void test_window_selection_and_unity_gain (void) {
  enum { size = 16 };
  double input[2 * size];
  double output[2 * size];
  OSCTRL a;
  int i;

  fill_iq (input, size, 0.5, 0.0);
  a = create_osctrl (1, size, input, output, 48000, 1.95);
  assert (a->pn == 5);
  assert (a->dl_len == 2);

  xosctrl (a);
  for (i = 0; i < size; i++) {
    double expected = i < a->dl_len ? 0.0 : 0.5;
    assert (fabs (output[2 * i + 0] - expected) < 1.0e-12);
    assert (output[2 * i + 1] == 0.0);
  }

  destroy_osctrl (a);
}

static void test_peak_control_and_nonfinite_containment (void) {
  enum { size = 16 };
  double input[2 * size];
  double output[2 * size];
  OSCTRL a;
  double expected = 2.0 / (1.0 + 1.95 * (2.0 - 1.0));
  int i;

  fill_iq (input, size, 2.0, 0.0);
  a = create_osctrl (1, size, input, output, 48000, 1.95);
  xosctrl (a);
  assert_finite_and_bounded (output, size);
  for (i = 0; i < size; i++) {
    double sample_expected = i < a->dl_len ? 0.0 : expected;
    assert (fabs (output[2 * i + 0] - sample_expected) < 1.0e-12);
  }

  input[0] = NAN;
  input[1] = INFINITY;
  xosctrl (a);
  assert_finite_and_bounded (output, size);

  destroy_osctrl (a);
}

static void test_flush_clears_all_state (void) {
  enum { size = 16 };
  double input[2 * size];
  double output[2 * size];
  OSCTRL a;
  int i;

  fill_iq (input, size, 1.5, 0.25);
  a = create_osctrl (1, size, input, output, 48000, 1.95);
  xosctrl (a);
  flush_osctrl (a);

  assert (a->in_idx == 0);
  assert (a->out_idx == a->dl_len);
  assert (a->max_env == 0.0);
  assert (a->env_out == 0.0);
  for (i = 0; i < a->pn; i++) {
    assert (a->dl[2 * i + 0] == 0.0);
    assert (a->dl[2 * i + 1] == 0.0);
    assert (a->dlenv[i] == 0.0);
  }

  fill_iq (input, size, 0.25, 0.0);
  xosctrl (a);
  for (i = a->dl_len; i < size; i++) {
    assert (fabs (output[2 * i + 0] - 0.25) < 1.0e-12);
  }

  destroy_osctrl (a);
}

static void test_window_geometry (void) {
  double input[2] = { 0.0, 0.0 };
  double output[2] = { 0.0, 0.0 };
  OSCTRL a48 = create_osctrl (1, 1, input, output, 48000, 1.95);
  OSCTRL a96 = create_osctrl (1, 1, input, output, 96000, 1.95);

  assert (a48->pn == 5);
  assert (setBandwidth_osctrl (a48, 4000.0) == 1);
  assert (a48->pn == 3);
  assert (a96->pn == 11);
  assert (setBandwidth_osctrl (a96, 4000.0) == 1);
  assert (a96->pn == 7);

  destroy_osctrl (a96);
  destroy_osctrl (a48);
}

static void test_block_partition_invariance (void) {
  enum { full_size = 32, chunk_size = 4 };
  double input[2 * full_size];
  double output_full[2 * full_size];
  double output_chunked[2 * full_size];
  OSCTRL full;
  OSCTRL chunked;
  int i;

  for (i = 0; i < full_size; i++) {
    double amplitude = (i % 9 == 4) ? 1.8 : 0.2 + 0.02 * i;
    input[2 * i + 0] = amplitude * cos (0.17 * i);
    input[2 * i + 1] = amplitude * sin (0.17 * i);
  }

  full = create_osctrl (1, full_size, input, output_full, 48000, 1.95);
  chunked = create_osctrl (1, chunk_size, input, output_chunked, 48000, 1.95);
  xosctrl (full);

  for (i = 0; i < full_size; i += chunk_size) {
    setBuffers_osctrl (chunked, input + 2 * i, output_chunked + 2 * i);
    xosctrl (chunked);
  }

  for (i = 0; i < 2 * full_size; i++) {
    assert (fabs (output_full[i] - output_chunked[i]) < 1.0e-12);
  }
  assert_finite_and_bounded (output_full, full_size);

  destroy_osctrl (chunked);
  destroy_osctrl (full);
}

static void test_live_bandwidth_change_is_clean_and_bounded (void) {
  enum { size = 16 };
  double input[2 * size];
  double output[2 * size];
  OSCTRL a;
  double* old_delay;
  int i;

  fill_iq (input, size, 0.5, 0.0);
  a = create_osctrl (1, size, input, output, 48000, 1.95);
  xosctrl (a);

  old_delay = a->dl;
  assert (setBandwidth_osctrl (a, 3000.0) == 1);
  assert (a->dl == old_delay);

  assert (setBandwidth_osctrl (a, 4000.0) == 1);
  assert (a->bw == 4000.0);
  assert (a->pn == 3);
  assert (a->dl_len == 1);
  assert (a->max_env == 0.0);
  for (i = 0; i < a->pn; i++) {
    assert (a->dl[2 * i + 0] == 0.0);
    assert (a->dl[2 * i + 1] == 0.0);
    assert (a->dlenv[i] == 0.0);
  }

  fill_iq (input, size, 0.25, 0.0);
  xosctrl (a);
  assert_finite_and_bounded (output, size);
  for (i = 0; i < size; i++) {
    double expected = i < a->dl_len ? 0.0 : 0.25;
    assert (fabs (magnitude_at (output, i) - expected) < 1.0e-12);
  }
  assert (fabs (output[2 * (size - 1)] - 0.25) < 1.0e-12);

  assert (setBandwidth_osctrl (a, 0.0) == 0);
  assert (a->bw == 4000.0);
  destroy_osctrl (a);
}

int main (void) {
  test_window_selection_and_unity_gain ();
  test_peak_control_and_nonfinite_containment ();
  test_flush_clears_all_state ();
  test_live_bandwidth_change_is_clean_and_bounded ();
  test_window_geometry ();
  test_block_partition_invariance ();
  puts ("wdsp osctrl tests passed");
  return 0;
}
