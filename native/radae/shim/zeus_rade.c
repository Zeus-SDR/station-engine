/* SPDX-License-Identifier: BSD-2-Clause
 *
 * zeus_rade — single-entry RADE V1 + V2 shim (see zeus_rade.h).
 *
 * RX: radae_c's `rade` modem (IQ -> feature frames) + Opus's FARGAN vocoder
 * (feature frames -> 16 kHz PCM). FARGAN is primed with the first 5 feature
 * frames, then synthesizes 160 PCM samples per subsequent frame.
 *
 * TX: Opus's LPCNet feature analyzer (16 kHz speech -> 36-float frames) +
 * radae_c's `rade_tx` (feature frames -> modem IQ @ 8 kHz). The real part of the
 * modem IQ is the transmitted SSB audio (the inverse of the RX feed). The V1
 * End-of-Over callsign is carried by the FreeDV reliable-text LDPC (rade_text),
 * matching FreeDV-GUI on the air. A V2 End-of-Over is detection-only (no data
 * bits), so V2 contexts never touch the reliable-text codec.
 *
 * Both protocols exchange padded 36-float feature frames with radae_c (V1: 12
 * per modem frame, V2: 4), so the FARGAN/LPCNet framing below is shared; only
 * the per-context sizes differ, and every one of them is validated against the
 * fixed scratch buffers at open time.
 *
 * RADE + FARGAN weights and the LPCNet feature analyzer are all compiled in
 * (Opus DEEP_PLC build) — no model files at runtime.
 *
 * Frame constants (from the Opus DNN build): a feature frame is
 * NB_TOTAL_FEATURES(36) floats; FARGAN consumes the first NB_FEATURES(20) and
 * emits LPCNET_FRAME_SIZE(160) samples at 16 kHz, and the LPCNet analyzer
 * consumes LPCNET_FRAME_SIZE samples per 36-float feature frame.
 */
#include "zeus_rade.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>

/* Opus FARGAN vocoder + LPCNet feature analyzer. */
#include "fargan.h"
#include "freq.h"      /* NB_TOTAL_FEATURES, NB_FEATURES */
#include "lpcnet.h"    /* LPCNET_FRAME_SIZE, LPCNetEncState, lpcnet_compute_single_frame_features */

/* FreeDV reliable-text (LDPC) EOO callsign codec — on-air compatible with
 * FreeDV-GUI. Replaces radae_c's internal 7-bit packing for the callsign. */
#include "rade_text.h"

/* radae_c's per-protocol frame geometry, used only for the compile-time buffer
 * checks below (the shim itself reads every size from the rade_api.h helpers). */
#include "radc_const.h"
#include "radc_const_v2.h"

/* radae_c's private handle layout and V2 receiver state, used ONLY to switch on
 * the V2 receiver's sample-clock timing correction (zr_enable_v2_timing_adj).
 * Field access by name, never by offset: an upstream rename or layout change
 * breaks this build instead of poking the wrong memory. */
#include "radc_internal.h"
#include "radc_rx_v2.h"

#define ZR_PRIME_FRAMES 5
/* A RADE modem frame yields a bounded number of feature frames; this caps the
 * PCM produced per rx call. Generous — guards the output buffer against any
 * upstream change without truncating real output. */
#define ZR_MAX_FRAMES_PER_RX 64
#define ZR_EOO_BITS_MAX 1024
/* rade_text's LDPC block (LDPC_TOTAL_SIZE_BITS, private to rade_text.c). */
#define ZR_TEXT_LDPC_BITS 112

/* Fail the build, not the radio, if a protocol outgrows a fixed buffer. Feature
 * frames per modem frame: V1 = NZMF(3) * FRAMES_PER_STEP(4) = 12, V2 = 1 * 4 = 4.
 * The same frame count sizes feat_scratch, tx_feat and the mic scratch. */
_Static_assert(RADC_NZMF * RADC_FRAMES_PER_STEP <= ZR_MAX_FRAMES_PER_RX,
               "V1 feature frames per modem frame exceed ZR_MAX_FRAMES_PER_RX");
