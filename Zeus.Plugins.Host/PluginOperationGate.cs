// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;

namespace Zeus.Plugins.Host;

/// <summary>
/// Serializes activation/deactivation lifecycle changes for one plugin while
/// allowing unrelated plugins to change concurrently. Entries are retained for
/// the process lifetime; the key set is bounded by plugins the operator engages.
/// </summary>
public sealed class PluginOperationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);

    public async ValueTask<IDisposable> EnterAsync(string pluginId, CancellationToken ct)
    {
        var key = pluginId.Trim().ToLowerInvariant();
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
