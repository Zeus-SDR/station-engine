# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (C) 2026 Douglas J. Cerrato (KB2UKA).
set(VCPKG_TARGET_ARCHITECTURE arm64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE static)
# FFTW has no MSVC ARM64 cycle counter; use QueryPerformanceCounter.
set(VCPKG_C_FLAGS "/FI\"${CMAKE_CURRENT_LIST_DIR}/fftw-arm64-timer.h\"")
set(VCPKG_CXX_FLAGS "${VCPKG_C_FLAGS}")
set(VCPKG_CMAKE_CONFIGURE_OPTIONS
    "-DCMAKE_PROJECT_INCLUDE=${CMAKE_CURRENT_LIST_DIR}/fftw-arm64-timer.cmake")
set(VCPKG_HASH_ADDITIONAL_FILES
    "${CMAKE_CURRENT_LIST_DIR}/fftw-arm64-timer.h"
    "${CMAKE_CURRENT_LIST_DIR}/fftw-arm64-timer.c"
    "${CMAKE_CURRENT_LIST_DIR}/fftw-arm64-timer.cmake")
