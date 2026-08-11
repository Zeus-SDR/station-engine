// SPDX-License-Identifier: GPL-3.0-only
#include "zeus_asio.h"

#include <windows.h>
#include <objbase.h>

#include "asiosys.h"
#include "asio.h"
#include "iasiodrv.h"
#include "sample_conversion.h"
#include "source_hash.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace {

using zeus_asio::detail::float_to_sample;
using zeus_asio::detail::sample_to_float;

constexpr uint32_t kSampleRate = 48000;
constexpr uint32_t kDefaultRingFrames = 16384;
constexpr uint32_t kMaxRingFrames = 1u << 24;
constexpr int32_t kError = -1;

thread_local std::string g_last_error;

void set_global_error(std::string message) { g_last_error = std::move(message); }

std::string wide_to_utf8(const wchar_t* value)
{
    if (value == nullptr || *value == L'\0') return {};
    const int required = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (required <= 1) return {};
    std::string result(static_cast<size_t>(required), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), required, nullptr, nullptr);
    result.pop_back();
    return result;
}

std::wstring utf8_to_wide(const char* value)
{
    if (value == nullptr || *value == '\0') return {};
    const int required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, nullptr, 0);
    if (required <= 1) return {};
    std::wstring result(static_cast<size_t>(required), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, result.data(), required);
    result.pop_back();
    return result;
}

std::string ansi_to_utf8(const char* value, size_t max_length)
{
    if (value == nullptr) return {};
    size_t length = 0;
    while (length < max_length && value[length] != '\0') ++length;
    if (length == 0) return {};
    const int wide_count = MultiByteToWideChar(CP_ACP, 0, value, static_cast<int>(length), nullptr, 0);
    if (wide_count <= 0) return std::string(value, length);
    std::wstring wide(static_cast<size_t>(wide_count), L'\0');
    MultiByteToWideChar(CP_ACP, 0, value, static_cast<int>(length), wide.data(), wide_count);
    const int bytes = WideCharToMultiByte(CP_UTF8, 0, wide.data(), wide_count, nullptr, 0, nullptr, nullptr);
    if (bytes <= 0) return std::string(value, length);
    std::string result(static_cast<size_t>(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), wide_count, result.data(), bytes, nullptr, nullptr);
    return result;
}

std::string guid_string(const CLSID& clsid)
{
    wchar_t buffer[40] = {};
    return StringFromGUID2(clsid, buffer, static_cast<int>(std::size(buffer))) > 0
        ? wide_to_utf8(buffer)
        : std::string{};
}

bool asio_ok(ASIOError error) { return error == ASE_OK || error == ASE_SUCCESS; }

uint32_t next_power_of_two(uint32_t value)
{
    value = std::max<uint32_t>(value, 2);
    value = std::min<uint32_t>(value, kMaxRingFrames);
    --value;
    value |= value >> 1;
    value |= value >> 2;
    value |= value >> 4;
    value |= value >> 8;
    value |= value >> 16;
    return value + 1;
}

class FloatRing final {
public:
    FloatRing() = default;
    FloatRing(uint32_t frames, uint32_t channels) { reset(frames, channels); }

    void reset(uint32_t frames, uint32_t channels)
    {
        channels_ = channels;
        capacity_ = channels == 0 ? 0 : next_power_of_two(frames == 0 ? kDefaultRingFrames : frames);
        mask_ = capacity_ == 0 ? 0 : capacity_ - 1;
        samples_.assign(static_cast<size_t>(capacity_) * channels_, 0.0f);
        read_.store(0, std::memory_order_relaxed);
        write_.store(0, std::memory_order_relaxed);
    }

    uint32_t available() const noexcept
    {
        const uint64_t write = write_.load(std::memory_order_acquire);
        const uint64_t read = read_.load(std::memory_order_acquire);
        return static_cast<uint32_t>(std::min<uint64_t>(write - read, capacity_));
    }

    uint32_t free() const noexcept { return capacity_ - available(); }

    uint32_t write(const float* source, uint32_t frames) noexcept
    {
        if (source == nullptr || channels_ == 0) return 0;
        const uint64_t write = write_.load(std::memory_order_relaxed);
        const uint64_t read = read_.load(std::memory_order_acquire);
        const uint32_t count = static_cast<uint32_t>(
            std::min<uint64_t>(frames, capacity_ - (write - read)));
        for (uint32_t frame = 0; frame < count; ++frame) {
            const size_t destination = static_cast<size_t>((write + frame) & mask_) * channels_;
            std::memcpy(samples_.data() + destination,
                source + static_cast<size_t>(frame) * channels_,
                sizeof(float) * channels_);
        }
        write_.store(write + count, std::memory_order_release);
        return count;
    }

    uint32_t read(float* destination, uint32_t frames) noexcept
    {
        if (destination == nullptr || channels_ == 0) return 0;
        const uint64_t read = read_.load(std::memory_order_relaxed);
        const uint64_t write = write_.load(std::memory_order_acquire);
        const uint32_t count = static_cast<uint32_t>(std::min<uint64_t>(frames, write - read));
        for (uint32_t frame = 0; frame < count; ++frame) {
            const size_t source = static_cast<size_t>((read + frame) & mask_) * channels_;
            std::memcpy(destination + static_cast<size_t>(frame) * channels_,
                samples_.data() + source,
                sizeof(float) * channels_);
        }
        read_.store(read + count, std::memory_order_release);
        return count;
    }

    bool write_mono(float sample) noexcept
    {
        if (channels_ != 1) return false;
        const uint64_t write = write_.load(std::memory_order_relaxed);
        const uint64_t read = read_.load(std::memory_order_acquire);
        if (write - read >= capacity_) return false;
        samples_[static_cast<size_t>(write & mask_)] = sample;
        write_.store(write + 1, std::memory_order_release);
        return true;
    }

    bool read_stereo(float& left, float& right) noexcept
    {
        if (channels_ != 2) return false;
        const uint64_t read = read_.load(std::memory_order_relaxed);
        const uint64_t write = write_.load(std::memory_order_acquire);
        if (write == read) return false;
        const size_t source = static_cast<size_t>(read & mask_) * 2;
        left = samples_[source];
        right = samples_[source + 1];
        read_.store(read + 1, std::memory_order_release);
        return true;
    }

private:
    uint32_t channels_ = 0;
    uint32_t capacity_ = 0;
    uint32_t mask_ = 0;
    std::vector<float> samples_;
    alignas(64) std::atomic<uint64_t> read_{0};
    alignas(64) std::atomic<uint64_t> write_{0};
};

static_assert(std::atomic<uint64_t>::is_always_lock_free,
    "The ASIO callback counters and rings require lock-free 64-bit atomics");

