<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# station-engine

`station-engine` is a headless OpenHPSDR station process for Protocol 1 and
Protocol 2 radios. It provides radio transport, DSP, transmit control, local
audio, band logic, safety services, and a versioned HTTP/WebSocket station
protocol. It does not include a user interface.

The source is maintained by Douglas J. Cerrato (KB2UKA) and Christian Suarez
(N9WAR).

## License

The station engine binary is conveyed under the **GNU General Public License,
version 3 or (at your option) any later version**. The full text is in
[`LICENSE`](LICENSE), which also states the scope summarised here; provenance
and per-component attribution are in [`ATTRIBUTIONS.md`](ATTRIBUTIONS.md);
third-party components and their preserved license texts are inventoried in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

Most first-party engine code is licensed **GPL-2.0-or-later**, and those files
individually remain available under that license; the version 2 text is in
[`LICENSE.GPL-2.0`](LICENSE.GPL-2.0). The SPE Expert 1.5K Taurus amplifier
support under `Station.Engine.Hosting/SpeTaurus/` is **GPL-3.0-or-later** (its
provenance is recorded in
[`Station.Engine.Hosting/SpeTaurus/SOURCE.md`](Station.Engine.Hosting/SpeTaurus/SOURCE.md)).
Because the engine combines both, the distributed binary is GPL-3.0-or-later —
permitted by the "or later" option on the GPL-2.0-or-later portions.

This repository is the **complete corresponding source** for the station engine
binary distributed with Zeus SDR. Each release tag here matches the engine
shipped in the corresponding Zeus release.

## Native libraries

The native source used by the station engine is included under `native/`:
Zeus-modified WDSP, its statically embedded libspecbleach and RNNoise sources,
miniaudio, the pinned codec2 fetch recipe and patch, and the RADE build glue,
shim, and pinned upstream provenance. Artifact-to-source mapping and the exact
per-platform build commands are in [`NATIVE-BUILD.md`](NATIVE-BUILD.md).
The proprietary VST3 and Audio Unit bridge binaries are not part of the station
engine and are not included; the published tree builds against
`Zeus.Plugins.VstHostStub` instead.

`Station.AudioRing` is first-party code additionally published under the MIT
license so it can be reused outside Zeus. Third-party components carry their own
licences — see [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

The Zeus SDR client that drives this engine is a separate, proprietary program
communicating over the loopback station protocol documented below. It is not
part of this repository and is not covered by this license.

## Requirements

- .NET 10 SDK
- macOS, Windows, or Linux on a supported .NET architecture

The checked-in runtime directories provide the native libraries used by the
supported release targets. Raspberry Pi and other Linux ARM64 systems use the
`linux-arm64` runtime assets.

## Build

From the repository root:

```sh
dotnet restore StationEngine/StationEngine.csproj
dotnet build StationEngine/StationEngine.csproj
```

## Run

The engine requires a loopback TCP port:

```sh
dotnet run --project StationEngine/StationEngine.csproj -- --port 6060
```

For a published native executable, pass the same argument directly:

```sh
./StationEngine --port 6060
```

On Windows, run `StationEngine.exe --port 6060`.

The process listens only on `127.0.0.1`. A successful startup exposes protocol
discovery at `http://127.0.0.1:6060/api/station/version`.

## Station Protocol v1

The implementation is the authoritative protocol reference:

- `Station.Engine.Hosting/StationProtocolEndpoints.cs` defines version
  discovery.
- `Station.Engine.Hosting/StationEngineEndpoints.cs` registers the HTTP and
  WebSocket surface.
- `Station.Engine.Hosting/StreamingHub.cs` implements `/ws` framing and
  session behavior.
- The contracts project defines binary frame layouts, message identifiers,
  DTOs, and wire helpers.
