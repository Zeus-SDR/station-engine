<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# station-engine

`station-engine` is a headless OpenHPSDR station process for Protocol 1 and
Protocol 2 radios. It provides radio transport, DSP, transmit control, local
audio, band logic, safety services, and a versioned HTTP/WebSocket station
protocol. It does not include a user interface.

The source is maintained by Douglas J. Cerrato (KB2UKA) and Christian Suarez
(N9WAR).

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

## License

First-party source is licensed under
[`GPL-2.0-or-later`](LICENSE). Bundled dependency licenses and the audited
component inventory are in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
