// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;

namespace Zeus.Plugins.Host;

/// <summary>
/// Loads one plugin from a directory containing <c>plugin.json</c>.
/// Stateless: every call creates a fresh <see cref="AssemblyLoadContext"/>.
/// </summary>
public sealed class PluginLoader
{
    internal static readonly string ShadowBaseRoot = Path.Combine(
        Path.GetTempPath(), "zeus-plugin-shadow");
    private static readonly long ProcessStartUtcTicks =
        Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    internal static readonly string ShadowRoot = Path.Combine(
        ShadowBaseRoot, $"{Environment.ProcessId}-{ProcessStartUtcTicks}");
    private static int _staleShadowSweepDone;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ILogger<PluginLoader> _log;

    public PluginLoader(ILogger<PluginLoader> log)
    {
        _log = log;
        if (Interlocked.Exchange(ref _staleShadowSweepDone, 1) == 0)
            SweepStaleShadowRoots();
    }

    /// <summary>
    /// Parse, validate, and activate the plugin in <paramref name="pluginDir"/>.
    /// Throws <see cref="PluginLoadException"/> for any failure mode.
    /// </summary>
    public LoadedPlugin Load(string pluginDir)
    {
        var manifest = Inspect(pluginDir);

        // Never map assemblies directly from the installed package directory.
        // Windows keeps files loaded into a collectible AssemblyLoadContext
        // locked until a later GC actually reclaims the context. Loading from a
        // per-activation shadow copy lets the installer replace the package
        // atomically as soon as ShutdownAsync completes.
        var shadowDir = CreateShadowCopy(pluginDir, manifest.Id);
        var asmPath = Path.Combine(shadowDir, manifest.Entrypoint.Assembly);

        var alc = new PluginLoadContext(manifest.Id, asmPath);
        Assembly asm;
        try
        {
            asm = alc.LoadFromAssemblyPath(asmPath);
        }
        catch (Exception ex)
        {
            alc.Unload();
            TryDeleteShadowDirectory(shadowDir);
            throw new PluginLoadException($"failed to load assembly '{manifest.Entrypoint.Assembly}': {ex.Message}", ex);
        }

        Type? pluginType;
        try
        {
            pluginType = ResolvePluginType(asm, manifest);
        }
        catch (Exception ex) when (ex is TypeLoadException or FileLoadException
                                   or FileNotFoundException or ReflectionTypeLoadException)
        {
            alc.Unload();
            TryDeleteShadowDirectory(shadowDir);
            throw new PluginLoadException(
                $"failed to resolve plugin type in {manifest.Entrypoint.Assembly}: {TypeLoadMessage(ex)}",
                ex);
        }
        if (pluginType is null)
        {
            alc.Unload();
            TryDeleteShadowDirectory(shadowDir);
            throw new PluginLoadException(
                $"no public IZeusPlugin implementation found in {manifest.Entrypoint.Assembly}"
                + (manifest.Entrypoint.Type is { } t ? $" (sought type '{t}')" : ""));
        }

        IZeusPlugin instance;
        try
        {
            instance = (IZeusPlugin)Activator.CreateInstance(pluginType)!;
        }
        catch (Exception ex)
        {
            alc.Unload();
            TryDeleteShadowDirectory(shadowDir);
            throw new PluginLoadException(
                $"failed to instantiate plugin type '{pluginType.FullName}': {ex.Message}", ex);
        }

        _log.LogInformation(
            "Loaded plugin {Id} v{Version} from {Dir}",
            manifest.Id, manifest.Version, pluginDir);

        return new LoadedPlugin(manifest, instance, alc, pluginDir, shadowDir);
    }

    /// <summary>
    /// Read and validate an installed plugin manifest without copying or loading
    /// its assembly. Scanned audio plugins use this catalog-only path until their
    /// persisted chain membership or an operator action requires activation.
    /// </summary>
    public PluginManifest Inspect(string pluginDir)
    {
        var manifest = ReadManifest(pluginDir);

        // Local load: allow an absolute audio.vst3Path. Operator directory
        // scans reference VSTs in place at their install location so stub +
        // sidecar plugins keep their dependency DLLs. Downloaded plugins are
        // validated strictly at install time (PluginInstaller), so this does
        // not widen the registry attack surface.
        var errors = ManifestValidator.Validate(manifest, allowAbsoluteAudioPath: true);
        if (errors.Count > 0)
            throw new PluginLoadException(
                $"manifest invalid: {string.Join("; ", errors)}");

        if (!ManifestValidator.IsAbiCompatible(manifest, AbiVersion.Current, AbiVersion.SdkVersion))
            throw new PluginLoadException(
                $"plugin '{manifest.Id}' requires SDK abi={manifest.Sdk.Abi} minVersion={manifest.Sdk.MinVersion}; "
                + $"host is abi={AbiVersion.Current} version={AbiVersion.SdkVersion}");

        var installedAsmPath = Path.Combine(pluginDir, manifest.Entrypoint.Assembly);
        if (!File.Exists(installedAsmPath))
            throw new PluginLoadException($"entrypoint assembly not found: {installedAsmPath}");
        return manifest;
    }