_Static_assert(RADC_V2_NZMF * RADC_V2_FRAMES_PER_STEP <= ZR_MAX_FRAMES_PER_RX,
               "V2 feature frames per modem frame exceed ZR_MAX_FRAMES_PER_RX");
_Static_assert(RADC_NB_TOTAL_FEATURES == NB_TOTAL_FEATURES
               && RADC_V2_NB_TOTAL_FEATURES == NB_TOTAL_FEATURES,
               "radae_c feature frames must match the Opus DNN NB_TOTAL_FEATURES");
/* rade_text_rx(syms, n) reads syms[2*i + 1] for every i < n (it treats n as a
 * symbol count while the shim passes the bit count), so the EOO scratch must
 * hold twice the V1 EOO bit count. */
_Static_assert(2 * RADC_NSEOO * RADC_BPS <= ZR_EOO_BITS_MAX,
               "V1 EOO bits exceed the EOO scratch buffers");

/* The timing-correction switch must still be the int flag radc_rx_v2.c reads
 * (`if (rx->timing_adj_enable && ...)`); a type change upstream fails here. */
_Static_assert(sizeof(((rade_rx_v2_state *)0)->timing_adj_enable) == sizeof(int),
               "radc_rx_v2 timing_adj_enable is no longer an int flag");

/* Local pi constant instead of M_PI: MSVC only defines M_PI under
 * _USE_MATH_DEFINES, which the shared CMakeLists doesn't set. */
#define ZR_PI 3.14159265358979323846f

/* Mic AGC: block RMS -> desired linear gain, smoothed onto the block. Fast
 * attack turns a hot mic down before it clips the encoder; slow release
 * avoids audible gain-pumping between syllables. */
#define ZR_AGC_MIN_GAIN  0.125f  /* -18 dB floor */
#define ZR_AGC_MAX_GAIN  8.0f    /* +18 dB ceiling */
#define ZR_AGC_ATTACK_S  0.010f
#define ZR_AGC_RELEASE_S 0.300f
#define ZR_MIC_AGC_DEFAULT_TARGET_DB -18.0f

/* Direct-Form-I-transposed biquad, used for the fixed 3-band mic EQ. */
typedef struct { float b0, b1, b2, a1, a2, z1, z2; } zr_biquad;

static float zr_biquad_process(zr_biquad *bq, float x)
{
    float y = bq->b0 * x + bq->z1;
    bq->z1 = bq->b1 * x - bq->a1 * y + bq->z2;
    bq->z2 = bq->b2 * x - bq->a2 * y;
    return y;
}

/* RBJ Audio EQ Cookbook shelf/peaking filters. sr/fc in Hz, gain_db the
 * band gain, s the shelf slope (1.0 = gentle), q the peaking Q. */
static void zr_biquad_low_shelf(zr_biquad *bq, float fc, float sr, float gain_db, float s)
{
    float A = powf(10.0f, gain_db / 40.0f);
    float w0 = 2.0f * ZR_PI * fc / sr;
    float cosw0 = cosf(w0), sinw0 = sinf(w0);
    float alpha = sinw0 / 2.0f * sqrtf((A + 1.0f / A) * (1.0f / s - 1.0f) + 2.0f);
    float sqrtA2alpha = 2.0f * sqrtf(A) * alpha;
    float b0 =      A * ((A + 1) - (A - 1) * cosw0 + sqrtA2alpha);
    float b1 =  2 * A * ((A - 1) - (A + 1) * cosw0);
    float b2 =      A * ((A + 1) - (A - 1) * cosw0 - sqrtA2alpha);
    float a0 =          (A + 1) + (A - 1) * cosw0 + sqrtA2alpha;
    float a1 =     -2 * ((A - 1) + (A + 1) * cosw0);
    float a2 =          (A + 1) + (A - 1) * cosw0 - sqrtA2alpha;
    bq->b0 = b0 / a0; bq->b1 = b1 / a0; bq->b2 = b2 / a0;
    bq->a1 = a1 / a0; bq->a2 = a2 / a0;
}

