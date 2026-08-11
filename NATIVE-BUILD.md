<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Building the native station-engine libraries

The corresponding-source export includes the tracked native source and build
control files used for the libraries conveyed under
`Zeus.Dsp/runtimes/<rid>/native/`. Commands below are the release build recipes
from the Zeus native workflows and the vendoring notes shipped beside each
source tree.

## Artifact to source map

| Conveyed artifact | Source and build control files |
| --- | --- |
| `wdsp.dll`, `libwdsp.so`, `libwdsp.dylib` | `native/wdsp/`, with `native/libspecbleach/` and `native/rnnoise/` statically embedded by the default build |
| `fftw3.dll`, `fftw3f.dll`, `libfftw3-3.dll`, `libfftw3f-3.dll`, `libfftw3.so.3`, `libfftw3f.so.3`, `libfftw3.3.dylib`, `libfftw3f.3.dylib` | Unmodified upstream FFTW; provenance and exact per-RID acquisition are below |
| `miniaudio.dll`, `libminiaudio.so`, `libminiaudio.dylib` | `native/miniaudio/` |
| `zeus_asio.dll` | `native/asio/`; Windows-only ASIO 2.3.4 host bridge, including the SDK interface source used to build it |
| `codec2.dll`, `libcodec2.so`, `libcodec2.dylib` | `native/codec2/`; its CMake recipe fetches codec2 1.2.0 and applies the included build-system patch |
| `zeus_rade.dll`, `libzeus_rade.so`, `libzeus_rade.dylib` | `native/radae/`; exact pinned upstream slices are materialized under `native/radae/vendor/` and bound to the binaries in `native/radae/vendor/BINARY-SOURCE-BINDING.json` |

The `.gitkeep` files in otherwise-empty RID directories are packaging
placeholders, not native artifacts.

## macOS arm64

Install FFTW and CMake, then build and stage WDSP, miniaudio, and codec2:

```sh
brew install fftw cmake
cmake -S native/wdsp -B native/build -DCMAKE_BUILD_TYPE=Release \
  -DWDSP_WITH_NR3=ON -DWDSP_WITH_NR4=ON
cmake --build native/build --config Release --parallel
cp native/build/libwdsp.dylib Zeus.Dsp/runtimes/osx-arm64/native/libwdsp.dylib

cmake -S native/miniaudio -B native/build-miniaudio -DCMAKE_BUILD_TYPE=Release
cmake --build native/build-miniaudio --config Release --parallel
cp native/build-miniaudio/libminiaudio.dylib \
  Zeus.Dsp/runtimes/osx-arm64/native/libminiaudio.dylib

cmake -S native/codec2 -B native/build-codec2 -DCMAKE_BUILD_TYPE=Release
cmake --build native/build-codec2 --config Release --target codec2 --parallel
dylib=$(find native/build-codec2 -name 'libcodec2*.dylib' ! -type l | head -n1)
test -n "$dylib"
cp "$dylib" Zeus.Dsp/runtimes/osx-arm64/native/libcodec2.dylib
```

Bundle the Homebrew FFTW libraries and make the WDSP references relocatable:

```sh
fftw_lib="$(brew --prefix fftw)/lib"
cp "${fftw_lib}/libfftw3.3.dylib" Zeus.Dsp/runtimes/osx-arm64/native/
cp "${fftw_lib}/libfftw3f.3.dylib" Zeus.Dsp/runtimes/osx-arm64/native/
install_name_tool \
  -change "${fftw_lib}/libfftw3.3.dylib" @loader_path/libfftw3.3.dylib \
  -change "${fftw_lib}/libfftw3f.3.dylib" @loader_path/libfftw3f.3.dylib \
  Zeus.Dsp/runtimes/osx-arm64/native/libwdsp.dylib
```

## Linux x64

The release recipe uses the distribution FFTW development package:

```sh
sudo apt-get update
sudo apt-get install -y libfftw3-dev cmake build-essential pkg-config
cmake -S native/wdsp -B native/build -DCMAKE_BUILD_TYPE=Release \
  -DWDSP_WITH_NR3=ON -DWDSP_WITH_NR4=ON
cmake --build native/build --config Release --parallel
cp native/build/libwdsp.so Zeus.Dsp/runtimes/linux-x64/native/libwdsp.so

cmake -S native/miniaudio -B native/build-miniaudio -DCMAKE_BUILD_TYPE=Release
cmake --build native/build-miniaudio --config Release --parallel
cp native/build-miniaudio/libminiaudio.so \
  Zeus.Dsp/runtimes/linux-x64/native/libminiaudio.so

cmake -S native/codec2 -B native/build-codec2 -DCMAKE_BUILD_TYPE=Release
cmake --build native/build-codec2 --config Release --target codec2 --parallel
so=$(find native/build-codec2 -name 'libcodec2.so*' ! -type l | head -n1)
test -n "$so"
cp "$so" Zeus.Dsp/runtimes/linux-x64/native/libcodec2.so
```