    private static string CreateShadowCopy(string pluginDir, string pluginId)
    {
        Directory.CreateDirectory(ShadowRoot);
        var safeId = new string(pluginId.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        var shadowDir = Path.Combine(ShadowRoot, $"{safeId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(shadowDir);

        try
        {
            foreach (var sourceDir in Directory.EnumerateDirectories(
                         pluginDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(pluginDir, sourceDir);
                Directory.CreateDirectory(Path.Combine(shadowDir, relative));
            }

            foreach (var sourceFile in Directory.EnumerateFiles(
                         pluginDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(pluginDir, sourceFile);
                var destination = Path.Combine(shadowDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(sourceFile, destination, overwrite: false);
            }

            return shadowDir;
        }
        catch
        {
            TryDeleteShadowDirectory(shadowDir);
            throw;
        }
    }

    internal static void TryDeleteShadowDirectory(string? shadowDir)
    {
        if (string.IsNullOrWhiteSpace(shadowDir)) return;
        try
        {
            if (Directory.Exists(shadowDir))
                Directory.Delete(shadowDir, recursive: true);
        }
        catch (IOException) { /* collectible ALC may still map the shadow DLL */ }
        catch (UnauthorizedAccessException) { /* best effort; next process start releases it */ }
    }

    /// <summary>
    /// Remove shadow trees whose owning process no longer exists. The directory
    /// name includes both PID and process start time, so PID reuse cannot make a
    /// new Zeus instance delete another live instance's mapped assemblies.
    /// </summary>
    internal static void SweepStaleShadowRoots()
    {
        if (!Directory.Exists(ShadowBaseRoot)) return;

        foreach (var dir in Directory.EnumerateDirectories(ShadowBaseRoot))
        {
            if (string.Equals(
                    Path.GetFullPath(dir),
                    Path.GetFullPath(ShadowRoot),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                continue;
            }

            if (ShadowOwnerIsAlive(Path.GetFileName(dir))) continue;
            TryDeleteShadowDirectory(dir);
        }
    }

    private static bool ShadowOwnerIsAlive(string directoryName)
    {
        var separator = directoryName.IndexOf('-');
        if (separator <= 0
            || !int.TryParse(directoryName.AsSpan(0, separator), out var processId)
            || !long.TryParse(directoryName.AsSpan(separator + 1), out var startTicks))
        {
            // Legacy pre-process-root shadow directories are always stale once
            // this loader version starts; active instances use PID roots.
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch
        {
            // If the platform refuses process inspection, preserve the tree.
            return true;
        }
    }

    private static PluginManifest ReadManifest(string pluginDir)
    {
        var manifestPath = Path.Combine(pluginDir, "plugin.json");
        if (!File.Exists(manifestPath))
            throw new PluginLoadException($"plugin.json not found in {pluginDir}");

        PluginManifest? m;
        try
        {
            var json = File.ReadAllText(manifestPath);
            m = JsonSerializer.Deserialize<PluginManifest>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new PluginLoadException($"plugin.json parse error: {ex.Message}", ex);
        }

        return m ?? throw new PluginLoadException("plugin.json deserialised to null");
    }

    private static Type? ResolvePluginType(Assembly asm, PluginManifest manifest)
    {
        if (manifest.Entrypoint.Type is { Length: > 0 } typeName)
            return asm.GetType(typeName, throwOnError: true, ignoreCase: false);

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
            var resolved = FindPluginType(types);
            if (resolved is not null) return resolved;
            throw;
        }

        return FindPluginType(types);
    }

    private static Type? FindPluginType(IEnumerable<Type> types) =>
        types.FirstOrDefault(t =>
            t is { IsClass: true, IsAbstract: false, IsPublic: true }
            && typeof(IZeusPlugin).IsAssignableFrom(t));

    private static string TypeLoadMessage(Exception ex)
    {
        if (ex is not ReflectionTypeLoadException reflectionError)
            return ex.Message;

        var messages = reflectionError.LoaderExceptions
            .Where(loaderError => loaderError is not null)
            .Select(loaderError => loaderError!.Message)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return messages.Length == 0
            ? reflectionError.Message
            : string.Join("; ", messages);
    }
}

/// <summary>Result of a successful <see cref="PluginLoader.Load"/> call.</summary>
public sealed record LoadedPlugin(
    PluginManifest Manifest,
    IZeusPlugin Plugin,
    AssemblyLoadContext LoadContext,
    string PluginDir,
    string? ShadowDir = null);

/// <summary>Failure mode for <see cref="PluginLoader.Load"/>.</summary>
public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message) : base(message) { }
    public PluginLoadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Collectible ALC with private dependency resolution. Plugin assemblies
/// in the plugin's own directory take priority; deps the host already
/// has loaded (System.*, Zeus.Plugins.Contracts) fall through to the
/// default context so the plugin sees the same types the host sees.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginId, string mainAssemblyPath)
        : base(name: pluginId, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Force contracts + ASP.NET / runtime types to come from the
        // default context so plugin-defined IZeusPlugin and host-side
        // IZeusPlugin are the same Type identity. Without this, the
        // cast `(IZeusPlugin)Activator.CreateInstance(...)` throws.
        if (assemblyName.Name is { } n &&
            (n.StartsWith("Zeus.Plugins.Contracts", StringComparison.Ordinal) ||
             n.StartsWith("Microsoft.", StringComparison.Ordinal) ||
             n.StartsWith("System.", StringComparison.Ordinal) ||
             n == "netstandard"))
        {
            return null; // delegate to default ALC
        }

        var asmPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return asmPath is null ? null : LoadFromAssemblyPath(asmPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
