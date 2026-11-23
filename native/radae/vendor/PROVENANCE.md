# native/radae/vendor — RADE V1 + V2 vendored source provenance

Zeus builds its RADE V1 + V2 modem (`libzeus_rade`, see `../CMakeLists.txt`) from three
upstream C slices. They are **not committed to the private Zeus git tree** — together
they are ~160 MB, almost all of it compiled-in neural-network weight tables. The shared
`fetch-sources.sh` path vendors and integrity-checks them before native builds and
materializes them into every corresponding-source export. The machine-readable pins
and content hashes are in `SOURCE-SLICES.json`; this file explains what is vendored,
from where, and under which license.

## Upstream

All three slices are vendored from a single upstream repository:

| field        | value |
|--------------|-------|
| Upstream     | https://github.com/sv1eia/Thetis-RADE |
| Pinned SHA   | `408f2b5232ff0a2aec9b538a40d4cb1b02627b17` (2026-06-27) |
| HEAD subject | *"RADE V2: per-RX V1/V2 protocol selection and refresh radae_c to V1+V2"* |
| Previous pin | `f7605a46bd21275ab8b9edd00d4a1b6fae6eabe8` (V1 only; see the [2026-11-22 pin move](#pin-move-to-408f2b5-rade-v1--v2-2026-11-22)) |

The previous pin, `f7605a4` (*"Remove vendored radae_nopy; build rade.lib from in-repo
radae_c and update credits/docs"*), is the commit that switched Thetis-RADE itself from
`radae_nopy` to the in-repo `radae_c` + co-vendored `opus_dnn` model, i.e. the
composition Zeus adopts here. The current pin keeps that composition and adds the RADE
V2 protocol to `radae_c`; `opus_dnn` and `freedv_text` are unchanged between the two.

## Slices to vendor

Copy these three directories from the pinned checkout into `native/radae/vendor/`,
preserving the directory name (drop the `Project Files/lib/` prefix):

| vendor/ target | upstream path (under the SHA above)        | license       |
|----------------|--------------------------------------------|---------------|
| `radae_c/`     | `Project Files/lib/radae_c/`               | BSD-2-Clause  |
| `opus_dnn/`    | `Project Files/lib/opus_dnn/`              | BSD-3-Clause  |
| `freedv_text/` | `Project Files/lib/freedv_text/`           | mixed: see below |

### `radae_c/` — BSD-2-Clause (David Rowe & Jean-Marc Valin, © 2024; C port by Christos Nikolaou SV1EIA, © 2026)
Pure-C, Python-free RADE modem, V1 and V2. IQ → 36-float feature frames; encoder/decoder
weights compiled in (V1: `src/rade_enc_data.c`, `src/rade_dec_data.c`, ~24 MB each; V2:
`src/rade_enc_v2_data.c` ~24 MB, `src/rade_dec_v2_data.c` ~39 MB, and the V2 frame-sync
net `src/rade_sync_data.c` ~0.2 MB). The public API (`src/rade_api.h`, implemented by
`src/radc_api.c`) stays compatible with the reference `rade_api.h`; the `RADE_PROTOCOL_V2`
open flag selects V2 per handle and `rade_protocol()` reports it. Zeus uses both the
decode and transmit paths of both protocols; the whole `src/` tree is vendored and the
compiled-in data tables are shared. The slice also carries a `NOTICE` file (added at the
current pin) listing the third-party notices radae_c's neural code reuses: Opus/Xiph
(BSD-3-Clause), KISS FFT (BSD-3-Clause), and the Opus DNN kernels and weight-export
tooling (BSD-2-Clause, © 2022-2023 Amazon, written by Jan Buethe; © 2018 Mozilla).

### `opus_dnn/` — BSD-3-Clause (Xiph.Org / Skype / Jean-Marc Valin et al.)
Xiph Opus pinned at upstream `xiph/opus` commit
`940d4e5af64351ca8ba8390df3f555484c567fbb` (per `opus_dnn/commit_pin.txt`), carrying the
DNN/FARGAN vocoder. Built via its own CMake with **`OPUS_DEEP_PLC=ON`**, which compiles
`dnn/fargan.c`, `dnn/fargan_data.c`, `dnn/nnet.c`, `dnn/freq.c`, etc. — providing
`fargan_synthesize()` (the 36-float-feature → 160-sample @ 16 kHz vocoder) that the shim
calls. (`OPUS_DRED`/`OPUS_OSCE` are **not** required for the V1 decode path.)

> **CI caveat — stripped SIMD subtrees.** The Thetis-RADE `opus_dnn` slice is the
> MSVC-x64 scalar build: its `silk/x86/`, `silk/arm/`, `celt/x86/`, `celt/arm/`,
> `dnn/x86/`, `dnn/arm/` directories are **absent**, but the CMake `*_headers.mk` /
> `*_sources.mk` lists still reference ~65 of those files. With
> `OPUS_DISABLE_INTRINSICS=ON` those SIMD *sources* are not compiled, but CMake's
> `target_sources()` still validates that the referenced *header* entries exist.
> CI must create empty placeholder files for every missing referenced path before
> configuring (one-liner: for each `silk|celt|dnn|src/...\.[ch]` token in the `.mk`
> files that does not exist on disk, `mkdir -p $(dirname) && : > $file`). This was the
> only patch needed to make `opus_dnn` configure on Linux/CMake.

### `freedv_text/` — mixed license (vendored AND wired)
The FreeDV-GUI reliable-text (LDPC) codec for the on-air **EOO callsign** frame.
radae_c's built-in `rade_rx_get_eoo_callsign()` uses a simple 7-bit-MSB packing
that is *not* the FreeDV-GUI on-air format. The shim therefore uses
`freedv_text/src/rade_text.c` (CRC8 + 6-bit chars + LDPC HRA_56_56 +
gp_interleaver) for **both** TX-encode and RX-decode of the callsign, so it
interoperates with FreeDV-GUI RADE stations. Compiled into `zeus_rade` via
`../CMakeLists.txt` (`FREEDV_TXT_SOURCES`) along with the codec2 LDPC files; on
Windows a one-file `shim/compat/alloca.h` maps `<alloca.h>`→`<malloc.h>` (the
codec2 sources hardcode the glibc/BSD header). Licenses within the slice:
- `freedv_text/src/rade_text.{c,h}` — BSD-2/3-Clause (Mooneer Salem)
- `freedv_text/codec2/*` (LDPC: `mpdecode_core.c`, `gp_interleaver.c`, `ldpc_codes.c`,
  `HRA_56_56.c`, `phi0.c`) — mixed per-file, composite **LGPL-2.1-only**:
  `gp_interleaver.c` is LGPL-2.1-only; the Iterative Solutions portion of
  `mpdecode_core.c` is LGPL-2.1-or-later; the other three inherit Codec2 1.2.0
  COPYING (LGPL-2.1)

## License roll-up

| component   | license      | copyright holders | copyleft? |
|-------------|--------------|-------------------|-----------|
| radae_c (V1 + V2) | BSD-2-Clause | © 2024 David Rowe & Jean-Marc Valin (RADE authors); © 2026 Christos Nikolaou SV1EIA (C port); bundled notices in `radae_c/NOTICE` | no |
| opus_dnn    | BSD-3-Clause | © Xiph.Org / Skype / Jean-Marc Valin et al. | no |
| freedv_text/src (rade_text) | BSD | © Mooneer Salem | no |
| freedv_text/codec2 (LDPC) | mixed per-file; composite LGPL-2.1-only | © David Rowe / Codec2; Iterative Solutions authors retained in `mpdecode_core.c` | section 3 election for the conveyed copies |

The audio RX/TX portion (`radae_c` + `opus_dnn` + Zeus shim) is BSD-licensed.
The five Codec2 LDPC units are compiled into the same `zeus_rade` shared
library for the EOO-callsign path. For the exact conveyed copies, the Station
Engine notice set records the LGPL-2.1 section 3 election to GPL-2.0-or-later
while preserving original authorship and LGPL provenance.

## Proven build + decode (WSL Ubuntu-24.04, 2026-06-24)

Superseded by the [2026-08-07 all-RID build and ABI export validation record](#proven-build--abi-export-validation-all-supported-rids-2026-08-07).

The one-shared-library composition above was built and validated end-to-end:

- **opus_dnn** built static via CMake: `-DOPUS_DEEP_PLC=ON -DOPUS_DISABLE_INTRINSICS=ON`
  (+ the placeholder-stub step). FARGAN symbols present in `libopus.a`.
- **radae_c** compiled with `-DRADE_STATIC -DRADE_PYTHON_FREE=1 -DIS_BUILDING_RADE_API=1`.
- Linked **radae_c objects + shim + whole-archived opus_dnn** into one
  `libzeus_rade.so` (11 MB). All `zeus_rade_*` symbols exported.
- **Decode test** on a 5'26" off-air RADE recording (`FDV_offair.wav`, 48 kHz mono),
  fed as **real samples** (`RADE_COMP.real = s, .imag = 0` — radae_c's modem expects a
  real 8 kHz signal; this is the reference feed in `ChannelMaster/radae.c`, *not* a
  Hilbert IQ front-end like radae_nopy's `real2iq`):

  ```
  zeus_rade_test: pcm_samples=5017920 ticks=2717 synced=2620 last_snr=14dB
  out.wav stat: Maximum amplitude 0.999969  RMS 0.116568  (313.6 s of real speech)
  ```

  **2620 synced ticks, 14 dB SNR, near-full-scale audio** — matches the prior
  radae_nopy reference run (~2628 ticks / 14 dB / 0.9999 max-amp). RADE V1 decode from
  the radae_c + opus_dnn base is PROVEN.

## Transmit + EOO callsign (proven, 2026-06-24)

Validated against the shim as it stood on this date. The TX path was later reworked
to add the microphone meter and pre-encoder conditioning, so this record does not
describe the currently conveyed binaries; of those, only win-x64 has been
re-exercised against the fidelity/loopback suite. See the
[2026-08-07 all-RID build and ABI export validation record](#proven-build--abi-export-validation-all-supported-rids-2026-08-07).

The shim gained a full TX path (`zeus_rade_tx`): 16 kHz speech → opus_dnn LPCNet
feature analyzer (`lpcnet_compute_single_frame_features`) → `rade_tx` → modem IQ;
the transmitted SSB audio is the **real** part of the IQ (inverse of the RX feed).
The EOO callsign uses `freedv_text` (`rade_text`), not radae_c's 7-bit packing, so
it matches FreeDV-GUI on the air. A self-contained encode→decode loopback
(`zeus_rade_test loopback N9WAR`, win-x64 MinGW) PROVES it end-to-end:

```
loopback: ticks=60 synced=48 last_snr=35dB pcm=89440 callsign=N9WAR
loopback: callsign round-trip OK ('N9WAR')   loopback: PASS
```

i.e. the TX modem waveform re-syncs in the decoder (48 ticks / 35 dB) and the
LDPC EOO callsign round-trips byte-exact. The managed RadeModem TX→RX loopback
test mirrors this through the C# resampler/ring/P-Invoke chain.

## Proven build + decode (Windows / MinGW, 2026-06-24)

Superseded by the [2026-08-07 all-RID build and ABI export validation record](#proven-build--abi-export-validation-all-supported-rids-2026-08-07).

The same composition was then PROVEN on native Windows with MinGW UCRT64 gcc 16.1
(Ninja, `OPUS_DISABLE_INTRINSICS=ON`):

- `zeus_rade.dll` (11.0 MB) linked clean; all 12 `zeus_rade_*` symbols exported.
- **Standalone** — `objdump -p` shows deps only on `KERNEL32` + `ADVAPI32` +
  system UCRT (`api-ms-win-crt-*`); NO `libgcc_s_seh-1.dll` / `libwinpthread-1.dll`
  / `libstdc++`. The static-link flags are now in `../CMakeLists.txt` (`if(MINGW)`).
- Decoded `FDV_offair.wav` (Windows exe): `synced=2620 last_snr=14dB`,
  decoded audio max-amp `0.999969` — **byte-for-byte identical to the Linux run**.

Windows build notes now folded into `../CMakeLists.txt`:
- `if(WIN32) PREFIX ""` so MinGW emits `zeus_rade.dll`, not `libzeus_rade.dll`
  (matches the .NET loader's probe name).
- `if(MINGW)` static-runtime link (`-static -static-libgcc -Wl,-Bstatic`).
- `-O1` on the two ~24 MB weight `.c` files (compile-time/RAM; they are data).
- The `shim/zeus_rade_test.c` harness needs `_setmode(_O_BINARY)` on Windows (stdio
  binary mode) — added there; the production P/Invoke path doesn't use stdio.

## Proven build + managed decode/transmit (macOS arm64, 2026-08-04)

Superseded by the [2026-08-07 all-RID build and ABI export validation record](#proven-build--abi-export-validation-all-supported-rids-2026-08-07).

The exact workflow recipe was run locally on Apple Silicon with macOS 26.5.2,
AppleClang 21.0.0, CMake 4.3.2, and Ninja 1.13.0. AppleClang required an
explicit `stdlib.h` include for the upstream `radc_rx.c` use of `abs`; Ninja
required an explicit target dependency from `zeus_rade` to the Opus static
archive. Both build fixes are constrained to Apple in `../CMakeLists.txt`.

- Artifact: `Zeus.Dsp/runtimes/osx-arm64/native/libzeus_rade.dylib`
- Architecture: Mach-O 64-bit dynamically linked shared library arm64
- SHA-256: `c2bcf319778617d5fab41c927f0aeea025fdfd309984cbec1ea0778fa6f6765f`
- ABI: all 18 managed-probe exports present
- Proof: all four managed RADE test families ran against the dylib; TX/RX
  loopback synced, fidelity output carried energy without clipping, sync
  gating and MOX flush reset correctly, and the held TX tail recovered the
  LDPC EOO callsign byte-exact.

## Proven build + ABI export validation (all supported RIDs, 2026-08-07)

Superseded by the [2026-11-22 binary record](#binary-record-build-rade-run-45074017012-2026-11-22).
These V1-only binaries from the previous pin are no longer conveyed.

The `build-rade` workflow for PR #1121, run
[`31221276828`](https://github.com/Zeus-SDR/zeussdr/actions/runs/31221276828),
rebuilt all four runtime artifacts from the pinned vendor slices and validated
all 26 managed-probe exports on each artifact.

- **linux-x64**
  - Artifact: `Zeus.Dsp/runtimes/linux-x64/native/libzeus_rade.so`
  - SHA-256: `ed569a070379c47c1e9583792b4d6f01eecdaacedceece90ad6379c5c66a8897`
  - ABI: all 26 managed-probe exports present
  - Execution: not exercised against the RADE fidelity/loopback suite in this workflow run
- **linux-arm64**
  - Artifact: `Zeus.Dsp/runtimes/linux-arm64/native/libzeus_rade.so`
  - SHA-256: `0f411f4a3eb8419c6cf9a7d1f3a2ff396da03f826d4118c02b2512abef233baa`
  - ABI: all 26 managed-probe exports present
  - Execution: unproven; there is no Raspberry Pi CI leg
- **win-x64**
  - Artifact: `Zeus.Dsp/runtimes/win-x64/native/zeus_rade.dll`
  - SHA-256: `79dfe2be219d9619758381307aea258007bd83775ee8f079b2613c69fee31867`
  - ABI: all 26 managed-probe exports present
  - Execution: RADE fidelity/loopback suite passed
- **osx-arm64**
  - Artifact: `Zeus.Dsp/runtimes/osx-arm64/native/libzeus_rade.dylib`
  - SHA-256: `a593e230c3e6e8c3d98f71d5a447c6de244f16f6e5692bea1753ea23e2cfcda7`
  - ABI: all 26 managed-probe exports present
  - Execution: not exercised against the RADE fidelity/loopback suite in this workflow run

## Pin move to `408f2b5`: RADE V1 + V2 (2026-11-22)

The maintainer (KB2UKA) authorised moving the Thetis-RADE pin from `f7605a4` to
`408f2b5232ff0a2aec9b538a40d4cb1b02627b17` (2026-06-27, *"RADE V2: per-RX V1/V2 protocol
selection and refresh radae_c to V1+V2"*) so `zeus_rade` can carry the RADE V2 protocol.
Three upstream commits separate the pins; only the last touches `Project Files/lib`.

**What changed.** Only the `radae_c` slice. Verified by `fetch-sources.sh` against the
fetched commit (the Git tree IDs are the trust anchor) and re-hashed with
`source-slices.mjs`:

| slice | Git tree at `408f2b5` | files | content SHA-256 | vs. previous pin |
|---|---|---|---|---|
| `radae_c` | `8df56be6ea66b698acc6d4cd4b8ce0c2a4ab7950` | 60 | `c374bc00299b349f76989c32f2fc05af9811a1677a3500f1faa8a911e05dac17` | changed (was tree `fcb4a88d…`, 31 files) |
| `opus_dnn` | `f44426488357dd209a046021e4bb5ec1a9dd5be2` | 636 | `ed9327e4cce6b5ed8a8fc8c12ee1139ad2ea20b38e2a02d97207e62cc87e53c3` | unchanged |
| `freedv_text` | `71c6810a72d5f4c459fe2612bd30898c844a6fda` | 17 | `49723f26e81de9f00d179fc5b4cf1bc1062bf876d492b37665ffbdfa4c9f6dea` | unchanged |

**New `radae_c` files (29).** `NOTICE`; V2 modem/codec sources `src/radc_acq_v2.{c,h}`,
`radc_dec_v2.{c,h}`, `radc_demod_v2.{c,h}`, `radc_enc_v2.{c,h}`, `radc_mod_v2.{c,h}`,
`radc_modem_v2.{c,h}`, `radc_rx_v2.{c,h}`, `radc_sync_v2.{c,h}`, `radc_tx_v2.{c,h}`,
`radc_const_v2.h`, `radc_internal.h`, `rade_v2_core.h`; generated V2 weight tables
`src/rade_dec_v2_data.{c,h}` (~39 MB), `src/rade_enc_v2_data.{c,h}` (~24 MB),
`src/rade_sync_data.{c,h}` (~0.2 MB), and `src/rade_v2_constants.h`. The compiled set
matches upstream's own `msvc/radae_c.vcxproj` at the pin (nine V2 `.c` units plus the
three V2 tables).

**Changed V1 files.** `src/radc_api.c` now dispatches every API call on the protocol
chosen at `rade_open()`; the V1 branch calls the same V1 internals as before. `rade_api.h`
makes `struct rade` opaque (layout moved to `radc_internal.h`) and adds
`RADE_PROTOCOL_V2` / `rade_protocol()`. `radc_rx.c` gains `#include <stdlib.h>`. Every
other modified V1 file changes only its copyright header (adding the SV1EIA C-port
line). The V1 weight tables, `LICENSE`, and `msvc/build_radae_c.bat` are unchanged.

**Authorship and licence, read from the files.** Every new V2 source and header carries
the same header: *"Copyright (C) 2024 David Rowe (original RADE reference
implementation)"* (2025 for `radc_acq_v2`, `radc_demod_v2`, `radc_rx_v2`, `radc_sync_v2`),
*"Copyright (C) 2026 Christos Nikolaou (SV1EIA) <sv1eia@gmail.com> - C port"*,
*"BSD-2-Clause (see LICENSE)"*. `radae_c/LICENSE` is the BSD 2-Clause License,
*"Copyright (c) 2024, David Rowe and Jean-Marc Valin (RADE authors)"* and *"Copyright (c)
2026, Christos Nikolaou (SV1EIA) — C port"*, unchanged from the previous pin. The new
`NOTICE` states radae_c is BSD-2-Clause, and that radae_c *"links against - and reuses
neural network code from"* Opus/Xiph (BSD-3-Clause), KISS FFT (BSD-3-Clause), and the
Opus DNN kernels and weight-export tooling (BSD-2-Clause, *"Copyright (c) 2022-2023
Amazon (written by Jan Buethe)"*, *"Copyright (c) 2018 Mozilla (LPCNet / lpcnet_demo)"*),
whose notices *"MUST accompany source and binary redistributions of any build that
includes them"*.

**Open provenance points for a maintainer.**
- The generated V2 tables (`rade_enc_v2_data.{c,h}`, `rade_dec_v2_data.{c,h}`,
  `rade_sync_data.{c,h}`, `rade_v2_constants.h`) carry no copyright or licence header,
  only *"Auto generated from checkpoint checkpoint_epoch_200.pth"*. They fall under the
  slice-level BSD-2-Clause `LICENSE` only. Nothing in the slice identifies which
  upstream RADE V2 training run produced that checkpoint or its training corpus. This is
  the same posture as the V1 tables (*"checkpoint_epoch_100.pth"*) and the FARGAN
  weights, but it is a new generated weight table, which
  `docs/designs/rade-clearance-engine-hosted.md` lists as requiring a new inventory.
- The new `radae_c/NOTICE` asks that its bundled notices accompany binary
  redistributions. It is now conveyed byte-for-byte as
  `packaging/station-engine/THIRD-PARTY-LICENSES/RADE-radae_c-NOTICE` (copied from the
  verified fetch, SHA-256 `8a808952b43f8e9c9d30e57ed4956e601add1179a59ad796a831feed599dfda3`),
  checked by `tools/check-station-engine-notices.mjs`, and compared against the freshly
  fetched slice by `tools/export-station-engine.sh`.

**Build.** `../CMakeLists.txt` adds the nine V2 units and pins all five generated tables
to `-O1`. The shim gains `zeus_rade_open_protocol`, `zeus_rade_protocol`, and
`zeus_rade_abi_version` (= 2); `zeus_rade_open()` remains V1 with unchanged behaviour.
The conveyed binaries were rebuilt from this pin by `build-rade.yml`; see the binary
record below.

**Shim-set upstream switch: V2 timing correction.** The C port leaves the V2 receiver's
sample-clock timing correction off (`radc_rx_v2.c`: `rx->timing_adj_enable = 0;`,
*"enable for long-run / clock-drift operation"*); the official RADE V2 receiver runs its
equivalent on by default. The shim sets `timing_adj_enable = 1` on every V2 handle right
after `rade_open` (field access by name through `radc_internal.h` / `radc_rx_v2.h`,
compile-time checked; the vendored files stay byte-exact). Why: in a reference comparison
against the official receiver (AWGN only), the port's feature error at a +100 ppm clock
offset fell from 0.734 (3 re-syncs) to 0.059 with it on, while the clean, noisy and
+/-20 Hz baselines stayed within 0.001 of off. The Zeus self-test reproduces the effect
on a 120 s V2 over at +100 ppm: one sync run with it on, a sync loss at about 97 s with it
off. At -100 ppm the upstream V2 End-of-Over detector false-triggers about every 10 s
(11 to 12 re-acquisitions per 120 s, about 97% of the audio still decoded) with the switch
on or off; that is an upstream limitation the switch does not address.

**Native proof (macOS arm64 dev build, not a conveyed artifact).** AppleClang with
CMake 4.4.2 and Unix Makefiles on Apple Silicon, `OPUS_DISABLE_INTRINSICS=ON`,
`ZEUS_RADE_BUILD_TEST=ON`. `zeus_rade_test selftest` passed every check for both
protocols; the legacy `zeus_rade_test loopback N9WAR` reproduced the 2026-06-24 V1 numbers
exactly (`ticks=60 synced=48 last_snr=35dB pcm=89440 callsign=N9WAR`). The V1 + V2
library is about 23.5 MB, compared with about 11 MB for V1 alone.

### Binary record: build-rade run 45074017012 (2026-11-22)

The five conveyed libraries are the artifacts of
[`build-rade` run 45074017012](https://github.com/Zeus-SDR/zeussdr/actions/runs/45074017012),
built from commit `6c3e79aa0ffd003f24cc61dd15c82615a901d309` (pin `408f2b5`, shim with the
V2 timing correction) with `-DCMAKE_BUILD_TYPE=Release -DOPUS_DISABLE_INTRINSICS=ON
-DZEUS_RADE_BUILD_TEST=OFF`. The committed files were checked byte-for-byte against the
downloaded run artifacts. CMake 4.5.1 and Ninja come from `lukka/get-cmake` on every job
(the Ninja version is not printed in the log).

| RID | file | SHA-256 | bytes | runner image | compiler | executed? |
|---|---|---|---|---|---|---|
| linux-x64 | `libzeus_rade.so` | `ccd70ca1ffd4d3da9fbf8e27e6a11c1f2accede1ed713d5ee6434fb3ab1a901c` | 23530520 | ubuntu-22.04 20261116.365.1 | GCC 11.4.0 | yes: self-test PASS (57 checks), `loopback N9WAR` PASS |
| linux-arm64 | `libzeus_rade.so` | `88a9ffb92c2a95ac46cba067863e0adf58d0a4ea91887bb643bd56aba9f5a040` | 23463424 | ubuntu-22.04 20261116.365.1 | aarch64-linux-gnu-gcc 11.4.0 (cross) | **no**: exports checked only; this library has never been executed |
| win-x64 | `zeus_rade.dll` | `3de93e80993dfdaea93ad29dd149dc9cef997a379a644e602e4b5ed9805f5c35` | 23569433 | windows-2022 20261116.370.1 | MinGW-w64 GCC 14.2.0 (runner-preinstalled `C:/mingw64`) | yes: self-test PASS (57 checks), `loopback N9WAR` PASS |
| osx-arm64 | `libzeus_rade.dylib` | `ec418df481e961800a50607d918531159be6d3b4ae723564c283a9f7f652be45` | 23498520 | macos-15-arm64 20261118.0550.1 | AppleClang 17.0.0.17000013, arm64, minos 14.0 | yes: self-test PASS (57 checks), `loopback N9WAR` PASS |
| osx-x64 | `libzeus_rade.dylib` | `36b13deed8575790745fe37ec9e01612a2970f1953cc3dddbc76acfb6410b65f` | 23373712 | macos-15-arm64 20261118.0550.1 | AppleClang 17.0.0.17000013, x86_64, minos 12.0 (cross) | **no**: Mach-O arch, deployment target, and exports checked only; this library has never been executed |

- Every RID passed the workflow's check that the library exports all 29 symbols the
  managed binding calls, including `zeus_rade_abi_version`, `zeus_rade_open_protocol`,
  and `zeus_rade_protocol`.
- On the three executing RIDs the self-test ran against the exact staged bytes (the
  workflow compares the build-tree library with the staged artifact first). Results
  matched across RIDs: V2 held one sync run through the 120 s +100 ppm over, the V1
  legacy loopback reproduced `ticks=60 synced=48 last_snr=35dB pcm=89440 callsign=N9WAR`,
  and both known WARN lines appeared (V2 at -100 ppm re-acquires 12 times; a V1 receiver
  falsely locks on a V2 signal for 0.96 s). RX real-time factor on the hosted runners:
  linux-x64 V1 0.044 / V2 0.057, win-x64 V1 0.061 / V2 0.105, osx-arm64 V1 0.023 / V2 0.022.
- win-x64 toolchain note: the workflow installs MSYS2 UCRT64 GCC 16.2.0, but the build
  step runs in Git Bash, where CMake selected the runner's preinstalled
  `C:/mingw64/bin/cc.exe` (GCC 14.2.0). The DLL imports only `KERNEL32.dll` and the UCRT
  `api-ms-win-crt-*` DLLs (no libgcc or libwinpthread). The earlier records' "MSYS2
  UCRT64" toolchain description may have had the same gap; it was not re-checked.
- No Raspberry Pi or other arm64 Linux host and no Intel Mac has run these binaries.
