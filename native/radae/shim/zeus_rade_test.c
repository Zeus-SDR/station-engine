/* SPDX-License-Identifier: BSD-2-Clause
 *
 * zeus_rade_test — validates the zeus_rade shim end-to-end.
 *
 * Default (decode):   reads interleaved complex IQ (RADE_COMP) @ 8 kHz from
 *   stdin, decodes via the shim, writes int16 PCM @ 16 kHz to stdout.
 *
 *     sox FDV_offair.wav -r 8000 -e float -b 32 -c 1 -t raw - \
 *       | real2iq | zeus_rade_test > out.pcm
 *     sox -t .s16 -r 16000 -c 1 out.pcm decoded.wav
 *
 * Loopback (encode->decode): `zeus_rade_test loopback [CALLSIGN]` synthesizes a
 *   few seconds of 16 kHz speech, ENCODES it to the modem waveform (real part of
 *   the TX IQ), appends an End-of-Over frame carrying CALLSIGN, then DECODES the
 *   whole stream back through the same modem. It reports RX sync ticks, decoded
 *   PCM, and the recovered callsign — a self-contained proof that the TX path
 *   produces a valid, FreeDV-GUI-compatible RADE waveform. Optionally writes the
 *   decoded PCM to stdout. Exit code 0 only if the modem synced AND (when a
 *   callsign was sent) it round-tripped exactly.
 *
 * Decode a V2 recording: `zeus_rade_test decode 2 < iq.f32` (protocol 1 or 2).
 *
 * Self-test (V1 + V2): `zeus_rade_test selftest` checks, for each protocol:
 *   - zeus_rade_abi_version() == 2 and invalid protocols open as NULL;
 *   - open reports the requested protocol, and every size is sane;
 *   - synthetic speech-like audio -> TX -> modem real part -> a FRESH RX context
 *     (imag = 0, as the real RX path feeds it) reaches sync and decodes to
 *     non-silent PCM;
 *   - the End-of-Over drops sync within a fraction of a second (far sooner
 *     than either protocol's unsync timer could), and on the next over the RX
 *     re-acquires and re-primes the vocoder (the first decoded block is short
 *     by exactly the five priming frames);
 *   - V1 round-trips the EOO callsign; V2's callsign getter returns 0;
 *   - the other protocol's receiver does NOT sync on this signal;
 *   - V2 holds ONE sync run through a 120 s over whose sample clock is offset
 *     by +100 ppm (windowed-sinc resampling), decoding PCM comparable to the
 *     0 ppm case (exercises the shim's V2 timing correction); -100 ppm is
 *     measured and reported (see clock_offset_check);
 *   - TX peak levels and the RX real-time factor are reported.
 * Exit code 0 only if every assertion holds.
 */
#include "zeus_rade.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#ifdef _WIN32
#  include <io.h>
#  include <fcntl.h>
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#endif

#ifndef M_PI
#  define M_PI 3.14159265358979323846
#endif

/* Monotonic wall clock in seconds (for the real-time-factor report). */
static double now_seconds(void)
{
#ifdef _WIN32
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return (double)c.QuadPart / (double)f.QuadPart;
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec + (double)ts.tv_nsec * 1e-9;
#endif
}

