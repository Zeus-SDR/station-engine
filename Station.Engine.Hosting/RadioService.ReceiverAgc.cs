// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;

namespace Zeus.Server;

public sealed partial class RadioService
{
    // One DspSettingsStore row per RX2+ receiver holds its AGC and noise block.
    private static string ReceiverDspProfile(int index) => $"receiver-{index}";
    private AgcConfig LoadReceiverAgc(int index) => NormalizeAgcConfig(
        _dspSettingsStore.GetAgc(ReceiverDspProfile(index)) ?? _dspSettingsStore.GetAgc() ?? new AgcConfig());
    private double LoadReceiverAgcTop(int index) => Math.Clamp(
        _dspSettingsStore.GetAgcTopDb(ReceiverDspProfile(index)) ?? _dspSettingsStore.GetAgcTopDb() ?? 90.0,
        MinAgcTopDb, MaxAgcTopDb);

    internal static StateDto ReceiverAgcState(StateDto state, int index)
    {
        if (index == 0) return state;
        var receiver = state.Receivers?.FirstOrDefault(r => r.Index == index);
        return state with
        {
            Agc = receiver?.Agc ?? new AgcConfig(),
            AgcTopDb = receiver?.AgcTopDb ?? 90.0,
            AutoAgcEnabled = receiver?.AutoAgcEnabled ?? false,
            AgcOffsetDb = receiver?.AgcOffsetDb ?? 0.0,
            FilterLowHz = receiver?.FilterLowHz ?? state.FilterLowHz,
            FilterHighHz = receiver?.FilterHighHz ?? state.FilterHighHz,
        };
    }

    private readonly long[] _receiverAgcTicks = Enumerable.Repeat(long.MinValue, 16).ToArray();
    private readonly double[] _receiverAgcSeats = Enumerable.Repeat(double.NaN, 16).ToArray();

    private void SetReceiverAgc(int index, AgcConfig? config, double? top, bool? auto)
    {
        if (index < 0 || index >= _extraReceivers.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (config is not null && (!Enum.IsDefined(config.Mode) || (config.FixedGainDb is double fixedGain && !double.IsFinite(fixedGain)))) throw new ArgumentException("invalid AGC configuration", nameof(config));
        if (top is double value && !double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(top));
        if (index == 0)
        {
            if (config is not null) SetAgc(config);
            if (top is double gain) SetAgcTop(gain);
            if (auto is bool enabled) SetAutoAgc(enabled);
            return;
        }
        AgcConfig? persisted = null;
        double persistedTop = 90.0;
        bool persistedAuto = false;
        Mutate(s =>
        {
            var current = index == 1 ? s.Rx2() : new ReceiverDto(index, false, 0, 0, RxMode.USB, 100, 2850, null, 0, s.SampleRate,
                Agc: _extraReceivers[index].Agc, AgcTopDb: _extraReceivers[index].AgcTopDb,
                AutoAgcEnabled: _extraReceivers[index].AutoAgcEnabled, AgcOffsetDb: _extraReceivers[index].AgcOffsetDb);
            var next = current;
            if (config is not null) next = next with { Agc = NormalizeAgcConfig(config), AgcOffsetDb = 0.0 };
            if (top is double gain)
            {
                var agc = next.Agc ?? new AgcConfig();
                double clamped = Math.Clamp(gain, agc.Mode == AgcMode.Fixed ? MinAgcFixedGainDb : MinAgcTopDb, MaxAgcTopDb);
                next = agc.Mode == AgcMode.Fixed
                    ? next with { Agc = NormalizeAgcConfig(agc with { FixedGainDb = clamped }) }
                    : next with { AgcTopDb = clamped };
                next = next with { AutoAgcEnabled = false, AgcOffsetDb = 0.0 };
            }
            if (auto is bool enabled) next = next with { AutoAgcEnabled = enabled, AgcOffsetDb = 0.0 };
            _receiverAgcTicks[index] = long.MinValue;
            _receiverAgcSeats[index] = double.NaN;
            persisted = next.Agc ?? new AgcConfig();
            persistedTop = next.AgcTopDb;
            persistedAuto = next.AutoAgcEnabled;
            if (index == 1) return WithRx2(s, _ => next);
            var extra = _extraReceivers[index];
            extra.Agc = persisted;
            extra.AgcTopDb = persistedTop;
            extra.AutoAgcEnabled = persistedAuto;
            extra.AgcOffsetDb = next.AgcOffsetDb;
            return s;
        });
        string profile = ReceiverDspProfile(index);
        _dspSettingsStore.SetAgc(persisted!, profile);
        _dspSettingsStore.SetAgcTopDb(persistedTop, profile);
        _dspSettingsStore.SetReceiverAutoAgc(persistedAuto, profile);
    }

    internal void HandleReceiverAutoAgc(int index, double floorDbm, long nowMs)
    {
        if (index <= 0 || index >= _extraReceivers.Length || !double.IsFinite(floorDbm) || floorDbm <= -250.0) return;
        Mutate(s =>
        {
            var agcState = ReceiverAgcState(s, index);
            if (_mox || !agcState.AutoAgcEnabled || (_receiverAgcTicks[index] != long.MinValue && nowMs - _receiverAgcTicks[index] < 500)) return null;
            _receiverAgcTicks[index] = nowMs;
            double raw = AutoAgcRawTopFromNoiseFloor(floorDbm, agcState);
            if (!double.IsFinite(_receiverAgcSeats[index]) || Math.Abs(raw - _receiverAgcSeats[index]) >= AgcDeadbandDb)
                _receiverAgcSeats[index] = raw;
            double offset = Math.Clamp(Math.Round(_receiverAgcSeats[index]), AgcTopMinDb, AgcTopMaxDb) - AgcBaseline(agcState);
            if (offset == agcState.AgcOffsetDb) return null;
            if (index == 1) return WithRx2(s, r => r with { AgcOffsetDb = offset });
            _extraReceivers[index].AgcOffsetDb = offset;
            return s;
        }, out _);
    }
}
