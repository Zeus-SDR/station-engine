// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// zeus_wspr_decode — portable decode ABI over the vendored wsprd (GPL-3),
// keeping the vendored source pristine. wsprd's decode lives in its command
// line main() (renamed to wsprd_cli_main at build time), which reads a WAV and
// writes spots to <data_dir>/wspr_spots.txt. This shim writes the slot audio
// into the caller's persistent data directory, runs the decoder there, parses
// the spot file, and removes every scratch file it or the decoder created. The
// decoder's callsign hash table (hashtable.txt) is the one file that stays, so
// hashed type-2/3 callsigns resolve across slots and restarts.
//
// Portability: Windows (MinGW-w64), macOS, Linux x64/arm64. The vendored
// decoder parses its options with getopt, whose global cursor must be reset
// before every call; glibc and MinGW-w64 reset with optind = 0, BSD libc
// (macOS) with optreset.

#include "zeus_wspr.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#if defined(_WIN32)
#include <windows.h>
#include <getopt.h>
#else
#include <pthread.h>
#include <unistd.h>
#endif

#if defined(_MSC_VER)
#error "zeus_wspr builds with GCC or Clang (MinGW-w64 on Windows); the vendored decoder uses POSIX getopt."
#endif

extern int wsprd_cli_main(int argc, char* argv[]);

#if defined(_WIN32)
static SRWLOCK g_wspr_lock = SRWLOCK_INIT;
static void wspr_lock(void) { AcquireSRWLockExclusive(&g_wspr_lock); }
static void wspr_unlock(void) { ReleaseSRWLockExclusive(&g_wspr_lock); }
#else
static pthread_mutex_t g_wspr_lock = PTHREAD_MUTEX_INITIALIZER;
static void wspr_lock(void) { pthread_mutex_lock(&g_wspr_lock); }
static void wspr_unlock(void) { pthread_mutex_unlock(&g_wspr_lock); }
#endif

static void reset_getopt(void)
{
#if defined(__GLIBC__) || defined(_WIN32)
    // glibc and MinGW-w64 (OpenBSD-derived getopt): optind = 0 requests a
    // full re-initialisation. MinGW-w64 does not declare optreset.
    optind = 0;
#else
    optreset = 1;  // BSD libc (macOS)
    optind = 1;
#endif
}

static void wr16(unsigned char* p, uint16_t v) { p[0] = (unsigned char)(v & 255); p[1] = (unsigned char)(v >> 8); }
static void wr32(unsigned char* p, uint32_t v) { for (int i = 0; i < 4; i++) p[i] = (unsigned char)((v >> (8 * i)) & 255); }

// A label must be exactly "YYMMDD_HHMM": wsprd reads the date and time back
// out of the file name at fixed offsets.
static int valid_label(const char* label)
{
    if (label == NULL || strlen(label) != 11) return 0;
    for (int i = 0; i < 11; ++i)
    {
        char c = label[i];
        if (i == 6) { if (c != '_') return 0; }
        else if (c < '0' || c > '9') return 0;
    }
    return 1;
}

static int write_slot_wav(const char* path, const float* samples, int32_t n)
{
    const long want = (long)ZEUS_WSPR_SLOT_SAMPLES;
    const uint32_t data_bytes = (uint32_t)(want * 2);
    unsigned char* buf = (unsigned char*)malloc(44 + (size_t)data_bytes);
    if (buf == NULL) return 0;

    memcpy(buf, "RIFF", 4); wr32(buf + 4, 36 + data_bytes); memcpy(buf + 8, "WAVE", 4);
    memcpy(buf + 12, "fmt ", 4); wr32(buf + 16, 16); wr16(buf + 20, 1); wr16(buf + 22, 1);
    wr32(buf + 24, ZEUS_WSPR_SAMPLE_RATE); wr32(buf + 28, ZEUS_WSPR_SAMPLE_RATE * 2);
    wr16(buf + 32, 2); wr16(buf + 34, 16);
    memcpy(buf + 36, "data", 4); wr32(buf + 40, data_bytes);

    long ncopy = (n < want) ? n : want;
    for (long i = 0; i < want; ++i)
    {
        float v = (i < ncopy) ? samples[i] : 0.0f;
        if (v != v) v = 0.0f;  // NaN
        if (v > 1.0f) v = 1.0f; else if (v < -1.0f) v = -1.0f;
        wr16(buf + 44 + 2 * i, (uint16_t)(int16_t)(v * 32767.0f));
    }

    FILE* f = fopen(path, "wb");
    if (f == NULL) { free(buf); return 0; }
    size_t written = fwrite(buf, 1, 44 + (size_t)data_bytes, f);
    int closed = fclose(f) == 0;
    free(buf);
    if (written != 44 + (size_t)data_bytes || !closed)
    {
        remove(path);
        return 0;
    }
    return 1;
}

