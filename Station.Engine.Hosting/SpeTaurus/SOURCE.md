<!-- SPDX-License-Identifier: GPL-3.0-or-later -->
<!-- Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
     Christian Suarez (N9WAR), and contributors. -->

# SPE Expert 1.5K Taurus — source and licensing provenance

This source ships in the public Zeus Station Engine under
**GPL-3.0-or-later**. Review found substantial source overlap with the
GPL-3.0-or-later Taurus desktop implementation, which was itself informed by
`netjordan/spe-expert-remote`; the earlier independent/clean-room claim was
withdrawn. Relocating the implementation out of the proprietary ZeusProduct
and into the separately distributed GPL station engine resolves the prior
licensing hold through the approved GPL distribution path described as option
3 below. Because the rest of the engine is GPL-2.0-or-later, its "or later"
option permits this combination and the resulting engine binary is distributed
as GPL-3.0-or-later.

The licensing review identified these valid resolution paths:

1. a valid dual-license/assignment covering every relevant contribution and
   upstream-derived portion;
2. a genuinely independent implementation with auditable separation and a
   completed similarity/provenance review; or
3. a separately distributed GPL process with complete corresponding source
   and notices, connected through an approved process boundary.

## Public specifications used

- SPE Application Programmer's Guide, revision 1.1 (15 October 2015):
  <https://www.spetlc.com/images/download/SPE_Application_Programmers_Guide.pdf>
  — packet synchronization, byte counts, checksums, command codes, serial
  settings, and the 19-field status record.
- SPE Taurus download page:
  <https://www.spetlc.com/en/download-taurus-uk.html>
  — current manufacturer entry point for the Taurus manual and programmer
  guide. Zeus links to the documents and does not redistribute them.
- Public July 2025 report of SPE's model-field clarification:
  <https://g0rvm.uk/spe-expert-amplifiers-programmers-guide/>
  — `13K`, `15K`, `15T`, and `20K` identifiers; `15T` identifies Taurus.
- FTDI D2XX Programmer's Guide, version 1.6 (document FT_000071):
  <https://ftdichip.com/wp-content/uploads/2025/06/D2XX_Programmers_Guide.pdf>
  — device enumeration, exact-serial open, UART configuration, queue, read,
  write, purge, close, and status codes.
- FTDI D2XX driver downloads:
  <https://ftdichip.com/drivers/d2xx-drivers/>
  — optional operator-installed runtimes for supported operating systems and
  architectures.

## Deliberate boundaries

- The implementation lives only in `Station.Engine.Hosting/SpeTaurus/` and is
  excluded from the proprietary `ZeusProduct/` source and binary.
- No SPE terminal executable, FTDI binary/header/installer, vendor PDF, or
  firmware image is stored or shipped here.
- D2XX loads only a current runtime installed by the operator. Zeus never
  unloads kernel modules, resets/cycles USB devices, writes EEPROM, or enables
  FTDI bit-bang modes.
- CAT is a separate radio-to-amplifier frequency link. It is not implemented
  as a Zeus-to-amplifier transport and no automatic band command is sent.

The local `Term_1.5K_Taurus` package may be used for attended interoperability
comparison on owner-controlled hardware, but its executable and implementation
are not inputs to, or artifacts of, this source tree.
