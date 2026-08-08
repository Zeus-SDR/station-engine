<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Prominent RADE Codec2 notice and LGPL-2.1 section 3 election

The conveyed `zeus_rade` shared library incorporates these five Codec2 LDPC
source units for the FreeDV reliable-text end-of-over path:

- `gp_interleaver.c`
- `ldpc_codes.c`
- `HRA_56_56.c`
- `mpdecode_core.c`
- `phi0.c`

Their original authorship and license provenance remain visible in the
corresponding source. `gp_interleaver.c` expressly grants LGPL version 2.1
only. The Iterative Solutions portion of `mpdecode_core.c` grants LGPL version
2.1 or, at the recipient's option, a later version. `HRA_56_56.c`,
`ldpc_codes.c`, and `phi0.c` have no per-file grant and inherit the GNU Lesser
General Public License version 2.1 from Codec2 1.2.0, commit
`06d4c11e699b0351765f10398abb4f663a984f36`. The conservative composite
disposition of the five-unit set is therefore **LGPL-2.1-only**.

For the exact copies of those five units incorporated into each conveyed
`zeus_rade` binary, the maintainers exercise the election in section 3 of the
GNU Lesser General Public License version 2.1 and apply the terms of GNU
General Public License version 2 or later instead. The resulting combined
native work is distributed with the Station Engine under GNU GPL version 3 or
later. This election does not remove or obscure the units' original
authorship, notices, or LGPL provenance, and it does not relicense upstream
copies outside this conveyed combination.

The complete LGPL-2.1 text is preserved as
`THIRD-PARTY-LICENSES/Codec2-COPYING`; the GPL version 2 and version 3 texts are
preserved as `LICENSE.GPL-2.0` and `LICENSE.GPL-3.0`.

The matching source release materializes the exact pinned `radae_c`,
`opus_dnn`, and `freedv_text` slices under `native/radae/vendor/`. Its
`native/radae/vendor/BINARY-SOURCE-BINDING.json` records the Thetis-RADE and
Opus pins, slice tree/content hashes, each RID-specific `zeus_rade` SHA-256,
and the local shim and CMake input hashes. `SOURCE-OFFER.json` in each binary
archive gives the exact release tag and archive URL. Rebuild instructions are
in `NATIVE-BUILD.md` and `native/radae/VENDORING.md` in that matching source
release.