static void zr_biquad_high_shelf(zr_biquad *bq, float fc, float sr, float gain_db, float s)
{
    float A = powf(10.0f, gain_db / 40.0f);
    float w0 = 2.0f * ZR_PI * fc / sr;
    float cosw0 = cosf(w0), sinw0 = sinf(w0);
    float alpha = sinw0 / 2.0f * sqrtf((A + 1.0f / A) * (1.0f / s - 1.0f) + 2.0f);
    float sqrtA2alpha = 2.0f * sqrtf(A) * alpha;
    float b0 =      A * ((A + 1) + (A - 1) * cosw0 + sqrtA2alpha);
    float b1 = -2 * A * ((A - 1) + (A + 1) * cosw0);
    float b2 =      A * ((A + 1) + (A - 1) * cosw0 - sqrtA2alpha);
    float a0 =          (A + 1) - (A - 1) * cosw0 + sqrtA2alpha;
    float a1 =      2 * ((A - 1) - (A + 1) * cosw0);
    float a2 =          (A + 1) - (A - 1) * cosw0 - sqrtA2alpha;
    bq->b0 = b0 / a0; bq->b1 = b1 / a0; bq->b2 = b2 / a0;
    bq->a1 = a1 / a0; bq->a2 = a2 / a0;
}

static void zr_biquad_peaking(zr_biquad *bq, float fc, float sr, float gain_db, float q)
{
    float A = powf(10.0f, gain_db / 40.0f);
    float w0 = 2.0f * ZR_PI * fc / sr;
    float cosw0 = cosf(w0), sinw0 = sinf(w0);
    float alpha = sinw0 / (2.0f * q);
    float b0 = 1 + alpha * A;
    float b1 = -2 * cosw0;
    float b2 = 1 - alpha * A;
    float a0 = 1 + alpha / A;
    float a1 = -2 * cosw0;
    float a2 = 1 - alpha / A;
    bq->b0 = b0 / a0; bq->b1 = b1 / a0; bq->b2 = b2 / a0;
    bq->a1 = a1 / a0; bq->a2 = a2 / a0;
}

/* OPUS_DISABLE_INTRINSICS build: the runtime CPU-dispatch arch is the scalar
 * path (0). The whole library is built scalar/portable, so 0 is correct on every
 * target; a future SIMD build would pass opus_select_arch() here instead. */
#define ZR_ARCH 0

struct zeus_rade {
    struct rade *r;
    int   protocol;       /* 1 = V1, 2 = V2 (as reported by rade_protocol) */

    /* ---- RX (decode) ---- */
    FARGANState  fargan;
    int   n_features;     /* rade_n_features_in_out (feature floats per RADE frame group) */
    int   n_eoo_bits;
    int   primed;         /* FARGAN continuation done */
    int   prime_have;     /* feature frames collected toward priming */
    float prime_buf[ZR_PRIME_FRAMES * NB_TOTAL_FEATURES];
    float feat_scratch[ZR_MAX_FRAMES_PER_RX * NB_TOTAL_FEATURES];
    float eoo_scratch[ZR_EOO_BITS_MAX];
    rade_text_t  txt_rx;  /* reliable-text decoder; fires zr_on_text_rx */
    int   have_callsign;
    char  callsign[RADE_EOO_CALLSIGN_MAX + 1];

    /* ---- TX (encode) ---- */
    LPCNetEncState *enc;  /* LPCNet feature analyzer (speech -> features) */
    int   n_speech;       /* int16 @16k consumed per zeus_rade_tx call */
    int   n_tx_out;       /* RADE_COMP produced per zeus_rade_tx call */
    int   n_tx_eoo_out;   /* RADE_COMP in the EOO frame */
    float tx_feat[ZR_MAX_FRAMES_PER_RX * NB_TOTAL_FEATURES]; /* features for one tx call */
    rade_text_t  txt_tx;  /* reliable-text encoder for the EOO callsign */
    float eoo_tx_bits[ZR_EOO_BITS_MAX]; /* EOO symbol payload handed to rade_tx_set_eoo_bits */

