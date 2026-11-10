using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Endpoints;

public static partial class JobRunEndpoints
{
    private static void MapAggregateRoutes(RouteGroupBuilder group)
    {
        group.MapPost("/preview/{jobId:long}", async (ulong jobId, RunJobRequest request,
            HttpContext http, IEffectiveAccessService access, IJobDefinitionService jobs,
            [FromServices] JobAggregateOrchestrator aggregates, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        {
            var job = await jobs.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, job.TenantId, ct)) return Results.Forbid();
            if (page is < 1 or > 10000 || pageSize is < 1 or > 100)
                return Results.BadRequest(new { code = "target_page_invalid", detail = "Use one-based pages with 1–100 targets." });
            try
            {
                var plan = await aggregates.PlanAsync(jobId, request, ct);
                return Results.Ok(new JobLaunchPreviewDto(plan.ComputeDigest(), plan.DefinitionDigest, plan.ExecutionTargetCount,
                    plan.Target, plan.ExecutionPolicy, aggregates.EffectiveConcurrency(plan), plan.GroupRevision, plan.Bindings,
                    plan.Nodes.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), page, pageSize,
                    (long)page * pageSize < plan.Nodes.Count ? page + 1 : null)
                { WorkerPoolCount = plan.NeedsHaWorkerSelection ? plan.Nodes.Count : null, GroupName = plan.GroupName });
            }
            catch (JobTargetValidationException error)
            { return Results.Json(new { code = error.Code, detail = error.Message }, statusCode: error.StatusCode); }
            catch (ArgumentException error)
            { return Results.BadRequest(new { code = "job_target_invalid", detail = error.Message }); }
        });
        group.MapGet("/{id:long}/nodes", async (ulong id, HttpContext http, IEffectiveAccessService access,
            IJobRunService runs, [FromServices] OrchestratorDbContext db, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
            CancellationToken ct = default) =>
        {
            var parent = await runs.GetAsync(id, ct);
            if (parent is null || parent.ParentRunId is not null) return Results.NotFound();
            if (!await CanManageAsync(http, access, parent.TenantId, ct)) return Results.Forbid();
            if (page is < 1 or > 10000 || pageSize is < 1 or > 100)
                return Results.BadRequest(new { code = "node_page_invalid", detail = "Use one-based pages with 1–100 nodes." });
            if (!parent.IsAggregate)
                return Results.Ok(new PagedJobRunNodesDto(page == 1 && parent.AgentId is { } agent ?
                    [LegacyNode(parent, agent)] : [], parent.AgentId is null ? 0 : 1));
            var query = db.Set<JobRunNodeRecord>().AsNoTracking().Where(node => node.ParentRunId == checked((long)id) && node.TenantId == parent.TenantId);
            var total = await query.CountAsync(ct);
            var rows = await query.OrderBy(node => node.Ordinal).ThenBy(node => node.AgentId)
                .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
            var childIds = rows.Where(row => row.NativeRunId is not null).Select(row => row.NativeRunId!.Value).ToArray();
            var steps = await db.JobRuns.AsNoTracking().Where(row => childIds.Contains(row.Id) && row.ParentRunId == checked((long)id) &&
                row.TenantId == parent.TenantId).ToDictionaryAsync(row => row.Id, row => row.CurrentStepOrdinal, ct);
            return Results.Ok(new PagedJobRunNodesDto(rows.Select(row => JobRunNodeProjection.ToDto(row,
                row.NativeRunId is { } native ? steps.GetValueOrDefault(native) : 0)).ToArray(), total));
        });
        group.MapGet("/{id:long}/target-plan", async (ulong id, HttpContext http, IEffectiveAccessService access,
            IJobRunService runs, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        {
            var parent = await runs.GetAsync(id, ct);
            if (parent is null || parent.ParentRunId is not null) return Results.NotFound();
            if (!await CanManageAsync(http, access, parent.TenantId, ct)) return Results.Forbid();
            if (page is < 1 or > 10000 || pageSize is < 1 or > 100) return Results.BadRequest(new { code = "target_page_invalid" });
            var plan = await runs.GetResolvedTargetPlanAsync(id, ct);
            if (plan is null) return Results.NotFound(new { code = "target_plan_not_recorded", detail = "This historical single-node run predates immutable target plans." });
            return Results.Ok(new JobLaunchPreviewDto(plan.ComputeDigest(), plan.DefinitionDigest, plan.ExecutionTargetCount,
                plan.Target, plan.ExecutionPolicy, parent.EffectiveConcurrency, plan.GroupRevision, plan.Bindings,
                plan.Nodes.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), page, pageSize,
                (long)page * pageSize < plan.Nodes.Count ? page + 1 : null)
            { WorkerPoolCount = plan.HaGroupSelection?.Candidates.Count, GroupName = plan.GroupName });
        });
        group.MapGet("/{id:long}/nodes/{agentId:guid}/steps", async (ulong id, Guid agentId,
            HttpContext http, IEffectiveAccessService access, IJobRunService runs, [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        {
            var parent = await runs.GetAsync(id, ct);
            if (parent is null || parent.ParentRunId is not null) return Results.NotFound();
            if (!await CanManageAsync(http, access, parent.TenantId, ct)) return Results.Forbid();
            var native = await FindNodeExecutionAsync(db, parent, agentId, ct);
            if (native is null)
            {
                var knownQueued = parent.IsAggregate && await db.Set<JobRunNodeRecord>().AsNoTracking()
                    .AnyAsync(node => node.ParentRunId == checked((long)id) && node.TenantId == parent.TenantId && node.AgentId == agentId && node.NativeRunId == null, ct);
                return knownQueued ? Results.Ok(Array.Empty<JobStepRunDto>()) : Results.NotFound();
            }
            var detail = await runs.GetDetailsAsync(native.Value, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail.Steps.OrderBy(step => step.Ordinal)
                .Select(step => MapStepRun(step, detail.Activities.SingleOrDefault(activity => activity.RequestId == step.TaskRequestId && activity.TenantId == parent.TenantId))));
        });
        group.MapGet("/{id:long}/nodes/{agentId:guid}/steps/{stepRunId:long}/logs", async (ulong id, Guid agentId,
            ulong stepRunId, HttpContext http, IEffectiveAccessService access, IJobRunService runs, [FromServices] OrchestratorDbContext db,
            [FromQuery] string? cursor = null, [FromQuery] int limit = 500, [FromQuery] int maximumBytes = 65536, CancellationToken ct = default) =>
        {
            var parent = await runs.GetAsync(id, ct);
            if (parent is null || parent.ParentRunId is not null) return Results.NotFound();
            if (!await CanManageAsync(http, access, parent.TenantId, ct)) return Results.Forbid();
            try { JobLogPageBuilder.ValidateBounds(limit, maximumBytes); _ = JobLogPageBuilder.ParseCursor(cursor); }
            catch (JobTargetValidationException error) { return Results.BadRequest(new { code = error.Code, detail = error.Message }); }
            var native = await FindNodeExecutionAsync(db, parent, agentId, ct);
            if (native is null) return Results.NotFound();
            var detail = await runs.GetDetailsAsync(native.Value, ct);
            var step = detail?.Steps.SingleOrDefault(step => step.Id == stepRunId && step.JobRunId == native);
            if (step is null) return Results.NotFound();
            var activity = detail!.Activities.SingleOrDefault(activity => activity.RequestId == step.TaskRequestId &&
                activity.JobRunId == native && activity.TenantId == parent.TenantId && activity.AgentId == agentId);
            return Results.Ok(activity is null || step.TaskRequestId is null
                ? new NetRatel.Shared.Contracts.Tasks.ExecutionLogDto(step.TaskRequestId, null, null, null, [])
                : await runs.GetForwardLogsPageAsync(step.TaskRequestId, cursor, limit, maximumBytes, ct));
        });
    }

    internal static async Task<ulong?> FindNodeExecutionAsync(OrchestratorDbContext db, JobRunInfo parent,
        Guid agentId, CancellationToken ct)
    {
        if (!parent.IsAggregate) return parent.ParentRunId is null && parent.AgentId == agentId ? parent.Id : null;
        var native = await (from node in db.Set<JobRunNodeRecord>().AsNoTracking()
                            join child in db.JobRuns.AsNoTracking() on node.NativeRunId equals (long?)child.Id
                            where node.ParentRunId == checked((long)parent.Id) && node.TenantId == parent.TenantId && node.AgentId == agentId &&
                                child.ParentRunId == node.ParentRunId && child.TenantId == node.TenantId && child.AgentId == node.AgentId &&
                                child.JobId == checked((long)parent.JobId)
                            select (long?)child.Id).SingleOrDefaultAsync(ct);
        return native is { } id ? checked((ulong)id) : null;
    }

    private static JobRunNodeDto LegacyNode(JobRunInfo parent, Guid agentId) => new(parent.Id, agentId, 1,
        parent.Id, parent.Status switch
        {
            JobRunState.Pending => JobRunNodeState.Claimed, JobRunState.Running => JobRunNodeState.Running,
            JobRunState.Succeeded => JobRunNodeState.Succeeded, JobRunState.Cancelled => JobRunNodeState.Cancelled,
            JobRunState.TimedOut => JobRunNodeState.TimedOut, JobRunState.Unknown => JobRunNodeState.Unknown, _ => JobRunNodeState.Failed
        }, parent.TargetHostName ?? string.Empty, parent.TargetDisplayName ?? parent.TargetName ?? agentId.ToString("D"),
        parent.Error, Micros(parent.StartedAtUtc), Micros(parent.CompletedAtUtc)) { CurrentStepOrdinal = parent.CurrentStepOrdinal, Environment = parent.Environment };

    private static async Task<JobRunDto> MapCurrentAsync(JobRunInfo run, IJobRunService runs,
        ITenantService tenants, OrchestratorDbContext db, CancellationToken ct)
    {
        var jobName = await db.Jobs.IgnoreQueryFilters().AsNoTracking().Where(job => job.Id == checked((long)run.JobId) && job.TenantId == run.TenantId)
            .Select(job => job.Name).SingleOrDefaultAsync(ct);
        var tenant = (await tenants.ListAsync(ct)).FirstOrDefault(tenant => tenant.TenantId == run.TenantId);
        var name = run.AgentId is { } agentId ? await db.Agents.AsNoTracking().Where(agent => agent.Id == agentId && agent.TenantId == run.TenantId)
            .Select(agent => agent.Name).SingleOrDefaultAsync(ct) : null;
        var result = Map(run, jobName ?? "Historical job", tenant?.Name, name);
        return run.IsAggregate ? result with { Nodes = await JobRunNodeProjection.ReadAsync(db, checked((long)run.Id), ct, 25), NodesTotalCount = run.TargetCount } : result;
    }
}
