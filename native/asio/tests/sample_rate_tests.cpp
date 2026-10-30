// SPDX-License-Identifier: GPL-3.0-only
#include <windows.h>
#include <objbase.h>

// Exercise the production session and its owner thread without registering a
// COM server or opening hardware. Only driver activation is replaced.
HRESULT activate_test_driver(REFCLSID, LPUNKNOWN, DWORD, REFIID, LPVOID*);
#define CoCreateInstance activate_test_driver
#include "../zeus_asio.cpp"
#undef CoCreateInstance

#include <cstdio>

namespace {
struct DriverState {
    double rate = 44100;
    bool ignore_set = false;
    bool revert_on_buffers = false;
    bool read_fails_after_set = false;
    bool read_fails_after_buffers = false;
    ASIOError can_result = ASE_OK;
    ASIOError set_result = ASE_OK;
    int sets = 0;
    int creates = 0;
    int disposes = 0;
    int releases = 0;
    int starts = 0;
} state;

class TestDriver final : public IASIO {
    float buffers_[2][64]{};
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void**) override { return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { ++state.releases; delete this; return 0; }
    ASIOBool init(void*) override { return ASIOTrue; }
    void getDriverName(char* name) override { std::strcpy(name, "Test driver"); }
    long getDriverVersion() override { return 1; }
    void getErrorMessage(char* message) override { message[0] = '\0'; }
    ASIOError start() override { ++state.starts; return ASE_OK; }
    ASIOError stop() override { return ASE_OK; }
    ASIOError getChannels(long* inputs, long* outputs) override { *inputs = 1; *outputs = 0; return ASE_OK; }
    ASIOError getLatencies(long* input, long* output) override { *input = *output = 64; return ASE_OK; }
    ASIOError getBufferSize(long* minimum, long* maximum, long* preferred, long* granularity) override
    { *minimum = *maximum = *preferred = 64; *granularity = 0; return ASE_OK; }
    ASIOError canSampleRate(ASIOSampleRate rate) override { return rate == 48000 ? state.can_result : ASE_NoClock; }
    ASIOError getSampleRate(ASIOSampleRate* rate) override
    {
        if ((state.read_fails_after_set && state.sets) || (state.read_fails_after_buffers && state.creates))
            return ASE_NoClock;
        *rate = state.rate;
        return ASE_OK;
    }
    ASIOError setSampleRate(ASIOSampleRate rate) override
    {
        ++state.sets;
        if (asio_ok(state.set_result) && !state.ignore_set) state.rate = rate;
        return state.set_result;
    }
    ASIOError getClockSources(ASIOClockSource*, long*) override { return ASE_NotPresent; }
    ASIOError setClockSource(long) override { return ASE_NotPresent; }
    ASIOError getSamplePosition(ASIOSamples*, ASIOTimeStamp*) override { return ASE_NotPresent; }
    ASIOError getChannelInfo(ASIOChannelInfo* info) override
    { info->type = ASIOSTFloat32LSB; std::strcpy(info->name, "Input"); return ASE_OK; }
    ASIOError createBuffers(ASIOBufferInfo* buffers, long count, long, ASIOCallbacks*) override
    {
        ++state.creates;
        if (state.revert_on_buffers) state.rate = 44100;
        for (long i = 0; i < count; ++i) {
            buffers[i].buffers[0] = buffers_[0];
            buffers[i].buffers[1] = buffers_[1];
        }
        return ASE_OK;
    }
    ASIOError disposeBuffers() override { ++state.disposes; return ASE_OK; }
    ASIOError controlPanel() override { return ASE_OK; }
    ASIOError future(long, void*) override { return ASE_NotPresent; }
    ASIOError outputReady() override { return ASE_NotPresent; }
};

int failures = 0;
void check(bool condition, const char* name, const char* assertion)
{
    if (!condition) { std::printf("FAIL %s: %s\n", name, assertion); ++failures; }
}

void run(const char* name, DriverState config, bool accepted, int expected_sets, int expected_creates)
{
    state = config;
    void* session = zeus_asio_session_create("{DC30AC77-3C04-444A-A189-4D132CBE8F21}", 0, -1, 64, 128, 0);
    check((session != nullptr) == accepted, name, "session acceptance");
    if (session) {
        check(zeus_asio_session_sample_rate(session) == 48000, name, "runtime ABI rate");
        check(zeus_asio_session_start(session) == 0, name, "start");
        zeus_asio_session_destroy(session);
    } else {
        char message[512]{};
        zeus_asio_last_error(nullptr, message, sizeof(message));
        check(std::strstr(message, "48") != nullptr, name, "actionable rate error");
    }
    check(state.sets == expected_sets, name, "set rate count");
    check(state.creates == expected_creates, name, "buffer creation count");
    check(state.disposes == state.creates, name, "buffers disposed");
    check(state.releases == 1, name, "driver released");
    check(state.starts == (accepted ? 1 : 0), name, "wrong-rate driver never starts");
}
}

HRESULT activate_test_driver(REFCLSID, LPUNKNOWN, DWORD, REFIID, LPVOID* result)
{
    *result = static_cast<IASIO*>(new TestDriver());
    return S_OK;
}

int main()
{
    DriverState config;
    run("44100 switches to 48000", config, true, 1, 1);
    config.rate = 48000;
    run("already 48000", config, true, 0, 1);
    config.can_result = ASE_NoClock;
    run("external clock already 48000 needs no set", config, true, 0, 1);
    config = {}; config.ignore_set = true;
    run("acknowledged set stays 44100", config, false, 1, 0);
    config = {}; config.revert_on_buffers = true;
    run("buffers revert to 44100", config, false, 1, 1);
    config = {}; config.read_fails_after_set = true;
    run("readback fails after set", config, false, 1, 0);
    config = {}; config.read_fails_after_buffers = true;
    run("readback fails after buffers", config, false, 1, 1);
    config = {}; config.rate = std::numeric_limits<double>::quiet_NaN(); config.ignore_set = true;
    run("NaN is not 48000", config, false, 1, 0);
    config = {}; config.rate = std::numeric_limits<double>::infinity(); config.ignore_set = true;
    run("infinity is not 48000", config, false, 1, 0);
    config = {}; config.set_result = ASE_NoClock;
    run("set rejected", config, false, 1, 0);
    config = {}; config.can_result = ASE_NoClock;
    run("48000 unsupported", config, false, 0, 0);
    config = {}; config.set_result = ASE_SUCCESS;
    run("alternate ASIO success code", config, true, 1, 1);
    run("reopen after failed sessions", {}, true, 1, 1);
    std::printf("13 sample-rate scenarios; %d failed assertions\n", failures);
    return failures == 0 ? 0 : 1;
}