Copy the system `libfftw3.so.3` and `libfftw3f.so.3` resolved by `ldconfig`
beside `libwdsp.so`, as the release workflow does.

## Linux arm64 and Raspberry Pi

For a native Raspberry Pi build, install `libfftw3-dev`, CMake, and the compiler
as in the Linux x64 recipe and run `./native/build.sh Release`. The release
workflow cross-builds FFTW 3.3.10 and the three libraries as follows:

```sh
sudo apt-get update
sudo apt-get install -y gcc-aarch64-linux-gnu g++-aarch64-linux-gnu cmake pkg-config wget make
wget https://www.fftw.org/fftw-3.3.10.tar.gz
tar xzf fftw-3.3.10.tar.gz
cp -a fftw-3.3.10 fftw-3.3.10-float

cd fftw-3.3.10
./configure --host=aarch64-linux-gnu --prefix="$HOME/fftw-arm64" \
  CC=aarch64-linux-gnu-gcc CXX=aarch64-linux-gnu-g++ \
  --enable-shared --disable-static
make -j"$(nproc)"
make install
cd ../fftw-3.3.10-float
./configure --host=aarch64-linux-gnu --prefix="$HOME/fftw-arm64" \
  CC=aarch64-linux-gnu-gcc CXX=aarch64-linux-gnu-g++ \
  --enable-float --enable-shared --disable-static
make -j"$(nproc)"
make install
cd ..

cmake -S native/wdsp -B native/build -DCMAKE_BUILD_TYPE=Release \
  -DWDSP_WITH_NR3=ON -DWDSP_WITH_NR4=ON -DFFTW_ROOT="$HOME/fftw-arm64" \
  -DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=aarch64 \
  -DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc \
  -DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++ \
  -DCMAKE_FIND_ROOT_PATH="$HOME/fftw-arm64" \
  -DCMAKE_PREFIX_PATH="$HOME/fftw-arm64"
cmake --build native/build --config Release --parallel

cmake -S native/miniaudio -B native/build-miniaudio -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=aarch64 \
  -DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc \
  -DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++
cmake --build native/build-miniaudio --config Release --parallel

cmake -S native/codec2 -B native/build-codec2 -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=aarch64 \
  -DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc \
  -DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++
cmake --build native/build-codec2 --config Release --target codec2 --parallel
```

Stage the resulting libraries and both `$HOME/fftw-arm64/lib/libfftw3*.so.3`
files under `Zeus.Dsp/runtimes/linux-arm64/native/`.

## Windows x64 and arm64

WDSP uses Visual Studio 2022 with ClangCL and vcpkg FFTW. Use the static-md
triplet for x64 and the static-CRT triplet for arm64:

```powershell
vcpkg install fftw3:x64-windows-static-md
cmake -S native\wdsp -B native\build -G "Visual Studio 17 2022" -A x64 -T ClangCL `
  -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_INSTALLATION_ROOT\scripts\buildsystems\vcpkg.cmake" `
  -DVCPKG_TARGET_TRIPLET=x64-windows-static-md -DWDSP_WITH_NR3=ON -DWDSP_WITH_NR4=ON
cmake --build native\build --config Release --parallel
copy native\build\Release\wdsp.dll Zeus.Dsp\runtimes\win-x64\native\wdsp.dll

vcpkg install fftw3:arm64-windows-static
cmake -S native\wdsp -B native\build-arm64 -G "Visual Studio 17 2022" -A ARM64 -T ClangCL `
  -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_INSTALLATION_ROOT\scripts\buildsystems\vcpkg.cmake" `
  -DVCPKG_TARGET_TRIPLET=arm64-windows-static `
  -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded -DWDSP_WITH_NR3=ON -DWDSP_WITH_NR4=ON
cmake --build native\build-arm64 --config Release --parallel
copy native\build-arm64\Release\wdsp.dll Zeus.Dsp\runtimes\win-arm64\native\wdsp.dll
```

Build miniaudio with the same Visual Studio generator and selected `-A x64` or
`-A ARM64`, then stage `Release/miniaudio.dll`. codec2 on win-x64 requires an
MSYS2 UCRT64 MinGW shell:

```sh
cmake -S native/codec2 -B native/build-codec2 -G Ninja \
  -DCMAKE_C_COMPILER=gcc -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SHARED_LINKER_FLAGS="-static-libgcc -static -s"
cmake --build native/build-codec2 --config Release --target codec2 --parallel
dll=$(find native/build-codec2 -name 'codec2.dll' | head -n1)
test -n "$dll"
cp "$dll" Zeus.Dsp/runtimes/win-x64/native/codec2.dll
```

codec2 is not currently built for win-arm64.

Build the optional ASIO host bridge from its tracked corresponding source with
Visual Studio 2022. The source metadata and official SDK archive checksum are
recorded in `ASIO-SOURCE.json`; no proprietary ASIO SDK license is used.

```powershell
cmake -S native\asio -B native\build-asio-x64 -G "Visual Studio 17 2022" -A x64
cmake --build native\build-asio-x64 --config Release --parallel
copy native\build-asio-x64\Release\zeus_asio.dll Zeus.Dsp\runtimes\win-x64\native\zeus_asio.dll

cmake -S native\asio -B native\build-asio-arm64 -G "Visual Studio 17 2022" -A ARM64
cmake --build native\build-asio-arm64 --config Release --parallel
copy native\build-asio-arm64\Release\zeus_asio.dll Zeus.Dsp\runtimes\win-arm64\native\zeus_asio.dll
```

Only build and stage a RID for which the bridge implementation declares
support. The station engine must remain usable through miniaudio when the ASIO
bridge or a compatible driver is unavailable.

## RADE on each supported RID

RADE has conveyed binaries for linux-x64, linux-arm64, win-x64, and osx-arm64.
The matching source release already contains the three exact pinned upstream
slices. To re-fetch and integrity-check them independently, run:

```sh
bash native/radae/vendor/fetch-sources.sh native/radae/vendor
```

The script fails closed unless the Thetis-RADE commit, each Git tree object,
each deterministic slice content hash, and the embedded Opus pin match
`native/radae/vendor/SOURCE-SLICES.json`. Next create the missing scalar-build
placeholder files described in `native/radae/vendor/PROVENANCE.md`, then
configure with Ninja:

```sh
cmake -S native/radae -B native/build-rade -G Ninja \
  -DCMAKE_BUILD_TYPE=Release -DOPUS_DISABLE_INTRINSICS=ON \
  -DZEUS_RADE_VENDOR="$PWD/native/radae/vendor" \
  -DZEUS_RADE_BUILD_TEST=OFF
cmake --build native/build-rade --config Release --parallel
```

For linux-arm64 add
`-DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc -DCMAKE_SYSTEM_NAME=Linux
-DCMAKE_SYSTEM_PROCESSOR=aarch64`. Run the Windows command in an MSYS2 UCRT64
MinGW shell. `native/radae/CMakeLists.txt` supplies the Windows static-runtime
link options and artifact naming. The osx-arm64 artifact was produced by the
same scalar recipe using AppleClang 21.0.0, CMake 4.3.2, and Ninja 1.13.0; the
Apple-only CMake path supplies the upstream `abs()` declaration and the Opus
archive build-order dependency required by Ninja.

## FFTW provenance and source

FFTW is used unmodified and is not vendored in this export. Its upstream source
is <https://www.fftw.org/>. The exact acquisition path used by each RID is:

| RID | Acquisition |
| --- | --- |
| `win-x64` | vcpkg `fftw3:x64-windows-static-md`; the checked-in side-by-side compatibility DLLs identify themselves as FFTW 3.3.8, while the current statically linked WDSP build identifies FFTW 3.3.10 |
| `win-arm64` | vcpkg `fftw3:arm64-windows-static`; FFTW 3.3.10 is statically linked into `wdsp.dll` |
| `linux-x64` | Distribution `libfftw3-dev`; the checked-in conveyed libraries identify themselves as FFTW 3.3.10 |
| `linux-arm64` | <https://www.fftw.org/fftw-3.3.10.tar.gz>, cross-built in double and single precision |
| `osx-arm64` | Homebrew `fftw`; the checked-in conveyed libraries identify themselves as FFTW 3.3.11 |

Source for the exact FFTW build conveyed with any release is additionally
available under the standing written offer for corresponding source supplied
with Zeus SDR. The offer is governed by the licence the binary you received was
conveyed under; this document is copied into the published source drop for a
station engine that conveys as GPL-3.0-or-later.