    /* TX-side mic tap meters, refreshed each zeus_rade_tx() call. */
    float tx_mic_peak;      /* most recent block's peak |sample| / 32768 */
    int   tx_mic_clip_hold; /* samples remaining in the ~500ms clip-hold window */

    /* Pre-encoder mic conditioning (TX-side), off by default. */
    int   mic_agc_enabled;
    float mic_agc_target_lin; /* linear target RMS, derived from a dBFS setter */
    float mic_agc_gain;       /* smoothed linear gain, persists across tx calls */
    int   mic_eq_enabled;
    zr_biquad eq_bass, eq_mid, eq_treble;
    float mic_float_scratch[ZR_MAX_FRAMES_PER_RX * LPCNET_FRAME_SIZE];
    short mic_scratch[ZR_MAX_FRAMES_PER_RX * LPCNET_FRAME_SIZE];
};

/* ~500ms of held "clipped" state at the 16 kHz speech rate, so a single
 * full-scale sample is visible on a UI meter instead of flickering for one
 * block. */
#define ZR_MIC_CLIP_HOLD_SAMPLES 8000

/* rade_text RX callback: a full, CRC-validated callsign was decoded. */
static void zr_on_text_rx(rade_text_t rt, const char *txt, int length, void *state)
{
    (void)rt;
    struct zeus_rade *z = (struct zeus_rade *)state;
    if (!z || length <= 0) return;
    int n = length < RADE_EOO_CALLSIGN_MAX ? length : RADE_EOO_CALLSIGN_MAX;
    memcpy(z->callsign, txt, (size_t)n);
    z->callsign[n] = '\0';
    z->have_callsign = 1;
}

void zeus_rade_global_init(void)     { rade_initialize(); }
void zeus_rade_global_shutdown(void) { rade_finalize(); }

int zeus_rade_abi_version(void) { return ZEUS_RADE_ABI_VERSION; }

/* Every per-context size radae_c reports must fit the fixed scratch buffers in
 * struct zeus_rade. Checked once at open so a mismatched upstream fails closed
 * (open returns NULL) instead of overflowing on the hot path. rade_rx writes at
 * most n_features floats for both protocols (V1: 12 padded frames, V2: 4). */
static int zr_sizes_fit(const zeus_rade *z)
{
    if (z->n_features <= 0 || z->n_features % NB_TOTAL_FEATURES != 0) return 0;
    int frames = z->n_features / NB_TOTAL_FEATURES;
    if (frames > ZR_MAX_FRAMES_PER_RX) return 0;
    if (z->n_tx_out <= 0 || z->n_tx_eoo_out <= 0) return 0;
    /* rade_text_rx reads up to 2 * n_eoo_bits floats (see the static assert). */
    if (z->n_eoo_bits < 0 || 2 * z->n_eoo_bits > ZR_EOO_BITS_MAX) return 0;
    /* The reliable-text codec reads/writes a full LDPC block from the EOO
     * payload, so a context that carries EOO bits must carry at least that. */
    if (z->n_eoo_bits > 0 && z->n_eoo_bits < ZR_TEXT_LDPC_BITS) return 0;
    if (z->protocol == 2 && z->n_eoo_bits != 0) return 0;
    return 1;
}

/* RADE V2: turn on the receiver's sample-clock timing correction. The C port
 * ships it off (radc_rx_v2.c: `rx->timing_adj_enable = 0;  enable for long-run /
 * clock-drift operation`); the official RADE V2 receiver has its equivalent on
 * by default. With it off, a sound-card or radio sample clock a few tens of ppm
 * away from the sender's slowly walks the symbol timing out of the cyclic
 * prefix and the receiver drops and re-acquires sync mid-over. With it on, the
 * receiver shifts its timing window by a quarter symbol whenever the estimate
 * nears either edge. Must be set after rade_open and before the first rade_rx.
 * Only called for a handle rade_protocol() reported as 2, so r->rx is the V2
 * receiver state. Vendored sources stay byte-exact; this is the only write the
 * shim makes into radae_c's private state. */