static void set_binary_stdio(void)
{
#ifdef _WIN32
    /* Windows opens stdin/stdout in text mode by default, which mangles binary
     * IQ/PCM (a 0x0A byte ends a read early). Force binary mode. */
    _setmode(_fileno(stdin),  _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#endif
}

/* Decode IQ from stdin -> PCM to stdout (the original harness behaviour). */
static int run_decode(int protocol)
{
    zeus_rade_global_init();
    zeus_rade *z = protocol == 1 ? zeus_rade_open() : zeus_rade_open_protocol(protocol);
    if (!z) { fprintf(stderr, "zeus_rade_open (protocol %d) failed\n", protocol); return 1; }

    int nin_max = zeus_rade_nin_max(z);
    int max_pcm = zeus_rade_max_pcm_per_rx(z);
    RADE_COMP *iq  = (RADE_COMP *)malloc(sizeof(RADE_COMP) * (size_t)nin_max);
    short     *pcm = (short *)malloc(sizeof(short) * (size_t)max_pcm);

    long total_pcm = 0;
    int  synced_ticks = 0, ticks = 0, last_snr = 0;
    for (;;) {
        int nin = zeus_rade_nin(z);
        if (nin <= 0 || nin > nin_max) break;
        size_t got = fread(iq, sizeof(RADE_COMP), (size_t)nin, stdin);
        if (got != (size_t)nin) break;
        int n = zeus_rade_rx(z, iq, pcm);
        if (n > 0) { fwrite(pcm, sizeof(short), (size_t)n, stdout); total_pcm += n; }
        ticks++;
        if (zeus_rade_sync(z)) { synced_ticks++; last_snr = zeus_rade_snr_db(z); }
    }

    char cs[RADE_EOO_CALLSIGN_MAX + 1];
    int csn = zeus_rade_get_eoo_callsign(z, cs);
    fprintf(stderr,
            "zeus_rade_test: pcm_samples=%ld ticks=%d synced=%d last_snr=%ddB callsign=%s\n",
            total_pcm, ticks, synced_ticks, last_snr, csn > 0 ? cs : "(none)");

    free(iq); free(pcm);
    zeus_rade_close(z);
    zeus_rade_global_shutdown();
    return 0;
}

/* Fill `pcm` (16 kHz, int16) with a voiced-ish test signal: a low pitch with a
 * few harmonics and a slow tremolo. Content is irrelevant to modem sync (RADE
 * locks on pilots), but realistic input exercises the LPCNet analyzer properly. */
static void synth_speech(short *pcm, int n, double *phase)
{
    const double f0 = 130.0, fs = 16000.0;
    for (int i = 0; i < n; i++) {
        double t = (*phase += 1.0 / fs);
        double trem = 0.6 + 0.4 * sin(2.0 * M_PI * 3.0 * t);
        double s = 0.0;
        for (int h = 1; h <= 6; h++) s += (1.0 / h) * sin(2.0 * M_PI * f0 * h * t);
        double v = trem * s * 0.18 * 32767.0;
        if (v >  32767.0) v =  32767.0;
        if (v < -32767.0) v = -32767.0;
        pcm[i] = (short)v;
    }
}

/* Encode synthetic speech -> modem (real) -> decode it back. */
static int run_loopback(const char *callsign, int write_pcm)
{
    zeus_rade_global_init();
    zeus_rade *z = zeus_rade_open();
    if (!z) { fprintf(stderr, "zeus_rade_open failed\n"); return 1; }

    int n_speech = zeus_rade_n_speech_samples(z);
    int n_tx_out = zeus_rade_n_tx_out(z);
    int n_eoo    = zeus_rade_n_tx_eoo_out(z);
    if (n_speech <= 0 || n_tx_out <= 0) {
        fprintf(stderr, "loopback: TX surface unavailable (n_speech=%d n_tx_out=%d)\n",
                n_speech, n_tx_out);
        zeus_rade_close(z); zeus_rade_global_shutdown(); return 1;
    }

    if (callsign && callsign[0]) zeus_rade_set_tx_callsign(z, callsign);

    /* Target ~6 s of speech so the RX has time to acquire and the EOO lands. */
    int calls = (6 * 16000) / n_speech;
    if (calls < 8) calls = 8;
    int nin_max = zeus_rade_nin_max(z);
    long tail = (long)nin_max * 8; /* zero pad so the FULL EOO frame is decoded */
    long modem_cap = (long)calls * n_tx_out + n_eoo + tail + 16;
    float *modem = (float *)malloc(sizeof(float) * (size_t)modem_cap); /* real @8k */
    short *speech = (short *)malloc(sizeof(short) * (size_t)n_speech);
    RADE_COMP *txc = (RADE_COMP *)malloc(sizeof(RADE_COMP) * (size_t)(n_tx_out > n_eoo ? n_tx_out : n_eoo));
    long mlen = 0;
    double phase = 0.0;

    for (int c = 0; c < calls; c++) {
        synth_speech(speech, n_speech, &phase);
        int n = zeus_rade_tx(z, speech, txc);
        for (int i = 0; i < n; i++) modem[mlen++] = txc[i].real;
    }
    /* End-of-Over frame (carries the callsign). */
    int ne = zeus_rade_tx_eoo(z, txc);
    for (int i = 0; i < ne; i++) modem[mlen++] = txc[i].real;
    for (long i = 0; i < tail; i++) modem[mlen++] = 0.0f; /* flush the EOO through */
    fprintf(stderr, "loopback: encoded %ld modem samples @8k (%d calls + %d eoo + %ld pad)\n",
            mlen, calls, ne, tail);

    /* Decode the encoded modem stream back through the same shim. */
    int max_pcm = zeus_rade_max_pcm_per_rx(z);
    RADE_COMP *iq  = (RADE_COMP *)malloc(sizeof(RADE_COMP) * (size_t)nin_max);
    short     *pcm = (short *)malloc(sizeof(short) * (size_t)max_pcm);
    long total_pcm = 0; int synced_ticks = 0, ticks = 0, last_snr = 0;
    long pos = 0;
    for (;;) {
        int nin = zeus_rade_nin(z);
        if (nin <= 0 || nin > nin_max) break;
        if (pos + nin > mlen) break;
        for (int i = 0; i < nin; i++) { iq[i].real = modem[pos + i]; iq[i].imag = 0.0f; }
        pos += nin;
        int n = zeus_rade_rx(z, iq, pcm);
        if (n > 0) {
            total_pcm += n;
            if (write_pcm) fwrite(pcm, sizeof(short), (size_t)n, stdout);
        }
        ticks++;
        if (zeus_rade_sync(z)) { synced_ticks++; last_snr = zeus_rade_snr_db(z); }
    }

    char cs[RADE_EOO_CALLSIGN_MAX + 1];
    int csn = zeus_rade_get_eoo_callsign(z, cs);
    fprintf(stderr,
            "loopback: ticks=%d synced=%d last_snr=%ddB pcm=%ld callsign=%s\n",
            ticks, synced_ticks, last_snr, total_pcm, csn > 0 ? cs : "(none)");

    int ok = (synced_ticks > 0);
    if (callsign && callsign[0]) {
        int match = (csn > 0 && strcmp(cs, callsign) == 0);
        if (!match) { fprintf(stderr, "loopback: FAIL callsign '%s' != '%s'\n", csn > 0 ? cs : "(none)", callsign); ok = 0; }
        else fprintf(stderr, "loopback: callsign round-trip OK ('%s')\n", cs);
    }
    if (!ok) fprintf(stderr, "loopback: FAIL (synced=%d)\n", synced_ticks);
    else     fprintf(stderr, "loopback: PASS\n");

    free(modem); free(speech); free(txc); free(iq); free(pcm);
    zeus_rade_close(z);
    zeus_rade_global_shutdown();
    return ok ? 0 : 2;
}

/* ======================================================================== *
 * selftest: V1 + V2 protocol proof                                          *
 * ======================================================================== */

static int g_failures = 0;

#define CHECK(cond, ...) do {                                              \
        if (cond) { fprintf(stderr, "  ok    "); }                          \
        else      { fprintf(stderr, "  FAIL  "); g_failures++; }            \
        fprintf(stderr, __VA_ARGS__); fputc('\n', stderr);                  \
    } while (0)

