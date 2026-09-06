# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (C) 2026 Douglas J. Cerrato (KB2UKA).
function(zeus_attach_fftw_counter)
    foreach(library fftw3 fftw3f fftw3l)
        if(TARGET ${library})
            target_sources(${library} PRIVATE "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/fftw-arm64-timer.c")
        endif()
    endforeach()
endfunction()
# CMAKE_PROJECT_INCLUDE runs before FFTW declares its precision target.
cmake_language(DEFER CALL zeus_attach_fftw_counter)