static void zr_enable_v2_timing_adj(struct rade *r)
{
    rade_rx_v2_state *rx = (rade_rx_v2_state *)r->rx;
    rx->timing_adj_enable = 1;
}

zeus_rade *zeus_rade_open_protocol(int protocol)
{
    if (protocol != 1 && protocol != 2) return NULL;

    zeus_rade *z = (zeus_rade *)calloc(1, sizeof(*z));
    if (!z) return NULL;

    /* radae_c compiles the weights in, so the model path is unused; the C
     * encoder/decoder paths are what make this Python-free. */
    int flags = RADE_USE_C_ENCODER | RADE_USE_C_DECODER | RADE_VERBOSE_0;
    if (protocol == 2) flags |= RADE_PROTOCOL_V2;
    z->r = rade_open("", flags);
    if (!z->r) { free(z); return NULL; }
    /* A radae_c that ignored the V2 flag would silently open V1; refuse it. */
    z->protocol = rade_protocol(z->r);
    if (z->protocol != protocol) { rade_close(z->r); free(z); return NULL; }

    z->n_features    = rade_n_features_in_out(z->r);
    z->n_eoo_bits    = rade_n_eoo_bits(z->r);
    z->n_tx_out      = rade_n_tx_out(z->r);
    z->n_tx_eoo_out  = rade_n_tx_eoo_out(z->r);
    if (!zr_sizes_fit(z)) { rade_close(z->r); free(z); return NULL; }
    if (z->protocol == 2) zr_enable_v2_timing_adj(z->r);
    /* One LPCNet feature frame is NB_TOTAL_FEATURES floats from LPCNET_FRAME_SIZE
     * speech samples; a tx call consumes n_features/NB_TOTAL_FEATURES frames. */
    z->n_speech      = (z->n_features / NB_TOTAL_FEATURES) * LPCNET_FRAME_SIZE;

    fargan_init(&z->fargan);
    z->primed = 0;
    z->prime_have = 0;
    z->have_callsign = 0;
    z->callsign[0] = '\0';

    /* LPCNet feature analyzer for TX. Pure DSP + compiled-in pitch DNN — no model
     * file. create() allocates; init() resets streaming state. */
    z->enc = lpcnet_encoder_create();
    if (z->enc) lpcnet_encoder_init(z->enc);

    /* Reliable-text EOO codecs (RX decode + TX encode). V2's EOO carries no
     * data bits, so V2 contexts leave both NULL and every text path no-ops. */
    if (z->n_eoo_bits > 0) {
        z->txt_rx = rade_text_create();
        if (z->txt_rx) rade_text_set_rx_callback(z->txt_rx, zr_on_text_rx, z);
        z->txt_tx = rade_text_create();
    }

    /* Mic conditioning: off by default; flat EQ + unity AGC gain so an
     * enable-without-a-prior-setter-call is still well-defined. */
    z->mic_agc_enabled = 0;
    z->mic_agc_gain = 1.0f;
    z->mic_agc_target_lin = powf(10.0f, ZR_MIC_AGC_DEFAULT_TARGET_DB / 20.0f);
    z->mic_eq_enabled = 0;
    zr_biquad_low_shelf(&z->eq_bass, 200.0f, (float)RADE_SPEECH_SAMPLE_RATE, 0.0f, 1.0f);
    zr_biquad_peaking(&z->eq_mid, 1000.0f, (float)RADE_SPEECH_SAMPLE_RATE, 0.0f, 0.7f);
    zr_biquad_high_shelf(&z->eq_treble, 3000.0f, (float)RADE_SPEECH_SAMPLE_RATE, 0.0f, 1.0f);

    return z;
}

/* RADE V1 — the original entry point, unchanged in behaviour and ABI. */
zeus_rade *zeus_rade_open(void) { return zeus_rade_open_protocol(1); }

int zeus_rade_protocol(zeus_rade *z) { return z ? z->protocol : 0; }

