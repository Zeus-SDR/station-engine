// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;

namespace Zeus.Server;

public sealed partial class RadioService
{
    // A receiver's own noise block: NB, NB threshold, ANF, SNB, NBP and every NR
    // tunable, with that receiver's NR mode. RX1 is StateDto.Nr. RX2+ carry theirs
    // on Receivers[i].Nr; a frame without one (legacy) falls back to the shared
    // block, the behaviour before per-receiver noise. Every consumer reads this.
    public static NrConfig ReceiverNrConfig(StateDto s, int index)
    {
        var shared = s.Nr ?? new NrConfig();
        if (index == 0) return NormalizeNrConfig(shared);
        var receiver = s.Receivers?.FirstOrDefault(r => r.Index == index);
        return NormalizeNrConfig(receiver is null ? shared : (receiver.Nr ?? shared) with { NrMode = receiver.NrMode });
    }

    // Seed for a receiver that has never had its own noise block saved: the shared
    // block it used before, so nothing an operator hears changes on upgrade.
    private NrConfig LoadReceiverNr(int index, NrConfig shared) =>
        NormalizeNrConfig(_dspSettingsStore.GetReceiverNr(ReceiverDspProfile(index)) ?? shared);

    // Replace one receiver's noise block. The NR mode is not part of this write:
    // it is set only through SetReceiver(nrMode), so the receiver keeps its own.
    private void SetReceiverNr(int index, NrConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Enum.IsDefined(config.NbMode)) throw new ArgumentException($"unknown NbMode {config.NbMode}", nameof(config));
        UpdateReceiverNr(index, _ => config);
    }

    // The NR settings popovers patch single knobs. The merge runs inside the
    // state mutation, on the receiver's own stored block (a disabled RX3+ is
    // absent from the projected Receivers[]), so two patches can't lose each
    // other's fields.
    private StateDto SetReceiverNrPatch(int? receiverIndex, Func<NrConfig, NrConfig> patch)
    {
        UpdateReceiverNr(receiverIndex ?? 0, patch);
        return Snapshot();
    }

    private void UpdateReceiverNr(int index, Func<NrConfig, NrConfig> update)
    {
        if (index < 0 || index >= _extraReceivers.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == 0)
        {
            Mutate(s => s with { Nr = NormalizeNrConfig(update(s.Nr ?? new NrConfig()) with { NrMode = Rx1NrMode(s) }) });
            _dspSettingsStore.Upsert(Snapshot().Nr ?? new NrConfig());
            return;
        }
        NrConfig stored = new();
        Mutate(s =>
        {
            if (index == 1)
            {
                return WithRx2(s, r =>
                {
                    stored = NormalizeNrConfig(update(r.Nr ?? s.Nr ?? new NrConfig()) with { NrMode = r.NrMode });
                    return r with { Nr = stored };
                });
            }
            var extra = _extraReceivers[index];
            stored = NormalizeNrConfig(update(extra.Nr) with { NrMode = extra.NrMode });
            extra.Nr = stored;
            return s;
        });
        _dspSettingsStore.SetReceiverNr(stored, ReceiverDspProfile(index));
    }
}
