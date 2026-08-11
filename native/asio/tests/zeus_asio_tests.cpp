// SPDX-License-Identifier: GPL-3.0-only
#include "zeus_asio.h"
#include "sample_conversion.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <thread>
#include <utility>
#include <vector>

namespace {

struct AlignedFormatCase {
    ASIOSampleType type;
    int bits;
    bool msb;
};

uint32_t raw_bits(int32_t value)
{
    uint32_t raw = 0;
    std::memcpy(&raw, &value, sizeof(raw));
    return raw;
}

std::array<uint8_t, 4> expected_bytes(int32_t value, int bits, bool msb)
{
    const uint32_t raw = raw_bits(value) & ((uint32_t{1} << bits) - 1u);
    return msb
        ? std::array<uint8_t, 4>{static_cast<uint8_t>(raw >> 24), static_cast<uint8_t>(raw >> 16),
              static_cast<uint8_t>(raw >> 8), static_cast<uint8_t>(raw)}
        : std::array<uint8_t, 4>{static_cast<uint8_t>(raw), static_cast<uint8_t>(raw >> 8),
              static_cast<uint8_t>(raw >> 16), static_cast<uint8_t>(raw >> 24)};
}

bool test_aligned_int32_formats()
{
    using zeus_asio::detail::float_to_sample;
    using zeus_asio::detail::sample_to_float;

    constexpr std::array formats{
        AlignedFormatCase{ASIOSTInt32LSB16, 16, false},
        AlignedFormatCase{ASIOSTInt32LSB18, 18, false},
        AlignedFormatCase{ASIOSTInt32LSB20, 20, false},
        AlignedFormatCase{ASIOSTInt32LSB24, 24, false},
        AlignedFormatCase{ASIOSTInt32MSB16, 16, true},
        AlignedFormatCase{ASIOSTInt32MSB18, 18, true},
        AlignedFormatCase{ASIOSTInt32MSB20, 20, true},
        AlignedFormatCase{ASIOSTInt32MSB24, 24, true},
    };

    for (const auto& format : formats) {
        const int32_t scale = int32_t{1} << (format.bits - 1);
        const std::array samples{
            std::pair{0.5f, scale / 2},
            std::pair{-0.5f, -scale / 2},
            std::pair{2.0f, scale - 1},
            std::pair{-2.0f, -scale},
        };
        for (const auto& [sample, expected] : samples) {
            std::array<uint8_t, 4> encoded{0xa5, 0xa5, 0xa5, 0xa5};
            float_to_sample(encoded.data(), format.type, 0, sample);
            if (encoded != expected_bytes(expected, format.bits, format.msb)) return false;

            const float decoded = sample_to_float(encoded.data(), format.type, 0);
            const float expected_float = static_cast<float>(expected) / static_cast<float>(scale);
            if (decoded != expected_float) return false;
        }

        // Frame addressing must retain the four-byte ASIO container stride.
        std::array<uint8_t, 8> two_frames{};
        two_frames.fill(0x5a);
        float_to_sample(two_frames.data(), format.type, 1, 0.5f);
        if (!std::all_of(two_frames.begin(), two_frames.begin() + 4,
                [](uint8_t value) { return value == 0x5a; })) return false;
        if (sample_to_float(two_frames.data(), format.type, 1) != 0.5f) return false;
    }
    return true;
}

bool test_packed_int24_last_sample()
{
    using zeus_asio::detail::float_to_sample;
    using zeus_asio::detail::sample_to_float;

    // Exact three-byte objects make an accidental four-byte read visible to
    // AddressSanitizer and other bounds instrumentation.
    std::array<uint8_t, 3> lsb{0x00, 0x00, 0x80};
    std::array<uint8_t, 3> msb{0x80, 0x00, 0x00};
    if (sample_to_float(lsb.data(), ASIOSTInt24LSB, 0) != -1.0f) return false;
    if (sample_to_float(msb.data(), ASIOSTInt24MSB, 0) != -1.0f) return false;

    float_to_sample(lsb.data(), ASIOSTInt24LSB, 0, 2.0f);
    float_to_sample(msb.data(), ASIOSTInt24MSB, 0, 2.0f);
    return lsb == std::array<uint8_t, 3>{0xff, 0xff, 0x7f} &&
        msb == std::array<uint8_t, 3>{0x7f, 0xff, 0xff};
}

} // namespace

