# Zeus — Provenance and Attributions

This file is the canonical, human-readable statement of provenance for the
Zeus project. It exists so that anyone reading the code — or auditing it —
can trace Zeus's lineage, see who the work rests on, and understand how the
licence obligations flow through the project.

Per-file headers reference this document by name. This file is the
authoritative list; those headers are a reminder.

**Scope note.** This document covers the Zeus project as a whole. It is also
copied verbatim into the station-engine corresponding-source export, because
every engine source file's header points a reader here. That export includes
the GPL and permissively licensed native source and build control files used by
the engine. The proprietary VST3 and Audio Unit bridge sources are excluded and
are not part of the station engine; product components such as `ZeusProduct/`
are also outside the export. The authoritative third-party inventory for the
exported engine, with each component's preserved licence text, is
`THIRD-PARTY-NOTICES.md` alongside this file.

## License

The Zeus Station Engine and repository files without a different per-file or
directory-level notice are distributed under the **GNU General Public License,
version 2 or (at your option) any later version** (GPL-2.0-or-later). The full
licence text and composition rules are in [`LICENSE`](LICENSE).

The Zeus SDR web client under [`zeus-web/`](zeus-web/), the `ZeusProduct/`
program, and the native VST3 and Audio Unit bridges are proprietary and carry
proprietary per-file license identifiers. The web client's in-tree notice is
[`zeus-web/LICENSE`](zeus-web/LICENSE). Proprietary client components and the
station engine are separate programs, and nothing in a proprietary notice
limits the rights granted under the GPL for the station engine.

This licence was chosen deliberately to align Zeus with its primary
upstreams and reference projects:

- **Thetis** — GPL v2 or later
- **WDSP** — GPL v2 or later
- **pihpsdr** — GPL v2 or later
- **DeskHPSDR** — GPL v2 or later

Zeus's "or later" clause preserves forward-compatibility with downstream
GPL v3 works.

## Zeus contributors

Zeus is maintained by:

- **Douglas J. Cerrato (KB2UKA)** — project lead
- **Christian Suarez (N9WAR)** — project lead

Additional contributions are visible in `git log` and in the repository's
pull-request history.

## Relationship to Thetis

Zeus is **an independent reimplementation in .NET — not a fork** of
Thetis. No Thetis binary is distributed with Zeus, and no Thetis source
file is carried in the Zeus tree.

That said, Zeus was **developed with direct reference to the Thetis
source** as the authoritative specification of OpenHPSDR Protocol-1 /
Protocol-2 client behaviour. The following categories of knowledge were
learned by reading Thetis source:

- Protocol-1 and Protocol-2 discovery and framing
- WDSP initialisation ordering and channel-state transitions
- Meter pipelines (S-meter, TX-stage meters)
- AGC curves, filter widths, bandwidth scheduling
- TX safety behaviour (SWR trip, TX timeout, TUNE)
- Console/radio wiring conventions

The station engine and other GPL-covered repository code preserve the
licensing obligations of their upstreams. Their per-file headers, this
document, and the root `LICENSE` file together carry the full GPL
v2-or-later notice through the derivation chain. The separately licensed
`zeus-web/` client retains this complete provenance statement and all upstream
acknowledgements.

Where any Zeus file is later identified as a close port of a specific
Thetis source file — rather than behaviour-informed original code — that
file will carry an additional per-file header naming the Thetis source,
the original copyright holders, and the date of modification, as required
by GPL v2 §2(a).

## Thetis — lineage and contributors

Thetis continues a long-running GPL-governed software lineage:

1. **FlexRadio PowerSDR** — the original GPL-licensed Software-Defined
   Radio client from FlexRadio Systems.
2. **OpenHPSDR ecosystem** (TAPR / OpenHPSDR) — continuation of the
   PowerSDR codebase as an open-hardware / open-source SDR platform.
3. **Thetis** — the modernised OpenHPSDR client implementation used as
   Zeus's reference.

