/* SPDX-License-Identifier: GPL-3.0-only */
/*
 * Stable C ABI for the Windows-only Zeus ASIO host shim.
 *
 * The audio callback never crosses into managed code. Playback and capture
 * move through native SPSC float32 rings; the managed host may wait on a
 * native event and drain/fill those rings from ordinary worker threads.
 */
#ifndef ZEUS_ASIO_H
#define ZEUS_ASIO_H

#include <stdint.h>

#if defined(_WIN32)
#define ZEUS_ASIO_API __declspec(dllexport)
#define ZEUS_ASIO_CALL __cdecl
#else
#define ZEUS_ASIO_API
#define ZEUS_ASIO_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum zeus_asio_event {
    ZEUS_ASIO_EVENT_CAPTURE_AVAILABLE  = 1u << 0,
    ZEUS_ASIO_EVENT_PLAYBACK_SPACE     = 1u << 1,
    ZEUS_ASIO_EVENT_RESET_REQUESTED    = 1u << 2,
    ZEUS_ASIO_EVENT_RESYNC_REQUESTED   = 1u << 3,
    ZEUS_ASIO_EVENT_SAMPLE_RATE_CHANGED = 1u << 4,
    ZEUS_ASIO_EVENT_LATENCIES_CHANGED  = 1u << 5,
    ZEUS_ASIO_EVENT_STOPPED            = 1u << 6,
    ZEUS_ASIO_EVENT_ERROR              = 1u << 7
};

enum zeus_asio_status_flag {
    ZEUS_ASIO_STATUS_INITIALIZED       = 1u << 0,
    ZEUS_ASIO_STATUS_RUNNING           = 1u << 1,
    ZEUS_ASIO_STATUS_INPUT_ENABLED     = 1u << 2,
    ZEUS_ASIO_STATUS_OUTPUT_ENABLED    = 1u << 3,
    ZEUS_ASIO_STATUS_RESET_REQUESTED   = 1u << 4,
    ZEUS_ASIO_STATUS_DRIVER_ERROR      = 1u << 5
};

typedef struct zeus_asio_status_v1 {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t pending_events;
    uint32_t sample_rate;
    uint32_t buffer_frames;
    uint32_t input_latency_frames;
    uint32_t output_latency_frames;
    uint64_t callback_count;
    uint64_t capture_frames;
    uint64_t playback_frames;
    uint64_t capture_overrun_count;
    uint64_t capture_overrun_frames;
    uint64_t playback_underrun_count;
    uint64_t playback_underrun_frames;
    uint64_t reset_request_count;
    uint64_t resync_request_count;
    int32_t last_asio_error;
    uint32_t reserved;
} zeus_asio_status_v1;

ZEUS_ASIO_API const char* ZEUS_ASIO_CALL zeus_asio_version(void);
/* Normalized SHA-256 of the runtime-affecting native source inputs. */
ZEUS_ASIO_API const char* ZEUS_ASIO_CALL zeus_asio_source_hash(void);

/* Registered 64-bit ASIO drivers. IDs are canonical CLSID strings. */
ZEUS_ASIO_API void* ZEUS_ASIO_CALL zeus_asio_drivers_create(void);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_drivers_count(void* snapshot);
ZEUS_ASIO_API const char* ZEUS_ASIO_CALL zeus_asio_drivers_id(void* snapshot, uint32_t index);
ZEUS_ASIO_API const char* ZEUS_ASIO_CALL zeus_asio_drivers_name(void* snapshot, uint32_t index);
ZEUS_ASIO_API void ZEUS_ASIO_CALL zeus_asio_drivers_destroy(void* snapshot);

/*
 * Query a driver without creating buffers or installing callbacks. The probe
 * initializes and releases the driver on a dedicated STA/COM owner thread.
 * Channel names remain valid until zeus_asio_probe_destroy.
 */
