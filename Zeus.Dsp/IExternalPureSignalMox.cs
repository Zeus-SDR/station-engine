// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Dsp;

/// <summary>
/// PureSignal MOX for a transmitter the engine does not key itself. The
/// product-operated radio's transmit state is the only keyed truth there, so
/// it also stands in for the engine's own MOX wherever PureSignal needs to
/// know the radio is on the air (the observed-peak hold decays only while
/// keyed). Engines keyed through <see cref="IDspEngine.SetMox(bool)"/> never
/// call this; their behaviour is unchanged.
/// </summary>
public interface IExternalPureSignalMox
{
    void SetExternalPsMox(bool moxOn);
}
