// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Zeus.Server;

/// <summary>
/// Requires the launcher-provided station token on the product-audio API.
/// </summary>
public static class StationAccessTokenAuthorization
{
    public static IApplicationBuilder UseStationAccessTokenAuthorization(
        this IApplicationBuilder app,
        string? stationAccessToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (stationAccessToken is null)
            return app;
        if (stationAccessToken.Length == 0)
        {
            throw new InvalidOperationException(
                "ZEUS_STATION_ACCESS_TOKEN is present but empty; refusing to start without a usable station access token.");
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(stationAccessToken));
        return app.Use(async (context, next) =>
        {
            if (RequiresAuthorization(context.Request)
                && !HasExpectedBearerToken(context.Request, expectedHash))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static bool RequiresAuthorization(HttpRequest request)
    {
        return request.Path.StartsWithSegments(
            "/api/station/product-audio",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExpectedBearerToken(HttpRequest request, byte[] expectedHash)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1
            || !AuthenticationHeaderValue.TryParse(values[0], out var authorization)
            || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(authorization.Parameter))
        {
            return false;
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization.Parameter));
        return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
    }
}
