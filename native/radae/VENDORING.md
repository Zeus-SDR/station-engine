<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# RADE V1 native source and rebuild procedure

`zeus_rade` is one shared library containing the Python-free RADE C modem,
Opus DNN/FARGAN, FreeDV reliable text and its five Codec2 LDPC units, and the
Zeus BSD-2-Clause shim. `CMakeLists.txt` is the authoritative composition.

## Obtain and verify the exact upstream source

The private development repository intentionally does not commit the roughly
95 MB upstream slices. Native CI and the corresponding-source exporter use the
same fail-closed command:

```sh
bash native/radae/vendor/fetch-sources.sh native/radae/vendor
```

That command fetches Thetis-RADE commit
`f7605a46bd21275ab8b9edd00d4a1b6fae6eabe8`, verifies the fetched commit and
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