void zeus_rade_close(zeus_rade *z)
{
    if (!z) return;
    if (z->txt_rx) rade_text_destroy(z->txt_rx);
    if (z->txt_tx) rade_text_destroy(z->txt_tx);
    if (z->enc) lpcnet_encoder_destroy(z->enc);
    if (z->r) rade_close(z->r);
    free(z);
}

int zeus_rade_nin(zeus_rade *z)            { return rade_nin(z->r); }
int zeus_rade_nin_max(zeus_rade *z)        { return rade_nin_max(z->r); }
int zeus_rade_max_pcm_per_rx(zeus_rade *z) { (void)z; return ZR_MAX_FRAMES_PER_RX * LPCNET_FRAME_SIZE; }
int zeus_rade_sync(zeus_rade *z)           { return rade_sync(z->r); }
float zeus_rade_freq_offset(zeus_rade *z)  { return rade_freq_offset(z->r); }
int zeus_rade_snr_db(zeus_rade *z)         { return rade_snrdB_3k_est(z->r); }

int zeus_rade_n_speech_samples(zeus_rade *z) { return z->n_speech; }
int zeus_rade_n_tx_out(zeus_rade *z)         { return z->n_tx_out; }
int zeus_rade_n_tx_eoo_out(zeus_rade *z)     { return z->n_tx_eoo_out; }

int zeus_rade_tx_mic_level_db(zeus_rade *z)
{
    if (z->tx_mic_peak <= 0.0f) return -120;
    float db = 20.0f * log10f(z->tx_mic_peak);
    if (db < -120.0f) db = -120.0f;
    return (int)lrintf(db);
}

int zeus_rade_tx_mic_clip(zeus_rade *z) { return z->tx_mic_clip_hold > 0 ? 1 : 0; }

void zeus_rade_set_mic_agc_enabled(zeus_rade *z, int enable) { z->mic_agc_enabled = enable ? 1 : 0; }

void zeus_rade_set_mic_agc_target_db(zeus_rade *z, float target_dbfs)
{
    z->mic_agc_target_lin = powf(10.0f, target_dbfs / 20.0f);
}

void zeus_rade_set_mic_eq_enabled(zeus_rade *z, int enable) { z->mic_eq_enabled = enable ? 1 : 0; }

void zeus_rade_set_mic_eq_bass_db(zeus_rade *z, float gain_db)
{
    zr_biquad_low_shelf(&z->eq_bass, 200.0f, (float)RADE_SPEECH_SAMPLE_RATE, gain_db, 1.0f);
}

void zeus_rade_set_mic_eq_mid_db(zeus_rade *z, float gain_db)
{
    zr_biquad_peaking(&z->eq_mid, 1000.0f, (float)RADE_SPEECH_SAMPLE_RATE, gain_db, 0.7f);
}

void zeus_rade_set_mic_eq_treble_db(zeus_rade *z, float gain_db)
{
    zr_biquad_high_shelf(&z->eq_treble, 3000.0f, (float)RADE_SPEECH_SAMPLE_RATE, gain_db, 1.0f);
}

/* Shape (EQ) then level (AGC) — a conventional mic-processing order. Only
 * called when at least one stage is enabled; writes n conditioned samples to
 * z->mic_scratch, leaving pcm_in untouched (the mic meter reads the raw tap). */
