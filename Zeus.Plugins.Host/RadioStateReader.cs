// SPDX-License-Identifier: GPL-2.0-or-later
//
// RadioStateReader — plugin-host implementation of the plugin-facing
// IRadioStateReader (read-only). Surfaces the operator's current VFO
// frequency, mode, band, MOX state, and normal/tune drive settings to plugins
// holding the ReadRadioState
// capability, wrapping the same RadioService snapshot + change events the UI
// uses. Band is derived from the VFO with BandUtils.FreqToBand. Registered as a
// singleton by AddZeusPlugins and surfaced via IPluginContext.Radio (gated on
// the capability in PluginManager), so a plugin gets a live reader in every
// host that installs the plugin system — the standalone station engine as well
// as the retired monolithic host. Copyright (C) 2026 contributors.

using Zeus.Contracts;
using Zeus.Plugins.Contracts;
using Zeus.Server;

namespace Zeus.Plugins.Host;

internal sealed class RadioStateReader : IRadioStateReader, IDisposable
{
    private readonly RadioService _radio;
    private long _lastFreq;
    private string _lastMode;
    private int _lastDrivePercent;
    private int _lastTuneDrivePercent;
    private volatile bool _mox;

    public RadioStateReader(RadioService radio)
    {
        _radio = radio;
        var s = _radio.Snapshot();
        _lastFreq = s.VfoHz;
        _lastMode = s.Mode.ToString();
        _lastDrivePercent = s.DrivePct;
        _lastTuneDrivePercent = s.TunePct;
        _radio.StateChanged += OnStateChanged;
        _radio.MoxChanged += OnMoxChanged;
    }

    public long FrequencyHz => _radio.Snapshot().VfoHz;
    public string Mode => _radio.Snapshot().Mode.ToString();
    public string Band => BandUtils.FreqToBand(_radio.Snapshot().VfoHz) ?? "";
    public bool Mox => _mox;
    public int DrivePercent => _radio.Snapshot().DrivePct;
    public int TuneDrivePercent => _radio.Snapshot().TunePct;

    public event Action<long>? FrequencyChanged;
    public event Action<string>? ModeChanged;
    public event Action<bool>? MoxChanged;
    public event Action<int>? DrivePercentChanged;
    public event Action<int>? TuneDrivePercentChanged;

    private void OnStateChanged(StateDto s)
    {
        if (s.VfoHz != _lastFreq)
        {
            _lastFreq = s.VfoHz;
            FrequencyChanged?.Invoke(s.VfoHz);
        }
        var mode = s.Mode.ToString();
        if (!string.Equals(mode, _lastMode, StringComparison.Ordinal))
        {
            _lastMode = mode;
            ModeChanged?.Invoke(mode);
        }
        if (s.DrivePct != _lastDrivePercent)
        {
            _lastDrivePercent = s.DrivePct;
            DrivePercentChanged?.Invoke(s.DrivePct);
        }
        if (s.TunePct != _lastTuneDrivePercent)
        {
            _lastTuneDrivePercent = s.TunePct;
            TuneDrivePercentChanged?.Invoke(s.TunePct);
        }
    }

    private void OnMoxChanged(bool on)
    {
        _mox = on;
        MoxChanged?.Invoke(on);
    }

    public void Dispose()
    {
        _radio.StateChanged -= OnStateChanged;
        _radio.MoxChanged -= OnMoxChanged;
    }
}
