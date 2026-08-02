// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Plugins.Host;

public sealed record PluginInstallAccessDecision(bool Allowed, string? Reason = null)
{
    public static readonly PluginInstallAccessDecision Allow = new(true);

    public static PluginInstallAccessDecision Deny(string reason) => new(false, reason);
}

public interface IPluginInstallAccessGate
{
    Task<PluginInstallAccessDecision> CheckInstallAsync(string pluginId, CancellationToken ct);
}

internal sealed class AllowAllPluginInstallAccessGate : IPluginInstallAccessGate
{
    public Task<PluginInstallAccessDecision> CheckInstallAsync(string pluginId, CancellationToken ct) =>
        Task.FromResult(PluginInstallAccessDecision.Allow);
}
