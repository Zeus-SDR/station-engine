// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

// Station-wide Drive memory per digital mode. Each digital mode
// (DigitalDriveModes: FT8 / FT4 / WSPR / DIGU / DIGL / FREEDV) keeps the last
// Drive % the operator set while transmitting in it, the way AM keeps its own:
//   - operator Drive changes while the TX mode is digital are recorded to that
//     mode only (SetDriveCore), never to the per-band voice memory;
//   - entering a digital mode recalls its Drive; leaving digital recalls the
//     band's voice Drive (or, with none stored, the Drive in use on entry);
//   - band recall leaves Drive alone while digital (RestoreBandDrive).
// Recall uses the guarded, non-persisting SetDriveIfCurrent path: never while
// keyed, never over a concurrent slider move, and the Drive max still clamps.
public sealed partial class RadioService
{
    // Leaf lock: guards the fields below only, never held across a store read
    // or a RadioService call.
    private readonly object _digitalDriveGate = new();
    private string? _digitalDriveKey;
    private int? _preDigitalDrivePct;
    private Task _digitalDriveRecall = Task.CompletedTask;
    // Serializes recalls between the background worker and the synchronous
    // pre-key settle. Taken before _sync (Snapshot / Mutate), never under it.
    private readonly object _digitalDriveRecallLock = new();

    /// <summary>The most recently scheduled recall (tests).</summary>
    internal Task PendingDigitalDriveRecall
    {
        get { lock (_digitalDriveGate) return _digitalDriveRecall; }
    }

    private void InitDigitalModeDrive()
    {
        var key = DigitalDriveModes.KeyFor(Snapshot());
        lock (_digitalDriveGate) _digitalDriveKey = key;
        StateChanged += OnStateChangedForDigitalDrive;
        // A recall skipped while keyed is retried on the falling edge (MOX and
        // TUN edges do not raise StateChanged).
        MoxChanged += on => { if (!on) ScheduleDigitalDriveRecall(); };
        TunActiveChanged += on => { if (!on) ScheduleDigitalDriveRecall(); };
    }

    // Cheap filter on the broadcast thread; the recall itself runs after the
    // broadcast so every subscriber sees this state before the recalled one.
    private void OnStateChangedForDigitalDrive(StateDto s)
    {
        if (DigitalDriveRecallDue(s)) ScheduleDigitalDriveRecall();
    }

    private bool DigitalDriveRecallDue(StateDto s)
    {
        lock (_digitalDriveGate)
            return DigitalDriveModes.KeyFor(s, _digitalDriveKey) != _digitalDriveKey;
    }

    // Called from SetMoxCore before MOX latches: a mode change followed at
    // once by PTT (CAT "set mode, key") must not send the first over at the
    // previous mode's Drive while the background recall is still queued.
    private void SettleDigitalModeDriveBeforeKey()
    {
        if (DigitalDriveRecallDue(Snapshot())) RecallDigitalModeDrive();
    }

    private void ScheduleDigitalDriveRecall()
    {
        lock (_digitalDriveGate)
        {
            if (_disposed) return;
            _digitalDriveRecall = _digitalDriveRecall.ContinueWith(
                _ => RecallDigitalModeDrive(),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void RecallDigitalModeDrive()
    {
        lock (_digitalDriveRecallLock)
        {
            try
            {
                RecallDigitalModeDriveCore();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "digital.drive.recall failed");
            }
        }
    }

    private void RecallDigitalModeDriveCore()
    {
        // A keyed radio can't take the recall; the unkey edge retries.
        if (_disposed || IsTxActive()) return;
        // PA calibration walks bands and modes on its own; it restores the
        // operator's mode/VFO and then schedules a recall when its lease ends.
        bool calibrating;
        lock (_sync) calibrating = _paCalibrationInvariantLeaseActive;
        if (calibrating) return;

        // Re-read live state: broadcasts can arrive out of order.
        var s = Snapshot();
        string? previous;
        int? preDigital;
        lock (_digitalDriveGate)
        {
            previous = _digitalDriveKey;
            preDigital = _preDigitalDrivePct;
        }
        var key = DigitalDriveModes.KeyFor(s, previous);
        if (key == previous) return;
        // First attempt at entering digital: remember the voice Drive. A
        // retried entry keeps the original, not a slider moved meanwhile.
        if (previous is null) preDigital ??= s.DrivePct;

        int? target = key is not null
            ? _paStore.GetDigitalModeDrive(key)
            : VoiceDriveFor(s) ?? preDigital;
        bool settled = target is not int pct || pct == s.DrivePct
            || SetDriveIfCurrent(pct, s.DrivePct);

        lock (_digitalDriveGate)
        {
            // Keyed or the slider moved under us: keep the old key so the next
            // state change or unkey retries.
            _preDigitalDrivePct = key is null && settled ? null : preDigital;
            if (settled) _digitalDriveKey = key;
        }
        if (settled && target is int applied && applied != s.DrivePct)
        {
            _log.LogInformation(
                "digital.drive.recall from={From} to={To} drivePct={Drive}",
                previous ?? "voice", key ?? "voice", applied);
        }
    }

    // The memory key the operator's slider is recorded under right now.
    private string? CurrentDigitalDriveKey(StateDto s)
    {
        string? current;
        lock (_digitalDriveGate) current = _digitalDriveKey;
        return DigitalDriveModes.KeyFor(s, current);
    }

    // The per-band voice Drive for the band the dial is on (same key the
    // operator slider persists under), or null when never set.
    private int? VoiceDriveFor(StateDto s)
    {
        var band = RuntimeBandKey(s.VfoHz);
        return band is null ? null : _paStore.GetBandDrive(band).DrivePct;
    }

    private bool IsDigitalDriveMode()
    {
        StateDto s;
        lock (_sync) s = _state;
        return DigitalDriveModes.KeyFor(s) is not null;
    }
}
