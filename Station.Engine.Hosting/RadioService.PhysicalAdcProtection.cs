// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net.Sockets;
using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Protocol2;

namespace Zeus.Server;

public sealed partial class RadioService
{
    private readonly PhysicalAdcProtectionController[] _physicalProtection = [new(), new()];
    private readonly int[] _physicalAttenBaselineDb = [0, 0];

    private StateDto ProjectPhysicalPrimaryNoLock(StateDto state)
    {
        int adc = PrimaryAttenuationAdcNoLock(state);
        _atten = new HpsdrAtten(_physicalAttenBaselineDb[adc]);
        _attOffsetDb = _physicalProtection[adc].OffsetDb;
        _adcOverloadLevel = _physicalProtection[adc].OverloadLevel;
        return state with { AttenDb = _atten.ClampedDb, AttOffsetDb = _attOffsetDb,
            AdcOverloadWarning = _physicalProtection[adc].Warning };
    }

    private void ResetPhysicalProtectionNoLock()
    {
        foreach (var controller in _physicalProtection) controller.Reset();
    }

    public int EffectivePhysicalAdcAttenuationDb(byte adc)
    {
        lock (_sync)
        {
            if (adc > 1) return 0;
            if (_p2Client is null)
                return ReceiverAdcSource(_state, 0) == adc ? EffectiveAttenDb : 0;
            return _physicalProtection[adc].EffectiveDb(_physicalAttenBaselineDb[adc]);
        }
    }

    /// <summary>Replay both physical attenuators without clearing the other antenna input.</summary>
    public void ApplyPhysicalAttenuatorsToP2Client(Protocol2Client? client)
    {
        if (client is null) return;
        lock (_sync)
        {
            client.SetAttenuator(_physicalProtection[0].EffectiveDb(_physicalAttenBaselineDb[0]));
            if (BoardCapabilitiesTable.For(_p2BoardKind, EffectiveOrionMkIIVariant).RxAdcCount > 1)
                client.SetRx1Attenuator(_physicalProtection[1].EffectiveDb(_physicalAttenBaselineDb[1]));
        }
    }

    private int PrimaryAttenuationAdcNoLock(StateDto state)
    {
        byte count = (byte)Math.Clamp(BoardCapabilitiesTable.For(EffectiveBoardKind, EffectiveOrionMkIIVariant).RxAdcCount, 1, 2);
        bool synchronizedPair = _p2Client is not null && Protocol2Client.UsesDiversityPair(
            state.Diversity?.Enabled == true, state.PsEnabled, _mox || _tunActive, _p2BoardKind, count);
        return synchronizedPair ? 0 : ReceiverAdcSource(state, 0) == 1 ? 1 : 0;
    }

    private byte ActualPrimaryPhysicalAdcNoLock() => _p2Client?.ReceiveFilters?.Banks
        .FirstOrDefault(b => b.Demands.Any(d => d.Role == "primary"))?.PhysicalAdcSource
        ?? ReceiverAdcSource(_state, 0);

    private bool[] ActivePhysicalReceiveAdcsNoLock()
    {
        bool[] active = [false, false];
        if (_p2Client?.ReceiveFilters is { } rf)
            foreach (var bank in rf.Banks)
                if (bank.PhysicalAdcSource < 2 && bank.Demands.Count > 0) active[bank.PhysicalAdcSource] = true;
        if (!active[0] && !active[1]) active[ReceiverAdcSource(_state, 0) == 1 ? 1 : 0] = true;
        return active;
    }