bool supported_pcm(ASIOSampleType type) noexcept
{
    return (type >= ASIOSTInt16MSB && type <= ASIOSTFloat64MSB) ||
        (type >= ASIOSTInt32MSB16 && type <= ASIOSTInt32MSB24) ||
        (type >= ASIOSTInt16LSB && type <= ASIOSTFloat64LSB) ||
        (type >= ASIOSTInt32LSB16 && type <= ASIOSTInt32LSB24);
}

struct DriverEntry { std::string id; std::string name; };
struct DriverSnapshot { std::vector<DriverEntry> entries; };
struct ProbeChannel { std::string name; bool supported = false; };

std::wstring read_registry_string(HKEY key, const wchar_t* name)
{
    DWORD type = 0;
    DWORD bytes = 0;
    if (RegQueryValueExW(key, name, nullptr, &type, nullptr, &bytes) != ERROR_SUCCESS ||
        (type != REG_SZ && type != REG_EXPAND_SZ) || bytes < sizeof(wchar_t)) return {};
    std::vector<wchar_t> buffer(bytes / sizeof(wchar_t) + 1, L'\0');
    if (RegQueryValueExW(key, name, nullptr, &type,
        reinterpret_cast<BYTE*>(buffer.data()), &bytes) != ERROR_SUCCESS) return {};
    return std::wstring(buffer.data());
}

std::unique_ptr<DriverSnapshot> enumerate_drivers()
{
    auto snapshot = std::make_unique<DriverSnapshot>();
    HKEY root = nullptr;
    const LSTATUS opened = RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\ASIO", 0,
        KEY_READ | KEY_WOW64_64KEY, &root);
    if (opened == ERROR_FILE_NOT_FOUND) return snapshot;
    if (opened != ERROR_SUCCESS) {
        set_global_error("Could not open HKLM\\SOFTWARE\\ASIO.");
        return nullptr;
    }

    for (DWORD index = 0;; ++index) {
        wchar_t subkey_name[256] = {};
        DWORD length = static_cast<DWORD>(std::size(subkey_name));
        const LSTATUS status = RegEnumKeyExW(root, index, subkey_name, &length,
            nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_NO_MORE_ITEMS) break;
        if (status != ERROR_SUCCESS) continue;

        HKEY subkey = nullptr;
        if (RegOpenKeyExW(root, subkey_name, 0, KEY_READ | KEY_WOW64_64KEY, &subkey) != ERROR_SUCCESS) continue;
        const std::wstring clsid_text = read_registry_string(subkey, L"CLSID");
        std::wstring description = read_registry_string(subkey, L"Description");
        RegCloseKey(subkey);
        if (clsid_text.empty()) continue;
        CLSID clsid{};
        if (FAILED(CLSIDFromString(clsid_text.c_str(), &clsid))) continue;
        if (description.empty()) description.assign(subkey_name, length);
        snapshot->entries.push_back({guid_string(clsid), wide_to_utf8(description.c_str())});
    }
    RegCloseKey(root);
    std::sort(snapshot->entries.begin(), snapshot->entries.end(), [](const DriverEntry& a, const DriverEntry& b) {
        const int by_name = _stricmp(a.name.c_str(), b.name.c_str());
        return by_name == 0 ? a.id < b.id : by_name < 0;
    });
    return snapshot;
}

class DriverProbe final {
public:
    explicit DriverProbe(std::string driver_id) : driver_id_(std::move(driver_id)) {}

    bool run()
    {
        std::thread owner([this] { owner_main(); });
        owner.join();
        return success_;
    }

    const std::string& error() const noexcept { return error_; }
    const std::vector<ProbeChannel>& inputs() const noexcept { return inputs_; }
    const std::vector<ProbeChannel>& outputs() const noexcept { return outputs_; }
    bool supports_48000() const noexcept { return supports_48000_; }
    uint32_t buffer_min() const noexcept { return buffer_min_; }
    uint32_t buffer_max() const noexcept { return buffer_max_; }
    uint32_t buffer_preferred() const noexcept { return buffer_preferred_; }
    int32_t buffer_granularity() const noexcept { return buffer_granularity_; }

private:
    static std::string channel_name(const ASIOChannelInfo& info, bool input, long index)
    {
        std::string name = ansi_to_utf8(info.name, sizeof(info.name));
        if (name.empty()) name = std::string(input ? "Input " : "Output ") + std::to_string(index + 1);
        return name;
    }

    void owner_main()
    {
        IASIO* driver = nullptr;
        HWND window = nullptr;
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        if (FAILED(apartment)) { error_ = "Could not initialize the ASIO probe COM apartment."; return; }

        try {
            window = CreateWindowExW(0, L"STATIC", L"Zeus ASIO probe", WS_OVERLAPPED,
                0, 0, 0, 0, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
            const std::wstring wide_id = utf8_to_wide(driver_id_.c_str());
            CLSID clsid{};
            if (window == nullptr || wide_id.empty() || FAILED(CLSIDFromString(wide_id.c_str(), &clsid))) {
                error_ = "Invalid ASIO driver CLSID or probe window creation failed.";
            } else {
                const HRESULT created = CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER,
                    clsid, reinterpret_cast<void**>(&driver));
                if (FAILED(created) || driver == nullptr) {
                    error_ = "Could not instantiate the selected ASIO driver for probing (wrong architecture, missing, or busy).";
                } else if (driver->init(window) != ASIOTrue) {
                    char message[124] = {};
                    driver->getErrorMessage(message);
                    error_ = std::string("ASIO driver probe initialization failed: ") +
                        ansi_to_utf8(message, sizeof(message));
                } else {
                    long input_count = 0, output_count = 0;
                    const ASIOError channels = driver->getChannels(&input_count, &output_count);
                    if (!asio_ok(channels) || input_count < 0 || output_count < 0) {
                        error_ = "ASIO driver probe channel query failed.";
                    } else {
                        inputs_.reserve(static_cast<size_t>(input_count));
                        outputs_.reserve(static_cast<size_t>(output_count));
                        for (long index = 0; index < input_count; ++index) {
                            ASIOChannelInfo info{}; info.channel = index; info.isInput = ASIOTrue;
                            const bool queried = asio_ok(driver->getChannelInfo(&info));
                            inputs_.push_back({queried ? channel_name(info, true, index) : "Input " + std::to_string(index + 1),
                                queried && supported_pcm(info.type)});
                        }
                        for (long index = 0; index < output_count; ++index) {
                            ASIOChannelInfo info{}; info.channel = index; info.isInput = ASIOFalse;
                            const bool queried = asio_ok(driver->getChannelInfo(&info));
                            outputs_.push_back({queried ? channel_name(info, false, index) : "Output " + std::to_string(index + 1),
                                queried && supported_pcm(info.type)});
                        }

                        ASIOSampleRate current = 0;
                        supports_48000_ = asio_ok(driver->getSampleRate(&current)) &&
                            std::fabs(current - static_cast<double>(kSampleRate)) <= 0.5;
                        if (!supports_48000_) {
                            supports_48000_ = asio_ok(driver->canSampleRate(static_cast<ASIOSampleRate>(kSampleRate)));
                        }

                        long minimum = 0, maximum = 0, preferred = 0, granularity = 0;
                        if (asio_ok(driver->getBufferSize(&minimum, &maximum, &preferred, &granularity)) &&
                            minimum > 0 && maximum >= minimum && preferred > 0) {
                            buffer_min_ = static_cast<uint32_t>(minimum);
                            buffer_max_ = static_cast<uint32_t>(maximum);
                            buffer_preferred_ = static_cast<uint32_t>(preferred);
                            buffer_granularity_ = static_cast<int32_t>(granularity);
                        }
                        success_ = true;
                    }
                }
            }
        } catch (const std::bad_alloc&) {
            error_ = "Out of memory probing ASIO driver channels.";
        } catch (...) {
            error_ = "Unexpected failure probing ASIO driver channels.";
        }

        if (driver != nullptr) driver->Release();
        if (window != nullptr) DestroyWindow(window);
        CoUninitialize();
    }

