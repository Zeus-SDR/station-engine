// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>Receive protection state for one physical ADC, without routing or I/O.</summary>
internal sealed class PhysicalAdcProtectionController
{
    private bool _hardSeen;
    private bool _softSeen;
    private bool _validMagnitudeSeen;
    private ushort _maxMagnitude;
    private int _warningThreshold = 3;

    internal int OffsetDb { get; private set; }
    internal int OverloadLevel { get; private set; }
    internal long LastTickMs { get; private set; } = long.MinValue;
    internal long LastAttackMs { get; private set; } = long.MinValue;
    internal long LastThreatMs { get; private set; } = long.MinValue;
    internal bool PredictiveMagnitudeActive { get; private set; }
    internal bool Warning => OffsetDb > 0 || OverloadLevel > _warningThreshold;

    internal int EffectiveDb(int baselineDb) => Math.Clamp(baselineDb + OffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb);

    // The caller owns RX/TX eligibility and passes only telemetry for this ADC.
    // A null or zero magnitude is missing telemetry, never a measured quiet ADC.
    internal void Observe(AdcProtectionConfig config, int baselineDb, bool hardOverload, ushort? magnitude, long nowMs)
    {
        _warningThreshold = config.WarningThreshold;
        if (!config.Enabled) return;
        bool validMagnitude = magnitude is > 0;
        var zones = MagnitudeZones(config.MagnitudeSoftLimit);
        bool softHit = validMagnitude && magnitude!.Value >= zones.Attack;
        _hardSeen |= hardOverload;
        _softSeen |= softHit;
        _validMagnitudeSeen |= validMagnitude;
        _maxMagnitude = Math.Max(_maxMagnitude, magnitude ?? 0);

        bool cooldownElapsed = LastAttackMs == long.MinValue || nowMs - LastAttackMs >= config.AttackMs;
        bool immediateAttack = (hardOverload || softHit) && cooldownElapsed;
        if (LastTickMs == long.MinValue)
        {
            LastTickMs = nowMs;
            if (!immediateAttack) return;
        }
        else if (!immediateAttack)
        {
            int intervalMs = _hardSeen || _softSeen ? config.AttackMs : config.ReleaseMs;
            if (nowMs - LastTickMs < intervalMs) return;
            LastTickMs = nowMs;
        }
        else LastTickMs = nowMs;

        bool hardSeen = _hardSeen;
        bool softSeen = _softSeen;
        bool validSeen = _validMagnitudeSeen;
        ushort maxSeen = _maxMagnitude;
        ClearWindow();

        bool attackZone = hardSeen || softSeen;
        bool holdZone = !attackZone && validSeen && maxSeen >= zones.Release;
        if (attackZone)
        {
            LastThreatMs = nowMs;
            if (validSeen) PredictiveMagnitudeActive = true;
            OverloadLevel = Math.Min(5, OverloadLevel + 1);
            int maxOffset = Math.Min(config.MaxOffsetDb, HpsdrAtten.MaxDb - baselineDb);
            if (OffsetDb < maxOffset)
            {
                int previous = OffsetDb;
                int step = config.AttackStepDb;
                if (validSeen) step = RadioService.MagnitudeAttackStepDb(maxSeen, zones.Target, step);
                if (hardSeen) step = Math.Max(4, step);
                OffsetDb = Math.Min(maxOffset, OffsetDb + step);
                if (OffsetDb > previous) LastAttackMs = nowMs;
            }
        }
        else if (holdZone)
        {
            LastThreatMs = nowMs;
            if (OffsetDb > 0) PredictiveMagnitudeActive = true;
            if (OverloadLevel > 0) OverloadLevel--;
        }
        else if (!validSeen && PredictiveMagnitudeActive && OffsetDb > 0)
        {
            if (OverloadLevel > 0) OverloadLevel--;
        }
        else
        {
            if (OverloadLevel > 0) OverloadLevel--;
            bool holdElapsed = LastThreatMs == long.MinValue || nowMs - LastThreatMs >= config.ReleaseHoldMs;
            if (holdElapsed && OffsetDb > 0) OffsetDb = Math.Max(0, OffsetDb - config.ReleaseStepDb);
            if (OffsetDb == 0) PredictiveMagnitudeActive = false;
        }
    }

    internal void ResetWindowForTx()
    {
        ClearWindow();
        LastTickMs = long.MinValue;
    }

    internal void Reset()
    {
        ResetWindowForTx();
        OffsetDb = 0;
        OverloadLevel = 0;
        LastAttackMs = LastThreatMs = long.MinValue;
        PredictiveMagnitudeActive = false;
    }

    internal void ResetTimingForConfigurationChange()
    {
        LastTickMs = LastAttackMs = long.MinValue;
    }

    internal void ClampOffset(AdcProtectionConfig config, int baselineDb)
    {
        _warningThreshold = config.WarningThreshold;
        OffsetDb = Math.Clamp(OffsetDb, 0, Math.Min(config.MaxOffsetDb, HpsdrAtten.MaxDb - baselineDb));
        if (OffsetDb == 0) PredictiveMagnitudeActive = false;
    }

    private void ClearWindow()
    {
        _hardSeen = _softSeen = _validMagnitudeSeen = false;
        _maxMagnitude = 0;
    }

    private static (int Attack, int Target, int Release) MagnitudeZones(int configuredSoftLimit)
    {
        if (configuredSoftLimit <= 0) return (26_029, 20_676, 14_638);
        int target = Math.Max(1, (int)Math.Ceiling(configuredSoftLimit * Math.Pow(10.0, -2.0 / 20.0)));
        int release = Math.Max(1, (int)Math.Ceiling(configuredSoftLimit * Math.Pow(10.0, -5.0 / 20.0)));
        return (configuredSoftLimit, target, release);
    }
}