ZEUS_ASIO_API void* ZEUS_ASIO_CALL zeus_asio_probe_create(const char* driver_id_utf8);
ZEUS_ASIO_API void ZEUS_ASIO_CALL zeus_asio_probe_destroy(void* probe);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_probe_input_count(void* probe);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_probe_output_count(void* probe);
ZEUS_ASIO_API const char* ZEUS_ASIO_CALL zeus_asio_probe_input_name(void* probe, uint32_t index);
ZEUS_ASIO_API const char* ZEUS_ASIO_CALL zeus_asio_probe_output_name(void* probe, uint32_t index);
/* Returns 1 for supported PCM, 0 for an unsupported format, -1 for invalid input. */
ZEUS_ASIO_API int32_t ZEUS_ASIO_CALL zeus_asio_probe_input_supported(void* probe, uint32_t index);
ZEUS_ASIO_API int32_t ZEUS_ASIO_CALL zeus_asio_probe_output_supported(void* probe, uint32_t index);
ZEUS_ASIO_API int32_t ZEUS_ASIO_CALL zeus_asio_probe_supports_48000(void* probe);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_probe_buffer_min(void* probe);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_probe_buffer_max(void* probe);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_probe_buffer_preferred(void* probe);
ZEUS_ASIO_API int32_t ZEUS_ASIO_CALL zeus_asio_probe_buffer_granularity(void* probe);

/*
 * Open one full-duplex, capture-only, or playback-only ASIO session at 48 kHz.
 * input_channel/output_first_channel == -1 disables that direction. Output is
 * always the adjacent stereo pair [output_first_channel, +1]. buffer_frames 0
 * selects the driver's preferred size. Ring sizes are in frames and are
 * rounded up to powers of two. The returned session is initialized but stopped.
 */
ZEUS_ASIO_API void* ZEUS_ASIO_CALL zeus_asio_session_create(
    const char* driver_id_utf8,
    int32_t input_channel,
    int32_t output_first_channel,
    uint32_t buffer_frames,
    uint32_t capture_ring_frames,
    uint32_t playback_ring_frames);

ZEUS_ASIO_API int32_t ZEUS_ASIO_CALL zeus_asio_session_start(void* session);
ZEUS_ASIO_API int32_t ZEUS_ASIO_CALL zeus_asio_session_stop(void* session);
ZEUS_ASIO_API int32_t ZEUS_ASIO_CALL zeus_asio_session_control_panel(void* session);
ZEUS_ASIO_API void ZEUS_ASIO_CALL zeus_asio_session_destroy(void* session);

ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_session_playback_free(void* session);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_session_playback_write(
    void* session, const float* interleaved_stereo, uint32_t frames);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_session_capture_available(void* session);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_session_capture_read(
    void* session, float* mono, uint32_t frames);

/* Returns 0 when signalled, 1 on timeout, and -1 on invalid/error. */
ZEUS_ASIO_API int32_t ZEUS_ASIO_CALL zeus_asio_session_wait(
    void* session, uint32_t timeout_ms, uint32_t* events);

ZEUS_ASIO_API int32_t ZEUS_ASIO_CALL zeus_asio_session_get_status(
    void* session, zeus_asio_status_v1* status);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_session_sample_rate(void* session);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_session_buffer_frames(void* session);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_session_input_latency_frames(void* session);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_session_output_latency_frames(void* session);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_session_input_channel_count(void* session);
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_session_output_channel_count(void* session);
ZEUS_ASIO_API const char* ZEUS_ASIO_CALL zeus_asio_session_input_channel_name(
    void* session, uint32_t index);
ZEUS_ASIO_API const char* ZEUS_ASIO_CALL zeus_asio_session_output_channel_name(
    void* session, uint32_t index);

/* Copies a UTF-8 diagnostic. Returns required bytes excluding the final NUL. */
ZEUS_ASIO_API uint32_t ZEUS_ASIO_CALL zeus_asio_last_error(
    void* nullable_session, char* utf8, uint32_t capacity);

#ifdef __cplusplus
}
#endif

#endif