The authoritative Thetis tree referenced by Zeus is:
<https://github.com/ramdor/Thetis>

Zeus gratefully acknowledges the Thetis contributors whose work — carried
forward through the lineage above — made this project possible:

| Name | Callsign |
| --- | --- |
| Richard Samphire | MW0LGE |
| Warren Pratt | NR0V |
| Laurence Barker | G8NJJ |
| Rick Koch | N1GP |
| Bryan Rambo | W4WMT |
| Chris Codella | W2PA |
| Doug Wigley | W5WC |
| Richard Allen | W5SD |
| Joe Torrey | WD5Y |
| Andrew Mansfield | M0YGG |
| Reid Campbell | MI0BOT |
| Sigi Jetzlsperger | DH1KLM |
| **FlexRadio Systems** | *(corporate)* |

Some Thetis contributions carry dual-licensing statements in addition
to the GPL. Where Zeus references or is informed by a specific Thetis
source file, any such dual-licensing notice from that file is to be
preserved in the corresponding Zeus per-file header — not stripped to
GPL alone.

## SPE Expert 1.5K Taurus amplifier support

The amplifier backend under
[`Station.Engine.Hosting/SpeTaurus/`](Station.Engine.Hosting/SpeTaurus/) is
distributed under **GNU General Public License, version 3 or (at your option)
any later version** (GPL-3.0-or-later). Its detailed source and licensing record
is preserved in
[`Station.Engine.Hosting/SpeTaurus/SOURCE.md`](Station.Engine.Hosting/SpeTaurus/SOURCE.md).

