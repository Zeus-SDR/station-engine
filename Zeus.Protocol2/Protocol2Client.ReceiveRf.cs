// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Protocol2;

public sealed record ReceiveRfDemand(byte PhysicalAdcSource, string Role, int ReceiverIndex, uint HardwareCenterHz);
public sealed record ReceiveRfBank(byte PhysicalAdcSource, uint HardwareCenterHz, string FilterKey,
    string Reason, IReadOnlyList<ReceiveRfDemand> Demands);
public sealed record ReceiveRfSnapshot(uint Rx1HardwareHz, uint Rx2HardwareHz, uint TxHardwareHz,
    bool TxActive, IReadOnlyList<ReceiveRfBank> Banks);

public sealed partial class Protocol2Client
{
    private const uint ReceiveFilterMask = 0x0000107e;
    private const uint LowPassFilterMask = 0xe0f00000;
    private ReceiveRfSnapshot? _receiveRfSnapshot;

    /// <summary>The filter selections in the most recently composed command, using hardware NCO centers.</summary>
    public bool SupportsRx6mLnaControl => _boardKind == HpsdrBoardKind.OrionMkII
        && _variant is OrionMkIIVariant.G2 or OrionMkIIVariant.G2_1K
        && (_rfFilterBoardKind is null or HpsdrBoardKind.OrionMkII);

    public ReceiveRfSnapshot? ReceiveFilters => Volatile.Read(ref _receiveRfSnapshot);

    public bool TryGetGuestAdcSource(int slot, out byte adcSource)
    {
        var placement = ActiveGuestPlacement();
        for (int i = 0; i < placement.Count; i++)
        {
            if (placement.Guests[i].Slot != slot) continue;
            adcSource = placement.Guests[i].Adc;
            return true;
        }
        adcSource = 0;
        return false;
    }

    private ReceiveRfDemand[] CollectReceiveRfDemands(bool diversityPair)
    {
        var demands = new List<ReceiveRfDemand>();
        if (diversityPair)
        {
            demands.Add(new(0, "primary", 0, _rxFreqHz));
            demands.Add(new((byte)Volatile.Read(ref _diversitySourceAdcSource), "diversity", 0, _rxFreqHz));
        }
        else
            demands.Add(new((byte)Volatile.Read(ref _rx1AdcSource), "primary", 0, _rxFreqHz));
        int extraCount = Volatile.Read(ref _extraReceiverCount);
        if (Volatile.Read(ref _rx2Enabled) != 0 || extraCount > 0)
            demands.Add(new((byte)Volatile.Read(ref _rx2AdcSource), "receiver", 1, _rx2FreqHz));
        for (int i = 0; i < extraCount; i++)
            demands.Add(new(_extraRxAdc[2 + i], "receiver", 2 + i, _extraRxFreqHz[2 + i]));
        var guests = ActiveGuestPlacement();
        for (int i = 0; i < guests.Count; i++)
            demands.Add(new(guests.Guests[i].Adc, "guest", guests.Guests[i].Slot, guests.Guests[i].FreqHz));
        if (EffectiveDisplayDdcIndex() >= 0)
            demands.Add(new((byte)Volatile.Read(ref _displayDdcAdcSource), "display", DisplayReceiverIndex, _displayDdcFreqHz));
        return demands.ToArray();
    }

    private (uint Bits, string Reason) ResolveReceiveBank(HpsdrBoardKind board, ReceiveRfDemand[] demands, uint fallbackHz, byte adc = 0)
    {
        bool classic = IsClassicAlexBoard(board);
        uint Select(uint hz) => classic
            ? BpfBitsClassicAlex(hz, _rfFilters, _rfFilters?.RxBypassAll == true)
            : BpfBitsAnan7000(hz, _rfFilters, _rfFilters?.RxBypassAll == true);
        bool disableLna = adc == 0 ? _rfFilters?.Adc0Rx6mLnaDisabled == true : _rfFilters?.Adc1Rx6mLnaDisabled == true;
        (uint Bits, string Reason) ApplyLnaOverride(uint bits, string reason) =>
            SupportsRx6mLnaControl && disableLna && bits == ALEX_ANAN7000_RX_6_PRE_BPF
                ? (ALEX_ANAN7000_RX_BYPASS_BPF, "receive-6m-lna-bypass") : (bits, reason);
        if (demands.Length == 0) return ApplyLnaOverride(Select(fallbackHz), "inactive-bank-fallback");
        if (classic) return (Select(demands.Min(d => d.HardwareCenterHz)), "shared-hpf-lowest-center");
        uint[] selections = demands.Select(d => Select(d.HardwareCenterHz)).Distinct().ToArray();
        if (selections.Length > 1) return (ALEX_ANAN7000_RX_BYPASS_BPF, "conflicting-centers-bypass");
        return ApplyLnaOverride(selections[0], _rfFilters?.RxBypassAll == true ? "operator-bypass" : "compatible-centers");
    }