int main()
{
    if (!test_aligned_int32_formats()) return 8;
    if (!test_packed_int24_last_sample()) return 9;

    const char* version = zeus_asio_version();
    if (version == nullptr || std::strstr(version, "ASIO SDK 2.3.4") == nullptr) return 1;
    const char* source_hash = zeus_asio_source_hash();
    if (source_hash == nullptr || std::strlen(source_hash) != 64) return 14;

    void* snapshot = zeus_asio_drivers_create();
    if (snapshot == nullptr) return 2;
    const uint32_t count = zeus_asio_drivers_count(snapshot);
    for (uint32_t i = 0; i < count; ++i) {
        if (zeus_asio_drivers_id(snapshot, i) == nullptr) return 3;
        if (zeus_asio_drivers_name(snapshot, i) == nullptr) return 4;
    }
    zeus_asio_drivers_destroy(snapshot);

    if (zeus_asio_probe_create(nullptr) != nullptr) return 10;
    if (zeus_asio_probe_input_count(nullptr) != 0 ||
        zeus_asio_probe_output_count(nullptr) != 0 ||
        zeus_asio_probe_input_name(nullptr, 0) != nullptr ||
        zeus_asio_probe_output_name(nullptr, 0) != nullptr ||
        zeus_asio_probe_input_supported(nullptr, 0) != -1 ||
        zeus_asio_probe_output_supported(nullptr, 0) != -1 ||
        zeus_asio_probe_supports_48000(nullptr) != -1) return 11;

    // A malformed CLSID exercises the probe's STA owner thread without
    // opening hardware or installing ASIO callbacks.
    if (zeus_asio_probe_create("not-a-clsid") != nullptr) return 12;
    char probe_error[256] = {};
    if (zeus_asio_last_error(nullptr, probe_error, sizeof(probe_error)) == 0 ||
        std::strstr(probe_error, "CLSID") == nullptr) return 13;

    // Repeated failed owners must release the process-wide driver claim; this
    // is the no-hardware form of fail-open-then-reopen lifecycle coverage.
    for (int attempt = 0; attempt < 4; ++attempt) {
        if (zeus_asio_session_create("not-a-clsid", 0, -1, 0, 64, 0) != nullptr) return 15;
        char retry_error[256] = {};
        if (zeus_asio_last_error(nullptr, retry_error, sizeof(retry_error)) == 0 ||
            std::strstr(retry_error, "CLSID") == nullptr) return 16;
    }

    if (zeus_asio_session_create(nullptr, -1, -1, 0, 0, 0) != nullptr) return 5;
    char error[256] = {};
    if (zeus_asio_last_error(nullptr, error, sizeof(error)) == 0 || error[0] == '\0') return 6;

    // Global diagnostics are thread-local: concurrent API requests must not
    // race or overwrite another request's create failure before it is read.
    std::atomic<uint32_t> failures{0};
    std::vector<std::thread> workers;
    for (uint32_t worker = 0; worker < 8; ++worker) {
        workers.emplace_back([&failures] {
            for (uint32_t iteration = 0; iteration < 100; ++iteration) {
                void* local_snapshot = zeus_asio_drivers_create();
                if (local_snapshot == nullptr) { failures.fetch_add(1); continue; }
                (void)zeus_asio_drivers_count(local_snapshot);
                zeus_asio_drivers_destroy(local_snapshot);
                if (zeus_asio_session_create(nullptr, -1, -1, 0, 0, 0) != nullptr) {
                    failures.fetch_add(1); continue;
                }
                char local_error[128] = {};
                if (zeus_asio_last_error(nullptr, local_error, sizeof(local_error)) == 0 ||
                    std::strstr(local_error, "driver ID") == nullptr) {
                    failures.fetch_add(1);
                }
            }
        });
    }
    for (auto& worker : workers) worker.join();
    if (failures.load() != 0) return 7;
    return 0;
}
