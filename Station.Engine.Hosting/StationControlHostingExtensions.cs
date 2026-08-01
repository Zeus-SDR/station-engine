// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using Zeus.Server.Cat;
using Zeus.Server.SpeTaurus;
using Zeus.Server.Tci;

namespace Zeus.Server;

/// <summary>Registers the engine-owned TCI and CAT station-control transports.</summary>
public static class StationControlHostingExtensions
{
    public static IServiceCollection AddTciServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TciConfigStore>();
        services.AddSingleton<SpotManager>();
        services.AddSingleton<TciServer>();
        services.AddHostedService(sp => sp.GetRequiredService<TciServer>());
        services.AddSingleton<TciManagementService>();
        return services;
    }

    public static IServiceCollection AddCatServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<CatConfigStore>();
        services.AddSingleton<CatServer>();
        services.AddHostedService(sp => sp.GetRequiredService<CatServer>());
        services.AddSingleton<CatManagementService>();
        services.AddSingleton<CatSerialConfigStore>();
        services.AddSingleton<CatSerialService>();
        services.AddHostedService(sp => sp.GetRequiredService<CatSerialService>());
        return services;
    }

    public static IServiceCollection AddSpeTaurusServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp => new SpeTaurusService(
            sp.GetRequiredService<ILogger<SpeTaurusService>>()));
        services.AddHostedService(sp => new SpeTaurusWorker(
            sp.GetRequiredService<SpeTaurusService>()));
        return services;
    }
}
