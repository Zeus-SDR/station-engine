<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# RADE V1 + V2 native source and rebuild procedure

`zeus_rade` is one shared library containing the Python-free RADE C modem
(both the V1 and V2 protocols, selected per context at open time), Opus
DNN/FARGAN, FreeDV reliable text and its five Codec2 LDPC units, and the Zeus
BSD-2-Clause shim. `CMakeLists.txt` is the authoritative composition.

## Obtain and verify the exact upstream source

The private development repository intentionally does not commit the roughly
160 MB upstream slices. Native CI and the corresponding-source exporter use the
same fail-closed command:

```sh
bash native/radae/vendor/fetch-sources.sh native/radae/vendor
```

That command fetches Thetis-RADE commit
`408f2b5232ff0a2aec9b538a40d4cb1b02627b17`, verifies the fetched commit and
the three recorded Git tree IDs, materializes `radae_c`, `opus_dnn`, and
`freedv_text`, then verifies deterministic SHA-256 content hashes and Opus pin
`940d4e5af64351ca8ba8390df3f555484c567fbb`. All machine-readable pins are in
`vendor/SOURCE-SLICES.json`; provenance and authorship are in
`vendor/PROVENANCE.md`.

The public Station Engine source export already carries the verified slices
under `native/radae/vendor/`, so fetching again is optional when rebuilding a
release-matched source tag.

## Create the scalar Opus placeholders

The pinned Opus slice omits SIMD subtrees while its `*.mk` lists still name
files there. With intrinsics disabled, CMake validates but does not compile
those paths. Create empty placeholders for missing `*.c` and `*.h` references:

```sh
python3 - <<'PY'
import glob, os, re
root = "native/radae/vendor/opus_dnn"
for mk in glob.glob(os.path.join(root, "**", "*.mk"), recursive=True):
    with open(mk, "r", errors="ignore") as source:
        text = source.read()
    for token in re.findall(r'[\w./\\-]+\.[ch]', text):
        relative = token.strip().replace("\\", "/")
        target = os.path.normpath(os.path.join(root, relative))
        if not os.path.exists(target):
            os.makedirs(os.path.dirname(target), exist_ok=True)
            open(target, "a").close()
PY
```

These zero-byte configure-time placeholders are not upstream source and are
not part of the recorded source-slice hashes.

## Configure and build

Install CMake and Ninja, then run:

```sh
cmake -S native/radae -B native/build-rade -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DOPUS_DISABLE_INTRINSICS=ON \
  -DZEUS_RADE_VENDOR="$PWD/native/radae/vendor" \
  -DZEUS_RADE_BUILD_TEST=OFF
cmake --build native/build-rade --config Release --parallel
```

For linux-arm64 add:

```sh
-DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc \
-DCMAKE_SYSTEM_NAME=Linux \
-DCMAKE_SYSTEM_PROCESSOR=aarch64
```

Run the Windows x64 command in an MSYS2 UCRT64 MinGW shell. The CMake project
sets the Windows output name and static GCC runtime link. macOS uses the same
scalar recipe; its conveyed arm64 dylib was built locally with AppleClang
21.0.0, CMake 4.3.2, and Ninja 1.13.0 and passed the managed RADE test families.

The result is `libzeus_rade.so` on Linux, `zeus_rade.dll` on Windows, or
`libzeus_rade.dylib` on macOS. Stage only a library built and validated for its
matching RID.

## Native self-test

Configure with `-DZEUS_RADE_BUILD_TEST=ON` to also build `zeus_rade_test`
(the harness adds an executable only; the library is built identically).
On a host that can execute the binary, run:

```sh
native/build-rade/zeus_rade_test selftest
native/build-rade/zeus_rade_test loopback N9WAR
```

`selftest` proves both protocols end to end: synthetic speech through TX, the
real part of the modem signal into a fresh receiver, sync, decoded PCM,
End-of-Over detection and vocoder re-priming, the V1 callsign round trip, the
empty V2 callsign, and that each protocol's receiver rejects the other's
signal. It also prints TX peak levels and the receive real-time factor. It
exits non-zero on any failed check. `build-rade.yml` runs both commands on
linux-x64, win-x64, and osx-arm64.

The shim switches on one upstream setting that radae_c leaves off: the RADE V2
receiver's sample-clock timing correction (`timing_adj_enable`), set on every
V2 handle at open without modifying the vendored sources. The official V2
receiver runs with it on; in a reference comparison (AWGN only) it cut the
feature error at a +100 ppm clock offset from 0.734 (3 re-syncs) to 0.059 with
no measurable change to the clean, noisy, or +/-20 Hz baselines. See the
2026-11-22 section of `vendor/PROVENANCE.md`.

## Binary/source binding and license disposition

Every source export contains
`vendor/BINARY-SOURCE-BINDING.json`, generated from the exported tree. It
binds each conveyed RID and SHA-256 to the Thetis-RADE and Opus pins, source
slice hashes, CMake file, shim inputs, build configuration, and recorded
toolchain evidence.

The five Codec2 LDPC units retain their original authorship and mixed LGPL
provenance. Their composite disposition is LGPL-2.1-only. The Station Engine
notice set prominently records the LGPL-2.1 section 3 election applying
GPL-2.0-or-later to the exact conveyed copies, and the combined Station Engine
binary is conveyed as GPL-3.0-or-later.
