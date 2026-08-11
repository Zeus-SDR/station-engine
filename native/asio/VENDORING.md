<!-- SPDX-License-Identifier: GPL-3.0-only -->

# Steinberg ASIO SDK vendoring

Zeus builds its Windows-only `zeus_asio.dll` host shim against the ASIO SDK
2.3.4 interface headers. The project elects the GNU GPL version 3 option in
Steinberg's dual license. No proprietary-license agreement, sample driver,
logo, artwork, or branding asset is included.

Pinned acquisition:

- Landing URL: <https://www.steinberg.net/asiosdk>
- Resolved archive: <https://download.steinberg.net/sdk_downloads/ASIO-SDK_2.3.4_2025-10-15.zip>
- Archive size: `8910208` bytes
- SHA-256: `D5EBF0C20DD2C5F43771FD0C1418F4B361BF52434EE670097CFA6B3A335E2ECA`

Only these unmodified files are vendored:

- `vendor/asiosdk-2.3.4/LICENSE.txt`
- `vendor/asiosdk-2.3.4/common/asio.h`
- `vendor/asiosdk-2.3.4/common/asiosys.h`
- `vendor/asiosdk-2.3.4/common/iasiodrv.h`

The Steinberg host helpers are intentionally not used. `zeus_asio.cpp` owns
driver registry enumeration, COM lifetime, format conversion, synchronization,
and the narrow exported C ABI. This avoids carrying obsolete sample projects
or a second public ABI into the product.

To verify a refresh, download the resolved archive, check its SHA-256, and
compare the four files byte-for-byte before replacing them. Do not add the
SDK's proprietary agreement PDF or logo directory.
