using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Endpoints;

/// <summary>
/// Development-only job adapter. It creates a job with exactly one owned,
/// server-generated marker-library-script step and never accepts executable
/// content, inputs, working directories, or runtime settings from the caller.
/// </summary>
public static class DevelopmentMcpMarkerJobEndpoints
{
    public static IEndpointRouteBuilder MapDevelopmentMcpMarkerJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/development/mcp/agents/{tenantId:int}/{agentId:guid}/marker-jobs")
            .WithTags("Development MCP Marker Jobs")
            .RequireAuthorization("Operator");

        group.MapPost("/{scriptId:long}", CreateAsync);
        group.MapPost("/{jobId:long}/runs", RunAsync);
        group.MapPost("/{jobId:long}/runs/{runId:long}/cancel", CancelAsync);
        group.MapDelete("/{jobId:long}", DeleteAsync);
        return app;
    }

    private static async Task<IResult> CreateAsync(
        int tenantId,
        Guid agentId,
        long scriptId,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        IScriptService scripts,
        IJobDefinitionService jobs,
        IEventRecorder events,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var script = await RequireOwnedScriptAsync(tenantId, agentId, scriptId, http, db, correlation, targets, cancellationToken).ConfigureAwait(false);
        if (script.Error is not null) return script.Error;
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.JobDefinitionMutation, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        if (await db.DevelopmentMcpMarkerJobs.AnyAsync(candidate => candidate.ScriptId == scriptId && candidate.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false))
        {
            return Results.Conflict(new { code = "marker_job_already_exists" });
        }

        var definition = await scripts.GetAsync(checked((ulong)scriptId), cancellationToken).ConfigureAwait(false);
        if (!IsExpectedScript(definition, script.Record!))
        {
            return Results.Conflict(new { code = "marker_script_integrity_invalid" });
        }

        var grant = await targets.GetActiveGrantAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (grant is null)
        {
            return TargetRejected("target_authorization_changed");
        }

        var markerJob = await jobs.CreateAsync(new CreateJobDefinitionCommand(
            $"MCP QA marker job {script.Record!.Marker}",
            Folder(tenantId, agentId),
            "Server-generated Development MCP marker job.",
            tenantId,
            agentId.ToString("D"),
            AgentId: agentId), cancellationToken).ConfigureAwait(false);
        var step = await jobs.AddStepAsync(new AddJobStepCommand(
            markerJob.Id,
            1,
            JobStepKind.LibraryScript,
            null,
            null,
            checked((ulong)scriptId),
            null,
            true), cancellationToken).ConfigureAwait(false);
        if (step is null)
        {
            await jobs.DeleteAsync(markerJob.Id, cancellationToken).ConfigureAwait(false);
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The Development marker job step could not be created.");
        }

        var now = DateTimeOffset.UtcNow;
        var record = new DevelopmentMcpMarkerJobRecord
        {
            Id = Guid.NewGuid(),
            JobId = checked((long)markerJob.Id),
            ScriptId = scriptId,
            TenantId = tenantId,
            AgentId = agentId,
            TargetGrantId = grant.GrantId,
            Marker = script.Record.Marker,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.DevelopmentMcpMarkerJobs.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await events.RecordAsync(new DomainEvent
        {
            EventType = NetRatelEventTypes.Job.Created,
            Source = "DevelopmentMcp",
            CorrelationId = correlation.GetOrCreate(),
            TenantId = tenantId.ToString(),
            EntityId = markerJob.Id.ToString(),
            Severity = "Info",
            Message = $"Development marker job {markerJob.Id} created.",
            Payload = new { jobId = markerJob.Id, scriptId, tenantId, agentId, ownershipId = record.Id }
        }, cancellationToken).ConfigureAwait(false);
        return Results.Created(MarkerJobPath(tenantId, agentId, markerJob.Id), ToDto(record));
    }

    private static async Task<IResult> RunAsync(
        int tenantId,
        Guid agentId,
        long jobId,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        IScriptService scripts,
        IAkkaJobAuthorityService authority,
        IEventRecorder events,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var job = await RequireOwnedJobAsync(tenantId, agentId, jobId, DevelopmentOperatorOperation.JobRunStart, http, db, correlation, targets, cancellationToken).ConfigureAwait(false);
        if (job.Error is not null) return job.Error;
        var jobRecord = job.Record!;
        var script = await db.DevelopmentMcpScripts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.ScriptId == jobRecord.ScriptId && candidate.TenantId == tenantId && candidate.AgentId == agentId && candidate.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        var definition = script is null ? null : await scripts.GetAsync(checked((ulong)jobRecord.ScriptId), cancellationToken).ConfigureAwait(false);
        if (!IsExpectedScript(definition, script))
        {
            return Results.Conflict(new { code = "marker_script_integrity_invalid" });
        }

        try
        {
            var run = await authority.StartAsync(checked((ulong)jobId), new RunJobRequest("mcp:marker-job", null, null), cancellationToken).ConfigureAwait(false);
            await events.RecordAsync(new DomainEvent
            {
                EventType = NetRatelEventTypes.Job.Submitted,
                Source = "DevelopmentMcp",
                CorrelationId = correlation.GetOrCreate(),
                TenantId = tenantId.ToString(),
                EntityId = run.Id.ToString(),
                Severity = "Info",
                Message = $"Development marker job {jobId} submitted.",
                Payload = new { jobId, runId = run.Id, tenantId, agentId, ownershipId = jobRecord.Id }
            }, cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"{MarkerJobPath(tenantId, agentId, checked((ulong)jobId))}/runs/{run.Id}", new DevelopmentMcpMarkerJobRunDto(checked((ulong)jobId), run.Id, tenantId, agentId, run.Status.ToString()));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { code = "marker_job_not_found" });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { code = "marker_job_authority_unavailable", detail = exception.Message });
        }
    }

    private static async Task<IResult> CancelAsync(
        int tenantId,
        Guid agentId,
        long jobId,
        long runId,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        IJobRunService runs,
        IAkkaJobAuthorityService authority,
        IEventRecorder events,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var job = await RequireOwnedJobAsync(tenantId, agentId, jobId, DevelopmentOperatorOperation.JobRunCancel, http, db, correlation, targets, cancellationToken).ConfigureAwait(false);
        if (job.Error is not null) return job.Error;
        var jobRecord = job.Record!;
        var run = await runs.GetAsync(checked((ulong)runId), cancellationToken).ConfigureAwait(false);
        if (run is null || run.JobId != checked((ulong)jobId) || run.TenantId != tenantId || run.AgentId != agentId)
        {
            return Results.NotFound(new { code = "marker_job_run_not_found" });
        }
        if (IsTerminal(run.Status))
        {
            return Results.Conflict(new { code = "marker_job_run_terminal" });
        }
        if (!await authority.CancelAsync(run.Id, "mcp-marker-job-cancelled", cancellationToken).ConfigureAwait(false))
        {
            return Results.Conflict(new { code = "marker_job_authority_unavailable" });
        }

        var cancelledAt = DateTimeOffset.UtcNow;
        await runs.UpsertRunAsync(new UpsertJobRunCommand(
            run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy, JobRunState.Cancelled,
            run.CurrentStepOrdinal, run.CreatedAtUtc, run.StartedAtUtc, cancelledAt, "Cancelled", run.InputsJson, run.OptionsJson, run.AgentId), cancellationToken).ConfigureAwait(false);
        await events.RecordAsync(new DomainEvent
        {
            EventType = NetRatelEventTypes.Job.StateChanged,
            Source = "DevelopmentMcp",
            CorrelationId = correlation.GetOrCreate(),
            TenantId = tenantId.ToString(),
            EntityId = run.Id.ToString(),
            Severity = "Info",
            Message = $"Development marker job run {run.Id} cancelled.",
            Payload = new { jobId, runId, tenantId, agentId, ownershipId = jobRecord.Id }
        }, cancellationToken).ConfigureAwait(false);
        return Results.Accepted($"{MarkerJobPath(tenantId, agentId, checked((ulong)jobId))}/runs/{run.Id}", new DevelopmentMcpMarkerJobRunDto(checked((ulong)jobId), run.Id, tenantId, agentId, JobRunState.Cancelled.ToString()));
    }

    private static async Task<IResult> DeleteAsync(
        int tenantId,
        Guid agentId,
        long jobId,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        IJobDefinitionService jobs,
        IEventRecorder events,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var job = await RequireOwnedJobAsync(tenantId, agentId, jobId, DevelopmentOperatorOperation.JobRunDelete, http, db, correlation, targets, cancellationToken).ConfigureAwait(false);
        if (job.Error is not null) return job.Error;
        var jobRecord = job.Record!;
        var activeRun = await db.JobRuns.AsNoTracking().AnyAsync(candidate => candidate.JobId == jobId && (candidate.Status == (int)JobRunState.Pending || candidate.Status == (int)JobRunState.Running), cancellationToken).ConfigureAwait(false);
        if (activeRun)
        {
            return Results.Conflict(new { code = "marker_job_has_active_run" });
        }
        if (await jobs.DeleteAsync(checked((ulong)jobId), cancellationToken).ConfigureAwait(false) is null)
        {
            return Results.NotFound(new { code = "marker_job_not_found" });
        }

        jobRecord.DeletedAtUtc = DateTimeOffset.UtcNow;
        jobRecord.UpdatedAtUtc = jobRecord.DeletedAtUtc.Value;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await events.RecordAsync(new DomainEvent
        {
            EventType = NetRatelEventTypes.Job.StateChanged,
            Source = "DevelopmentMcp",
            CorrelationId = correlation.GetOrCreate(),
            TenantId = tenantId.ToString(),
            EntityId = jobId.ToString(),
            Severity = "Info",
            Message = $"Development marker job {jobId} deleted.",
            Payload = new { jobId, tenantId, agentId, ownershipId = jobRecord.Id }
        }, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<(DevelopmentMcpScriptRecord? Record, IResult? Error)> RequireOwnedScriptAsync(
        int tenantId,
        Guid agentId,
        long scriptId,
        HttpContext http,
        OrchestratorDbContext db,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.ScriptMutation, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return (null, rejection);
        }
        var record = await db.DevelopmentMcpScripts.SingleOrDefaultAsync(candidate => candidate.ScriptId == scriptId && candidate.TenantId == tenantId && candidate.AgentId == agentId && candidate.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        return record is null ? (null, Results.NotFound(new { code = "marker_script_not_found" })) : (record, null);
    }

    private static async Task<(DevelopmentMcpMarkerJobRecord? Record, IResult? Error)> RequireOwnedJobAsync(
        int tenantId,
        Guid agentId,
        long jobId,
        DevelopmentOperatorOperation operation,
        HttpContext http,
        OrchestratorDbContext db,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        if (await RequireAcceptedAsync(http, tenantId, agentId, operation, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return (null, rejection);
        }
        var record = await db.DevelopmentMcpMarkerJobs.SingleOrDefaultAsync(candidate => candidate.JobId == jobId && candidate.TenantId == tenantId && candidate.AgentId == agentId && candidate.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        return record is null ? (null, Results.NotFound(new { code = "marker_job_not_found" })) : (record, null);
    }

    private static Task<IResult?> RequireAcceptedAsync(HttpContext http, int tenantId, Guid agentId, DevelopmentOperatorOperation operation, ICorrelationContext correlation, IDevelopmentOperatorTargetAuthority targets, CancellationToken cancellationToken)
        => DevelopmentOperatorTargetGate.RequireAcceptedAsync(http, tenantId, agentId, operation, correlation, targets, cancellationToken);

    private static bool IsExpectedScript(ScriptInfo? definition, DevelopmentMcpScriptRecord? record) =>
        definition is not null && record is not null &&
        string.Equals(definition.Content, DevelopmentMcpScriptEndpoints.Content(record.Marker, record.Shell, record.ExecutionMode), StringComparison.Ordinal) &&
        string.Equals(definition.ScriptType, record.Shell == "sh" ? "bash" : "powershell", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminal(JobRunState status) => status is JobRunState.Succeeded or JobRunState.Failed or JobRunState.Cancelled or JobRunState.TimedOut;
    private static string Folder(int tenantId, Guid agentId) => $"/mcp-dev/{tenantId}/{agentId:N}/jobs/";
    private static string MarkerJobPath(int tenantId, Guid agentId, ulong jobId) => $"/api/v2/development/mcp/agents/{tenantId}/{agentId:D}/marker-jobs/{jobId}";
    private static IResult TargetRejected(string code) => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Development target is not eligible for this operation.", extensions: new Dictionary<string, object?> { ["code"] = code });
    private static DevelopmentMcpMarkerJobDto ToDto(DevelopmentMcpMarkerJobRecord record) => new(checked((ulong)record.JobId), checked((ulong)record.ScriptId), record.TenantId, record.AgentId, record.Marker, record.CreatedAtUtc, record.UpdatedAtUtc);
}

public sealed record DevelopmentMcpMarkerJobDto(ulong JobId, ulong ScriptId, int TenantId, Guid AgentId, string Marker, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record DevelopmentMcpMarkerJobRunDto(ulong JobId, ulong RunId, int TenantId, Guid AgentId, string Status);
