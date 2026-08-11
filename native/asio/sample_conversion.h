// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "asio.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>

namespace zeus_asio::detail {

inline uint16_t read_u16_be(const uint8_t* p) noexcept
{
    return static_cast<uint16_t>((static_cast<uint16_t>(p[0]) << 8) | p[1]);
}

inline uint32_t read_u24_be(const uint8_t* p) noexcept
{
    return (static_cast<uint32_t>(p[0]) << 16) |
        (static_cast<uint32_t>(p[1]) << 8) | p[2];
}

inline uint32_t read_u24_le(const uint8_t* p) noexcept
{
    return static_cast<uint32_t>(p[0]) |
        (static_cast<uint32_t>(p[1]) << 8) |
        (static_cast<uint32_t>(p[2]) << 16);
}

inline uint32_t read_u32_be(const uint8_t* p) noexcept
{
    return (static_cast<uint32_t>(p[0]) << 24) |
        (static_cast<uint32_t>(p[1]) << 16) |
        (static_cast<uint32_t>(p[2]) << 8) | p[3];
}

inline uint32_t read_u32_le(const uint8_t* p) noexcept
{
    return static_cast<uint32_t>(p[0]) |
        (static_cast<uint32_t>(p[1]) << 8) |
        (static_cast<uint32_t>(p[2]) << 16) |
        (static_cast<uint32_t>(p[3]) << 24);
}

inline int32_t signed_from_bits(uint32_t raw) noexcept
{
    int32_t value = 0;
    static_assert(sizeof(value) == sizeof(raw));
    std::memcpy(&value, &raw, sizeof(value));
    return value;
}

inline int aligned_valid_bits(ASIOSampleType type) noexcept
{
    switch (type) {
    case ASIOSTInt32MSB16: case ASIOSTInt32LSB16: return 16;
    case ASIOSTInt32MSB18: case ASIOSTInt32LSB18: return 18;
    case ASIOSTInt32MSB20: case ASIOSTInt32LSB20: return 20;
    case ASIOSTInt32MSB24: case ASIOSTInt32LSB24: return 24;
    default: return 0;
    }
}

inline bool aligned_is_msb(ASIOSampleType type) noexcept
{
    return type >= ASIOSTInt32MSB16 && type <= ASIOSTInt32MSB24;
}

inline int32_t decode_aligned_int32(const uint8_t* p, ASIOSampleType type) noexcept
{
    const int bits = aligned_valid_bits(type);
    if (bits == 0) return 0;

    uint32_t raw = aligned_is_msb(type) ? read_u32_be(p) : read_u32_le(p);
    const uint32_t mask = (uint32_t{1} << bits) - 1u;
    const uint32_t sign = uint32_t{1} << (bits - 1);
    raw &= mask;
    if ((raw & sign) != 0) raw |= ~mask;
    return signed_from_bits(raw);
}

inline int32_t quantize_signed(double sample, int bits) noexcept
{
    const int64_t scale = int64_t{1} << (bits - 1);
    const int64_t rounded = std::llround(sample * static_cast<double>(scale));
    return static_cast<int32_t>(std::max(-scale, std::min(scale - 1, rounded)));
}

inline void write_u32(uint8_t* p, uint32_t raw, bool msb) noexcept
{
    if (msb) {
        p[0] = static_cast<uint8_t>(raw >> 24);
        p[1] = static_cast<uint8_t>(raw >> 16);
        p[2] = static_cast<uint8_t>(raw >> 8);
        p[3] = static_cast<uint8_t>(raw);
    } else {
        p[0] = static_cast<uint8_t>(raw);
        p[1] = static_cast<uint8_t>(raw >> 8);
        p[2] = static_cast<uint8_t>(raw >> 16);
        p[3] = static_cast<uint8_t>(raw >> 24);
    }
}

inline void encode_aligned_int32(uint8_t* p, ASIOSampleType type, double sample) noexcept
{
    const int bits = aligned_valid_bits(type);
    if (bits == 0) return;
    const int32_t value = quantize_signed(sample, bits);
    uint32_t raw = 0;
    std::memcpy(&raw, &value, sizeof(raw));
    raw &= (uint32_t{1} << bits) - 1u;
    write_u32(p, raw, aligned_is_msb(type));
}

inline float sample_to_float(const void* buffer, ASIOSampleType type, uint32_t frame) noexcept
{
    const auto* bytes = static_cast<const uint8_t*>(buffer);
    switch (type) {
    case ASIOSTInt16LSB: {
        int16_t value = 0; std::memcpy(&value, bytes + frame * 2u, sizeof(value));
        return static_cast<float>(value) / 32768.0f;
    }
    case ASIOSTInt24LSB: {
        uint32_t raw = read_u24_le(bytes + frame * 3u);
        if ((raw & 0x00800000u) != 0) raw |= 0xff000000u;
        return static_cast<float>(signed_from_bits(raw)) / 8388608.0f;
    }
    case ASIOSTInt32LSB:
        return static_cast<float>(static_cast<double>(signed_from_bits(read_u32_le(bytes + frame * 4u))) / 2147483648.0);
    case ASIOSTInt32LSB16: case ASIOSTInt32LSB18:
    case ASIOSTInt32LSB20: case ASIOSTInt32LSB24: {
        const int bits = aligned_valid_bits(type);
        return static_cast<float>(decode_aligned_int32(bytes + frame * 4u, type)) /
            static_cast<float>(uint32_t{1} << (bits - 1));
    }
    case ASIOSTFloat32LSB: {
        float value = 0; std::memcpy(&value, bytes + frame * 4u, sizeof(value)); return value;
    }
    case ASIOSTFloat64LSB: {
        double value = 0; std::memcpy(&value, bytes + frame * 8u, sizeof(value)); return static_cast<float>(value);
    }
    case ASIOSTInt16MSB: {
        const int16_t value = static_cast<int16_t>(read_u16_be(bytes + frame * 2u));
        return static_cast<float>(value) / 32768.0f;
    }
    case ASIOSTInt24MSB: {
        uint32_t raw = read_u24_be(bytes + frame * 3u);
        if ((raw & 0x00800000u) != 0) raw |= 0xff000000u;
        return static_cast<float>(signed_from_bits(raw)) / 8388608.0f;
    }
    case ASIOSTInt32MSB:
        return static_cast<float>(static_cast<double>(signed_from_bits(read_u32_be(bytes + frame * 4u))) / 2147483648.0);
    case ASIOSTInt32MSB16: case ASIOSTInt32MSB18:
    case ASIOSTInt32MSB20: case ASIOSTInt32MSB24: {
        const int bits = aligned_valid_bits(type);
        return static_cast<float>(decode_aligned_int32(bytes + frame * 4u, type)) /
            static_cast<float>(uint32_t{1} << (bits - 1));
    }
    case ASIOSTFloat32MSB: {
        const uint32_t raw = read_u32_be(bytes + frame * 4u);
        float value = 0; std::memcpy(&value, &raw, sizeof(value)); return value;
    }
    case ASIOSTFloat64MSB: {
        const uint8_t* p = bytes + frame * 8u;
        uint64_t raw = 0;
        for (int i = 0; i < 8; ++i) raw = (raw << 8) | p[i];
        double value = 0; std::memcpy(&value, &raw, sizeof(value)); return static_cast<float>(value);
    }
    default: return 0.0f;
    }
}

inline void float_to_sample(void* buffer, ASIOSampleType type, uint32_t frame, float sample) noexcept
{
    auto* bytes = static_cast<uint8_t*>(buffer);
    const double clamped = std::max(-1.0, std::min(0.9999999995343387, static_cast<double>(sample)));
    switch (type) {
    case ASIOSTFloat32LSB: std::memcpy(bytes + frame * 4u, &sample, 4); return;
    case ASIOSTFloat64LSB: { const double value = sample; std::memcpy(bytes + frame * 8u, &value, 8); return; }
    case ASIOSTInt16LSB: { const int16_t value = static_cast<int16_t>(quantize_signed(clamped, 16)); std::memcpy(bytes + frame * 2u, &value, 2); return; }
    case ASIOSTInt24LSB: {
        const int32_t value = quantize_signed(clamped, 24); auto* p = bytes + frame * 3u;
        p[0] = static_cast<uint8_t>(value); p[1] = static_cast<uint8_t>(value >> 8); p[2] = static_cast<uint8_t>(value >> 16); return;
    }
    case ASIOSTInt32LSB: { const int32_t value = quantize_signed(clamped, 32); std::memcpy(bytes + frame * 4u, &value, 4); return; }
    case ASIOSTInt32LSB16: case ASIOSTInt32LSB18:
    case ASIOSTInt32LSB20: case ASIOSTInt32LSB24:
    case ASIOSTInt32MSB16: case ASIOSTInt32MSB18:
    case ASIOSTInt32MSB20: case ASIOSTInt32MSB24:
        encode_aligned_int32(bytes + frame * 4u, type, clamped); return;
    case ASIOSTFloat32MSB: {
        uint32_t raw = 0; std::memcpy(&raw, &sample, 4); auto* p = bytes + frame * 4u;
        write_u32(p, raw, true); return;
    }
    case ASIOSTFloat64MSB: {
        const double value = sample; uint64_t raw = 0; std::memcpy(&raw, &value, 8); auto* p = bytes + frame * 8u;
        for (int i = 7; i >= 0; --i) { p[i] = static_cast<uint8_t>(raw); raw >>= 8; } return;
    }
    case ASIOSTInt16MSB: {
        const uint16_t value = static_cast<uint16_t>(static_cast<int16_t>(quantize_signed(clamped, 16))); auto* p = bytes + frame * 2u;
        p[0] = static_cast<uint8_t>(value >> 8); p[1] = static_cast<uint8_t>(value); return;
    }
    case ASIOSTInt24MSB: {
        const int32_t value = quantize_signed(clamped, 24); auto* p = bytes + frame * 3u;
        p[0] = static_cast<uint8_t>(value >> 16); p[1] = static_cast<uint8_t>(value >> 8); p[2] = static_cast<uint8_t>(value); return;
    }
    case ASIOSTInt32MSB: {
        const uint32_t value = static_cast<uint32_t>(quantize_signed(clamped, 32));
        write_u32(bytes + frame * 4u, value, true); return;
    }
    default: return;
    }
}

} // namespace zeus_asio::detail