static void zr_condition_mic(zeus_rade *z, const short *pcm_in, int n)
{
    float *f = z->mic_float_scratch;
    for (int i = 0; i < n; i++) f[i] = pcm_in[i] * (1.0f / 32768.0f);

    if (z->mic_eq_enabled) {
        for (int i = 0; i < n; i++) {
            float x = f[i];
            x = zr_biquad_process(&z->eq_bass, x);
            x = zr_biquad_process(&z->eq_mid, x);
            x = zr_biquad_process(&z->eq_treble, x);
            f[i] = x;
        }
    }

    if (z->mic_agc_enabled) {
        double sumsq = 0.0;
        for (int i = 0; i < n; i++) sumsq += (double)f[i] * (double)f[i];
        float rms = (float)sqrt(sumsq / (n > 0 ? n : 1));
        float desired = rms > 1e-6f ? z->mic_agc_target_lin / rms : z->mic_agc_gain;
        if (desired > ZR_AGC_MAX_GAIN) desired = ZR_AGC_MAX_GAIN;
        if (desired < ZR_AGC_MIN_GAIN) desired = ZR_AGC_MIN_GAIN;
        /* Smooth the GAIN (not each sample) toward `desired`: attack when
         * turning down (mic too hot), release when turning up (mic too quiet). */
        float dt = n / (float)RADE_SPEECH_SAMPLE_RATE;
        float tau = (desired < z->mic_agc_gain) ? ZR_AGC_ATTACK_S : ZR_AGC_RELEASE_S;
        float coeff = 1.0f - expf(-dt / tau);
        z->mic_agc_gain += (desired - z->mic_agc_gain) * coeff;
        for (int i = 0; i < n; i++) f[i] *= z->mic_agc_gain;
    }

    for (int i = 0; i < n; i++) {
        float s = f[i] * 32768.0f;
        if (s >  32767.0f) s =  32767.0f;
        if (s < -32768.0f) s = -32768.0f;
        z->mic_scratch[i] = (short)lrintf(s);
    }
}

int zeus_rade_get_eoo_callsign(zeus_rade *z, char *callsign_out)
{
    if (!z->have_callsign) { if (callsign_out) callsign_out[0] = '\0'; return 0; }
    int n = (int)strlen(z->callsign);
    if (callsign_out) memcpy(callsign_out, z->callsign, (size_t)n + 1);
    z->have_callsign = 0; /* consume */
    return n;
}

/* Prime FARGAN exactly as the reference does: 5 frames of NB_TOTAL_FEATURES read
 * into NB_FEATURES-strided slots, with 2 frames of zero PCM history. */
static void zr_try_prime(zeus_rade *z, const float *frame36)
{
    memcpy(&z->prime_buf[z->prime_have * NB_FEATURES], frame36,
           NB_TOTAL_FEATURES * sizeof(float));
    z->prime_have++;
    if (z->prime_have == ZR_PRIME_FRAMES) {
        float zeros[2 * LPCNET_FRAME_SIZE];
        memset(zeros, 0, sizeof(zeros));
        fargan_cont(&z->fargan, zeros, z->prime_buf);
        z->primed = 1;
    }
}

int zeus_rade_rx(zeus_rade *z, const RADE_COMP *rx_in, short *pcm_out)
{
    int has_eoo = 0;
    int nfloats = rade_rx(z->r, z->feat_scratch, &has_eoo, z->eoo_scratch,
                          (RADE_COMP *)rx_in);

    /* On loss of sync, drop the priming so we re-prime cleanly on re-acquire.
     * An End-of-Over drops sync on the same call in both protocols (V1 moves to
     * SEARCH, V2 to IDLE); has_eoo is checked too so the re-prime does not
     * depend on that upstream detail. */
    if (!rade_sync(z->r) || has_eoo) { z->primed = 0; z->prime_have = 0; }

    /* End-of-Over: decode the callsign with the reliable-text LDPC (CRC-checked).
     * zr_on_text_rx fires from inside rade_text_rx when a valid block lands.
     * Only V1 fills eoo_scratch; V2 contexts have no txt_rx and no EOO bits. */
    if (has_eoo && z->txt_rx && z->n_eoo_bits > 0) {
        rade_text_rx(z->txt_rx, z->eoo_scratch, z->n_eoo_bits);
    }

    if (nfloats <= 0) return 0;

    int n_pcm = 0;
    int frames = nfloats / NB_TOTAL_FEATURES;
    if (frames > ZR_MAX_FRAMES_PER_RX) frames = ZR_MAX_FRAMES_PER_RX;
    for (int f = 0; f < frames; f++) {
        const float *frame36 = &z->feat_scratch[f * NB_TOTAL_FEATURES];
        if (!z->primed) {
            zr_try_prime(z, frame36);
            continue; /* priming frames produce no audio (matches reference) */
        }
        float feats[NB_FEATURES];
        float fpcm[LPCNET_FRAME_SIZE];
        memcpy(feats, frame36, NB_FEATURES * sizeof(float)); /* OPUS_COPY first 20 */
        fargan_synthesize(&z->fargan, fpcm, feats);
        for (int i = 0; i < LPCNET_FRAME_SIZE; i++) {
            float s = 32768.0f * fpcm[i];
            if (s >  32767.0f) s =  32767.0f;
            if (s < -32767.0f) s = -32767.0f;
            pcm_out[n_pcm++] = (short)floorf(0.5f + s);
        }
    }
    return n_pcm;
}

