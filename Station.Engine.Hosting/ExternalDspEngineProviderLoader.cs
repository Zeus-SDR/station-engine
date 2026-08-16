// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

internal enum Protocol3TxEngineMode
{
    Wdsp,
    External,
}

/// <summary>
/// Loads an independently distributed DSP provider from one explicit path.
/// The provider load context is intentionally non-collectible: once native DSP
/// code has entered the process, Zeus never attempts to unload it underneath
/// realtime callbacks. Engine instances are still disposed on disconnect.
/// </summary>
internal sealed class ExternalDspEngineProviderLoader
{
    internal const string TxEngineEnvironmentVariable = "ZEUS_PROTOCOL3_TX_ENGINE";
    internal const string AssemblyEnvironmentVariable = "ZEUS_PROTOCOL3_EXTERNAL_DSP_PROVIDER_ASSEMBLY";
    internal const string TypeEnvironmentVariable = "ZEUS_PROTOCOL3_EXTERNAL_DSP_PROVIDER_TYPE";

    private readonly IConfiguration? _configuration;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private string? _loadedAssemblyPath;
    private string? _loadedTypeName;
    private IExternalDspEngineProvider? _provider;

    internal ExternalDspEngineProviderLoader(IConfiguration? configuration, ILogger log)
    {
        _configuration = configuration;
        _log = log;
    }

    internal Protocol3TxEngineMode Mode => ResolveMode(_configuration);

    internal ExternalDspEngineCapabilities Capabilities
    {
        get
        {
            if (Mode != Protocol3TxEngineMode.External)
                return ExternalDspEngineCapabilities.None;

            var provider = LoadProvider(
                ResolveAssemblyPath(_configuration),
                ResolveTypeName(_configuration));
            return provider is IExternalDspEngineCapabilitiesProvider declared
                ? declared.Capabilities
                : ExternalDspEngineCapabilities.None;
        }
    }

    internal IDspEngine CreateEngine(ExternalDspEngineRequest request, out string providerId)
    {
        if (Mode != Protocol3TxEngineMode.External)
            throw new InvalidOperationException("The external Protocol 3 TX engine was not selected.");

        var assemblyPath = ResolveAssemblyPath(_configuration);
        var typeName = ResolveTypeName(_configuration);
        var provider = LoadProvider(assemblyPath, typeName);
        providerId = NormalizeProviderId(provider.Id);

        var engine = provider.CreateEngine(request);
        return engine ?? throw new InvalidOperationException(
            $"External DSP provider '{providerId}' returned no engine.");
    }

    internal static Protocol3TxEngineMode ResolveMode(IConfiguration? configuration)
    {
        var raw = Environment.GetEnvironmentVariable(TxEngineEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            raw = configuration?["Zeus:Protocol3:TxEngine"];
        if (string.IsNullOrWhiteSpace(raw) ||
            string.Equals(raw, "wdsp", StringComparison.OrdinalIgnoreCase))
        {
            return Protocol3TxEngineMode.Wdsp;
        }
        if (string.Equals(raw, "external", StringComparison.OrdinalIgnoreCase))
            return Protocol3TxEngineMode.External;

        throw new InvalidOperationException(
            $"Unsupported Protocol 3 TX engine '{raw}'. Expected 'wdsp' or 'external'.");
    }

    internal static string ResolveAssemblyPath(IConfiguration? configuration)
    {
        var raw = Environment.GetEnvironmentVariable(AssemblyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            raw = configuration?["Zeus:Protocol3:ExternalDspProvider:AssemblyPath"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                $"External Protocol 3 TX engine selected, but no provider assembly was configured. Set {AssemblyEnvironmentVariable}.");
        }
        if (!Path.IsPathFullyQualified(raw))
        {
            throw new InvalidOperationException(
                $"External DSP provider assembly path must be fully qualified: {raw}");
        }

        var path = Path.GetFullPath(raw);
        if (!File.Exists(path))
            throw new InvalidOperationException($"External DSP provider assembly was not found: {path}");
        return path;
    }

    internal static string? ResolveTypeName(IConfiguration? configuration)
    {
        var raw = Environment.GetEnvironmentVariable(TypeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            raw = configuration?["Zeus:Protocol3:ExternalDspProvider:Type"];
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private IExternalDspEngineProvider LoadProvider(string assemblyPath, string? typeName)
    {
        lock (_sync)
        {
            if (_provider is not null)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(_loadedAssemblyPath, assemblyPath) ||
                    !StringComparer.Ordinal.Equals(_loadedTypeName, typeName))
                {
                    throw new InvalidOperationException(
                        "The external DSP provider cannot be changed after it has been loaded. Restart Zeus to select another provider.");
                }
                return _provider;
            }

            var context = new ExternalProviderLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var providerType = SelectProviderType(assembly, typeName);
            if (Activator.CreateInstance(providerType) is not IExternalDspEngineProvider provider)
            {
                throw new InvalidOperationException(
                    $"External DSP provider type '{providerType.FullName}' could not be constructed with a public parameterless constructor.");
            }

            var id = NormalizeProviderId(provider.Id);
            _loadedAssemblyPath = assemblyPath;
            _loadedTypeName = typeName;
            _provider = provider;
            _log.LogInformation(
                "dsp.external-provider.loaded id={ProviderId} assembly={Assembly} type={Type}",
                id,
                assemblyPath,
                providerType.FullName);
            return provider;
        }
    }

    private static Type SelectProviderType(Assembly assembly, string? typeName)
    {
        if (typeName is not null)
        {
            var selected = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (selected is null)
                throw new InvalidOperationException(
                    $"External DSP provider type '{typeName}' was not found in '{assembly.Location}'.");
            ValidateProviderType(selected);
            return selected;
        }

        var candidates = assembly.ExportedTypes
            .Where(IsProviderType)
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidOperationException(
                $"External DSP provider assembly '{assembly.Location}' contains {candidates.Length} public provider types; " +
                $"configure {TypeEnvironmentVariable} when the count is not exactly one.");
        }
        return candidates[0];
    }

    private static bool IsProviderType(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsPublic: true } &&
        typeof(IExternalDspEngineProvider).IsAssignableFrom(type);

    private static void ValidateProviderType(Type type)
    {
        if (!IsProviderType(type))
            throw new InvalidOperationException(
                $"External DSP provider type '{type.FullName}' must be a public, concrete {nameof(IExternalDspEngineProvider)} implementation.");
    }

    private static string NormalizeProviderId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("External DSP provider returned an empty identifier.");
        return id.Trim();
    }

    private sealed class ExternalProviderLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        internal ExternalProviderLoadContext(string providerAssemblyPath)
            : base($"Zeus.ExternalDspProvider:{Path.GetFileNameWithoutExtension(providerAssemblyPath)}", isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(providerAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share Zeus.Dsp from the default context so the provider contract
            // and IDspEngine retain one type identity across the boundary.
            if (AssemblyName.ReferenceMatchesDefinition(
                    assemblyName,
                    typeof(IExternalDspEngineProvider).Assembly.GetName()) ||
                AssemblyName.ReferenceMatchesDefinition(
                    assemblyName,
                    typeof(RxMode).Assembly.GetName()))
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