    std::string driver_id_;
    std::string error_;
    std::vector<ProbeChannel> inputs_;
    std::vector<ProbeChannel> outputs_;
    bool success_ = false;
    bool supports_48000_ = false;
    uint32_t buffer_min_ = 0;
    uint32_t buffer_max_ = 0;
    uint32_t buffer_preferred_ = 0;
    int32_t buffer_granularity_ = 0;
};

class Session;
std::atomic<Session*> g_active_session{nullptr};
std::atomic<uint32_t> g_callback_entries{0};
std::atomic<bool> g_driver_abandoned{false};
std::atomic<bool> g_driver_claimed{false};
std::mutex g_create_mutex;

class DriverClaim final {
public:
    DriverClaim() noexcept
    {
        bool expected = false;
        acquired_ = g_driver_claimed.compare_exchange_strong(expected, true, std::memory_order_acq_rel);
    }
    ~DriverClaim()
    {
        if (acquired_) g_driver_claimed.store(false, std::memory_order_release);
    }
    DriverClaim(const DriverClaim&) = delete;
    DriverClaim& operator=(const DriverClaim&) = delete;

    bool acquired() const noexcept { return acquired_; }
    void retain() noexcept { acquired_ = false; }

private:
    bool acquired_ = false;
};

class CallbackEntry final {
public:
    CallbackEntry() noexcept { g_callback_entries.fetch_add(1, std::memory_order_seq_cst); }
    ~CallbackEntry() { g_callback_entries.fetch_sub(1, std::memory_order_seq_cst); }
    CallbackEntry(const CallbackEntry&) = delete;
    CallbackEntry& operator=(const CallbackEntry&) = delete;

    Session* session() const noexcept { return g_active_session.load(std::memory_order_seq_cst); }
};

class Session final {
public:
    Session(std::string driver_id, int32_t input_channel, int32_t output_first,
        uint32_t requested_buffer, uint32_t capture_ring_frames, uint32_t playback_ring_frames)
        : driver_id_(std::move(driver_id)), input_channel_(input_channel),
          output_first_(output_first), requested_buffer_(requested_buffer),
          capture_ring_(capture_ring_frames, input_channel >= 0 ? 1u : 0u),
          playback_ring_(playback_ring_frames, output_first >= 0 ? 2u : 0u)
    {
        wait_event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        command_event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    }

    ~Session()
    {
        shutdown();
        release_driver_claim();
        if (command_event_) CloseHandle(command_event_);
        if (wait_event_) CloseHandle(wait_event_);
    }

    bool initialize()
    {
        if (!wait_event_ || !command_event_) { set_error("Could not create ASIO synchronization events."); return false; }
        owner_ = std::thread([this] { owner_main(); });
        std::unique_lock lock(init_mutex_);
        init_cv_.wait(lock, [this] { return init_done_; });
        if (!init_success_ && owner_.joinable()) owner_.join();
        return init_success_;
    }

    void accept_driver_claim() noexcept { driver_claim_owned_ = true; }

    int32_t start() { return submit(Command::Start); }
    int32_t stop() { return submit(Command::Stop); }
    int32_t control_panel() { return submit(Command::ControlPanel); }

    void shutdown()
    {
        if (!owner_.joinable()) return;
        if (init_success_) (void)submit(Command::Exit);
        owner_.join();
        init_success_ = false;
    }

    uint32_t playback_free() const noexcept { return output_first_ >= 0 ? playback_ring_.free() : 0; }
    uint32_t playback_write(const float* samples, uint32_t frames) noexcept { return playback_ring_.write(samples, frames); }
    uint32_t capture_available() const noexcept { return input_channel_ >= 0 ? capture_ring_.available() : 0; }
    uint32_t capture_read(float* samples, uint32_t frames) noexcept { return capture_ring_.read(samples, frames); }

    int32_t wait(uint32_t timeout_ms, uint32_t* result_events) noexcept
    {
        if (result_events == nullptr || wait_event_ == nullptr) return -1;
        const ULONGLONG deadline = timeout_ms == INFINITE ? 0 : GetTickCount64() + timeout_ms;
        for (;;) {
            const uint32_t immediate = events_.exchange(0, std::memory_order_acq_rel);
            if (immediate != 0) { *result_events = immediate; return 0; }
            DWORD remaining = timeout_ms;
            if (timeout_ms != INFINITE) {
                const ULONGLONG now = GetTickCount64();
                if (now >= deadline) { *result_events = 0; return 1; }
                remaining = static_cast<DWORD>(std::min<ULONGLONG>(deadline - now, MAXDWORD - 1));
            }
            const DWORD waited = WaitForSingleObject(wait_event_, remaining);
            if (waited == WAIT_TIMEOUT) { *result_events = 0; return 1; }
            if (waited != WAIT_OBJECT_0) { *result_events = 0; return -1; }
        }
    }