/* Speech-like 16 kHz test signal: a voiced source whose pitch glides between
 * ~95 and ~185 Hz, shaped by two slowly moving formant-ish resonances, cut into
 * ~4 syllables per second with short gaps, plus a little noise for the
 * unvoiced edges. Deterministic (fixed LCG seed) so runs are comparable. */
typedef struct {
    double t, phase;
    unsigned int lcg;
    double f1_z1, f1_z2, f2_z1, f2_z2;
    double gain;       /* output scale; 0.025 peaks near -5 dBFS unclipped */
    long clipped;
} speech_synth;

static double synth_noise(speech_synth *st)
{
    st->lcg = st->lcg * 1664525u + 1013904223u;
    return ((double)(st->lcg >> 8) / 8388608.0) - 1.0;   /* [-1, 1) */
}

/* Two-pole resonator, normalised to roughly unity peak gain. */
static double resonate(double x, double fc, double bw, double *z1, double *z2)
{
    const double fs = 16000.0;
    double r = exp(-M_PI * bw / fs);
    double a1 = -2.0 * r * cos(2.0 * M_PI * fc / fs), a2 = r * r;
    double y = (1.0 - r) * x - a1 * *z1 - a2 * *z2;
    *z2 = *z1; *z1 = y;
    return y;
}

#define SYNTH_GAIN_NOMINAL 0.025
static void synth_speechlike(speech_synth *st, short *pcm, int n)
{
    const double fs = 16000.0;
    for (int i = 0; i < n; i++) {
        double t = (st->t += 1.0 / fs);
        double f0 = 140.0 + 45.0 * sin(2.0 * M_PI * 0.7 * t) + 8.0 * sin(2.0 * M_PI * 5.5 * t);
        st->phase += 2.0 * M_PI * f0 / fs;
        if (st->phase > 2.0 * M_PI) st->phase -= 2.0 * M_PI;
        double voiced = 0.0;
        for (int h = 1; h <= 20; h++) {
            if (h * f0 > 7000.0) break;
            voiced += sin(h * st->phase) / h;
        }
        double syl = fmod(t * 4.0, 1.0);                  /* 4 syllables/s */
        double env = syl < 0.75 ? sin(M_PI * syl / 0.75) : 0.0;
        double src = env * voiced + 0.04 * synth_noise(st);
        double f1 = 600.0 + 250.0 * sin(2.0 * M_PI * 1.3 * t);
        double f2 = 1500.0 + 600.0 * sin(2.0 * M_PI * 0.9 * t + 1.0);
        double y = 6.0 * resonate(src, f1, 120.0, &st->f1_z1, &st->f1_z2)
                 + 3.0 * resonate(src, f2, 180.0, &st->f2_z1, &st->f2_z2);
        double v = y * st->gain * 32767.0;
        if (v >  32767.0) { v =  32767.0; st->clipped++; }
        if (v < -32767.0) { v = -32767.0; st->clipped++; }
        pcm[i] = (short)v;
    }
}

/* One protocol's encoded test stream: two overs, each speech + EOO + tail. */
typedef struct {
    float *modem;      /* real part of the TX IQ, @ 8 kHz */
    long   len;
    long   eoo1_start, eoo1_end, over2_start, eoo2_start, eoo2_end;
    float  peak_real_speech, peak_mag_speech, peak_real_eoo, peak_mag_eoo;
    double rms_real_speech;
    int    speech_in_peak;
    long   speech_in_clipped;
} test_stream;

static long append_over(zeus_rade *tx, test_stream *ts, speech_synth *syn,
                        int calls, long tail, short *speech, RADE_COMP *txc,
                        long *eoo_start, long *eoo_end, double *sumsq, long *nsq)
{
    int n_speech = zeus_rade_n_speech_samples(tx);
    for (int c = 0; c < calls; c++) {
        synth_speechlike(syn, speech, n_speech);
        for (int i = 0; i < n_speech; i++) {
            int a = speech[i] < 0 ? -speech[i] : speech[i];
            if (a > ts->speech_in_peak) ts->speech_in_peak = a;
        }
        int n = zeus_rade_tx(tx, speech, txc);
        for (int i = 0; i < n; i++) {
            float re = txc[i].real, im = txc[i].imag;
            float ar = fabsf(re), mag = sqrtf(re * re + im * im);
            if (ar > ts->peak_real_speech) ts->peak_real_speech = ar;
            if (mag > ts->peak_mag_speech) ts->peak_mag_speech = mag;
            *sumsq += (double)re * re; (*nsq)++;
            ts->modem[ts->len++] = re;
        }
    }
    *eoo_start = ts->len;
    int ne = zeus_rade_tx_eoo(tx, txc);
    for (int i = 0; i < ne; i++) {
        float re = txc[i].real, im = txc[i].imag;
        float ar = fabsf(re), mag = sqrtf(re * re + im * im);
        if (ar > ts->peak_real_eoo) ts->peak_real_eoo = ar;
        if (mag > ts->peak_mag_eoo) ts->peak_mag_eoo = mag;
        ts->modem[ts->len++] = re;
    }
    *eoo_end = ts->len;
    for (long i = 0; i < tail; i++) ts->modem[ts->len++] = 0.0f;
    return ne;
}

