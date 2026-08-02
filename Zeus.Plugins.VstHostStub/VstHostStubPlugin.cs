// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugins.Contracts;

namespace Zeus.Plugins.VstHostStub;

/// <summary>
/// No-op <see cref="IZeusPlugin"/> used as the managed entrypoint for
/// host-scanned VST3 plugins, which have no managed code of their own.
/// The plugin loader requires every package to ship an assembly
/// implementing <see cref="IZeusPlugin"/>; this satisfies that contract
/// while contributing nothing. The audio path comes entirely from the
/// generated manifest's <c>audio.vst3Path</c>, which
/// <c>AudioPluginBridge</c> wraps in a <c>VstHostAudioPlugin</c>.
/// </summary>
public sealed class VstHostStubPlugin : IZeusPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