// Parse wsprd's wspr_spots.txt:
//   date time 10*sync snr dt freq  <message:%-22s> drift cycles jitter
static int32_t parse_spots(const char* path, zeus_wspr_spot_t* out, int32_t max_results)
{
    int32_t count = 0;
    FILE* sp = fopen(path, "r");
    if (sp == NULL) return 0;
    char line[512];
    while (count < max_results && fgets(line, sizeof line, sp))
    {
        char date[24], tm[24], sync[24];
        float snr = 0, dt = 0;
        double freq = 0;
        int pos = 0;
        if (sscanf(line, "%23s %23s %23s %f %f %lf %n", date, tm, sync, &snr, &dt, &freq, &pos) != 6
            || pos <= 0)
            continue;

        const char* m = line + pos;  // start of the fixed-width message field
        char msg[sizeof out[0].message];
        int j = 0;
        for (int k = 0; k < 22 && m[k] && m[k] != '\n' && m[k] != '\r'; ++k)
            msg[j++] = m[k];
        msg[j] = '\0';
        while (j > 0 && msg[j - 1] == ' ') msg[--j] = '\0';
        if (j == 0) continue;

        int drift = 0;
        if (strlen(m) >= 22) sscanf(m + 22, " %d", &drift);

        out[count].freq_mhz = freq;
        out[count].snr_db = snr;
        out[count].dt_sec = dt;
        out[count].drift_hz = drift;
        memcpy(out[count].message, msg, (size_t)j + 1);
        ++count;
    }
    fclose(sp);
    return count;
}

static void join(char* dst, size_t cap, const char* dir, const char* name)
{
    snprintf(dst, cap, "%s/%s", dir, name);
}

int32_t zeus_wspr_decode(const float* samples, int32_t n, int32_t sample_rate,
                         double dial_freq_mhz, const char* data_dir, const char* slot_label,
                         int32_t flags, zeus_wspr_spot_t* out, int32_t max_results)
{
    if (samples == NULL || out == NULL || n <= 0 || max_results <= 0 || !valid_label(slot_label))
        return ZEUS_WSPR_E_ARGS;
    if (sample_rate != ZEUS_WSPR_SAMPLE_RATE)
        return ZEUS_WSPR_E_RATE;
    if (data_dir == NULL || data_dir[0] == '\0' || strlen(data_dir) > ZEUS_WSPR_MAX_DATA_DIR)
        return ZEUS_WSPR_E_DIR;
#if defined(_WIN32)
    // The vendored decoder opens files with the narrow CRT, which reads paths
    // in the ANSI code page: a non-ASCII UTF-8 path would silently fail there.
    // The caller resolves an ASCII directory (short path or ProgramData).
    for (const unsigned char* c = (const unsigned char*)data_dir; *c; ++c)
        if (*c >= 0x80) return ZEUS_WSPR_E_DIR;
#endif

    char wav[256], spots[256], all[256], timer[256], freqstr[32], dirarg[ZEUS_WSPR_MAX_DATA_DIR + 1];
    char label[16];
    snprintf(label, sizeof label, "%s.wav", slot_label);
    join(wav, sizeof wav, data_dir, label);
    join(spots, sizeof spots, data_dir, "wspr_spots.txt");
    join(all, sizeof all, data_dir, "ALL_WSPR.TXT");
    join(timer, sizeof timer, data_dir, "wspr_timer.out");
    snprintf(freqstr, sizeof freqstr, "%.6f", dial_freq_mhz);
    snprintf(dirarg, sizeof dirarg, "%s", data_dir);

    wspr_lock();

    if (!write_slot_wav(wav, samples, n))
    {
        wspr_unlock();
        return ZEUS_WSPR_E_IO;
    }

    char a0[] = "wsprd", a1[] = "-a", a3[] = "-f", ad[] = "-d";
    char* argv[8];
    int argc = 0;
    argv[argc++] = a0;
    argv[argc++] = a1;
    argv[argc++] = dirarg;
    argv[argc++] = a3;
    argv[argc++] = freqstr;
    if (flags & ZEUS_WSPR_FLAG_DEEP) argv[argc++] = ad;
    argv[argc++] = wav;
    argv[argc] = NULL;

    remove(spots);
    reset_getopt();
    int rc = wsprd_cli_main(argc, argv);

    int32_t count = rc == 0 ? parse_spots(spots, out, max_results) : ZEUS_WSPR_E_DECODER;

    // Scratch only: the slot audio, the per-slot spot list, the decoder's
    // ever-growing ALL_WSPR.TXT log and its timing file. hashtable.txt stays.
    remove(wav);
    remove(spots);
    remove(all);
    remove(timer);

    wspr_unlock();
    return count;
}

int32_t zeus_wspr_abi_version(void)
{
    return ZEUS_WSPR_ABI_VERSION;
}

const char* zeus_wspr_version(void)
{
    return "zeus_wspr 2.0 (wsprd K1JT/K9AN via pavel-demin/wsprd 8aa9030, GPL-3)";
}
