// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA). All rights reserved.

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting;
#elif ZEUS_DSP_HOST
namespace Zeus.Dsp;
#else
namespace Zeus.Server;
#endif

/// <summary>
/// Per-edition storage supplied by the launcher. Without an edition root all
/// existing platform paths and development overrides retain their behavior.
/// </summary>
public static class EditionPaths
{
    public const string DataRootVariable = "ZEUS_EDITION_DATA_ROOT";
    public const string DownloadsRootVariable = "ZEUS_EDITION_DOWNLOADS_ROOT";

    public static string? DataRoot => ReadRoot(DataRootVariable, Environment.GetEnvironmentVariable);

    public static string LocalApplicationData(
        Environment.SpecialFolderOption option = Environment.SpecialFolderOption.None)
    {
        var root = DataRoot;
        if (root is null)
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, option);
        if (option == Environment.SpecialFolderOption.Create)
            Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Keep explicit development overrides only when they remain inside the
    /// edition. This also preserves private temporary recovery databases.
    /// </summary>
    public static string? ScopedOverride(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        var root = DataRoot;
        if (root is null || value is null) return value;
        if (!Path.IsPathFullyQualified(value)) return null;
        var relative = Path.GetRelativePath(root, Path.GetFullPath(value));
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative)
                ? value
                : null;
    }

    public static string? DownloadsRoot() => DownloadsRoot(Environment.GetEnvironmentVariable);

    public static string? DownloadsRoot(Func<string, string?> getEnvironmentVariable)
    {
        var configured = ReadRoot(DownloadsRootVariable, getEnvironmentVariable);
        if (configured is not null) return configured;
        var data = ReadRoot(DataRootVariable, getEnvironmentVariable);
        return data is null ? null : Path.Combine(data, "Downloads");
    }

    private static string? ReadRoot(string variable, Func<string, string?> getEnvironmentVariable)
    {
        var value = getEnvironmentVariable(variable);
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"{variable} must be an absolute directory path.");
        return Path.GetFullPath(value);
    }
}