    int32_t status(zeus_asio_status_v1* value) const noexcept
    {
        if (value == nullptr || value->struct_size < sizeof(zeus_asio_status_v1)) return -1;
        uint32_t flags = initialized_.load(std::memory_order_acquire) ? ZEUS_ASIO_STATUS_INITIALIZED : 0;
        if (running_.load(std::memory_order_acquire)) flags |= ZEUS_ASIO_STATUS_RUNNING;
        if (input_channel_ >= 0) flags |= ZEUS_ASIO_STATUS_INPUT_ENABLED;
        if (output_first_ >= 0) flags |= ZEUS_ASIO_STATUS_OUTPUT_ENABLED;
        if ((events_.load(std::memory_order_acquire) & ZEUS_ASIO_EVENT_RESET_REQUESTED) != 0) flags |= ZEUS_ASIO_STATUS_RESET_REQUESTED;
        if (driver_error_.load(std::memory_order_acquire)) flags |= ZEUS_ASIO_STATUS_DRIVER_ERROR;
        value->flags = flags;
        value->pending_events = events_.load(std::memory_order_acquire);
        value->sample_rate = kSampleRate;
        value->buffer_frames = buffer_frames_;
        value->input_latency_frames = input_latency_;
        value->output_latency_frames = output_latency_;
        value->callback_count = callback_count_.load(std::memory_order_relaxed);
        value->capture_frames = capture_frames_.load(std::memory_order_relaxed);
        value->playback_frames = playback_frames_.load(std::memory_order_relaxed);
        value->capture_overrun_count = capture_overrun_count_.load(std::memory_order_relaxed);
        value->capture_overrun_frames = capture_overrun_frames_.load(std::memory_order_relaxed);
        value->playback_underrun_count = playback_underrun_count_.load(std::memory_order_relaxed);
        value->playback_underrun_frames = playback_underrun_frames_.load(std::memory_order_relaxed);
        value->reset_request_count = reset_request_count_.load(std::memory_order_relaxed);
        value->resync_request_count = resync_request_count_.load(std::memory_order_relaxed);
        value->last_asio_error = last_asio_error_.load(std::memory_order_relaxed);
        value->reserved = 0;
        return 0;
    }

    uint32_t buffer_frames() const noexcept { return buffer_frames_; }
    uint32_t input_latency() const noexcept { return input_latency_; }
    uint32_t output_latency() const noexcept { return output_latency_; }
    uint32_t input_count() const noexcept { return static_cast<uint32_t>(input_names_.size()); }
    uint32_t output_count() const noexcept { return static_cast<uint32_t>(output_names_.size()); }
    const char* input_name(uint32_t index) const noexcept { return index < input_names_.size() ? input_names_[index].c_str() : nullptr; }
    const char* output_name(uint32_t index) const noexcept { return index < output_names_.size() ? output_names_[index].c_str() : nullptr; }

    std::string error() const
    {
        std::lock_guard lock(error_mutex_);
        return error_;
    }

    void process(long buffer_index) noexcept
    {
        callback_count_.fetch_add(1, std::memory_order_relaxed);
        const uint32_t index = buffer_index == 0 ? 0u : 1u;

        if (input_channel_ >= 0 && !buffer_infos_.empty()) {
            void* input = buffer_infos_[0].buffers[index];
            uint32_t accepted = 0;
            for (uint32_t frame = 0; frame < buffer_frames_; ++frame) {
                if (!capture_ring_.write_mono(sample_to_float(input, input_type_, frame))) break;
                ++accepted;
            }
            capture_frames_.fetch_add(accepted, std::memory_order_relaxed);
            if (accepted < buffer_frames_) {
                capture_overrun_count_.fetch_add(1, std::memory_order_relaxed);
                capture_overrun_frames_.fetch_add(buffer_frames_ - accepted, std::memory_order_relaxed);
            }
            if (accepted != 0) signal(ZEUS_ASIO_EVENT_CAPTURE_AVAILABLE);
        }

        if (output_first_ >= 0) {
            const size_t offset = input_channel_ >= 0 ? 1u : 0u;
            void* left_buffer = buffer_infos_[offset].buffers[index];
            void* right_buffer = buffer_infos_[offset + 1].buffers[index];
            uint32_t supplied = 0;
            for (uint32_t frame = 0; frame < buffer_frames_; ++frame) {
                float left = 0.0f, right = 0.0f;
                if (playback_ring_.read_stereo(left, right)) ++supplied;
                float_to_sample(left_buffer, output_types_[0], frame, left);
                float_to_sample(right_buffer, output_types_[1], frame, right);
            }
            playback_frames_.fetch_add(supplied, std::memory_order_relaxed);
            if (supplied < buffer_frames_) {
                playback_underrun_count_.fetch_add(1, std::memory_order_relaxed);
                playback_underrun_frames_.fetch_add(buffer_frames_ - supplied, std::memory_order_relaxed);
            }
            signal(ZEUS_ASIO_EVENT_PLAYBACK_SPACE);
            if (supports_output_ready_ && driver_ != nullptr) (void)driver_->outputReady();
        }
    }

    void sample_rate_changed(ASIOSampleRate) noexcept { signal(ZEUS_ASIO_EVENT_SAMPLE_RATE_CHANGED); }

    long asio_message(long selector, long value) noexcept
    {
        switch (selector) {
        case kAsioSelectorSupported:
            return value == kAsioResetRequest || value == kAsioResyncRequest ||
                value == kAsioLatenciesChanged || value == kAsioEngineVersion ||
                value == kAsioSupportsTimeInfo ? 1 : 0;
        case kAsioEngineVersion: return 2;
        case kAsioResetRequest:
            reset_request_count_.fetch_add(1, std::memory_order_relaxed);
            signal(ZEUS_ASIO_EVENT_RESET_REQUESTED); return 1;
        case kAsioResyncRequest:
            resync_request_count_.fetch_add(1, std::memory_order_relaxed);
            signal(ZEUS_ASIO_EVENT_RESYNC_REQUESTED); return 1;
        case kAsioLatenciesChanged: signal(ZEUS_ASIO_EVENT_LATENCIES_CHANGED); return 1;
        case kAsioSupportsTimeInfo: return 1;
        case kAsioSupportsTimeCode: return 0;
        default: return 0;
        }
    }

private:
    enum class Command { None, Start, Stop, ControlPanel, Exit };

    void signal(uint32_t event) noexcept
    {
        events_.fetch_or(event, std::memory_order_release);
        if (wait_event_ != nullptr) SetEvent(wait_event_);
    }

    void set_error(std::string message, ASIOError asio_error = ASE_OK)
    {
        { std::lock_guard lock(error_mutex_); error_ = std::move(message); }
        last_asio_error_.store(static_cast<int32_t>(asio_error), std::memory_order_release);
        driver_error_.store(true, std::memory_order_release);
    }

    int32_t submit(Command command)
    {
        std::lock_guard submit_lock(submit_mutex_);
        std::unique_lock lock(command_mutex_);
        if (!owner_available_) return -1;
        command_cv_.wait(lock, [this] { return pending_command_ == Command::None || !owner_available_; });
        if (!owner_available_) return -1;
        pending_command_ = command;
        command_completed_ = false;
        if (!SetEvent(command_event_)) {
            pending_command_ = Command::None;
            command_completed_ = true;
            command_result_ = -1;
            lock.unlock();
            set_error("Could not signal the ASIO owner thread.");
            return -1;
        }
        command_cv_.wait(lock, [this] { return command_completed_ || !owner_available_; });
        if (!command_completed_) return -1;
        return command_result_;
    }