/* ---- Transmit ---------------------------------------------------------- */

/* Scan one TX call's mic PCM for the block peak and clip-hold state. Cheap
 * (one pass, no allocation) — safe to call every zeus_rade_tx(). */
static void zr_update_mic_meter(zeus_rade *z, const short *pcm_in, int n)
{
    int peak = 0;
    int clipped = 0;
    for (int i = 0; i < n; i++) {
        int s = pcm_in[i];
        int a = s < 0 ? -s : s;
        if (a > peak) peak = a;
        if (a >= 32767) clipped = 1;
    }
    z->tx_mic_peak = peak / 32768.0f;
    if (clipped) z->tx_mic_clip_hold = ZR_MIC_CLIP_HOLD_SAMPLES;
    else {
        z->tx_mic_clip_hold -= n;
        if (z->tx_mic_clip_hold < 0) z->tx_mic_clip_hold = 0;
    }
}

int zeus_rade_tx(zeus_rade *z, const short *pcm_in, RADE_COMP *tx_out)
{
    if (!z->enc) return 0;
    int frames = z->n_features / NB_TOTAL_FEATURES;
    if (frames > ZR_MAX_FRAMES_PER_RX) frames = ZR_MAX_FRAMES_PER_RX;
    int n = frames * LPCNET_FRAME_SIZE;
    zr_update_mic_meter(z, pcm_in, n);

    /* The meter above always reads the raw mic tap; conditioning (if enabled)
     * runs on a separate scratch buffer so pcm_in itself is never mutated. */
    const short *speech = pcm_in;
    if (z->mic_eq_enabled || z->mic_agc_enabled) {
        zr_condition_mic(z, pcm_in, n);
        speech = z->mic_scratch;
    }

    /* Analyze each 160-sample speech frame into 36 features, contiguously, so the
     * block matches rade_tx's expected n_features layout. */
    for (int f = 0; f < frames; f++) {
        lpcnet_compute_single_frame_features(
            z->enc, speech + (size_t)f * LPCNET_FRAME_SIZE,
            &z->tx_feat[f * NB_TOTAL_FEATURES], ZR_ARCH);
    }
    return rade_tx(z->r, tx_out, z->tx_feat);
}

int zeus_rade_tx_eoo(zeus_rade *z, RADE_COMP *tx_eoo_out)
{
    return rade_tx_eoo(z->r, tx_eoo_out);
}

void zeus_rade_set_tx_callsign(zeus_rade *z, const char *callsign)
{
    /* V2 contexts have no txt_tx (no EOO data bits): a safe no-op. */
    if (!z->txt_tx || z->n_eoo_bits <= 0) return;
    int n = z->n_eoo_bits;
    if (n > (int)(sizeof(z->eoo_tx_bits) / sizeof(z->eoo_tx_bits[0])))
        n = (int)(sizeof(z->eoo_tx_bits) / sizeof(z->eoo_tx_bits[0]));
    /* The reliable-text block lives at the front of the EOO payload (offset 0);
     * the remainder defaults to zero — matches FreeDV-GUI's EOO layout. */
    memset(z->eoo_tx_bits, 0, sizeof(float) * (size_t)n);
    if (callsign && callsign[0]) {
        rade_text_generate_tx_string(z->txt_tx, callsign,
                                     (int)strlen(callsign), z->eoo_tx_bits, n);
    }
    rade_tx_set_eoo_bits(z->r, z->eoo_tx_bits);
}