Review found substantial source overlap with the GPL-3.0-or-later Taurus
desktop implementation, which was itself informed by
[`netjordan/spe-expert-remote`](https://github.com/netjordan/spe-expert-remote).
The earlier independent/clean-room claim was withdrawn. Relocation into the
public GPL station engine provides the approved distribution path for that
provenance. The implementation was also developed with the public
[SPE Application Programmer's Guide, revision 1.1](https://www.spetlc.com/images/download/SPE_Application_Programmers_Guide.pdf)
and the [FTDI D2XX Programmer's Guide](https://ftdichip.com/wp-content/uploads/2025/06/D2XX_Programmers_Guide.pdf),
plus FTDI's public D2XX driver documentation.

The rest of the engine is GPL-2.0-or-later, whose "or later" option permits
combination with this GPL-3.0-or-later work. Station-engine distributions that
do not include the optional ASIO host bridge are therefore distributed as
**GPL-3.0-or-later**. Windows distributions that include that bridge are
conveyed under **GPL-3.0-only**, as described below.

## WDSP

Zeus loads **WDSP** (Warren Pratt, NR0V) via P/Invoke for all on-air DSP.
WDSP source ships in-tree under [`native/wdsp/`](native/wdsp/); its
upstream licence, copyright notices, and author attribution are
preserved in every file as received. Zeus builds a shared library from
that source at build time — it does not modify WDSP.

WDSP is Copyright (C) Warren Pratt (NR0V) and is distributed under
**GNU General Public License, version 2 or later**. See
<https://github.com/TAPR/OpenHPSDR-Thetis/tree/master/Project%20Files/Source/wdsp>
for the upstream.

Five small shim / glue files under `native/wdsp/` and
`native/wdsp/stubs/` were authored by Zeus contributors and are
GPL-2.0-or-later under the Zeus copyright:

- `native/wdsp/wdsp_export.h`
- `native/wdsp/stubs/nr3/rnnoise.h`
- `native/wdsp/stubs/nr3/rnnr_stub.c`
- `native/wdsp/stubs/nr4/sbnr_stub.c`
- `native/wdsp/stubs/nr4/specbleach_adenoiser.h`

## libspecbleach

Zeus's NR4 (SBNR — Spectral Bleaching Noise Reduction) signal path links
against **libspecbleach** (Luciano Dato), vendored in-tree under
[`native/libspecbleach/`](native/libspecbleach/). The library is built as
a static sub-target of `libwdsp` with hidden symbol visibility, so the
SBNR exports surface from `libwdsp.{so,dll,dylib}` directly and end-users
do not see a separate runtime dependency.

libspecbleach is **Copyright (C) 2022 Luciano Dato
&lt;lucianodato@gmail.com&gt;** and is distributed under the **GNU Lesser
General Public License, version 2.1 or (at your option) any later
version** (LGPL-2.1-or-later). The full licence text is preserved
verbatim at
[`native/libspecbleach/LICENSE`](native/libspecbleach/LICENSE);
provenance and a re-vendor recipe are in
[`native/libspecbleach/VENDORING.md`](native/libspecbleach/VENDORING.md).

The vendored copy is the **MW0LGE-modified snapshot that ships with
Thetis**, sourced from
`Thetis/Project Files/lib/NR_Algorithms_x64/src/libspecbleach/`. This was
chosen over upstream `lucianodato/libspecbleach` so that Zeus's
`specbleach_adaptive_*` calls in `native/wdsp/sbnr.c` match Thetis's NR4
reference behaviour bit-for-bit. The MW0LGE modifications are
concentrated in `CMakeLists.txt` (FFTW3f path discovery for the Windows
build, marked `# MW0LGE (c) 2025`); the algorithmic source under `src/`
matches upstream as of the Thetis snapshot.

Upstream:
- Original library — <https://github.com/lucianodato/libspecbleach>
- Thetis-modified snapshot — <https://github.com/ramdor/Thetis>

LGPL-2.1-or-later → GPL-2.0-or-later is one-way licence-compatible, so
linking libspecbleach into Zeus's GPL-2-or-later distribution is
consistent with both the LGPL's permissive linking clause and Zeus's own
licence terms. Zeus does not modify the vendored libspecbleach source;
per-file headers in `native/libspecbleach/` are preserved as received
from upstream and must remain so on re-vendor.

libspecbleach also introduces a build-time dependency on **FFTW3f** (the
single-precision build of FFTW3) on every host that rebuilds the native
library. FFTW3f is a separately-distributed library and is not vendored
into Zeus; see `native/README.md` for the per-platform install hint.

## librnnoise (RNNoise)

Zeus's NR3 (RNNoise) signal path links against **RNNoise** (xiph), vendored
in-tree under [`native/rnnoise/`](native/rnnoise/). Like libspecbleach it is
built as a static sub-target of `libwdsp` with hidden symbol visibility, so the
RNNR exports surface from `libwdsp.{so,dll,dylib}` directly with no separate
runtime dependency.

RNNoise is **Copyright (C) Jean-Marc Valin and the Xiph.Org Foundation** and is
distributed under the **BSD 3-Clause License**. The full licence text is
preserved verbatim at [`native/rnnoise/COPYING`](native/rnnoise/COPYING);
provenance, the pinned upstream commit, and a re-vendor recipe are in
[`native/rnnoise/VENDORING.md`](native/rnnoise/VENDORING.md).

The vendored copy is the upstream xiph `main`-branch architecture (the
weights-file / DNN variant whose `rnnoise_model_from_filename` API
`native/wdsp/rnnr.c` calls). The library is built with `USE_WEIGHTS_FILE` and a
minimal `rnnoise_data.c` (the `init_rnnoise()` function only, no default weights
compiled in), so NR3 loads its model at runtime rather than baking one into
`libwdsp`. See `native/rnnoise/VENDORING.md` for details.

### Bundled default model

Zeus ships a **default RNNoise model** so NR3 works out of the box:
[`Station.Engine.Hosting/nr3-data/rnnoise-default.bin`](Station.Engine.Hosting/nr3-data/rnnoise-default.bin).
It is a standard xiph/rnnoise model in the DNNw weights-file format, compatible
with the vendored RNNoise architecture (43 weight arrays: `conv1`/`conv2`, three
GRU layers, `dense_out`, `vad_dense`). The model weights carry the same
**BSD-3-Clause** licence as RNNoise itself (`native/rnnoise/COPYING`). The
operator may override it at runtime by installing their own weights file via the
DSP menu (upload or URL); removing that reverts to this bundled default.

Upstream:
- <https://github.com/xiph/rnnoise> (mirror of <https://gitlab.xiph.org/xiph/rnnoise>)
- Models: <https://media.xiph.org/rnnoise/models/>

BSD-3-Clause → GPL-2.0-or-later is one-way licence-compatible, so linking
RNNoise into Zeus's GPL-2-or-later distribution is consistent with both
licences. The RNNoise `src/` is vendored unmodified except for the minimal
`rnnoise_data.c` described above; per-file headers are preserved as received
from upstream and must remain so on re-vendor.

## Steinberg VST 3 SDK

The proprietary native VST3 host bridge under
[`native/zeus-vst-bridge/`](native/zeus-vst-bridge/) embeds the **Steinberg
VST 3 SDK**, vendored as the `third_party/vst3sdk` submodule. The SDK is
distributed under the **MIT License**:

> Copyright (c) 2025, Steinberg Media Technologies GmbH
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in
> all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

The licence text is preserved verbatim at
[`native/zeus-vst-bridge/third_party/vst3sdk/LICENSE.txt`](native/zeus-vst-bridge/third_party/vst3sdk/LICENSE.txt).
Upstream: <https://github.com/steinbergmedia/vst3sdk>.

## Steinberg ASIO SDK

The optional Windows local-audio host bridge is built from Steinberg ASIO SDK
2.3.4. Zeus selects the SDK's **GNU General Public License version 3** option;
the SDK notice does not grant an "or later" option, so the bridge is classified
as **GPL-3.0-only**. The complete SDK-derived source and Zeus build controls are
published only with the GPL station-engine corresponding source under
[`native/asio/`](native/asio/). The proprietary `ZeusProduct` program neither
references that source nor contains the ASIO bridge binary.

The official SDK archive is available from <https://www.steinberg.net/asiosdk>.
The 2.3.4 archive used by Zeus has SHA-256
`d5ebf0c20dd2c5f43771fd0c1418f4b361bf52434ee670097cfa6b3a335e2eca`.
Steinberg's license notice is preserved at
`packaging/station-engine/THIRD-PARTY-LICENSES/Steinberg-ASIO-SDK-LICENSE.txt`.
The ASIO name is used descriptively; Zeus does not ship an ASIO logo.

## ft8_lib / wsprd (moved to the Zeus Digital plugin)

The native FT8/FT4 core (**ft8_lib**, Kārlis Goba, MIT) and the WSPR
encoder/decoder (**wsprd**, Joe Taylor K1JT / Steven Franke K9AN, GPL-3)
moved out of the Zeus tree together with the FT8/FT4/WSPR suite, which now
ships as the installable **com.kb2uka.digital** plugin. Their vendored
sources, build glue, and full attribution/licence statements live in the
plugin repository:
<https://github.com/Zeus-SDR/openhpsdr-zeus-plugins> under
`modes/Digital/`.

## RADE V1 (Radio Autoencoder — radae_c, opus_dnn, freedv_text)

Zeus's RADE V1 (Radio Autoencoder) digital-voice mode builds a single shared
library, `libzeus_rade.{so,dll,dylib}`, from three upstream C slices. The
slices are vendored at build time into
[`native/radae/vendor/`](native/radae/vendor/) by CI and are **not** committed
to the Zeus tree (~95 MB, almost all of it compiled-in neural-network weight
tables). All three slices are taken from a single upstream composition:

| field | value |
|---|---|
| Upstream | <https://github.com/sv1eia/Thetis-RADE> |
| Pinned SHA | `f7605a46bd21275ab8b9edd00d4a1b6fae6eabe8` |

The composition itself — selecting `radae_c` as the Python-free RADE modem,
pairing it with the `opus_dnn` FARGAN vocoder, and wiring the FreeDV-GUI
`freedv_text` LDPC path for the on-air EOO callsign frame so RADE stations
interoperate — is the work of **Christos Nikolaou (SV1EIA)** in the
[`sv1eia/Thetis-RADE`](https://github.com/sv1eia/Thetis-RADE) project. Zeus
gratefully acknowledges that adaptation; the build recipe, slice layout, and
SHA pin in [`native/radae/vendor/PROVENANCE.md`](native/radae/vendor/PROVENANCE.md)
trace directly to it.

### `radae_c` — BSD-2-Clause

The pure-C, Python-free RADE modem (IQ → 36-float feature frames; encoder /
decoder weights compiled in). **Copyright © 2024 David Rowe (VK5DGR,
[drowe67](https://github.com/drowe67))** and distributed under the **BSD
2-Clause License**. Zeus uses the decode path on RX and the encode path on
TX; the source tree is vendored unmodified except for compilation flags set
in [`native/radae/CMakeLists.txt`](native/radae/CMakeLists.txt). Upstream
reference: <https://github.com/drowe67/radae>.

### `opus_dnn` — BSD-3-Clause

The Xiph Opus tree pinned at upstream `xiph/opus` commit
`940d4e5af64351ca8ba8390df3f555484c567fbb`, built with `OPUS_DEEP_PLC=ON` to
provide the FARGAN deep neural vocoder (`fargan_synthesize()` — 36-float
feature frame → 160-sample @ 16 kHz speech) that the RADE shim calls on
decode, and the LPCNet feature analyzer (`lpcnet_compute_single_frame_features()`)
used on encode. **Copyright © Xiph.Org Foundation, Jean-Marc Valin and the
Opus contributors** (with FARGAN/LPCNet contributions also under Skype/Microsoft
attribution as carried in the upstream source headers) and distributed under
the **BSD 3-Clause License**. The vendored copy preserves the upstream
per-file headers. Upstream: <https://github.com/xiph/opus>.

### `freedv_text/src/rade_text.{c,h}` — BSD

The FreeDV-GUI reliable-text codec used by Zeus for the RADE EOO callsign
frame (CRC8 + 6-bit packing + LDPC HRA_56_56 + gp_interleaver). This replaces
`radae_c`'s built-in 7-bit-MSB packing so Zeus interoperates with FreeDV-GUI
RADE stations on the air. **Copyright © Mooneer Salem** and distributed
under a permissive **BSD** licence. Upstream reference:
<https://github.com/drowe67/freedv-gui>.

### `freedv_text/codec2/*` (LDPC primitives) — LGPL-2.1-or-later

The LDPC primitives the reliable-text path links against
(`mpdecode_core.c`, `gp_interleaver.c`, `ldpc_codes.c`, `HRA_56_56.c`,
`phi0.c`) come from the **codec2** project. **Copyright © David Rowe and
the codec2 contributors** and distributed under the **GNU Lesser General
Public License, version 2.1 or (at your option) any later version**
(LGPL-2.1-or-later). Upstream: <https://github.com/drowe67/codec2>.

`libzeus_rade` is a shared library; Zeus's managed code reaches it through
P/Invoke. Dynamic linking against LGPL-2.1-or-later code from Zeus's
GPL-2.0-or-later distribution is consistent with both licences (LGPL §6's
permissive linking clause is preserved, and the combined work remains
distributable under Zeus's licence terms).

### License roll-up

| component | license | copyleft? |
|---|---|---|
| `radae_c` | BSD-2-Clause | no |
| `opus_dnn` | BSD-3-Clause | no |
| `freedv_text/src` (rade_text) | BSD | no |
| `freedv_text/codec2` (LDPC) | LGPL-2.1-or-later | weak (dynamic-link OK) |

BSD-2-Clause, BSD-3-Clause, and LGPL-2.1-or-later are all one-way
licence-compatible with Zeus's GPL-2.0-or-later distribution. The vendored
sources retain their upstream per-file headers and copyright notices as
received and must remain so on re-vendor. The `CMakeLists.txt` glue under
[`native/radae/`](native/radae/) is original Zeus work under GPL-2.0-or-later,
while the `shim/` files are BSD-2-Clause under their per-file SPDX grants.

## Relationship to pihpsdr

Zeus is independent of pihpsdr but **routinely consulted pihpsdr source as
the authoritative reference for Saturn-class (ANAN G2, G2 MkII, Saturn /
Saturn-XDMA) Protocol-2 behaviour**, particularly for:

- Hardware-peak values per board class (`transmitter.c`)
- Wire-format byte semantics on `CmdHighPriority` and `CmdTx` (`new_protocol.c`)
- PureSignal arm sequence and `tx_ps_reset` / `tx_ps_resume` patterns
- ALEX antenna routing for the PS feedback DDC pair
- DDC0 / DDC1 sample-pair convention into `pscc()`

pihpsdr is maintained by **Christoph Wüllen, DL1YCF** at
[github.com/dl1ycf/pihpsdr](https://github.com/dl1ycf/pihpsdr) and is
licensed GPL-2.0-or-later, compatible with Zeus.

Zeus acknowledges the following pihpsdr contributors whose work informed
Zeus's Protocol-2 / PureSignal implementation:

| Callsign |
| --- |
| DL1YCF (Christoph Wüllen) |

## Relationship to DeskHPSDR

Zeus is independent of DeskHPSDR but consulted DeskHPSDR as a
cross-reference for HPSDR client behaviour. DeskHPSDR is maintained by
**Heiko, DL1BZ** at [github.com/dl1bz/deskhpsdr](https://github.com/dl1bz/deskhpsdr)
and is licensed GPL-2.0-or-later, compatible with Zeus.

## Third-party assets and imagery

### Bouncy Castle Cryptography for .NET

The separately built `ZeusProduct` host uses
**BouncyCastle.Cryptography 2.6.2** to verify the Ed25519 signatures on Zeus
Link entitlement grants and their nested session tokens. Bouncy Castle is
Copyright (c) 2000-2025 The Legion of the Bouncy Castle Inc. and is distributed
under the MIT license. The package's license text is preserved at
[`ZeusProduct/ThirdParty/BouncyCastle-LICENSE.md`](ZeusProduct/ThirdParty/BouncyCastle-LICENSE.md)
and copied beside the product host on build and publish.

### Assets and imagery

Images under `docs/pics/` are original screenshots of the Zeus user
interface, unless explicitly stated otherwise in an adjacent caption or
`NOTICE` entry. No FlexRadio, Apache Labs (ANAN), or Thetis marketing
imagery is reproduced in this repository.

### Amateur-license map data

The optional map overlay queries [HamInfo](https://haminfo.tetranz.com/map),
which geocodes active amateur licenses from the public
[FCC Universal Licensing System](https://opendata.fcc.gov/Wireless/FCC-Universal-Licensing-System-ULS-/x28i-i4z4/data).
Zeus does not ship or mirror that database. The local host performs bounded,
cached queries only while the operator enables the layer, and removes licensee
names and street-address text before the response reaches the UI. Plotted
coordinates represent license mailing addresses, not verified transmitter
locations.

## Per-file header format

Every first-party Zeus source file begins with an SPDX identifier,
the Zeus copyright line, the short GPL notice, and an acknowledgement
block that names all thirteen Thetis contributors, references pihpsdr
(DL1YCF) and DeskHPSDR (DL1BZ), and points back at this file.
See any source file for the canonical form.

## Reporting attribution concerns

If you believe Zeus has inadequately attributed your work — or carries
content that should be attributed to you or to an upstream project —
please open an issue at
<https://github.com/Zeus-SDR/openhpsdr-zeus/issues> or contact
the project lead directly. Zeus will treat attribution corrections as
a priority class of change.
