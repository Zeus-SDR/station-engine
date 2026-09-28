# SPDX-License-Identifier: GPL-2.0-or-later
#
# zeus_wspr — decode-only shared library over the vendored K1JT/K9AN wsprd
# (GPL-3). Loaded only by the GPL station engine. See README.md.
#
#   cmake -S native/wspr -B build-wspr -DCMAKE_BUILD_TYPE=Release
#   cmake --build build-wspr --config Release
#   ctest --test-dir build-wspr        (round-trip gate; needs ZEUS_WSPR_TESTS=ON)
#
# Windows builds with MinGW-w64 (GCC): the vendored decoder uses POSIX getopt
# and unistd.h, which MinGW-w64 provides. MSVC is not supported.

cmake_minimum_required(VERSION 3.20)
project(zeus_wspr
    VERSION 2.0.0
    DESCRIPTION "WSPR decode — Zeus station engine native core"
    LANGUAGES C
)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

if(MSVC)
    message(FATAL_ERROR "zeus_wspr builds with GCC or Clang (MinGW-w64 on Windows), not MSVC.")
endif()

set(WSPR_VENDOR ${CMAKE_CURRENT_SOURCE_DIR}/vendor)

# The full vendored decoder. wsprsim_utils.c is part of it: the decoder
# regenerates each decoded message's channel symbols for signal subtraction.
# Nothing from it is exported.
set(WSPR_DECODER_SRC
    ${WSPR_VENDOR}/wsprd.c
    ${WSPR_VENDOR}/wsprd_utils.c
    ${WSPR_VENDOR}/wsprsim_utils.c
    ${WSPR_VENDOR}/fano.c
    ${WSPR_VENDOR}/jelinek.c
    ${WSPR_VENDOR}/nhash.c
    ${WSPR_VENDOR}/tab.c
    ${WSPR_VENDOR}/pffft.c
    ${CMAKE_CURRENT_SOURCE_DIR}/osdwspr_stub.c
)
# metric_tables.c is #included by wsprd.c, never compiled on its own.

# wsprd.c's command-line entry becomes a callable function; the vendored file
# itself stays byte-for-byte pristine.
set_source_files_properties(${WSPR_VENDOR}/wsprd.c
    PROPERTIES COMPILE_DEFINITIONS "main=wsprd_cli_main")

function(zeus_wspr_configure target)
    target_include_directories(${target} PRIVATE ${WSPR_VENDOR} ${CMAKE_CURRENT_SOURCE_DIR})
    if(WIN32)
        target_compile_definitions(${target} PRIVATE _USE_MATH_DEFINES _CRT_SECURE_NO_WARNINGS)
    else()
        find_package(Threads REQUIRED)
        target_link_libraries(${target} PRIVATE m Threads::Threads)
    endif()
endfunction()

# The vendored code predates several modern warnings; silence them for the
# vendored files only, without touching vendored source.
set_source_files_properties(${WSPR_DECODER_SRC} PROPERTIES COMPILE_OPTIONS "-w")

add_library(zeus_wspr SHARED zeus_wspr_decode.c ${WSPR_DECODER_SRC})
zeus_wspr_configure(zeus_wspr)
set_target_properties(zeus_wspr PROPERTIES OUTPUT_NAME "zeus_wspr")
if(WIN32)
    # Standalone DLL: no libgcc/libwinpthread runtime DLLs next to it.
    set_target_properties(zeus_wspr PROPERTIES PREFIX "")
    target_link_options(zeus_wspr PRIVATE -static -static-libgcc)
endif()

option(ZEUS_WSPR_TESTS "Build the zeus_wspr decode round-trip gate" OFF)
if(ZEUS_WSPR_TESTS)
    enable_testing()
    add_executable(zeus_wspr_roundtrip
        test/zeus_wspr_roundtrip.c
        zeus_wspr_decode.c
        ${WSPR_DECODER_SRC}
    )
    zeus_wspr_configure(zeus_wspr_roundtrip)
    if(WIN32)
        # The decoder keeps ~0.8 MB of arrays on the stack; the Windows main
        # thread defaults to 1 MB. Match the engine's 16 MB decode thread.
        target_link_options(zeus_wspr_roundtrip PRIVATE -static -static-libgcc -Wl,--stack,16777216)
    endif()
    set(ZEUS_WSPR_TEST_DIR ${CMAKE_CURRENT_BINARY_DIR}/roundtrip-data)
    file(MAKE_DIRECTORY ${ZEUS_WSPR_TEST_DIR})
    add_test(NAME wspr_decode_roundtrip COMMAND zeus_wspr_roundtrip ${ZEUS_WSPR_TEST_DIR})
endif()