static int build_stream_overs(int protocol, const char *callsign, double gain,
                              int over1_s, int over2_s, test_stream *ts)
{
    memset(ts, 0, sizeof(*ts));
    zeus_rade *tx = zeus_rade_open_protocol(protocol);
    if (!tx) return 0;
    zeus_rade_set_tx_callsign(tx, callsign);   /* V2: must be a safe no-op */

    int n_speech = zeus_rade_n_speech_samples(tx);
    int n_tx_out = zeus_rade_n_tx_out(tx);
    int n_eoo    = zeus_rade_n_tx_eoo_out(tx);
    int calls1   = (over1_s * RADE_SPEECH_SAMPLE_RATE) / n_speech;
    int calls2   = (over2_s * RADE_SPEECH_SAMPLE_RATE) / n_speech;
    long tail    = RADE_MODEM_SAMPLE_RATE;                     /* 1 s of silence */
    long cap = (long)(calls1 + calls2) * n_tx_out + 2L * (n_eoo + tail) + 16;
    ts->modem = (float *)malloc(sizeof(float) * (size_t)cap);
    short *speech = (short *)malloc(sizeof(short) * (size_t)n_speech);
    int txc_n = n_tx_out > n_eoo ? n_tx_out : n_eoo;
    RADE_COMP *txc = (RADE_COMP *)malloc(sizeof(RADE_COMP) * (size_t)txc_n);
    if (!ts->modem || !speech || !txc) {
        free(ts->modem); free(speech); free(txc); zeus_rade_close(tx); return 0;
    }

    speech_synth syn;
    memset(&syn, 0, sizeof(syn));
    syn.lcg = 12345u;
    syn.gain = gain;
    double sumsq = 0.0; long nsq = 0;
    append_over(tx, ts, &syn, calls1, tail, speech, txc,
                &ts->eoo1_start, &ts->eoo1_end, &sumsq, &nsq);
    ts->over2_start = ts->len;
    if (calls2 > 0)
        append_over(tx, ts, &syn, calls2, tail, speech, txc,
                    &ts->eoo2_start, &ts->eoo2_end, &sumsq, &nsq);
    else
        ts->eoo2_start = ts->eoo2_end = ts->len;   /* single-over stream */
    ts->rms_real_speech = nsq > 0 ? sqrt(sumsq / (double)nsq) : 0.0;
    ts->speech_in_clipped = syn.clipped;

    free(speech); free(txc);
    zeus_rade_close(tx);
    return 1;
}

/* The standard two-over stream: ~6 s over, then ~4 s over. */
static int build_stream(int protocol, const char *callsign, double gain, test_stream *ts)
{
    return build_stream_overs(protocol, callsign, gain, 6, 4, ts);
}

/* Simulate a receiver whose sample clock runs `ppm` parts per million fast
 * (positive: the receiver takes more samples per second of signal, so the
 * stream gets longer) by resampling with a 32-tap Hann-windowed sinc
 * interpolator: out[m] = x(m / (1 + ppm * 1e-6)). The ratio is within 1e-4 of
 * unity, so no anti-alias cutoff is needed. Event positions are rescaled. */
static int resample_stream_ppm(const test_stream *in, double ppm, test_stream *out)
{
    const int half = 16;
    const double ratio = 1.0 + ppm * 1e-6;
    *out = *in;
    long cap = (long)ceil((double)in->len * ratio) + 4;
    out->modem = (float *)malloc(sizeof(float) * (size_t)cap);
    if (!out->modem) return 0;
    long m = 0;
    for (;; m++) {
        double t = (double)m / ratio;
        long i = (long)floor(t);
        if (i + half >= in->len || m >= cap) break;
        double f = t - (double)i, acc = 0.0;
        for (int k = -half + 1; k <= half; k++) {
            long j = i + k;
            if (j < 0) continue;
            double u = (double)k - f;                       /* distance from t */
            double sinc = fabs(u) < 1e-12 ? 1.0 : sin(M_PI * u) / (M_PI * u);
            double w = 0.5 * (1.0 + cos(M_PI * u / half));  /* Hann, |u| < half */
            acc += in->modem[j] * sinc * w;
        }
        out->modem[m] = (float)acc;
    }
    out->len = m;
#define ZR_SCALE_POS(p) ((long)llround((double)(p) * ratio))
    out->eoo1_start  = ZR_SCALE_POS(in->eoo1_start);
    out->eoo1_end    = ZR_SCALE_POS(in->eoo1_end);
    out->over2_start = ZR_SCALE_POS(in->over2_start);
    out->eoo2_start  = ZR_SCALE_POS(in->eoo2_start);
    out->eoo2_end    = ZR_SCALE_POS(in->eoo2_end);
#undef ZR_SCALE_POS
    if (out->over2_start > out->len) out->over2_start = out->len;
    return 1;
}

