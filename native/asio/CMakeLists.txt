# SPDX-License-Identifier: GPL-3.0-only
cmake_minimum_required(VERSION 3.20)
project(zeus_asio VERSION 1.0.0 LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "zeus_asio is Windows-only")
endif()

# Embed a normalized hash of the runtime-affecting source inputs. The repository
# audit compares this marker with the checked-in x64 DLL so source fixes cannot
# silently ship with an older installer artifact.
set(ZEUS_ASIO_HASH_INPUTS
    CMakeLists.txt
    zeus_asio.h
    zeus_asio.cpp
    sample_conversion.h
    vendor/asiosdk-2.3.4/common/asio.h
    vendor/asiosdk-2.3.4/common/asiosys.h
    vendor/asiosdk-2.3.4/common/iasiodrv.h)
set(ZEUS_ASIO_HASH_MATERIAL "")
foreach(relative_path IN LISTS ZEUS_ASIO_HASH_INPUTS)
    set(source_path "${CMAKE_CURRENT_SOURCE_DIR}/${relative_path}")
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${source_path}")
    file(READ "${source_path}" source_text)
    string(REPLACE "\r\n" "\n" source_text "${source_text}")
    string(REPLACE "\r" "\n" source_text "${source_text}")
    string(SHA256 source_digest "${source_text}")
    string(APPEND ZEUS_ASIO_HASH_MATERIAL "${relative_path}:${source_digest}\n")
endforeach()
string(SHA256 ZEUS_ASIO_SOURCE_HASH "${ZEUS_ASIO_HASH_MATERIAL}")
file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/generated")
configure_file(source_hash.h.in generated/source_hash.h @ONLY)

add_library(zeus_asio SHARED zeus_asio.cpp)
target_compile_features(zeus_asio PRIVATE cxx_std_17)
target_include_directories(zeus_asio PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_BINARY_DIR}/generated
    ${CMAKE_CURRENT_SOURCE_DIR}/vendor/asiosdk-2.3.4/common)
target_compile_definitions(zeus_asio PRIVATE
    WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_link_libraries(zeus_asio PRIVATE ole32 advapi32 user32)

if(MSVC)
    target_compile_options(zeus_asio PRIVATE /W4 /wd4324 /permissive- /EHsc /utf-8)
    set_property(TARGET zeus_asio PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

set_target_properties(zeus_asio PROPERTIES
    OUTPUT_NAME "zeus_asio"
    PREFIX "")

include(CTest)
if(BUILD_TESTING)
    add_executable(zeus_asio_tests tests/zeus_asio_tests.cpp)
    target_compile_features(zeus_asio_tests PRIVATE cxx_std_17)
    target_include_directories(zeus_asio_tests PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_SOURCE_DIR}/vendor/asiosdk-2.3.4/common)
    target_link_libraries(zeus_asio_tests PRIVATE zeus_asio)
    if(MSVC)
        target_compile_options(zeus_asio_tests PRIVATE /W4 /permissive- /EHsc /utf-8)
        set_property(TARGET zeus_asio_tests PROPERTY
            MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    endif()
    add_test(NAME zeus_asio_abi_smoke COMMAND zeus_asio_tests)
endif()
