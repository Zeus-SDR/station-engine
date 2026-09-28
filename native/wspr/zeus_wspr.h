// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// zeus_wspr — decode-only C ABI over the vendored K1JT/K9AN wsprd (GPL-3, see
// vendor/). This library is loaded ONLY by the GPL station engine. The
// proprietary Zeus product and web client never load, link, or call it; they
// read decoded spots from the engine's /api/wspr/decoder routes.
//
// The ABI passes only flat C types so the managed P/Invoke stays stable across
// re-vendoring. There is deliberately no encode export: WSPR transmit lives
// outside this library.

#ifndef ZEUS_WSPR_H
#define ZEUS_WSPR_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define ZEUS_WSPR_API __declspec(dllexport)
#else
#define ZEUS_WSPR_API __attribute__((visibility("default")))
#endif

// Bumped whenever a signature or struct layout below changes.
#define ZEUS_WSPR_ABI_VERSION 2

// One WSPR slot at the decoder's native rate.
#define ZEUS_WSPR_SAMPLE_RATE 12000
#define ZEUS_WSPR_SLOT_SAMPLES (114 * 12000)

// Decode flags.
#define ZEUS_WSPR_FLAG_DEEP 0x1  // wsprd -d: deeper candidate search, slower

// Longest data directory accepted. wsprd builds its file names in 200-byte
// buffers and appends up to 15 characters.
#define ZEUS_WSPR_MAX_DATA_DIR 180

// Return codes (negative) from zeus_wspr_decode.
#define ZEUS_WSPR_E_ARGS    (-1)  // null pointer, bad count, bad label
#define ZEUS_WSPR_E_RATE    (-2)  // sample_rate is not 12000
#define ZEUS_WSPR_E_DIR     (-3)  // data dir missing, unwritable, or too long
#define ZEUS_WSPR_E_IO      (-4)  // could not write the slot audio file
#define ZEUS_WSPR_E_DECODER (-5)  // the decoder rejected its arguments

// One decoded WSPR spot.
typedef struct
{
    double freq_mhz;   // absolute decoded frequency, MHz
    float snr_db;      // signal-to-noise ratio, dB in 2500 Hz
    float dt_sec;      // time offset, seconds
    int32_t drift_hz;  // frequency drift, Hz
    char message[32];  // e.g. "KB2UKA FN30 37", "PJ4/K1ABC 37", "<KB2UKA> FN30AB 37"
} zeus_wspr_spot_t;

// Decode one two-minute WSPR slot of mono audio.
//   samples       mono float PCM in [-1,1] at 12 kHz; the first 114 s are used,
//                 a shorter buffer is zero-padded
//   n             number of samples
//   sample_rate   must be 12000
//   dial_freq_mhz transceiver USB dial frequency, MHz (labels decoded frequencies)
//   data_dir      existing, writable, PERSISTENT directory. The decoder's
//                 callsign hash table (hashtable.txt) lives here so hashed
//                 type-2/3 callsigns resolve across slots and restarts. The
//                 slot audio and the decoder's scratch files are written here
//                 and removed before the call returns.
//   slot_label    "YYMMDD_HHMM" (UTC slot start); names the scratch audio file
//   flags         ZEUS_WSPR_FLAG_* bits
//   out           caller-allocated spots, capacity max_results
// Returns the number of spots (>= 0) or a negative ZEUS_WSPR_E_* code.
//
// Thread-safety: serialised internally (the vendored decoder keeps process-
// global state). The decoder allocates about 0.8 MB on the calling thread's
// stack; call it from a thread with at least 4 MB of stack (the engine uses 16 MB).
ZEUS_WSPR_API int32_t zeus_wspr_decode(const float* samples, int32_t n,
                                       int32_t sample_rate, double dial_freq_mhz,
                                       const char* data_dir, const char* slot_label,
                                       int32_t flags,
                                       zeus_wspr_spot_t* out, int32_t max_results);

// ZEUS_WSPR_ABI_VERSION of this build.
ZEUS_WSPR_API int32_t zeus_wspr_abi_version(void);

// Library version string (diagnostics / about panel).
ZEUS_WSPR_API const char* zeus_wspr_version(void);

#ifdef __cplusplus
}
#endif

#endif // ZEUS_WSPR_H