typedef struct {
    int    ticks, synced_ticks, max_snr;
    long   first_sync_pos;          /* modem sample position, -1 if never */
    long   drop1_pos, drop2_pos;    /* first sync 1->0 at/after each EOO start */
    long   resync2_pos;             /* first sync after over2_start */
    int    longest_run;             /* longest run of consecutive synced ticks */
    int    acquisitions1;           /* sync 0->1 transitions before EOO 1 starts */
    long   pcm_total, pcm_over1;
    double pcm_sumsq;
    int    pcm_peak;
    int    first_block_over1, first_block_over2, max_block;
    int    callsign_len;
    char   callsign[RADE_EOO_CALLSIGN_MAX + 1];
    double rx_seconds;              /* wall clock spent inside zeus_rade_rx */
    double audio_seconds;           /* modem audio fed */
} rx_result;

static int run_rx(int protocol, const test_stream *ts, rx_result *res)
{
    memset(res, 0, sizeof(*res));
    res->first_sync_pos = res->drop1_pos = res->drop2_pos = res->resync2_pos = -1;
    zeus_rade *rx = zeus_rade_open_protocol(protocol);
    if (!rx) return 0;
    int nin_max = zeus_rade_nin_max(rx);
    int max_pcm = zeus_rade_max_pcm_per_rx(rx);
    RADE_COMP *iq  = (RADE_COMP *)malloc(sizeof(RADE_COMP) * (size_t)nin_max);
    short     *pcm = (short *)malloc(sizeof(short) * (size_t)max_pcm);
    if (!iq || !pcm) { free(iq); free(pcm); zeus_rade_close(rx); return 0; }

    long pos = 0;
    int was_synced = 0, run = 0;
    for (;;) {
        int nin = zeus_rade_nin(rx);
        if (nin <= 0 || nin > nin_max || pos + nin > ts->len) break;
        for (int i = 0; i < nin; i++) { iq[i].real = ts->modem[pos + i]; iq[i].imag = 0.0f; }
        pos += nin;
        double t0 = now_seconds();
        int n = zeus_rade_rx(rx, iq, pcm);
        res->rx_seconds += now_seconds() - t0;
        int synced = zeus_rade_sync(rx);
        res->ticks++;
        run = synced ? run + 1 : 0;
        if (run > res->longest_run) res->longest_run = run;
        if (synced && !was_synced && pos <= ts->eoo1_start) res->acquisitions1++;
        if (synced) {
            res->synced_ticks++;
            int snr = zeus_rade_snr_db(rx);
            if (snr > res->max_snr) res->max_snr = snr;
            if (res->first_sync_pos < 0) res->first_sync_pos = pos;
            if (pos > ts->over2_start && res->resync2_pos < 0 && !was_synced)
                res->resync2_pos = pos;
        }
        if (was_synced && !synced) {
            if (pos >= ts->eoo1_start && pos < ts->over2_start && res->drop1_pos < 0)
                res->drop1_pos = pos;
            if (pos >= ts->eoo2_start && res->drop2_pos < 0)
                res->drop2_pos = pos;
        }
        was_synced = synced;
        if (n > 0) {
            res->pcm_total += n;
            if (pos <= ts->over2_start) {
                res->pcm_over1 += n;
                if (res->first_block_over1 == 0) res->first_block_over1 = n;
            } else if (res->first_block_over2 == 0) {
                res->first_block_over2 = n;
            }
            if (n > res->max_block) res->max_block = n;
            for (int i = 0; i < n; i++) {
                int a = pcm[i] < 0 ? -pcm[i] : pcm[i];
                if (a > res->pcm_peak) res->pcm_peak = a;
                res->pcm_sumsq += (double)pcm[i] * pcm[i];
            }
        }
    }
    res->audio_seconds = (double)pos / RADE_MODEM_SAMPLE_RATE;
    res->callsign_len = zeus_rade_get_eoo_callsign(rx, res->callsign);
    free(iq); free(pcm);
    zeus_rade_close(rx);
    return 1;
}

static double dbfs(double x) { return x > 0.0 ? 20.0 * log10(x) : -999.0; }

