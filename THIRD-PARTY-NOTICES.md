<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Third-party notices

This inventory covers every external package, native library, embedded native
component, and model conveyed by the `station-engine` source export as audited
on 2026-07-19. The corresponding upstream license and notice files are
preserved verbatim in [`THIRD-PARTY-LICENSES`](THIRD-PARTY-LICENSES/).

## Inventory

| Component | Version or pinned source | License | Why it is in the export | Preserved text |
| --- | --- | --- | --- | --- |
| WDSP | upstream 1.29 snapshot | GPL-2.0-or-later | `Zeus.Dsp/runtimes/**/native/{lib,}wdsp.*`; loaded by the DSP native loader. The `calculus` and `zetaHat.bin` data files are WDSP runtime data. | `WDSP-LICENSE`; `WDSP-NOTICE` |
| FFTW | 3.3.x RID builds; audited macOS artifact 3.3.11 | GPL-2.0-or-later | Double- and single-precision libraries under `Zeus.Dsp/runtimes/**/native/`; both are dynamic dependencies of WDSP where not statically linked. | `FFTW-COPYING`; `FFTW-COPYRIGHT` |
| libspecbleach | MW0LGE-modified Thetis snapshot | LGPL-2.1-or-later | Statically embedded in WDSP for NR4/SBNR. | `libspecbleach-LICENSE` |
| RNNoise | commit `70f1d256acd4b34a572f999a05c87bf00b67730d` | BSD-3-Clause | Statically embedded in WDSP for NR3. The bundled `rnnoise-default.bin` model uses the same license. | `RNNoise-COPYING` |
| miniaudio | 0.11.25 | MIT-0 or public domain | `Zeus.Dsp/runtimes/**/native/{lib,}miniaudio.*`; loaded by the local-audio interop layer. This distribution uses the MIT-0 option. | `Miniaudio-LICENSE` |
| codec2 | 1.2.0, commit `06d4c11e699b0351765f10398abb4f663a984f36` | LGPL-2.1 | `Zeus.Dsp/runtimes/**/native/{lib,}codec2.*`; conveyed by the exported DSP project. | `Codec2-COPYING` |
| RADE C modem | Thetis-RADE commit `f7605a46bd21275ab8b9edd00d4a1b6fae6eabe8` | BSD-2-Clause | Compiled into `libzeus_rade` / `zeus_rade.dll`, which is conveyed by the exported DSP project. | `RADE-radae_c-LICENSE` |
| Opus DNN/FARGAN | Opus commit `940d4e5af64351ca8ba8390df3f555484c567fbb` through the pinned Thetis-RADE composition | BSD-3-Clause | Compiled into the RADE shared library. | `RADE-opus_dnn-COPYING` |
| FreeDV reliable text | pinned Thetis-RADE composition above | BSD | `rade_text.c` / `rade_text.h` are compiled into the RADE shared library. | `RADE-rade_text-NOTICE` |
| codec2 LDPC primitives in RADE | pinned Thetis-RADE composition above | LGPL-2.1-or-later | Five codec2 LDPC source units are compiled into the RADE shared library for the end-of-over text path. | `Codec2-COPYING` |
| LiteDB | 5.0.20, package commit `9843a4e38b4d46d544a3261f9711dbc559c4c4fc` | MIT | Direct NuGet dependency of `Station.Engine.Hosting`; used for local engine preferences. | `LiteDB-LICENSE` |
| System.IO.Ports and platform runtime packages | 10.0.0, package commit `b0f34d51fccc69fd334253924abd8d6853fad7aa` | MIT | Direct NuGet dependency of `Station.Engine.Hosting` for migrated CAT serial-port support. The package resolves the matching native runtime packages for supported platforms. | `Microsoft-dotnet-LICENSE`; `Microsoft-dotnet-THIRD-PARTY-NOTICES` |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0, package commit `b0f34d51fccc69fd334253924abd8d6853fad7aa` | MIT | Direct NuGet dependency of the Protocol 1, Protocol 2, and DSP projects. | `Microsoft-dotnet-LICENSE`; `Microsoft-dotnet-THIRD-PARTY-NOTICES` |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0, package commit `b0f34d51fccc69fd334253924abd8d6853fad7aa` | MIT | Transitive NuGet dependency of the logging abstractions package. | `Microsoft-dotnet-LICENSE`; `Microsoft-dotnet-THIRD-PARTY-NOTICES` |
| .NET / ASP.NET Core shared framework | 10.0 | MIT | SDK/runtime prerequisite and framework reference; it is not vendored in this source tree. | `Microsoft-dotnet-LICENSE`; `Microsoft-dotnet-THIRD-PARTY-NOTICES` |

## Verification against actual use

- `dotnet list <project> package --include-transitive` reports LiteDB 5.0.20,
  System.IO.Ports 10.0.0 and its platform runtime packages,
  Microsoft.Extensions.Logging.Abstractions 10.0.0, and
  Microsoft.Extensions.DependencyInjection.Abstractions 10.0.0, with no other
  NuGet packages for the seven exported projects.
- `Station.Engine.Hosting.csproj` explicitly copies the RNNoise model and WDSP
  `calculus` / `zetaHat.bin` data into build and publish output.
- `Zeus.Dsp.csproj` explicitly includes every `*.dylib`, `*.so*`, and `*.dll`
  below `Zeus.Dsp/runtimes/`; therefore even optional codec2 and RADE artifacts
  are part of the conveyed source tree and this inventory.
- Native dependency inspection confirms `libwdsp` requires both FFTW precisions
  on the checked Linux and macOS builds. The Windows ARM64 WDSP build links FFTW
  statically, so the same FFTW license still applies there.
- The WDSP build recipe statically embeds the vendored RNNoise and
  libspecbleach sources. The RADE build recipe identifies the pinned
  `radae_c`, Opus DNN, FreeDV reliable-text, and codec2 LDPC slices compiled
  into its shared library.
- Operating-system libraries shown by native dependency inspection are system
  components and are not copied into this repository.

The checked-in binaries are build conveniences. Corresponding-source and
relinking obligations for GPL/LGPL components remain governed by their license
texts and upstream source links below.

## Upstream source

- WDSP: <https://github.com/TAPR/OpenHPSDR-Thetis/tree/master/Project%20Files/Source/wdsp>
- FFTW: <https://www.fftw.org/>
- libspecbleach: <https://github.com/lucianodato/libspecbleach>
- RNNoise: <https://github.com/xiph/rnnoise>
- miniaudio: <https://github.com/mackron/miniaudio>
- codec2: <https://github.com/drowe67/codec2>
- RADE composition: <https://github.com/sv1eia/Thetis-RADE>
- LiteDB: <https://github.com/mbdavid/LiteDB>
- .NET: <https://github.com/dotnet/dotnet>
