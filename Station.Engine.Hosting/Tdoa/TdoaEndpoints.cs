// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.AspNetCore.Mvc;

namespace Zeus.Server.Tdoa;

public static class TdoaEndpoints
{
    public static IEndpointRouteBuilder MapTdoaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/tdoa/solve", HandleSolveAsync)
            .WithMetadata(new RequestSizeLimitAttribute(TdoaLimits.MaxHttpBodyBytes));
        return endpoints;
    }

    internal static async Task<IResult> HandleSolveAsync(
        TdoaSolveRequest request, TdoaSolver solver, HttpContext context)
    {
        if (!LocalRequestGuard.IsLocalRequest(context))
            return Results.Json(new { error = "TDoA solving is available only from this station computer." },
                statusCode: StatusCodes.Status403Forbidden);
        if (context.Request.ContentLength is > TdoaLimits.MaxHttpBodyBytes)
            return Results.Json(new { error = $"Request body exceeds {TdoaLimits.MaxHttpBodyBytes} bytes." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        try
        {
            return Results.Ok(await solver.SolveAsync(request, context.RequestAborted).ConfigureAwait(false));
        }
        catch (TdoaBusyException ex)
        {
            context.Response.Headers.RetryAfter = "1";
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (TdoaValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
    }
}
