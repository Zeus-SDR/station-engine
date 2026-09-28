<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# native/wspr — engine-hosted WSPR decoder

`zeus_wspr` is a decode-only shared library over the K1JT/K9AN WSPR decoder
(`wsprd`). Only the GPL station engine loads it
(`Station.Engine.Hosting/Wspr/`); the engine publishes decoded spots on the
loopback `/api/wspr/decoder` routes. The proprietary Zeus product and web
client never load, link, or call this library, and WSPR transmit does not use
it. Design: [`docs/designs/wspr-return.md`](../../docs/designs/wspr-return.md).

## Provenance and licence

[`vendor/`](vendor/) is a byte-for-byte copy of
[pavel-demin/wsprd](https://github.com/pavel-demin/wsprd) (the minimal
buildable extract of the WSJT-X `wsprd`) at commit
`8aa903085479910c77de95f7e7c178f66a245ed3`.

- `wsprd.c`, `wsprd_utils.c`, `wsprsim_utils.c`, `fano.c`, `jelinek.c`,
  `nhash.c`, `tab.c`, `metric_tables.c` — the decoder: candidate search, 4-FSK
  demodulation, K=32 r=1/2 sequential (Fano / Jelinek) decoding, signal
  subtraction and the callsign hash table. **GPL-3.0-or-later**, Copyright
  2001-2018 Joe Taylor (K1JT) and Steven Franke (K9AN). `wsprsim_utils.c`
  belongs to the decoder: it regenerates each decoded message's channel
  symbols for subtraction. Nothing from it is exported.
- `pffft.c` / `pffft.h` — Julien Pommier's PFFFT, BSD-style FFTPACK terms.

Zeus files (GPL-2.0-or-later): `zeus_wspr.h`, `zeus_wspr_decode.c`,
`osdwspr_stub.c`, `CMakeLists.txt`, `test/zeus_wspr_roundtrip.c`. The combined
library is GPL-3.0-or-later, which is how the station engine is conveyed.

## How the shim drives the decoder

`wsprd.c` keeps its decode in a command-line `main()`. The build renames it to
`wsprd_cli_main` (`-Dmain=wsprd_cli_main`) so the vendored file stays pristine,
and `zeus_wspr_decode()`:

1. writes the slot as a 12 kHz 16-bit WAV named `YYMMDD_HHMM.wav` (wsprd reads
   the slot time from the name) into the caller's data directory;
2. resets the `getopt` cursor (glibc and MinGW-w64: `optind = 0`; macOS:
   `optreset = 1; optind = 1`) — without this every call after the first
   parses no options;
3. runs the decoder with `-a <data dir> -f <dial MHz>` (plus `-d` for deep);
4. parses `wspr_spots.txt`, then removes the WAV, `wspr_spots.txt`,
   `ALL_WSPR.TXT` (which the decoder otherwise appends forever) and
   `wspr_timer.out`.

`hashtable.txt` is the one file left in the data directory, deliberately: it
lets hashed type-2/3 callsigns (`<KB2UKA>`) resolve across slots and engine
restarts. Calls are serialised by a mutex because the decoder keeps
process-global state. The decoder allocates about 0.8 MB on the calling
thread's stack; the engine's `NativeWsprSlotDecoder` runs every call on a
dedicated 16 MB-stack thread (macOS secondary threads get only 512 KB).

The optional ordered-statistics pass (`osdwspr.f90`, Fortran) is not built;
`osdwspr_stub.c` satisfies the symbol and the pass stays at its default of off.

## Build

```bash
cmake -S native/wspr -B build-wspr -DCMAKE_BUILD_TYPE=Release -DZEUS_WSPR_TESTS=ON
cmake --build build-wspr
ctest --test-dir build-wspr --output-on-failure
```

Windows builds with MinGW-w64 GCC (the decoder uses POSIX `getopt`); MSVC is
rejected at configure time. Release binaries come from the `build-wspr` job in
`.github/workflows/build-native-libs.yml` (dispatch with `only_wspr: true`)
for linux-x64, linux-arm64, win-x64, osx-arm64 and osx-x64. There is no
win-arm64 build (no aarch64 MinGW-w64 toolchain, the same gap as codec2), so
WSPR reports itself unavailable there. The job runs the round-trip gate on the
native hosts (linux-x64, win-x64, osx-arm64); the cross-built linux-arm64 and
osx-x64 binaries are checked for architecture, exports and (Linux) the glibc
floor only, and the engine's own round-trip test exercises every shipped
binary wherever the test suite runs. Commit each artifact to
`Zeus.Dsp/runtimes/<rid>/native/`.

## Replacing the binary

Per the GPL, an operator may substitute their own build: place it in
`<Zeus data dir>/native-overrides/wspr/<rid>/` or point
`ZEUS_WSPR_NATIVE_OVERRIDE_DIR` at a directory containing it. The engine
checks the three exports and the ABI version and refuses a mismatched
replacement rather than falling back silently.
