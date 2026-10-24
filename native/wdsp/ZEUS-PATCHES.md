# WDSP 2.11 source and Zeus integration

WDSP revision **2.1.1**, dated **2026-10-15**, returns **211** from
`GetWDSPVersion`; the existing Zeus version formatter displays **2.11**.
KB2UKA authorized the upstream PureSignal calibration changes of 2.10 on
2026-09-05 and the 2.11 `PSSaveCorr`/`PSRestoreCorr` filename bound on
2026-10-23 (no calibration change).

## Pinned sources

Warren Pratt's release repository is https://github.com/TAPR/OpenHPSDR-wdsp.
2.11 is imported from its `wdsp 2.11/Source` directory at
`e69de50d1d702216098019013899ad87534c64e0` (four commits of 2026-10-15 by
NR0V), merged three-way onto Zeus's tree with `wdsp 2.10/Source` at the same
revision as the base, so only Warren's 2.10 -> 2.11 changes were applied. The
manual is `wdsp 2.11/WDSP_Guide__Rev_2_1_1.pdf` in the same directory.

Before import the 2.10 -> 2.11 delta was audited line by line: no network,
process, registry, dynamic-loading or memory-protection calls anywhere in the
tree, licence headers unchanged (GPL v2 or later), and the manual PDF carries
no active content.

NNR model data (raw blob SHA-256, the bytes inside the C array):

- `nnr_model_0.c` (voice Standard): `e1ebfed6f522746b...`, byte-identical to 2.10
- `nnr_model_1.c` (voice Premium): `925fdb6830627d84...`, byte-identical to 2.10
- `nnr_model_2.c` (CW Standard): `bf8bae198ddd3b94c4a1473a4075fa7d28312c839c8f4009a83d7ea9f85090eb`
- `nnr_model_3.c` (CW Premium): `bf6facf2e039eee6b31f2c5eb015eb940ceb8fae3142898928db50ab5a960f67`

Zeus keeps its existing `nnr_model_0.c`/`nnr_model_1.c` files; 2.11 changed
only their text formatting. Upstream copyright, licence and acknowledgment
notices remain intact. Line endings are normalised to LF and trailing
whitespace may be removed when importing. Do not overwrite this directory
wholesale: Zeus's later fixes are required.

## 2.11 changes taken, and how

- NNR CW networks: `nnrbypass.{c,h}` (new; its Windows-only includes are
  replaced by `comm.h`), `nnr.{c,h}`, `nnet.{c,h}`, `nnio.c`,
  `nnr_model_2.c`, `nnr_model_3.c`. `NNET_NSLOTS` is 4. `SetRXANNRPosition`
  is deprecated upstream and has no effect; NNR placement follows the model.
- `RXA.c`: the training-band bypass path in `xrxa`, `RXAbp1Position`, and the
  new `RXAbp1Set`. Zeus NR3/NR4 are carried through: they count towards bp1's
  run and position like ANF/ANR/EMNR, their pre-AGC instances are skipped with
  the other pre-AGC blocks while the bypass runs, and `rnnr.c`/`sbnr.c` pass
  the bypass flag to `RXAbp1CheckEx` and call `RXAbp1Set` from their position
  setters instead of writing `bp1.position` directly.
- `SetChannelState` (channel.c) turn-on and already-off waits.
- Bounds checks: `nnio.c`, `emnr.c` (zetaHat.bin must be 60x60; Zeus's shipped
  file is), `nurbs_fit.c`, `nurbs_spline.c`, `siphon.c` (folded into Zeus's
  reader functions), `cfcomp.c`, `calcc.c`, `wisdom.c` (may now return -1),
  and Zeus's own `impulse_cache.c` reader (ported by hand: entry count, entry
  size, allocation failure, partial cache freed on error).
- Fixes: `emnr.c` getZeta bound (`i_xi`, not `xi_dB`; Trained gain read past
  the 3600-entry tables for a-priori SNR above +20.5 dB), `emph.c`/`fmd.c`
  NC setters (use-after-free; Zeus does not call them), `SetTXAPostGenNoiseMag`.
- Not taken: `wdsp_align.h` and its includes (Zeus's `linux_port.h` already
  maps `_aligned_malloc`; the inline definitions would collide with that
  macro), the 2.11 `wdsp.h` (Zeus keeps its own public header; the new export
  is declared there), and the reformatting of `destroy_rxa`/`flush_rxa`.

`CloseChannel`: the 2.11 README asks hosts to call `SetChannelState(ch, 0, 1)`
first so no flush runs while the channel is destroyed. Zeus satisfies the
condition without the call: in 2.11 a channel that is ON cannot have a flush
pending (turn-on waits for it), and a Zeus channel that is OFF was turned off
by a blocking `SetChannelState(id, 0, 1)` that completed or timed out
(clearing the flush flag). Adding the call after the worker has stopped would
cost the full 100-200 ms timeout per channel on every disconnect and rate
change, because no IQ is flowing then.

## Integration patches to preserve

- `cfcomp.c`, `wdsp.h`: selectable CFC FFT sizes via `SetTXACFCOMPFftSize`.
  Preserve the 16384-point default, operator profiles through workspace rebuilds,
  and the display getter's DSP lock through its complete array read. See
  [CFC FFT latency](../../docs/lessons/cfc-fft-latency.md).

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
  are unchanged. Managed code picks the NNR slot from the operator's
  Standard/Premium choice and the mode (CW networks in CWU/CWL only;
  `WdspDspEngine.NnrSlotFor`).
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
- `fm_tone.{c,h}`, `fmmod.{c,h}`, `fmd.{c,h}`, `emph.{c,h}`, `tests/fm_tests.c`:
  Zeus FM extension. TX: `SetTXACTCSSLevel`, DCS encode (`SetTXADCSRun`,
  `SetTXADCSCode`; Golay (23,12) codewords pinned against published vectors),
  `GetTXAFMDeviationPeak`, and `SetTXAFMEmphRun` (a `zeus_bypass` flag in
  `emphp`, independent of the run flag `SetTXAMode` drives). RX: a CTCSS / DCS
  decoder on the raw discriminator with a ramped tone squelch
  (`SetRXAFMToneSquelch`, `GetRXAFMToneStatus`) and `SetRXAFMDeemphRun`. The
  detector state lives outside `calc_fmd`/`decalc_fmd`. Managed callers probe
  these exports; they are optional in `WdspRuntimeCompatibilityTests`.
- `sip_cursor.{c,h}`, `siphon.{c,h}`, `tests/sip_cursor_tests.c`: Zeus
  contiguous siphon reader. `xsiphon` mode 0 counts written samples,
  `flush_siphon` resets the cursor under the siphon's update lock, and
  `TXAGetaSipBlocksF1` returns each block written since the previous call
  exactly once. The managed TX spectrum feed uses it because `TXAGetaSipF1`
  ("the newest N samples"), called from the fexchange thread, repeated or
  skipped blocks and drew a false floor outside the TX filter.
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
managed `LibraryImport` entry point (including aliases), requires version 211,
checks both existing NR engines and the NNR exports, and selects each of the
four NNR slots to prove the packaged library carries every network. Run the existing
DSP fixtures, the opt-in NR combination walk and CESSB CTest harness as well as
all repository gates. Check exported-symbol sets against the prior native build.

Hardware validation remains separate: automation uses synthetic audio/IQ and
never keys a transmitter. G2 RX/TX/PureSignal listening and calibration require
an operator; other boards and Raspberry Pi performance require their own bench.