    void complete_command(int32_t result)
    {
        std::lock_guard lock(command_mutex_);
        command_result_ = result;
        pending_command_ = Command::None;
        command_completed_ = true;
        command_cv_.notify_all();
    }

    void set_owner_available(bool available, int32_t pending_result = -1)
    {
        std::lock_guard lock(command_mutex_);
        owner_available_ = available;
        if (!available && pending_command_ != Command::None) {
            command_result_ = pending_result;
            pending_command_ = Command::None;
            command_completed_ = true;
        }
        command_cv_.notify_all();
    }

    void owner_main()
    {
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        const bool com_initialized = SUCCEEDED(apartment);
        if (!com_initialized) {
            set_error("Could not initialize the ASIO COM apartment.");
            finish_initialization(false);
            set_owner_available(false);
            return;
        }
        window_ = CreateWindowExW(0, L"STATIC", L"Zeus audio host", WS_OVERLAPPED,
            0, 0, 0, 0, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (!initialize_driver()) {
            cleanup_driver();
            if (window_) DestroyWindow(window_);
            CoUninitialize();
            finish_initialization(false);
            set_owner_available(false);
            return;
        }
        set_owner_available(true);
        finish_initialization(true);

        bool exiting = false;
        bool owner_wait_failed = false;
        while (!exiting) {
            const DWORD waited = MsgWaitForMultipleObjects(1, &command_event_, FALSE, INFINITE, QS_ALLINPUT);
            if (waited == WAIT_OBJECT_0) {
                Command command;
                { std::lock_guard lock(command_mutex_); command = pending_command_; }
                int32_t result = 0;
                switch (command) {
                case Command::Start:
                    if (!running_.load(std::memory_order_acquire)) {
                        const ASIOError error = driver_->start();
                        if (!asio_ok(error)) { set_error("ASIO driver start failed.", error); result = -1; signal(ZEUS_ASIO_EVENT_ERROR); }
                        else { running_.store(true, std::memory_order_release); signal(ZEUS_ASIO_EVENT_PLAYBACK_SPACE); }
                    }
                    break;
                case Command::Stop:
                    result = stop_driver();
                    break;
                case Command::ControlPanel: {
                    const ASIOError error = driver_->controlPanel();
                    if (!asio_ok(error)) { set_error("ASIO control panel failed.", error); result = -1; }
                    break;
                }
                case Command::Exit:
                    result = stop_driver_with_retries();
                    if (running_.load(std::memory_order_acquire)) abandon_driver();
                    else cleanup_driver();
                    exiting = true;
                    break;
                default: result = -1; break;
                }
                complete_command(result);
            } else if (waited == WAIT_OBJECT_0 + 1) {
                MSG message{};
                while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                }
            } else {
                set_error("ASIO owner thread wait failed.");
                signal(ZEUS_ASIO_EVENT_ERROR);
                owner_wait_failed = true;
                set_owner_available(false);
                break;
            }
        }
        if (owner_wait_failed) {
            (void)stop_driver_with_retries();
            if (running_.load(std::memory_order_acquire)) abandon_driver();
            else cleanup_driver();
        }
        set_owner_available(false);
        if (window_) DestroyWindow(window_);
        CoUninitialize();
    }

    void finish_initialization(bool success)
    {
        std::lock_guard lock(init_mutex_);
        init_success_ = success;
        init_done_ = true;
        init_cv_.notify_all();
    }

    uint32_t negotiate_buffer(long minimum, long maximum, long preferred, long granularity) const
    {
        long chosen = requested_buffer_ == 0 ? preferred : static_cast<long>(requested_buffer_);
        chosen = std::max(minimum, std::min(maximum, chosen));
        if (granularity == -1) {
            long power = 1;
            while (power < chosen && power < maximum) power <<= 1;
            const long lower = power > minimum ? power >> 1 : power;
            chosen = (power <= maximum && power - chosen < chosen - lower) ? power : std::max(minimum, lower);
        } else if (granularity > 0) {
            const long steps = (chosen - minimum + granularity / 2) / granularity;
            chosen = minimum + steps * granularity;
            chosen = std::max(minimum, std::min(maximum, chosen));
        } else if (granularity == 0) {
            chosen = preferred;
        }
        return static_cast<uint32_t>(chosen);
    }

    bool initialize_driver()
    {
        const std::wstring wide_id = utf8_to_wide(driver_id_.c_str());
        CLSID clsid{};
        if (wide_id.empty() || FAILED(CLSIDFromString(wide_id.c_str(), &clsid))) {
            set_error("Invalid ASIO driver CLSID."); return false;
        }
        const HRESULT created = CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER,
            clsid, reinterpret_cast<void**>(&driver_));
        if (FAILED(created) || driver_ == nullptr) {
            set_error("Could not instantiate the selected ASIO driver (wrong architecture, missing, or busy)."); return false;
        }
        if (driver_->init(window_) != ASIOTrue) {
            char message[124] = {}; driver_->getErrorMessage(message);
            set_error(std::string("ASIO driver initialization failed: ") + ansi_to_utf8(message, sizeof(message))); return false;
        }

        long input_count = 0, output_count = 0;
        ASIOError error = driver_->getChannels(&input_count, &output_count);
        if (!asio_ok(error)) { set_error("ASIO channel query failed.", error); return false; }
        if (input_channel_ >= input_count || output_first_ >= 0 && output_first_ + 1 >= output_count) {
            set_error("Selected ASIO channel is outside the driver's channel range.", ASE_InvalidParameter); return false;
        }
        if (input_channel_ < 0 && output_first_ < 0) {
            set_error("ASIO session must enable input, output, or both.", ASE_InvalidParameter); return false;
        }

        input_names_.reserve(static_cast<size_t>(std::max(0L, input_count)));
        output_names_.reserve(static_cast<size_t>(std::max(0L, output_count)));
        for (long channel = 0; channel < input_count; ++channel) {
            ASIOChannelInfo info{}; info.channel = channel; info.isInput = ASIOTrue;
            if (asio_ok(driver_->getChannelInfo(&info))) input_names_.push_back(ansi_to_utf8(info.name, sizeof(info.name)));
            else input_names_.push_back("Input " + std::to_string(channel + 1));
        }
        for (long channel = 0; channel < output_count; ++channel) {
            ASIOChannelInfo info{}; info.channel = channel; info.isInput = ASIOFalse;
            if (asio_ok(driver_->getChannelInfo(&info))) output_names_.push_back(ansi_to_utf8(info.name, sizeof(info.name)));
            else output_names_.push_back("Output " + std::to_string(channel + 1));
        }

        ASIOSampleRate current_rate = 0;
        error = driver_->getSampleRate(&current_rate);
        if (!asio_ok(error) || std::fabs(current_rate - kSampleRate) > 0.5) {
            error = driver_->canSampleRate(static_cast<ASIOSampleRate>(kSampleRate));
            if (!asio_ok(error)) { set_error("Selected ASIO driver does not support 48 kHz.", error); return false; }
            error = driver_->setSampleRate(static_cast<ASIOSampleRate>(kSampleRate));
            if (!asio_ok(error)) { set_error("Could not set selected ASIO driver to 48 kHz.", error); return false; }
        }

