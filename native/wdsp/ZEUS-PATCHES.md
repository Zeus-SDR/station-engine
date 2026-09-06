# WDSP 2.10 source and Zeus integration

WDSP revision **2.1.0**, dated **2026-09-04**, returns **210** from
`GetWDSPVersion`; the existing Zeus version formatter displays **2.10**.
KB2UKA authorized the upstream PureSignal calibration changes on 2026-09-05.

## Pinned sources

Warren Pratt's personal repository is https://github.com/NR0V/wdsp. His release
repository is https://github.com/TAPR/OpenHPSDR-wdsp. At import time the latter
still contained 2.00 at `584e8aca5ba1c4c6bc66fc0cc164ce567c8ba1e3`.

This upgrade uses the published 2.10 source mirror in
[the September 4 port](https://github.com/abhishekprakash22/zeus/tree/db86c1152d286705e820c0e008a0c6adc3bc5d69/native/wdsp),
which identifies its input as `2026-09-04 WDSP, ver 210.zip`. This is a ported
mirror, not a claim that TAPR has published that revision. Cross-checks used
[deskHPSDR's independent import](https://github.com/dl1bz/deskhpsdr/tree/e307f0fafe7b410a87c1eb1f2d5a909c6bcfa457/wdsp-2.10)
and [Warren's revision 2.1.0 manual](https://github.com/dl1bz/deskhpsdr/blob/e307f0fafe7b410a87c1eb1f2d5a909c6bcfa457/wdsp-2.10/WDSP_Guide__Rev_2_1_0.pdf).
The manual's revision history confirms the new neural noise reduction, PureSignal
calibration tuning, and filter-generation efficiency changes. Both ports carry
identical model tokens despite different formatting.

The source models in the first mirror have SHA-256:

- `nnr_model_0.c`: `af9174a63cd0683efed03feb97d45419ca52a881943c25153299b02aed607a98`
- `nnr_model_1.c`: `62593d935c5a7b4970fac988b76aa28d038e97f7b56a48480a3be7346749a331`

Upstream copyright, licence and acknowledgment notices remain intact. Trailing
whitespace may be removed when importing. Do not overwrite this directory
wholesale: Zeus's prior version is **2.00**, and its later fixes are required.

## Integration patches to preserve

- `CMakeLists.txt`, `linux_port.{c,h}`, `wdsp_export.h`: Zeus build, exports,
  POSIX thread/semaphore handling and aligned allocation. The Linux thread-name
  dispatcher declares `doPSCorrChange` locally because 2.10 moved its prototype
  out of the public calibrator header. New standalone NURBS
  and extrapolation code includes `linux_port.h` off Windows so allocation
  pointers are declared correctly. Implicit function declarations are errors.
- `comm.h`: existing platform includes, exports and NR3/NR4 headers, plus `nnr.h`.
  Keep the existing debug-helper rename in `utilities.h` and its portable output
  in `utilities.c`; these must not be replaced by the mirror's implementations.
- `RXA.{c,h}`, `rnnr.c`, `sbnr.c`: existing NR3/NR4 create, process, configure and
  destroy hooks coexist with upstream NNR. `RXAbp1CheckEx` includes NR3/NR4 in
  bandpass gating; the upstream-signature wrapper serves upstream callers.
  NNR is created disabled, as upstream specifies. Existing NR modes and defaults
  are unchanged; this dependency upgrade adds no new operator controls.
- `TXA.{c,h}`, `bandpass.{c,h}`, `fir.c`, `firmin.{c,h}`,
  `impulse_cache.{c,h}`: preserve Zeus's filter profile, minimum-phase and
  ultra-resolution changes from PRs #1350 and #1366. In particular, call
  `ApplyTXABandpassProfile` at the end of `TXASetupBPFilters`.
- `osctrl.{c,h}`, `tests/osctrl_tests.c`: preserve CESSB bandwidth control and
  deterministic tests from PR #1754, including `SetTXAosctrlBandwidth`.
- `calcc.c`: preserve the existing `psccF` float-IQ wrapper and per-calibrator
  staging buffers inside the now-private struct, allocate and release with the
  calibrator, and retain the realized `SetPSTXDelay` value. Do not import the
  mirror's separate global compatibility-buffer implementation.
- `wdsp.h`: retain Zeus's public compatibility declarations, update `GetPSDisp`
  to the new native signature, and add NNR/model and CFIR-curve declarations.
  Zeus does not call `GetPSDisp` from managed code.

All files unaffected by the upstream update retain Zeus's existing versions,
including analyzer behavior, wisdom ordering, NR3/NR4 support, and portability
fixes. `FDnoiseIQ.c` remains in the tree/build to avoid unrelated deletion; the
updated EMNR implementation no longer references it.

## Validation and future upgrades

Build from source for every packaged RID and refresh **all** committed WDSP
libraries together. Source exports alone do not prove the packaged binary.
`WdspRuntimeCompatibilityTests` loads the packaged runtime, verifies every
managed `LibraryImport` entry point (including aliases), requires version 210,
and checks both existing NR engines and the new NNR exports. Run the existing
DSP fixtures, the opt-in NR combination walk and CESSB CTest harness as well as
all repository gates. Check exported-symbol sets against the prior native build.

Hardware validation remains separate: automation uses synthetic audio/IQ and
never keys a transmitter. G2 RX/TX/PureSignal listening and calibration require
an operator; other boards and Raspberry Pi performance require their own bench.