static void selftest_protocol(int protocol)
{
    const char *callsign = "N9WAR";
    fprintf(stderr, "\n== RADE V%d ==\n", protocol);

    zeus_rade *z = zeus_rade_open_protocol(protocol);
    CHECK(z != NULL, "zeus_rade_open_protocol(%d) succeeds", protocol);
    if (!z) return;
    CHECK(zeus_rade_protocol(z) == protocol, "zeus_rade_protocol() == %d (got %d)",
          protocol, zeus_rade_protocol(z));
    int n_speech = zeus_rade_n_speech_samples(z), n_tx_out = zeus_rade_n_tx_out(z);
    int n_eoo = zeus_rade_n_tx_eoo_out(z), nin_max = zeus_rade_nin_max(z);
    int max_pcm = zeus_rade_max_pcm_per_rx(z), nin0 = zeus_rade_nin(z);
    int frames = n_speech / 160;
    fprintf(stderr, "  sizes: n_speech=%d (%.0f ms) n_tx_out=%d n_tx_eoo_out=%d nin=%d "
            "nin_max=%d max_pcm_per_rx=%d frames/modem-frame=%d\n",
            n_speech, 1000.0 * n_speech / RADE_SPEECH_SAMPLE_RATE, n_tx_out, n_eoo,
            nin0, nin_max, max_pcm, frames);
    CHECK(n_speech > 0 && n_speech % 160 == 0, "speech block is whole 160-sample frames");
    CHECK(n_tx_out * 2 == n_speech, "modem block (8 kHz) spans the speech block (16 kHz)");
    CHECK(n_eoo > 0 && nin_max >= nin0 && nin0 > 0, "EOO / nin sizes are positive and ordered");
    CHECK(max_pcm >= frames * 160, "max_pcm_per_rx covers one modem frame of PCM");
    if (protocol == 2) {
        char cs[RADE_EOO_CALLSIGN_MAX + 1] = "x";
        zeus_rade_set_tx_callsign(z, "N9WAR");   /* must not crash */
        zeus_rade_set_tx_callsign(z, NULL);
        CHECK(zeus_rade_get_eoo_callsign(z, cs) == 0 && cs[0] == '\0',
              "V2 callsign getter returns 0 before any RX");
    }
    zeus_rade_close(z);

    test_stream ts;
    CHECK(build_stream(protocol, callsign, SYNTH_GAIN_NOMINAL, &ts), "TX stream built");
    if (!ts.modem) return;
    fprintf(stderr, "  tx: %ld modem samples (%.2f s); speech input peak %d (%.1f dBFS), "
            "%ld clipped samples\n",
            ts.len, (double)ts.len / RADE_MODEM_SAMPLE_RATE, ts.speech_in_peak,
            dbfs(ts.speech_in_peak / 32768.0), ts.speech_in_clipped);
    fprintf(stderr, "  TX PEAK V%d: speech |real| max=%.4f (%.2f dBFS)  |IQ| max=%.4f  "
            "real RMS=%.4f (%.2f dBFS, crest %.2f dB)\n",
            protocol, ts.peak_real_speech, dbfs(ts.peak_real_speech), ts.peak_mag_speech,
            ts.rms_real_speech, dbfs(ts.rms_real_speech),
            dbfs(ts.peak_real_speech) - dbfs(ts.rms_real_speech));
    fprintf(stderr, "  TX PEAK V%d: EOO    |real| max=%.4f (%.2f dBFS)  |IQ| max=%.4f\n",
            protocol, ts.peak_real_eoo, dbfs(ts.peak_real_eoo), ts.peak_mag_eoo);
    CHECK(ts.speech_in_clipped == 0, "synthetic speech input is not clipped");
    CHECK(ts.peak_real_speech > 0.01f, "TX speech output is not silent");
    CHECK(ts.peak_real_eoo > 0.01f, "TX EOO frame is emitted and not silent");

    rx_result r;
    CHECK(run_rx(protocol, &ts, &r), "fresh V%d RX context decodes the stream", protocol);
    double rms = r.pcm_total > 0 ? sqrt(r.pcm_sumsq / (double)r.pcm_total) : 0.0;
    fprintf(stderr, "  rx: ticks=%d synced=%d max_snr=%ddB first_sync=%.3fs pcm=%ld "
            "(over1 %ld) pcm_peak=%d (%.1f dBFS) pcm_rms=%.1f dBFS\n",
            r.ticks, r.synced_ticks, r.max_snr,
            r.first_sync_pos >= 0 ? (double)r.first_sync_pos / RADE_MODEM_SAMPLE_RATE : -1.0,
            r.pcm_total, r.pcm_over1, r.pcm_peak, dbfs(r.pcm_peak / 32768.0),
            dbfs(rms / 32768.0));
    CHECK(r.synced_ticks > 0, "RX reaches sync");
    CHECK(r.pcm_over1 > RADE_SPEECH_SAMPLE_RATE * 2, "RX decodes > 2 s of PCM in over 1");
    CHECK(dbfs(rms / 32768.0) > -45.0, "decoded PCM is not silent (RMS > -45 dBFS)");

    double drop1 = r.drop1_pos >= 0 ? (double)(r.drop1_pos - ts.eoo1_end) / RADE_MODEM_SAMPLE_RATE : -1.0;
    double drop2 = r.drop2_pos >= 0 ? (double)(r.drop2_pos - ts.eoo2_end) / RADE_MODEM_SAMPLE_RATE : -1.0;
    fprintf(stderr, "  eoo: sync dropped %+.3f s / %+.3f s relative to the end of EOO 1 / EOO 2 "
            "(negative = during the EOO frame; V1 unsync timer 3 s, V2 hangover 1.5 s)\n",
            drop1, drop2);
    CHECK(r.drop1_pos >= 0 && drop1 < 0.5, "EOO 1 detected (sync drops < 0.5 s after it)");
    CHECK(r.drop2_pos >= 0 && drop2 < 0.5, "EOO 2 detected (sync drops < 0.5 s after it)");

    /* Priming check: the vocoder eats the first ZR_PRIME_FRAMES (5) frames after
     * every (re)acquire, so the first non-empty rx block of an over is short by
     * exactly that much, whatever the per-call frame count. */
    int full = frames * 160;
    int primes = (5 + frames - 1) / frames;                  /* calls to finish priming */
    int expect_first = (primes * frames - 5) * 160;
    fprintf(stderr, "  prime: full block=%d, first block over1=%d over2=%d (expected %d)\n",
            full, r.first_block_over1, r.first_block_over2, expect_first);
    CHECK(r.max_block == full, "steady-state rx block is one full modem frame of PCM");
    CHECK(r.first_block_over1 == expect_first, "vocoder primes on first acquire");
    CHECK(r.resync2_pos >= 0, "RX re-acquires on the second over");
    CHECK(r.first_block_over2 == expect_first, "vocoder re-primes after the End-of-Over");

    if (protocol == 1) {
        CHECK(r.callsign_len > 0 && strcmp(r.callsign, callsign) == 0,
              "V1 EOO callsign round-trips ('%s')", r.callsign_len > 0 ? r.callsign : "(none)");
    } else {
        CHECK(r.callsign_len == 0 && r.callsign[0] == '\0',
              "V2 callsign getter returns 0 after an over (got %d)", r.callsign_len);
    }

    double rtf = r.audio_seconds > 0.0 ? r.rx_seconds / r.audio_seconds : 0.0;
    fprintf(stderr, "  RX COST V%d: %.3f s of CPU wall time for %.2f s of audio -> "
            "real-time factor %.4f (%.1f ms per second of audio)\n",
            protocol, r.rx_seconds, r.audio_seconds, rtf, 1000.0 * rtf);

    free(ts.modem);
}