    private (uint Adc0FallbackHz, uint Adc1FallbackHz, string Adc0Reason, string Adc1Reason) ApplyReceiveRfBanks(HpsdrBoardKind board, ReceiveRfDemand[] demands, ref uint alex0, ref uint alex1)
    {
        var bank0 = demands.Where(d => d.PhysicalAdcSource == 0).ToArray();
        var bank1 = demands.Where(d => d.PhysicalAdcSource == 1).ToArray();
        uint adc0FallbackHz = _rxFreqHz;
        uint adc0Preserved = alex0 & ~ReceiveFilterMask;
        var adc0Selection = ResolveReceiveBank(board, bank0, adc0FallbackHz);
        alex0 = adc0Preserved | adc0Selection.Bits;
        uint adc1FallbackHz = Volatile.Read(ref _rx2Enabled) != 0 ? _rx2FreqHz : _rxFreqHz;
        uint adc1Preserved = alex1 & ~ReceiveFilterMask;
        var adc1Selection = ResolveReceiveBank(board, bank1, adc1FallbackHz, 1);
        alex1 = adc1Preserved | adc1Selection.Bits;
        if (IsClassicAlexBoard(board) && demands.Length > 0)
        {
            uint sharedLpf = LpfBits(demands.Max(d => d.HardwareCenterHz), _rfFilters);
            alex0 = (alex0 & ~LowPassFilterMask) | sharedLpf;
            alex1 = (alex1 & ~LowPassFilterMask) | sharedLpf;
        }
        return (adc0FallbackHz, adc1FallbackHz, adc0Selection.Reason, adc1Selection.Reason);
    }

    private ReceiveRfSnapshot BuildReceiveRfSnapshot(HpsdrBoardKind board, ReceiveRfDemand[] demands,
        uint alex0, uint alex1, bool transmitting, bool feedbackArmed, bool rx2Enabled,
        uint alex0RxCenterHz, uint alex1Rx1CenterHz, uint alex1Rx2CenterHz, uint txDucCommandHz,
        uint? receiveAdc0FallbackHz, uint? receiveAdc1FallbackHz,
        string? receiveAdc0Reason, string? receiveAdc1Reason)
    {
        var banks = new List<ReceiveRfBank>();
        for (byte adc = 0; adc < Math.Min(2, (int)_numAdc); adc++)
        {
            var allocated = demands.Where(d => d.PhysicalAdcSource == adc).ToArray();
            uint fallback = adc == 0 ? alex0RxCenterHz : rx2Enabled ? alex1Rx2CenterHz : alex1Rx1CenterHz;
            if (!transmitting && !feedbackArmed)
                fallback = (adc == 0 ? receiveAdc0FallbackHz : receiveAdc1FallbackHz) ?? fallback;
            string reason = transmitting ? "transmit-relay-policy" : feedbackArmed ? "feedback-relay-policy" :
                (adc == 0 ? receiveAdc0Reason : receiveAdc1Reason) ?? "unknown";
            uint bits = (adc == 0 ? alex0 : alex1) & ReceiveFilterMask;
            string[] keys = IsClassicAlexBoard(board)
                ? ["bypass", "1_5", "6_5", "9_5", "13", "20", "6_pre"]
                : ["bypass", "160", "80_60", "40_30", "20_15", "12_10", "6_pre"];
            string key = keys.FirstOrDefault(k => (IsClassicAlexBoard(board) ? ClassicAlexRxKeyToBits(k) : Anan7000RxKeyToBits(k)) == bits)
                ?? "unknown";
            uint selectedCenter = transmitting || feedbackArmed || allocated.Length == 0
                ? fallback : allocated.Min(d => d.HardwareCenterHz);
            banks.Add(new(adc, selectedCenter,
                key, reason, allocated));
        }
        return new(alex0RxCenterHz, alex1Rx2CenterHz, txDucCommandHz, transmitting, banks.ToArray());
    }
}
