// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace Zeus.Plugins.Host;

/// <summary>
/// Mutable <see cref="EndpointDataSource"/> that owns every activated
/// <see cref="Zeus.Plugins.Contracts.Extensions.IBackendPlugin"/>'s
/// <c>/api/plugins/{id}/...</c> routes.
///
/// Why this exists: routes Map*'d onto the app's route builder after the
/// server has started never register — the route matcher only rebuilds when
/// an already-registered data source fires its change token. So a plugin
/// installed from the store mid-session used to activate (panel visible)
/// while every backend call 404'd until the next restart. Publishing plugin
/// routes through this ONE data source (added to the route table at startup)
/// makes install / uninstall / reinstall take effect immediately: each
/// mutation swaps the composed snapshot and fires the change token, and the
/// matcher re-reads it on the next request.
///
/// Thread-safety: mutations and snapshot reads are guarded by a lock; the
/// change-token cancel runs OUTSIDE the lock because the matcher's rebuild
/// callback executes synchronously on it.
/// </summary>
public sealed class PluginBackendEndpointDataSource : EndpointDataSource
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IReadOnlyList<Endpoint>> _byPlugin =
        new(StringComparer.Ordinal);
    private IReadOnlyList<Endpoint> _snapshot = Array.Empty<Endpoint>();
    private CancellationTokenSource _cts = new();

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get { lock (_sync) return _snapshot; }
    }

    public override IChangeToken GetChangeToken()
    {
        lock (_sync) return new CancellationChangeToken(_cts.Token);
    }

    /// <summary>
    /// Replace (or add) one plugin's endpoints and signal the matcher.
    /// An empty list removes the plugin — activation failures and
    /// deactivation converge on the same "no routes" state.
    /// </summary>
    public void SetPluginEndpoints(string pluginId, IReadOnlyList<Endpoint> endpoints)
    {
        CancellationTokenSource previous;
        lock (_sync)
        {
            if (endpoints.Count == 0)
            {
                // Removing an id that was never mapped is a no-op — skip the
                // rebuild AND the matcher signal.
                if (!_byPlugin.Remove(pluginId)) return;
            }
            else
            {
                _byPlugin[pluginId] = endpoints;
            }

            _snapshot = _byPlugin.Values.SelectMany(e => e).ToArray();
            previous = _cts;
            _cts = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
    }

    /// <summary>Remove one plugin's endpoints (deactivation / uninstall).</summary>
    public void RemovePluginEndpoints(string pluginId)
        => SetPluginEndpoints(pluginId, Array.Empty<Endpoint>());
}

/// <summary>
/// Throwaway <see cref="IEndpointRouteBuilder"/> used to BUILD a plugin's
/// endpoints without registering them anywhere — the built endpoints are
/// then published via <see cref="PluginBackendEndpointDataSource"/>. Handler
/// services still resolve per-request from HttpContext.RequestServices; this
/// provider only serves endpoint-construction needs (route options etc.).
/// </summary>
internal sealed class DetachedEndpointRouteBuilder : IEndpointRouteBuilder
{
    public DetachedEndpointRouteBuilder(IServiceProvider services)
        => ServiceProvider = services;

    public IServiceProvider ServiceProvider { get; }
    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
}