    private PhysicalAdcProtectionStatusDto[] BuildPhysicalAdcStatusNoLock()
    {
        bool[] active = ActivePhysicalReceiveAdcsNoLock();
        bool fresh = _lastAdcTelemetryUtc is { } timestamp
            && DateTimeOffset.UtcNow - timestamp <= TimeSpan.FromMilliseconds(2500);
        int count = Math.Clamp(BoardCapabilitiesTable.For(EffectiveBoardKind, EffectiveOrionMkIIVariant).RxAdcCount, 1, 2);
        var result = new PhysicalAdcProtectionStatusDto[count];
        for (byte adc = 0; adc < count; adc++)
        {
            ushort? magnitude = adc == 0 ? _lastAdc0MaxMagnitude : _lastAdc1MaxMagnitude;
            double? peak = fresh && magnitude is > 0 ? 20 * Math.Log10(magnitude.Value / 32767.0) : null;
            var controller = _physicalProtection[adc];
            bool independent = _p2Client is not null;
            int baseline = independent ? _physicalAttenBaselineDb[adc] : ReceiverAdcSource(_state, 0) == adc ? _atten.ClampedDb : 0;
            int offset = independent ? controller.OffsetDb : ReceiverAdcSource(_state, 0) == adc ? _attOffsetDb : 0;
            result[adc] = new(adc, baseline, offset, Math.Clamp(baseline + offset, 0, 31),
                _state.AutoAttEnabled && active[adc], active[adc], independent ? controller.Warning : ReceiverAdcSource(_state, 0) == adc && _state.AdcOverloadWarning,
                independent ? controller.OverloadLevel : ReceiverAdcSource(_state, 0) == adc ? _adcOverloadLevel : 0,
                (_lastAdcOverloadBits & (1 << adc)) != 0, magnitude, fresh, peak, peak.HasValue ? -peak : null);
        }
        return result;
    }

    private void HandlePhysicalAdcProtection(byte overloadBits, ushort? adc0Magnitude, ushort? adc1Magnitude,
        bool transmitterActive, long nowMs)
    {
        bool publish = false;
        lock (_sync)
        {
            _lastAdcOverloadBits = overloadBits;
            _lastAdc0MaxMagnitude = adc0Magnitude;
            _lastAdc1MaxMagnitude = adc1Magnitude;
            _lastAdcTelemetryUtc = DateTimeOffset.UtcNow;
            if ((overloadBits & 1) != 0 && adc0Magnitude is ushort m0) _adc0MaxMagnitudeAtOverload = m0;
            if ((overloadBits & 2) != 0 && adc1Magnitude is ushort m1) _adc1MaxMagnitudeAtOverload = m1;
            if (_mox || transmitterActive || (_adcProtectionResumeAfterMs != long.MinValue && nowMs < _adcProtectionResumeAfterMs))
            {
                foreach (var controller in _physicalProtection) controller.ResetWindowForTx();
                return;
            }
            _adcProtectionResumeAfterMs = long.MinValue;
            var active = ActivePhysicalReceiveAdcsNoLock();
            for (byte adc = 0; _state.AutoAttEnabled && adc < 2; adc++)
            {
                if (!active[adc]) continue;
                var controller = _physicalProtection[adc];
                int before = controller.EffectiveDb(_physicalAttenBaselineDb[adc]);
                bool warned = controller.Warning;
                controller.Observe(_adcProtection, _physicalAttenBaselineDb[adc], (overloadBits & (1 << adc)) != 0,
                    adc == 0 ? adc0Magnitude : adc1Magnitude, nowMs);
                int effective = controller.EffectiveDb(_physicalAttenBaselineDb[adc]);
                publish |= effective != before || warned != controller.Warning;
                if (effective == before || _p2Client is not { } client) continue;
                try { ApplyPrimaryAttenuatorToProtocol2Client(client, adc, effective); }
                catch (SocketException ex) { _log.LogDebug(ex, "p2.auto_att physical ADC send raced socket teardown"); }
                catch (ObjectDisposedException ex) { _log.LogDebug(ex, "p2.auto_att physical ADC send raced disposal"); }
            }
            var projected = ProjectPhysicalPrimaryNoLock(_state);
            publish |= projected.AttenDb != _state.AttenDb || projected.AttOffsetDb != _state.AttOffsetDb || projected.AdcOverloadWarning != _state.AdcOverloadWarning;
            _state = projected;
        }
        if (publish) StateChanged?.Invoke(Snapshot());
    }
}