        long minimum = 0, maximum = 0, preferred = 0, granularity = 0;
        error = driver_->getBufferSize(&minimum, &maximum, &preferred, &granularity);
        if (!asio_ok(error) || minimum <= 0 || maximum < minimum || preferred <= 0) {
            set_error("ASIO buffer-size query returned invalid values.", error); return false;
        }
        buffer_frames_ = negotiate_buffer(minimum, maximum, preferred, granularity);

        buffer_infos_.clear();
        if (input_channel_ >= 0) {
            ASIOChannelInfo info{}; info.channel = input_channel_; info.isInput = ASIOTrue;
            error = driver_->getChannelInfo(&info);
            if (!asio_ok(error) || !supported_pcm(info.type)) { set_error("Selected ASIO input sample format is unsupported.", error); return false; }
            input_type_ = info.type;
            ASIOBufferInfo buffer{}; buffer.isInput = ASIOTrue; buffer.channelNum = input_channel_;
            buffer_infos_.push_back(buffer);
        }
        if (output_first_ >= 0) {
            for (long offset = 0; offset < 2; ++offset) {
                ASIOChannelInfo info{}; info.channel = output_first_ + offset; info.isInput = ASIOFalse;
                error = driver_->getChannelInfo(&info);
                if (!asio_ok(error) || !supported_pcm(info.type)) { set_error("Selected ASIO output sample format is unsupported.", error); return false; }
                output_types_[offset] = info.type;
                ASIOBufferInfo buffer{}; buffer.isInput = ASIOFalse; buffer.channelNum = output_first_ + offset;
                buffer_infos_.push_back(buffer);
            }
        }

        Session* expected = nullptr;
        if (!g_active_session.compare_exchange_strong(expected, this, std::memory_order_acq_rel)) {
            set_error("Only one ASIO session may be active at a time.", ASE_InvalidMode); return false;
        }
        callbacks_.bufferSwitch = &buffer_switch;
        callbacks_.sampleRateDidChange = &rate_changed;
        callbacks_.asioMessage = &message;
        callbacks_.bufferSwitchTimeInfo = &buffer_switch_time_info;
        error = driver_->createBuffers(buffer_infos_.data(), static_cast<long>(buffer_infos_.size()),
            static_cast<long>(buffer_frames_), &callbacks_);
        if (!asio_ok(error)) {
            detach_callbacks();
            set_error("ASIO buffer creation failed.", error); return false;
        }
        buffers_created_ = true;
        long input_latency = 0, output_latency = 0;
        if (asio_ok(driver_->getLatencies(&input_latency, &output_latency))) {
            input_latency_ = static_cast<uint32_t>(std::max(0L, input_latency));
            output_latency_ = static_cast<uint32_t>(std::max(0L, output_latency));
        }
        supports_output_ready_ = output_first_ >= 0 && asio_ok(driver_->outputReady());
        if (output_first_ >= 0) {
            const size_t offset = input_channel_ >= 0 ? 1u : 0u;
            for (uint32_t half = 0; half < 2; ++half) {
                for (uint32_t frame = 0; frame < buffer_frames_; ++frame) {
                    float_to_sample(buffer_infos_[offset].buffers[half], output_types_[0], frame, 0.0f);
                    float_to_sample(buffer_infos_[offset + 1].buffers[half], output_types_[1], frame, 0.0f);
                }
            }
        }
        initialized_.store(true, std::memory_order_release);
        driver_error_.store(false, std::memory_order_release);
        return true;
    }

    void wait_for_callbacks() noexcept
    {
        while (g_callback_entries.load(std::memory_order_seq_cst) != 0) SwitchToThread();
    }

    void detach_callbacks() noexcept
    {
        Session* expected = this;
        g_active_session.compare_exchange_strong(expected, nullptr, std::memory_order_seq_cst);
        wait_for_callbacks();
    }

    int32_t stop_driver()
    {
        if (!running_.load(std::memory_order_acquire)) return 0;
        const ASIOError error = driver_->stop();
        if (!asio_ok(error)) { set_error("ASIO driver stop failed.", error); signal(ZEUS_ASIO_EVENT_ERROR); return -1; }
        running_.store(false, std::memory_order_release);
        wait_for_callbacks();
        signal(ZEUS_ASIO_EVENT_STOPPED);
        return 0;
    }

    int32_t stop_driver_with_retries()
    {
        if (!running_.load(std::memory_order_acquire)) return 0;
        for (int attempt = 0; attempt < 3; ++attempt) {
            if (stop_driver() == 0) return 0;
            SwitchToThread();
        }
        set_error("ASIO driver would not stop; callbacks were detached and driver resources were retained.",
            static_cast<ASIOError>(last_asio_error_.load(std::memory_order_acquire)));
        signal(ZEUS_ASIO_EVENT_ERROR);
        return -1;
    }

    void cleanup_driver()
    {
        detach_callbacks();
        initialized_.store(false, std::memory_order_release);
        if (driver_ != nullptr && buffers_created_) {
            (void)driver_->disposeBuffers();
            buffers_created_ = false;
        }
        if (driver_ != nullptr) { driver_->Release(); driver_ = nullptr; }
        release_driver_claim();
    }

    void abandon_driver() noexcept
    {
        // A driver that reports stop failure may still invoke callbacks. Remove
        // the session from the static callback rendezvous and wait out any
        // callback that already acquired it. Releasing the COM object or its
        // buffers would be a use-after-free, so retain them until process exit.
        detach_callbacks();
        g_driver_abandoned.store(true, std::memory_order_release);
        driver_claim_owned_ = false; // The process-wide claim stays latched.
        initialized_.store(false, std::memory_order_release);
        running_.store(false, std::memory_order_release);
        buffers_created_ = false;
        driver_ = nullptr;
    }

    static void buffer_switch(long index, ASIOBool) noexcept
    {
        CallbackEntry entry;
        Session* session = entry.session();
        if (session) session->process(index);
    }

    void release_driver_claim() noexcept
    {
        if (!driver_claim_owned_) return;
        driver_claim_owned_ = false;
        g_driver_claimed.store(false, std::memory_order_release);
    }
    static ASIOTime* buffer_switch_time_info(ASIOTime* time, long index, ASIOBool) noexcept
    {
        CallbackEntry entry;
        Session* session = entry.session();
        if (session) session->process(index);
        return time;
    }
    static void rate_changed(ASIOSampleRate rate) noexcept
    {
        CallbackEntry entry;
        Session* session = entry.session();
        if (session) session->sample_rate_changed(rate);
    }
    static long message(long selector, long value, void*, double*) noexcept
    {
        CallbackEntry entry;
        Session* session = entry.session();
        return session ? session->asio_message(selector, value) : 0;
    }

    std::string driver_id_;
    int32_t input_channel_ = -1;
    int32_t output_first_ = -1;
    uint32_t requested_buffer_ = 0;
    FloatRing capture_ring_;
    FloatRing playback_ring_;

    HANDLE wait_event_ = nullptr;
    HANDLE command_event_ = nullptr;
    std::thread owner_;
    HWND window_ = nullptr;
    IASIO* driver_ = nullptr;
    ASIOCallbacks callbacks_{};
    std::vector<ASIOBufferInfo> buffer_infos_;
    ASIOSampleType input_type_ = ASIOSTFloat32LSB;
    ASIOSampleType output_types_[2] = {ASIOSTFloat32LSB, ASIOSTFloat32LSB};
    bool buffers_created_ = false;
    bool supports_output_ready_ = false;
    bool driver_claim_owned_ = false;
    uint32_t buffer_frames_ = 0;
    uint32_t input_latency_ = 0;
    uint32_t output_latency_ = 0;
    std::vector<std::string> input_names_;
    std::vector<std::string> output_names_;

    std::mutex init_mutex_;
    std::condition_variable init_cv_;
    bool init_done_ = false;
    bool init_success_ = false;
    std::mutex command_mutex_;
    std::mutex submit_mutex_;
    std::condition_variable command_cv_;
    Command pending_command_ = Command::None;
    bool command_completed_ = false;
    bool owner_available_ = false;
    int32_t command_result_ = -1;
    mutable std::mutex error_mutex_;
    std::string error_;

    std::atomic<bool> initialized_{false};
    std::atomic<bool> running_{false};
    std::atomic<bool> driver_error_{false};
    std::atomic<uint32_t> events_{0};
    std::atomic<int32_t> last_asio_error_{0};
    std::atomic<uint64_t> callback_count_{0};
    std::atomic<uint64_t> capture_frames_{0};
    std::atomic<uint64_t> playback_frames_{0};
    std::atomic<uint64_t> capture_overrun_count_{0};
    std::atomic<uint64_t> capture_overrun_frames_{0};
    std::atomic<uint64_t> playback_underrun_count_{0};
    std::atomic<uint64_t> playback_underrun_frames_{0};
    std::atomic<uint64_t> reset_request_count_{0};
    std::atomic<uint64_t> resync_request_count_{0};
};