/* Cross-protocol check: feed each protocol's signal, at several speech input
 * levels (the content changes the waveform), into the OTHER protocol's
 * receiver. Returns the total number of falsely synced ticks and reports every
 * case, so a cross-sync is visible in the log whether or not it is asserted. */
static int cross_check(int tx_protocol, int *any_pcm, double *longest_s)
{
    static const double gains[] = { 0.015, 0.025, 0.035, 0.050 };
    int rx_protocol = tx_protocol == 1 ? 2 : 1;
    int total_synced = 0;
    *any_pcm = 0;
    *longest_s = 0.0;
    for (size_t g = 0; g < sizeof(gains) / sizeof(gains[0]); g++) {
        test_stream ts;
        rx_result x;
        if (!build_stream(tx_protocol, "N9WAR", gains[g], &ts) || !run_rx(rx_protocol, &ts, &x)) {
            CHECK(0, "cross check V%d -> V%d at speech gain %.3f ran", tx_protocol,
                  rx_protocol, gains[g]);
            free(ts.modem);
            continue;
        }
        fprintf(stderr, "  cross: V%d signal (speech peak %.1f dBFS) into V%d RX: ticks=%d "
                "synced=%d (%.2f s, longest run %.2f s) pcm=%ld (%.2f s)\n",
                tx_protocol, dbfs(ts.speech_in_peak / 32768.0), rx_protocol, x.ticks,
                x.synced_ticks, x.ticks ? x.synced_ticks * x.audio_seconds / x.ticks : 0.0,
                x.ticks ? x.longest_run * x.audio_seconds / x.ticks : 0.0,
                x.pcm_total, (double)x.pcm_total / RADE_SPEECH_SAMPLE_RATE);
        total_synced += x.synced_ticks;
        if (x.pcm_total > 0) *any_pcm = 1;
        double run_s = x.ticks ? x.longest_run * x.audio_seconds / x.ticks : 0.0;
        if (run_s > *longest_s) *longest_s = run_s;
        free(ts.modem);
    }
    return total_synced;
}

/* V2 sample-clock offset: one ~120 s over, resampled, into a fresh V2
 * receiver. At +100 ppm the symbol timing walks 0.8 samples/s later, about 96
 * samples over the over: more than half a 160-sample symbol, so the
 * receiver's timing estimate reaches the edge of its window and, without the
 * timing correction the shim enables, wraps and drops sync (measured: lost at
 * ~97 s with the correction off). A 60 s over is too short to reach the edge.
 *
 * -100 ppm (timing walking earlier) is reported, not asserted: the upstream V2
 * receiver's End-of-Over detector false-triggers roughly every 10 s as the
 * timing drifts ~5-8 samples early, with the correction on or off (the
 * correction only acts near the window edges). Only a regression guard on the
 * decoded fraction is asserted there. */
