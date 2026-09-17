using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Application.Events;
using NetRatel.Application.Scripts;
using NetRatel.Shared.Contracts.Scripts;

namespace NetRatel.API.Endpoints;

public static class ScriptEndpoints
{
    public static IEndpointRouteBuilder MapScriptEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/script-library")
            .WithTags("Script Library")
            .RequireAuthorization("Operator");

        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (Exception exception) when (exception is ScriptSourceConcurrencyException or DbUpdateConcurrencyException)
            { return Results.Conflict(new { code = "script_source_revision_conflict", remediation = "Reload the script before editing." }); }
            catch (ScriptSourceInUseException)
            { return Results.Conflict(new { code = "script_source_in_use", remediation = "Remove job definition references before deletion." }); }
        });

        group.MapGet("/", async (IScriptService scripts, CancellationToken ct) =>
        {
            var payload = (await scripts.ListAsync(ct)).Select(MapResponse);
            return Results.Ok(payload);
        });

        group.MapGet("/{id:long}", async (ulong id, IScriptService scripts, CancellationToken ct) =>
        {
            var script = await scripts.GetAsync(id, ct);
            if (script is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(MapResponse(script));
        });

        group.MapGet("/{id:long}/params", async (ulong id, IScriptService scripts, CancellationToken ct) =>
        {
            var rows = (await scripts.GetParamsAsync(id, ct))
                .Select(x => new
                {
                    x.Id,
                    x.ScriptId,
                    x.Name,
                    x.Type,
                    x.Required,
                    x.Default,
                    x.Description,
                    x.OptionsJson
                });
            return Results.Ok(rows);
        });

        group.MapPost("/{id:long}/parse-manifest", async (ulong id, HttpContext ctx, IScriptService scripts, CancellationToken ct) =>
        {
            string? manifestRaw = null;
            long? expectedSourceRevision = null;
            if (ctx.Request.ContentLength > 0)
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("manifestRaw", out var mr) && mr.ValueKind != System.Text.Json.JsonValueKind.Null)
                    manifestRaw = mr.GetString();
                if (doc.RootElement.TryGetProperty("expectedSourceRevision", out var revision) && revision.ValueKind == System.Text.Json.JsonValueKind.Number && revision.TryGetInt64(out var number))
                    expectedSourceRevision = number;
            }

            if (expectedSourceRevision is not > 0) return RevisionRequired();
            var script = await scripts.ParseManifestAsync(id, manifestRaw, expectedSourceRevision.Value, ct);
            if (script is null)
            {
                return Results.NotFound();
            }

            return Results.NoContent();
        });

        group.MapPost("/", async (
            [FromBody] CreateScriptRequest request,
            IScriptService scripts,
            IEventRecorder events,
            ICorrelationContext correlation,
            CancellationToken ct) =>
        {
            var created = await scripts.CreateAsync(new CreateScriptCommand(
                request.Name,
                NormalizeFolderPath(request.FolderPath),
                request.Description,
                request.Content,
                NormalizeScriptType(request.ScriptType),
                ManifestRaw: null), ct);

            await events.RecordAsync(new DomainEvent
            {
                EventType = NetRatelEventTypes.Script.Created,
                Source = "Orchestration",
                CorrelationId = correlation.GetOrCreate(),
                EntityId = created.Id.ToString(),
                Severity = "Info",
                Message = $"Script {created.Id} created.",
                Payload = new ScriptChangedPayload(created.Id, created.Name, created.ScriptType, Actor: null)
            }, ct);
            return Results.Ok(new { id = created.Id });
        });

        group.MapPut("/{id:long}", async (
            ulong id,
            [FromBody] UpdateScriptRequest request,
            IScriptService scripts,
            IEventRecorder events,
            ICorrelationContext correlation,
            CancellationToken ct) =>
        {
            if (request.ExpectedSourceRevision is not > 0) return RevisionRequired();
            var before = await scripts.GetAsync(id, ct);
            if (before is null)
            {
                return Results.NotFound();
            }

            var after = await scripts.UpdateAsync(new UpdateScriptCommand(
                id,
                request.Name,
                request.FolderPath is null ? null : NormalizeFolderPath(request.FolderPath),
                request.Description,
                request.Content,
                request.ScriptType is null ? null : NormalizeScriptType(request.ScriptType),
                ManifestRaw: null, ExpectedSourceRevision: request.ExpectedSourceRevision), ct);

            if (after is null)
            {
                return Results.NotFound();
            }

            var changed = !string.Equals(before.Name, after.Name, StringComparison.Ordinal) ||
                          !string.Equals(before.FolderPath, after.FolderPath, StringComparison.Ordinal) ||
                          !string.Equals(before.Description, after.Description, StringComparison.Ordinal) ||
                          !string.Equals(before.Content, after.Content, StringComparison.Ordinal) ||
                          !string.Equals(before.ScriptType, after.ScriptType, StringComparison.Ordinal);

            if (changed)
            {
                await events.RecordAsync(new DomainEvent
                {
                    EventType = NetRatelEventTypes.Script.Updated,
                    Source = "Orchestration",
                    CorrelationId = correlation.GetOrCreate(),
                    EntityId = after.Id.ToString(),
                    Severity = "Info",
                    Message = $"Script {after.Id} updated.",
                    Payload = new ScriptChangedPayload(after.Id, after.Name, after.ScriptType, Actor: null)
                }, ct);
            }

            return Results.Ok(new { id = after.Id, sourceRevision = after.SourceRevision });
        });

        group.MapDelete("/{id:long}", async (
            ulong id, long? expectedSourceRevision,
            IScriptService scripts,
            IEventRecorder events,
            ICorrelationContext correlation,
            CancellationToken ct) =>
        {
            if (expectedSourceRevision is not > 0) return RevisionRequired();
            var existing = await scripts.DeleteAsync(id, expectedSourceRevision.Value, ct);
            if (existing is null)
            {
                return Results.NotFound();
            }

            await events.RecordAsync(new DomainEvent
            {
                EventType = NetRatelEventTypes.Script.Deleted,
                Source = "Orchestration",
                CorrelationId = correlation.GetOrCreate(),
                EntityId = id.ToString(),
                Severity = "Warning",
                Message = $"Script {id} deleted.",
                Payload = new ScriptChangedPayload(id, existing.Name, existing.ScriptType, Actor: null)
            }, ct);
            return Results.Accepted();
        });

        return app;
    }

    private static IResult RevisionRequired() => Results.Json(new
    {
        code = "script_source_revision_required",
        remediation = "Read the script and supply its observed sourceRevision."
    }, statusCode: StatusCodes.Status428PreconditionRequired);

    private static ScriptResponse MapResponse(ScriptInfo script)
        => new(script.Id, script.Name, script.FolderPath, script.Description, script.Content, script.ScriptType, script.CreatedAtUtc, script.UpdatedAtUtc, script.SourceRevision);

    private static string NormalizeFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return "/";
        }

        var normalized = folderPath.Replace("\\", "/", StringComparison.Ordinal);
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        return normalized;
    }

    private static string NormalizeScriptType(string? scriptType)
    {
        if (string.IsNullOrWhiteSpace(scriptType))
        {
            return "plaintext";
        }

        return scriptType.Trim();
    }
}
