// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WSPR decode round-trip gate through the production zeus_wspr_decode ABI.
// The vendored wsprd channel-symbol generator (linked into this test only; the
// shipped library exports no encoder) produces 162 symbols, the test renders
// continuous-phase 4-FSK at 12 kHz, and the decoder must recover the message.
//
// It also proves the three properties the engine relies on:
//   1. repeated calls work (the getopt cursor is reset between calls);
//   2. the callsign hash table persists in the data directory, so a type-3
//      message decoded in a LATER call resolves "<KB2UKA>" instead of "<...>";
//   3. only hashtable.txt survives a call — no scratch file accumulates.
//
// usage: zeus_wspr_roundtrip <scratch-dir>   (the directory must exist)

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../zeus_wspr.h"

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

extern int get_wspr_channel_symbols(const char* message, char* hashtab, char* loctab, unsigned char* symbols);

static int fail(const char* what)
{
    fprintf(stderr, "FAIL: %s\n", what);
    return 1;
}

static int exists(const char* dir, const char* name)
{
    char path[512];
    snprintf(path, sizeof path, "%s/%s", dir, name);
    FILE* f = fopen(path, "rb");
    if (f == NULL) return 0;
    fclose(f);
    return 1;
}

// Render one message into a zeroed 114 s slot, starting 1 s in at 1500 Hz audio.
static int render(const char* message, float* slot)
{
    char msg[32];
    snprintf(msg, sizeof msg, "%s", message);
    char* hashtab = (char*)calloc(32768 * 13, 1);
    char* loctab = (char*)calloc(32768 * 5, 1);
    unsigned char sym[162];
    int ok = hashtab && loctab && get_wspr_channel_symbols(msg, hashtab, loctab, sym);
    free(hashtab);
    free(loctab);
    if (!ok) return 0;

    memset(slot, 0, sizeof(float) * ZEUS_WSPR_SLOT_SAMPLES);
    const int sps = 8192;  // samples per symbol at 12 kHz
    const long start = 12000;
    double phase = 0.0;
    for (int s = 0; s < 162; ++s)
    {
        double f = 1500.0 + sym[s] * (12000.0 / 8192.0);
        double dphi = 2.0 * M_PI * f / 12000.0;
        for (int i = 0; i < sps; ++i)
        {
            long k = start + (long)s * sps + i;
            if (k >= ZEUS_WSPR_SLOT_SAMPLES) break;
            slot[k] = (float)(0.3 * sin(phase));
            phase += dphi;
        }
    }
    return 1;
}

static int decode_expect(const char* dir, const float* slot, const char* label, const char* expect)
{
    zeus_wspr_spot_t spots[16];
    int32_t n = zeus_wspr_decode(slot, ZEUS_WSPR_SLOT_SAMPLES, 12000, 14.0956, dir, label, 0, spots, 16);
    if (n < 0) { fprintf(stderr, "decode returned %d\n", n); return 0; }
    int found = 0;
    for (int i = 0; i < n; ++i)
    {
        fprintf(stderr, "  spot %5.1f dB %+4.1f s %.6f MHz drift %d '%s'\n",
                spots[i].snr_db, spots[i].dt_sec, spots[i].freq_mhz, spots[i].drift_hz, spots[i].message);
        if (strcmp(spots[i].message, expect) == 0
            && fabs(spots[i].freq_mhz - (14.0956 + 1500.0e-6)) < 8e-6)
            found = 1;
    }
    return found;
}

int main(int argc, char** argv)
{
    if (argc != 2) return fail("usage: zeus_wspr_roundtrip <scratch-dir>");
    const char* dir = argv[1];

    // Start from an empty hash table so the "unresolved" step is meaningful on
    // every run, not only the first.
    char stale[512];
    snprintf(stale, sizeof stale, "%s/hashtable.txt", dir);
    remove(stale);

    if (zeus_wspr_abi_version() != ZEUS_WSPR_ABI_VERSION) return fail("abi version");

    float* slot = (float*)malloc(sizeof(float) * ZEUS_WSPR_SLOT_SAMPLES);
    if (slot == NULL) return fail("alloc");

    zeus_wspr_spot_t probe[1];
    if (zeus_wspr_decode(slot, 10, 48000, 14.0956, dir, "260927_1200", 0, probe, 1) != ZEUS_WSPR_E_RATE)
        return fail("wrong sample rate must be rejected");
    if (zeus_wspr_decode(slot, 10, 12000, 14.0956, dir, "bad", 0, probe, 1) != ZEUS_WSPR_E_ARGS)
        return fail("malformed label must be rejected");

    // Type 3 before the call is known: the hash cannot resolve yet.
    if (!render("<KB2UKA> FN30AB 37", slot)) return fail("render type 3");
    if (!decode_expect(dir, slot, "260927_1200", "<...> FN30AB 37")) return fail("unresolved type 3");

    // Type 1 teaches the hash table the call.
    if (!render("KB2UKA FN30 37", slot)) return fail("render type 1");
    if (!decode_expect(dir, slot, "260927_1202", "KB2UKA FN30 37")) return fail("type 1 decode");

    // A later call resolves the hashed call from the persisted table.
    if (!render("<KB2UKA> FN30AB 37", slot)) return fail("render type 3 again");
    if (!decode_expect(dir, slot, "260927_1204", "<KB2UKA> FN30AB 37")) return fail("resolved type 3");

    // Deep search decodes the same slot.
    zeus_wspr_spot_t spots[16];
    if (!render("KB2UKA FN30 37", slot)) return fail("render deep");
    int32_t deep = zeus_wspr_decode(slot, ZEUS_WSPR_SLOT_SAMPLES, 12000, 14.0956, dir, "260927_1206",
                                    ZEUS_WSPR_FLAG_DEEP, spots, 16);
    if (deep < 1 || strcmp(spots[0].message, "KB2UKA FN30 37") != 0) return fail("deep decode");

    // Silence decodes nothing.
    memset(slot, 0, sizeof(float) * ZEUS_WSPR_SLOT_SAMPLES);
    if (zeus_wspr_decode(slot, ZEUS_WSPR_SLOT_SAMPLES, 12000, 14.0956, dir, "260927_1208", 0, spots, 16) != 0)
        return fail("silence must decode nothing");
    free(slot);

    if (!exists(dir, "hashtable.txt")) return fail("hashtable.txt must persist");
    if (exists(dir, "ALL_WSPR.TXT") || exists(dir, "wspr_timer.out") || exists(dir, "wspr_spots.txt")
        || exists(dir, "260927_1200.wav"))
        return fail("scratch files must be removed");

    fprintf(stderr, "PASS: WSPR decode round trip\n");
    return 0;
}