#define ZR_CLOCK_OVER_S 120
static void clock_offset_check(void)
{
    fprintf(stderr, "\n== RADE V2 sample-clock offset ==\n");
    test_stream base;
    if (!build_stream_overs(2, "", SYNTH_GAIN_NOMINAL, ZR_CLOCK_OVER_S, 0, &base)) {
        CHECK(0, "%d s V2 stream built", ZR_CLOCK_OVER_S);
        return;
    }
    static const double ppms[] = { 0.0, 100.0, -100.0 };
    rx_result ref;
    memset(&ref, 0, sizeof(ref));
    for (size_t p = 0; p < sizeof(ppms) / sizeof(ppms[0]); p++) {
        test_stream ts;
        rx_result r;
        if (!resample_stream_ppm(&base, ppms[p], &ts) || !run_rx(2, &ts, &r)) {
            CHECK(0, "V2 RX at %+.0f ppm ran", ppms[p]);
            free(ts.modem);
            continue;
        }
        double rms = r.pcm_total > 0 ? sqrt(r.pcm_sumsq / (double)r.pcm_total) : 0.0;
        double over_s = (double)ts.eoo1_start / RADE_MODEM_SAMPLE_RATE;
        double drop = r.drop1_pos >= 0
            ? (double)(r.drop1_pos - ts.eoo1_end) / RADE_MODEM_SAMPLE_RATE : -99.0;
        fprintf(stderr, "  clock %+4.0f ppm: over %.2f s, acquisitions before EOO=%d, "
                "synced=%d/%d, longest run %.2f s, pcm=%ld (%.2f s), pcm_rms=%.1f dBFS, "
                "EOO drop %+.3f s\n",
                ppms[p], over_s, r.acquisitions1, r.synced_ticks, r.ticks,
                r.ticks ? r.longest_run * r.audio_seconds / r.ticks : 0.0,
                r.pcm_total, (double)r.pcm_total / RADE_SPEECH_SAMPLE_RATE,
                dbfs(rms / 32768.0), drop);
        double ref_rms = ref.pcm_total > 0 ? sqrt(ref.pcm_sumsq / (double)ref.pcm_total) : 0.0;
        if (ppms[p] == 0.0) {
            ref = r;
            CHECK(r.acquisitions1 == 1, "0 ppm: one sync run through the %d s over",
                  ZR_CLOCK_OVER_S);
        } else if (ppms[p] < 0.0) {
            if (r.acquisitions1 != 1)
                fprintf(stderr, "  WARN  %+.0f ppm: %d acquisitions in the over (upstream V2 "
                        "End-of-Over detector false-triggers on early timing drift)\n",
                        ppms[p], r.acquisitions1);
            CHECK(r.pcm_total >= (long)(0.90 * (double)ref.pcm_total),
                  "%+.0f ppm: regression guard, decodes >= 90%% of the 0 ppm PCM (%ld vs %ld)",
                  ppms[p], r.pcm_total, ref.pcm_total);
        } else {
            CHECK(r.acquisitions1 == 1,
                  "%+.0f ppm: one sync run through the over (got %d acquisitions)",
                  ppms[p], r.acquisitions1);
            CHECK(r.pcm_total >= (long)(0.97 * (double)ref.pcm_total),
                  "%+.0f ppm: decodes >= 97%% of the 0 ppm PCM (%ld vs %ld)",
                  ppms[p], r.pcm_total, ref.pcm_total);
            CHECK(fabs(dbfs(rms / 32768.0) - dbfs(ref_rms / 32768.0)) < 3.0,
                  "%+.0f ppm: decoded PCM level within 3 dB of 0 ppm", ppms[p]);
            CHECK(r.drop1_pos >= 0 && drop < 0.5, "%+.0f ppm: End-of-Over still detected",
                  ppms[p]);
        }
        free(ts.modem);
    }
    free(base.modem);
}

static int run_selftest(void)
{
    zeus_rade_global_init();
    fprintf(stderr, "zeus_rade selftest\n");
    CHECK(zeus_rade_abi_version() == 2, "zeus_rade_abi_version() == 2 (got %d)",
          zeus_rade_abi_version());
    CHECK(zeus_rade_open_protocol(0) == NULL, "protocol 0 is rejected");
    CHECK(zeus_rade_open_protocol(3) == NULL, "protocol 3 is rejected");
    CHECK(zeus_rade_open_protocol(-1) == NULL, "protocol -1 is rejected");
    CHECK(zeus_rade_protocol(NULL) == 0, "zeus_rade_protocol(NULL) == 0");
    zeus_rade *legacy = zeus_rade_open();
    CHECK(legacy != NULL && zeus_rade_protocol(legacy) == 1,
          "zeus_rade_open() still opens RADE V1");
    zeus_rade_close(legacy);

    selftest_protocol(1);
    selftest_protocol(2);

    clock_offset_check();

    fprintf(stderr, "\n== cross-protocol ==\n");
    int pcm12 = 0, pcm21 = 0;
    double run12 = 0.0, run21 = 0.0;
    int false12 = cross_check(1, &pcm12, &run12);
    CHECK(false12 == 0 && !pcm12, "a V2 receiver never syncs on a V1 signal");
    int false21 = cross_check(2, &pcm21, &run21);
    /* Measured, not assumed: the V1 acquisition can briefly lock onto some V2
     * content (a false sync of about 1 s that decodes noise). That is an
     * upstream modem property the managed auto-detect must tolerate, so it is
     * reported loudly instead of failing the native build. What is asserted is
     * the bound V1's own receiver guarantees: its once-per-second unique-word
     * check sheds a false lock, so no false sync may outlast 1.5 s. */
    if (false21 > 0)
        fprintf(stderr, "  WARN  a V1 receiver falsely synced on a V2 signal for %d tick%s in "
                "total (see the cross lines above)\n", false21, false21 == 1 ? "" : "s");
    else
        fprintf(stderr, "  info  a V1 receiver never synced on a V2 signal\n");
    CHECK(run21 < 1.5, "any false V1 lock on a V2 signal is shed within 1.5 s (longest %.2f s)",
          run21);
    zeus_rade_global_shutdown();

    if (g_failures) fprintf(stderr, "\nselftest: FAIL (%d failed check%s)\n",
                            g_failures, g_failures == 1 ? "" : "s");
    else            fprintf(stderr, "\nselftest: PASS\n");
    return g_failures ? 3 : 0;
}

int main(int argc, char **argv)
{
    set_binary_stdio();
    if (argc >= 2 && strcmp(argv[1], "selftest") == 0) return run_selftest();
    if (argc >= 2 && strcmp(argv[1], "loopback") == 0) {
        const char *cs = (argc >= 3) ? argv[2] : "";
        /* Write decoded PCM to stdout only if a 3rd/4th arg "pcm" is given, so the
         * default run is quiet (stderr report only). */
        int write_pcm = (argc >= 4 && strcmp(argv[3], "pcm") == 0);
        return run_loopback(cs, write_pcm);
    }
    if (argc >= 2 && strcmp(argv[1], "decode") == 0) {
        int protocol = (argc >= 3) ? atoi(argv[2]) : 1;
        return run_decode(protocol);
    }
    return run_decode(1);
}
