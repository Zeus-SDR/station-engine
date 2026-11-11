// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Globalization;
using System.Text.Json;
using Zeus.Contracts;
using Zeus.Server.Diagnostics;

namespace Zeus.Server;

/// <summary>
/// Maps the unified memory library routes. Every non-2xx answer is a
/// <see cref="StationMemoryError"/>; bodies are read and written with the
/// library's own serializer options, so the JSON is the same in every host.
/// </summary>
public static class StationMemoryEndpoints
{
    private const string Origin = "api";

    // The file itself is capped at 2 MiB of UTF-8 by the reader. It travels as
    // a JSON string beside the row decisions, so the body may be a few times
    // that; anything larger is refused before it is buffered.
    internal const long ImportRequestBodyLimitBytes = 16L * 1024 * 1024;

    public static IEndpointRouteBuilder MapStationMemoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var journal = new Journal(
            endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StationMemoryEndpoints)),
            endpoints.ServiceProvider.GetService<MemoryOperationHistory>());

        endpoints.MapGet("/api/station/memories", (
            HttpRequest request,
            StationMemoryStore store,
            StationMemoryRecallService recall) => Guard(() =>
        {
            var query = ListQuery(request.Query);
            var page = store.List(query);
            return Ok(new StationMemoryListResponse(
                page.Memories,
                page.Total,
                page.LibraryTotal,
                query.Offset,
                query.Limit,
                page.Groups,
                recall.Capabilities()));
        }));

        endpoints.MapPost("/api/station/memories", async (
            HttpRequest request,
            StationMemoryStore store,
            StationMemoryRecallService recall) =>
        {
            var (body, malformed) = await ReadBody<StationMemoryCreateRequest>(request);
            if (malformed is not null) return malformed;
            return Guard(journal, "create", null, () =>
            {
                var memory = store.Create(Required(body!.Memory, "memory"));
                return new Outcome(Ok(new StationMemoryResponse(memory, recall.Capabilities())), memory.Id, memory.Revision);
            });
        });

        endpoints.MapGet("/api/station/memories/{id}", (
            string id,
            StationMemoryStore store,
            StationMemoryRecallService recall) => Guard(() =>
        {
            var memory = store.Get(id) ?? throw new StationMemoryNotFoundException("memory", id);
            return Ok(new StationMemoryResponse(memory, recall.Capabilities()));
        }));

        endpoints.MapPut("/api/station/memories/{id}", async (
            string id,
            HttpRequest request,
            StationMemoryStore store,
            StationMemoryRecallService recall) =>
        {
            var (body, malformed) = await ReadBody<StationMemoryUpdateRequest>(request);
            if (malformed is not null) return malformed;
            return Guard(journal, "update", id, () =>
            {
                var memory = store.Update(id, body!.ExpectedRevision, Required(body.Memory, "memory"));
                return new Outcome(Ok(new StationMemoryResponse(memory, recall.Capabilities())), memory.Id, memory.Revision);
            });
        });

        endpoints.MapDelete("/api/station/memories/{id}", (
            string id,
            HttpRequest request,
            StationMemoryStore store) => Guard(journal, "delete", id, () =>
        {
            int expectedRevision = QueryInt(request.Query, "expectedRevision")
                ?? throw Invalid("expectedRevision", "expectedRevision is required");
            store.Delete(id, expectedRevision);
            return new Outcome(Results.NoContent(), id, expectedRevision);
        }));

        endpoints.MapPost("/api/station/memories/capture", async (
            HttpRequest request,
            StationMemoryRecallService recall) =>
        {
            var (body, malformed) = await ReadBody<StationMemoryCaptureRequest>(request, allowEmpty: true);
            if (malformed is not null) return malformed;
            return Guard(() => Ok(recall.Capture(body ?? new StationMemoryCaptureRequest())));
        });

        endpoints.MapPost("/api/station/memories/{id}/recall-preview", async (
            string id,
            HttpRequest request,
            StationMemoryRecallService recall) =>
        {
            var (body, malformed) = await ReadBody<StationMemoryRecallPreviewRequest>(request, allowEmpty: true);
            if (malformed is not null) return malformed;
            return Guard(() => Ok(recall.Preview(id, body ?? new StationMemoryRecallPreviewRequest())));
        });

        endpoints.MapPost("/api/station/memories/{id}/recall", async (
            string id,
            HttpRequest request,
            StationMemoryRecallService recall) =>
        {
            var (body, malformed) = await ReadBody<StationMemoryRecallRequest>(request);
            if (malformed is not null) return malformed;
            return Guard(() => Ok(recall.Recall(id, body!, Origin)));
        });

        endpoints.MapPost("/api/station/memories/group-operation", async (
            HttpRequest request,
            StationMemoryStore store) =>
        {
            var (body, malformed) = await ReadBody<StationMemoryGroupOperationRequest>(request);
            if (malformed is not null) return malformed;
            string operation = body!.Operation switch
            {
                "renameGroup" => "groupRename",
                "moveToGroup" => "groupMove",
                "delete" => "groupDelete",
                _ => "groupOperation",
            };
            return Guard(journal, operation, null, () =>
            {
                var result = body.Operation switch
                {
                    "renameGroup" => store.RenameGroup(body.FromGroup, body.ToGroup),
                    "moveToGroup" => store.MoveToGroup(Selection(body), body.ToGroup),
                    "delete" => store.DeleteMany(Selection(body)),
                    _ => throw Invalid("operation", "operation must be renameGroup, moveToGroup or delete"),
                };
                return new Outcome(Ok(result), Count: result.Affected);
            });
        });

        endpoints.MapPost("/api/station/memories/import-preview", async (
            HttpRequest request,
            StationMemoryImportExport imports) =>
        {
            var (body, malformed) = await ReadBody<StationMemoryImportPreviewRequest>(request);
            if (malformed is not null) return malformed;
            return Guard(() => Ok(imports.Preview(body!)));
        })
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
                ImportRequestBodyLimitBytes));

        endpoints.MapPost("/api/station/memories/import-commit", async (
            HttpRequest request,
            StationMemoryImportExport imports) =>
        {
            var (body, malformed) = await ReadBody<StationMemoryImportCommitRequest>(request);
            if (malformed is not null) return malformed;
            return Guard(journal, "importCommit", null, () =>
            {
                var result = imports.Commit(body!);
                return new Outcome(Ok(result), Count: result.Added + result.Updated + result.Removed);
            });
        })
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
                ImportRequestBodyLimitBytes));

        endpoints.MapGet("/api/station/memories/export", (
            HttpRequest request,
            StationMemoryImportExport imports) => Guard(journal, "export", null, () =>
        {
            var query = request.Query;
            if (query.TryGetValue("format", out var format) && format.ToString() != StationMemoryImportFormats.ZeusCsv)
                throw Invalid("format", "format must be zeus-csv");
            var export = imports.Export(
                query.TryGetValue("group", out var group) ? group.ToString() : null,
                query.ContainsKey("group"),
                query.TryGetValue("backupToken", out var backupToken) ? backupToken.ToString() : null);
            // No client reads a response header: the adapters name the file
            // themselves. This one is for a plain browser download.
            request.HttpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{export.FileName}\"";
            return new Outcome(Results.Bytes(export.Utf8Csv, "text/csv; charset=utf-8"), Count: export.Count);
        }));

        return endpoints;
    }

    private static StationMemoryQuery ListQuery(IQueryCollection query) => new(
        Group: query.TryGetValue("group", out var group) ? group.ToString() : null,
        GroupSpecified: query.ContainsKey("group"),
        Query: query.TryGetValue("query", out var text) ? text.ToString() : null,
        Source: query.TryGetValue("source", out var source) ? source.ToString() : null,
        Offset: QueryInt(query, "offset") ?? 0,
        Limit: QueryInt(query, "limit") ?? StationMemorySchema.ListDefaultLimit,
        ShortcutSlot: QueryInt(query, "shortcutSlot"),
        CatChannel: QueryInt(query, "catChannel"));

    private static int? QueryInt(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var raw) || raw.Count == 0) return null;
        return int.TryParse(raw.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw Invalid(name, $"{name} must be a whole number");
    }

    private static IReadOnlyList<StationMemoryRevisionRef> Selection(StationMemoryGroupOperationRequest request) =>
        request.Memories is { Count: > 0 } memories && memories.All(memory => memory is not null)
            ? memories
            : throw Invalid("memories", "memories must list at least one memory");

    private static T Required<T>(T? value, string field) where T : class =>
        value ?? throw Invalid(field, $"{field} is required");

    private static StationMemoryValidationException Invalid(string field, string message) =>
        new([new StationMemoryFieldError(field, message)]);

    // Read as text so a body that is missing or is not the expected JSON is
    // answered in this API's error shape instead of the framework's.
    //
    // A body is taken only as application/json, the same rule the framework
    // applies to a typed body. Another page can post text/plain to this
    // port without a preflight, and the CORS policy stops only what is
    // preflighted, so without this check any site the operator visits could
    // replace the library or retune the radio.
    private static async Task<(T? Body, IResult? Malformed)> ReadBody<T>(HttpRequest request, bool allowEmpty = false)
        where T : class
    {
        bool json = request.HasJsonContentType();
        if (!json && !allowEmpty) return (null, UnsupportedBody());
        long? limit = request.HttpContext.GetEndpoint()?.Metadata
            .GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()?.MaxRequestBodySize;
        if (limit is not null && request.ContentLength > limit) return (null, OversizedBody());
        using var reader = new StreamReader(request.Body);
        string text;
        try
        {
            text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // A chunked body states no length: the server stops it at the limit.
            return (null, OversizedBody());
        }
        if (!json && !string.IsNullOrWhiteSpace(text)) return (null, UnsupportedBody());
        if (string.IsNullOrWhiteSpace(text))
            return allowEmpty ? (null, null) : (null, BodyProblem("A request body is required."));
        try
        {
            return JsonSerializer.Deserialize<T>(text, StationMemoryJson.Options) is { } body
                ? (body, null)
                : (null, BodyProblem("A request body is required."));
        }
        catch (JsonException)
        {
            return (null, BodyProblem("The request body is not valid."));
        }
    }

    private static IResult UnsupportedBody() =>
        Problem(
            StatusCodes.Status415UnsupportedMediaType,
            StationMemoryErrorCodes.Validation,
            "The request body must be application/json.",
            new StationMemoryErrorDetails(Field: "body"));

    private static IResult OversizedBody() =>
        Problem(
            StatusCodes.Status413PayloadTooLarge,
            StationMemoryErrorCodes.Validation,
            "The request body is too large.",
            new StationMemoryErrorDetails(Field: "body"));

    private static IResult BodyProblem(string message) =>
        Problem(
            StatusCodes.Status400BadRequest,
            StationMemoryErrorCodes.Validation,
            message,
            new StationMemoryErrorDetails(Field: "body"));

    private static IResult Ok<T>(T value) => Results.Json(value, StationMemoryJson.Options);

    private static IResult Problem(int status, string code, string error, StationMemoryErrorDetails? details = null) =>
        Results.Json(new StationMemoryError(code, error, details), StationMemoryJson.Options, statusCode: status);

    private static IResult Guard(Func<IResult> work) =>
        Guard(null, null, null, () => new Outcome(work()));

    // Maps the library's exceptions to its error shape. A write also leaves
    // one line in the log and one row in the operation ring.
    private static IResult Guard(Journal? journal, string? operation, string? memoryId, Func<Outcome> work)
    {
        Outcome outcome;
        try
        {
            outcome = work();
        }
        catch (Exception ex) when (Refusal(ex) is { } refusal)
        {
            journal?.Write(operation!, memoryId, null, MemoryOperationHistory.ResultRefused, refusal.Code, null);
            return Problem(refusal.Status, refusal.Code, refusal.Error, refusal.Details);
        }

        journal?.Write(
            operation!,
            outcome.MemoryId ?? memoryId,
            outcome.Revision,
            MemoryOperationHistory.ResultOk,
            null,
            outcome.Count);
        return outcome.Result;
    }

    private static (int Status, string Code, string Error, StationMemoryErrorDetails? Details)? Refusal(Exception ex) => ex switch
    {
        StationMemoryValidationException invalid => (
            StatusCodes.Status400BadRequest,
            StationMemoryErrorCodes.Validation,
            invalid.Message,
            new StationMemoryErrorDetails(Field: invalid.Errors.FirstOrDefault()?.Field, Fields: invalid.Errors)),
        StationMemoryImportInvalidException unusable => (
            StatusCodes.Status400BadRequest,
            StationMemoryErrorCodes.ImportInvalid,
            unusable.Message,
            unusable.Rows is null ? null : new StationMemoryErrorDetails(Rows: unusable.Rows)),
        StationMemoryNotFoundException missing => (
            StatusCodes.Status404NotFound,
            StationMemoryErrorCodes.NotFound,
            missing.Message,
            new StationMemoryErrorDetails(Field: missing.Kind)),
        StationMemoryConflictException conflict => (
            StatusCodes.Status409Conflict,
            conflict.Code,
            conflict.Message,
            conflict.Details),
        StationMemoryRecallRefusedException refused => (
            refused.Status,
            refused.Code,
            refused.Message,
            refused.Details),
        _ => null,
    };

    private sealed record Outcome(IResult Result, string? MemoryId = null, int? Revision = null, int? Count = null);

    private sealed class Journal(ILogger log, MemoryOperationHistory? history)
    {
        public void Write(string operation, string? memoryId, int? revision, string result, string? code, int? count) =>
            MemoryOperationJournal.Write(
                log,
                history,
                new MemoryOperationEntry(
                    DateTimeOffset.UtcNow,
                    operation,
                    Origin,
                    memoryId,
                    revision,
                    Generation: null,
                    result,
                    code,
                    FailedStep: null,
                    RollbackSucceeded: null,
                    count));
    }
}