uint32_t copy_error(const std::string& error, char* destination, uint32_t capacity)
{
    const uint32_t required = static_cast<uint32_t>(std::min<size_t>(error.size(), UINT32_MAX));
    if (destination != nullptr && capacity != 0) {
        const uint32_t copied = std::min<uint32_t>(required, capacity - 1);
        if (copied != 0) std::memcpy(destination, error.data(), copied);
        destination[copied] = '\0';
    }
    return required;
}

} // namespace

extern "C" {

const char* ZEUS_ASIO_CALL zeus_asio_version(void) { return "zeus-asio 1.0; Steinberg ASIO SDK 2.3.4"; }
const char* ZEUS_ASIO_CALL zeus_asio_source_hash(void) { return ZEUS_ASIO_SOURCE_HASH; }

void* ZEUS_ASIO_CALL zeus_asio_drivers_create(void)
{
    try { g_last_error.clear(); return enumerate_drivers().release(); }
    catch (const std::bad_alloc&) { set_global_error("Out of memory enumerating ASIO drivers."); return nullptr; }
    catch (...) { set_global_error("Unexpected failure enumerating ASIO drivers."); return nullptr; }
}

uint32_t ZEUS_ASIO_CALL zeus_asio_drivers_count(void* snapshot)
{
    const auto* value = static_cast<DriverSnapshot*>(snapshot);
    return value ? static_cast<uint32_t>(value->entries.size()) : 0;
}

const char* ZEUS_ASIO_CALL zeus_asio_drivers_id(void* snapshot, uint32_t index)
{
    const auto* value = static_cast<DriverSnapshot*>(snapshot);
    return value && index < value->entries.size() ? value->entries[index].id.c_str() : nullptr;
}

const char* ZEUS_ASIO_CALL zeus_asio_drivers_name(void* snapshot, uint32_t index)
{
    const auto* value = static_cast<DriverSnapshot*>(snapshot);
    return value && index < value->entries.size() ? value->entries[index].name.c_str() : nullptr;
}

void ZEUS_ASIO_CALL zeus_asio_drivers_destroy(void* snapshot) { delete static_cast<DriverSnapshot*>(snapshot); }

void* ZEUS_ASIO_CALL zeus_asio_probe_create(const char* driver_id_utf8)
{
    g_last_error.clear();
    if (driver_id_utf8 == nullptr || *driver_id_utf8 == '\0') {
        set_global_error("ASIO driver ID is required for probing.");
        return nullptr;
    }
    try {
        std::lock_guard create_lock(g_create_mutex);
        if (g_driver_abandoned.load(std::memory_order_acquire)) {
            set_global_error("An ASIO driver failed to stop safely; restart Zeus before probing another ASIO driver.");
            return nullptr;
        }
        DriverClaim claim;
        if (!claim.acquired()) {
            set_global_error("Another ASIO session or driver probe is active.");
            return nullptr;
        }
        auto probe = std::make_unique<DriverProbe>(driver_id_utf8);
        if (!probe->run()) { set_global_error(probe->error()); return nullptr; }
        return probe.release();
    } catch (const std::bad_alloc&) { set_global_error("Out of memory creating ASIO driver probe."); return nullptr; }
      catch (...) { set_global_error("Unexpected failure creating ASIO driver probe."); return nullptr; }
}

void ZEUS_ASIO_CALL zeus_asio_probe_destroy(void* probe) { delete static_cast<DriverProbe*>(probe); }
uint32_t ZEUS_ASIO_CALL zeus_asio_probe_input_count(void* probe)
{
    const auto* value = static_cast<DriverProbe*>(probe);
    return value ? static_cast<uint32_t>(value->inputs().size()) : 0;
}
uint32_t ZEUS_ASIO_CALL zeus_asio_probe_output_count(void* probe)
{
    const auto* value = static_cast<DriverProbe*>(probe);
    return value ? static_cast<uint32_t>(value->outputs().size()) : 0;
}
const char* ZEUS_ASIO_CALL zeus_asio_probe_input_name(void* probe, uint32_t index)
{
    const auto* value = static_cast<DriverProbe*>(probe);
    return value && index < value->inputs().size() ? value->inputs()[index].name.c_str() : nullptr;
}
const char* ZEUS_ASIO_CALL zeus_asio_probe_output_name(void* probe, uint32_t index)
{
    const auto* value = static_cast<DriverProbe*>(probe);
    return value && index < value->outputs().size() ? value->outputs()[index].name.c_str() : nullptr;
}
int32_t ZEUS_ASIO_CALL zeus_asio_probe_input_supported(void* probe, uint32_t index)
{
    const auto* value = static_cast<DriverProbe*>(probe);
    return value && index < value->inputs().size() ? (value->inputs()[index].supported ? 1 : 0) : -1;
}
int32_t ZEUS_ASIO_CALL zeus_asio_probe_output_supported(void* probe, uint32_t index)
{
    const auto* value = static_cast<DriverProbe*>(probe);
    return value && index < value->outputs().size() ? (value->outputs()[index].supported ? 1 : 0) : -1;
}
int32_t ZEUS_ASIO_CALL zeus_asio_probe_supports_48000(void* probe)
{
    const auto* value = static_cast<DriverProbe*>(probe);
    return value ? (value->supports_48000() ? 1 : 0) : -1;
}
uint32_t ZEUS_ASIO_CALL zeus_asio_probe_buffer_min(void* probe)
{
    const auto* value = static_cast<DriverProbe*>(probe); return value ? value->buffer_min() : 0;
}
uint32_t ZEUS_ASIO_CALL zeus_asio_probe_buffer_max(void* probe)
{
    const auto* value = static_cast<DriverProbe*>(probe); return value ? value->buffer_max() : 0;
}
uint32_t ZEUS_ASIO_CALL zeus_asio_probe_buffer_preferred(void* probe)
{
    const auto* value = static_cast<DriverProbe*>(probe); return value ? value->buffer_preferred() : 0;
}
int32_t ZEUS_ASIO_CALL zeus_asio_probe_buffer_granularity(void* probe)
{
    const auto* value = static_cast<DriverProbe*>(probe); return value ? value->buffer_granularity() : 0;
}

void* ZEUS_ASIO_CALL zeus_asio_session_create(const char* driver_id_utf8,
    int32_t input_channel, int32_t output_first_channel, uint32_t buffer_frames,
    uint32_t capture_ring_frames, uint32_t playback_ring_frames)
{
    g_last_error.clear();
    if (driver_id_utf8 == nullptr || *driver_id_utf8 == '\0') { set_global_error("ASIO driver ID is required."); return nullptr; }
    if (input_channel < -1 || output_first_channel < -1) { set_global_error("ASIO channel indices must be -1 or non-negative."); return nullptr; }
    if (input_channel < 0 && output_first_channel < 0) { set_global_error("ASIO session must enable input, output, or both."); return nullptr; }
    try {
        std::lock_guard create_lock(g_create_mutex);
        if (g_driver_abandoned.load(std::memory_order_acquire)) {
            set_global_error("An ASIO driver failed to stop safely; restart Zeus before opening another ASIO session.");
            return nullptr;
        }
        DriverClaim claim;
        if (!claim.acquired()) { set_global_error("Another ASIO session or driver probe is active."); return nullptr; }
        auto session = std::make_unique<Session>(driver_id_utf8, input_channel, output_first_channel,
            buffer_frames, capture_ring_frames, playback_ring_frames);
        session->accept_driver_claim();
        claim.retain();
        if (!session->initialize()) { set_global_error(session->error()); return nullptr; }
        return session.release();
    } catch (const std::bad_alloc&) { set_global_error("Out of memory creating ASIO session."); return nullptr; }
      catch (...) { set_global_error("Unexpected failure creating ASIO session."); return nullptr; }
}

int32_t ZEUS_ASIO_CALL zeus_asio_session_start(void* session) { return session ? static_cast<Session*>(session)->start() : kError; }
int32_t ZEUS_ASIO_CALL zeus_asio_session_stop(void* session) { return session ? static_cast<Session*>(session)->stop() : kError; }
int32_t ZEUS_ASIO_CALL zeus_asio_session_control_panel(void* session) { return session ? static_cast<Session*>(session)->control_panel() : kError; }
void ZEUS_ASIO_CALL zeus_asio_session_destroy(void* session) { delete static_cast<Session*>(session); }
uint32_t ZEUS_ASIO_CALL zeus_asio_session_playback_free(void* session) { return session ? static_cast<Session*>(session)->playback_free() : 0; }
uint32_t ZEUS_ASIO_CALL zeus_asio_session_playback_write(void* session, const float* samples, uint32_t frames) { return session ? static_cast<Session*>(session)->playback_write(samples, frames) : 0; }
uint32_t ZEUS_ASIO_CALL zeus_asio_session_capture_available(void* session) { return session ? static_cast<Session*>(session)->capture_available() : 0; }
uint32_t ZEUS_ASIO_CALL zeus_asio_session_capture_read(void* session, float* samples, uint32_t frames) { return session ? static_cast<Session*>(session)->capture_read(samples, frames) : 0; }
int32_t ZEUS_ASIO_CALL zeus_asio_session_wait(void* session, uint32_t timeout_ms, uint32_t* events) { return session ? static_cast<Session*>(session)->wait(timeout_ms, events) : kError; }
int32_t ZEUS_ASIO_CALL zeus_asio_session_get_status(void* session, zeus_asio_status_v1* status) { return session ? static_cast<Session*>(session)->status(status) : kError; }
uint32_t ZEUS_ASIO_CALL zeus_asio_session_sample_rate(void* session) { return session ? kSampleRate : 0; }
uint32_t ZEUS_ASIO_CALL zeus_asio_session_buffer_frames(void* session) { return session ? static_cast<Session*>(session)->buffer_frames() : 0; }
uint32_t ZEUS_ASIO_CALL zeus_asio_session_input_latency_frames(void* session) { return session ? static_cast<Session*>(session)->input_latency() : 0; }
uint32_t ZEUS_ASIO_CALL zeus_asio_session_output_latency_frames(void* session) { return session ? static_cast<Session*>(session)->output_latency() : 0; }
uint32_t ZEUS_ASIO_CALL zeus_asio_session_input_channel_count(void* session) { return session ? static_cast<Session*>(session)->input_count() : 0; }
uint32_t ZEUS_ASIO_CALL zeus_asio_session_output_channel_count(void* session) { return session ? static_cast<Session*>(session)->output_count() : 0; }
const char* ZEUS_ASIO_CALL zeus_asio_session_input_channel_name(void* session, uint32_t index) { return session ? static_cast<Session*>(session)->input_name(index) : nullptr; }
const char* ZEUS_ASIO_CALL zeus_asio_session_output_channel_name(void* session, uint32_t index) { return session ? static_cast<Session*>(session)->output_name(index) : nullptr; }
uint32_t ZEUS_ASIO_CALL zeus_asio_last_error(void* session, char* utf8, uint32_t capacity)
{
    return copy_error(session ? static_cast<Session*>(session)->error() : g_last_error, utf8, capacity);
}

} // extern "C"
