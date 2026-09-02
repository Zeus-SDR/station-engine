// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>
/// Chooses between the connected transceiver's bridge and an explicitly
/// selected external NanoVNA. Serial devices are never probed implicitly:
/// opening an arbitrary station port could disrupt CAT, PTT, or an amplifier.
/// </summary>
public sealed class VnaHardwareRouter : IVnaSweepHardware
{
    private readonly ConnectedRadioVnaSweepHardware _radio;
    private readonly NanoVnaSweepHardware _nanoVna;
    private readonly object _sync = new();
    private string _source = "radio";
    private int _captureActive;

    public VnaHardwareRouter(
        ConnectedRadioVnaSweepHardware radio,
        NanoVnaSweepHardware nanoVna)
    {
        _radio = radio;
        _nanoVna = nanoVna;
    }

    private IVnaSweepHardware Active
    {
        get { lock (_sync) return _source == "nanovna" ? _nanoVna : _radio; }
    }

    public VnaCapabilityDto Capability => Active.Capability;

    public VnaSourceStatusDto SourceStatus()
    {
        string source;
        lock (_sync) source = _source;
        NanoVnaConnectionStatus nano = _nanoVna.ConnectionStatus;
        return new VnaSourceStatusDto(
            source,
            source == "nanovna" ? nano.PortName : null,
            source == "nanovna" ? nano.DeviceName : _radio.Capability.Board,
            source == "nanovna" ? nano.Connected : _radio.Capability.Available,
            source == "nanovna" ? nano.Error : null,
            _nanoVna.AvailablePorts());
    }

    public async Task<VnaSourceStatusDto> SelectAsync(
        string source, string? deviceId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _captureActive) != 0)
            throw new InvalidOperationException("Cancel the active sweep before changing measurement source.");

        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Choose a measurement source.", nameof(source));
        string normalized = source.Trim().ToLowerInvariant();
        if (normalized == "radio")
        {
            await _nanoVna.DisconnectAsync().ConfigureAwait(false);
            lock (_sync) _source = "radio";
            return SourceStatus();
        }
        if (normalized != "nanovna")
            throw new ArgumentException("Measurement source must be 'radio' or 'nanovna'.", nameof(source));
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Select a NanoVNA serial device before connecting.", nameof(deviceId));

        await _nanoVna.ConnectAsync(deviceId.Trim(), cancellationToken).ConfigureAwait(false);
        lock (_sync) _source = "nanovna";
        return SourceStatus();
    }

    public async Task<VnaCaptureResult> CaptureAsync(
        long startHz, long endHz, int points, int driveLevel, bool fixedRxGainHigh,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _captureActive, 1) != 0)
            throw new InvalidOperationException("An analyzer capture is already running.");
        try
        {
            IVnaSweepHardware hardware = Active;
            return await hardware.CaptureAsync(startHz, endHz, points, driveLevel,
                fixedRxGainHigh, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _captureActive, 0);
        }
    }
}
