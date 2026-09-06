#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (C) 2026 Douglas J. Cerrato (KB2UKA).
set -euo pipefail
arch=${1:?architecture required}
minimum=${2:?deployment target required}
prefix=${3:?install prefix required}
case "$arch:$minimum" in
  arm64:14.0|x86_64:12.0) ;;
  *) echo 'Unsupported macOS architecture/deployment target' >&2; exit 1 ;;
esac
mkdir -p "$prefix"
prefix=$(cd "$prefix" && pwd)
build_root="$prefix/build"
mkdir -p "$build_root"
archive="$build_root/fftw-3.3.10.tar.gz"
curl -fsSL https://www.fftw.org/fftw-3.3.10.tar.gz -o "$archive"
echo "56c932549852cddcfafdab3820b0200c7742675be92179e59e6215b340e26467  $archive" | shasum -a 256 -c -
tar -xzf "$archive" -C "$build_root"
# Upstream's CMake template omits the detected Mach header and carries an
# outdated version string. Preserve the detections in the generated config.
printf '\n#cmakedefine HAVE_MACH_MACH_TIME_H 1\n' >> "$build_root/fftw-3.3.10/cmake.config.h.in"
sed -i '' 's/"3\.3\.9"/"3.3.10"/g' "$build_root/fftw-3.3.10/cmake.config.h.in"
for precision in double float; do
  float=OFF
  if [ "$precision" = float ]; then float=ON; fi
  cmake -S "$build_root/fftw-3.3.10" -B "$build_root/$precision" \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES="$arch" \
    -DCMAKE_OSX_DEPLOYMENT_TARGET="$minimum" -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DBUILD_SHARED_LIBS=ON -DBUILD_TESTS=OFF -DENABLE_FLOAT="$float"
  # Missing timer detection silently turns PATIENT into cost estimation.
  grep -q '^#define HAVE_MACH_ABSOLUTE_TIME 1' "$build_root/$precision/config.h"
  grep -q '^#define HAVE_MACH_MACH_TIME_H 1' "$build_root/$precision/config.h"
  cmake --build "$build_root/$precision" --config Release --parallel 4
  cmake --install "$build_root/$precision" --prefix "$prefix"
done
for library in "$prefix/lib/libfftw3.3.dylib" "$prefix/lib/libfftw3f.3.dylib"; do
  [ "$(lipo -archs "$library")" = "$arch" ]
  [ "$(vtool -show-build "$library" | awk '$1 == "minos" { print $2; exit }')" = "$minimum" ]
done
