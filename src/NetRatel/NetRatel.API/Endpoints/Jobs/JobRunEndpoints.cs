using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.API.Endpoints;

/// <summary>Akka-authoritative job execution with PostgreSQL run history.</summary>
public static class JobRunEndpoints
{
    public static IEndpointRouteBuilder MapJobRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/jobruns").WithTags("Job Runs").RequireAuthorization();
        group.MapPost("/start/{jobId:long}", async (ulong jobId, [FromBody] RunJobRequest request, HttpContext http, IJobDefinitionService jobs, [FromServices] IEffectiveAccessService access, IAkkaJobAuthorityService authority, IEventRecorder events, ICorrelationContext correlation, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(jobId, ct); if (job is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, job.TenantId, ct)) return Results.Forbid();
            try
            {
                var run = await authority.StartAsync(jobId, request, ct);
                await events.RecordAsync(new DomainEvent { EventType = NetRatelEventTypes.Job.Submitted, Source = "Orchestration", CorrelationId = correlation.GetOrCreate(), EntityId = jobId.ToString(), Severity = "Info", Message = $"Job {jobId} submitted to Akka.", Payload = new { jobId, run.Id, run.TenantId, run.AgentId } }, ct);
                return Results.Ok(Map(run, string.Empty, null, null));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { code = "job_authority_unavailable", detail = ex.Message }); }
        });
        group.MapPost("/{id:long}/cancel", async (ulong id, HttpContext http, [FromServices] IEffectiveAccessService access, IAkkaJobAuthorityService authority, IJobRunService runs, CancellationToken ct) =>
        {
            var run = await runs.GetAsync(id, ct); if (run is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, run.TenantId, ct)) return Results.Forbid();
            if (Terminal(run.Status)) return Results.Conflict(new { message = $"Run {id} is already terminal ({run.Status})." });
            if (!await authority.CancelAsync(id, "operator-cancelled", ct)) return Results.Conflict(new { code = "job_authority_unavailable" });
            await ReconcileCancellationAsync(run, runs, ct);
            return Results.Accepted($"/api/v1/jobruns/{id}", new { runId = id, status = "Cancelled" });
        });
        group.MapDelete("/{id:long}", async (ulong id, HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, CancellationToken ct) =>
        {
            var run = await runs.GetAsync(id, ct); if (run is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, run.TenantId, ct)) return Results.Forbid();
            await runs.DeleteAsync(id, ct); return Results.NoContent();
        });
        group.MapGet("/", async (HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, IJobDefinitionService jobs, ITenantService tenants, [FromServices] OrchestratorDbContext db, [FromQuery] JobRunStatusDto? status, [FromQuery] ulong? jobId, [FromQuery] int? tenantId, [FromQuery] int? take, CancellationToken ct) =>
        {
            var list = await FilterAuthorizedAsync(await BuildAsync(runs, jobs, tenants, db, ct), http.User, access, ct);
            var filtered = list.Where(x => (!status.HasValue || x.Status == status) && (!jobId.HasValue || x.JobId == jobId) && (!tenantId.HasValue || x.TenantId == tenantId)).OrderByDescending(x => x.CreatedAtMicros);
            return Results.Ok(take is > 0 ? filtered.Take(take.Value) : filtered);
        });
        group.MapGet("/{id:long}", async (ulong id, HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, IJobDefinitionService jobs, ITenantService tenants, [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        {
            var run = await runs.GetAsync(id, ct); if (run is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, run.TenantId, ct)) return Results.Forbid();
            return Results.Ok((await BuildAsync(runs, jobs, tenants, db, ct)).First(x => x.Id == id));
        });
        group.MapGet("/query", async (HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, IJobDefinitionService jobs, ITenantService tenants, [FromServices] OrchestratorDbContext db, [FromQuery] ulong? jobId, [FromQuery] int? tenantId, [FromQuery] Guid? agentId, [FromQuery] string? search, [FromQuery] JobRunStatusDto? status, [FromQuery] int page = 0, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        {
            var all = await FilterAuthorizedAsync(await BuildAsync(runs, jobs, tenants, db, ct), http.User, access, ct);
            var q = all.Where(x => (!jobId.HasValue || x.JobId == jobId) && (!tenantId.HasValue || x.TenantId == tenantId) && (!agentId.HasValue || x.AgentId == agentId) && (!status.HasValue || x.Status == status));
            if (!string.IsNullOrWhiteSpace(search)) q = q.Where(x => $"{x.JobName} {x.TenantDisplayName} {x.ClientDisplayName} {x.Id}".Contains(search, StringComparison.OrdinalIgnoreCase));
            var total = q.Count(); return Results.Ok(new PagedJobRunsDto(q.OrderByDescending(x => x.CreatedAtMicros).Skip(Math.Max(0, page) * Math.Clamp(pageSize, 1, 100)).Take(Math.Clamp(pageSize, 1, 100)).ToList(), total));
        });
        group.MapGet("/{id:long}/steps", async (ulong id, HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, CancellationToken ct) =>
        {
            var detail = await runs.GetDetailsAsync(id, ct); if (detail is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, detail.Run.TenantId, ct)) return Results.Forbid();
            return Results.Ok(detail.Steps.OrderBy(x => x.Ordinal).Select(x => new JobStepRunDto(x.Id, x.JobRunId, x.JobStepId, (JobStepRunStatusDto)x.Status, x.Ordinal, x.TaskRequestId, Micros(x.StartedAtUtc), Micros(x.CompletedAtUtc), x.Error, detail.Activities.FirstOrDefault(a => a.RequestId == x.TaskRequestId)?.Error)));
        });
        group.MapGet("/{id:long}/steps/{ordinal:int}/logs", async (ulong id, int ordinal, HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, CancellationToken ct) =>
        {
            var detail = await runs.GetDetailsAsync(id, ct); var step = detail?.Steps.FirstOrDefault(x => x.Ordinal == ordinal); if (step is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, detail!.Run.TenantId, ct)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(step.TaskRequestId)) return Results.Ok(new { logs = Array.Empty<TaskLogDto>() });
            return Results.Ok(new { logs = await runs.GetLogsByRequestIdAsync(step.TaskRequestId, ct) });
        });
        return app;
    }
    private static async Task<List<JobRunDto>> BuildAsync(IJobRunService runs, IJobDefinitionService jobs, ITenantService tenants, OrchestratorDbContext db, CancellationToken ct)
    {
        var jobNames = (await jobs.ListAsync(ct)).ToDictionary(x => x.Id, x => x.Name); var tenantNames = (await tenants.ListAsync(ct)).ToDictionary(x => x.TenantId, x => x.Name); var agents = await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        return (await runs.ListAsync(ct)).Select(run => { jobNames.TryGetValue(run.JobId, out var job); tenantNames.TryGetValue(run.TenantId ?? -1, out var tenant); agents.TryGetValue(run.AgentId ?? Guid.Empty, out var agent); return Map(run, job ?? string.Empty, tenant, agent?.Name); }).ToList();
    }
    private static Task<bool> CanManageAsync(HttpContext http, IEffectiveAccessService access, int? tenantId, CancellationToken ct) =>
        access.AuthorizeAsync(http.User, NetRatelPermissions.JobManagement, tenantId, ct);
    private static async Task<List<JobRunDto>> FilterAuthorizedAsync(IEnumerable<JobRunDto> runs, System.Security.Claims.ClaimsPrincipal principal, IEffectiveAccessService access, CancellationToken ct)
    {
        var visible = new List<JobRunDto>();
        foreach (var run in runs)
            if (await access.AuthorizeAsync(principal, NetRatelPermissions.JobManagement, run.TenantId, ct)) visible.Add(run);
        return visible;
    }
    private static JobRunDto Map(JobRunInfo run, string jobName, string? tenantName, string? agentName) => new(run.Id, run.JobId, jobName, run.TenantId, run.ClientIdentity, run.StartedBy, (JobRunStatusDto)run.Status, run.CurrentStepOrdinal, Micros(run.CreatedAtUtc)!.Value, Micros(run.StartedAtUtc), Micros(run.CompletedAtUtc), run.Error, run.InputsJson, JobTemplateHelper.BuildOptions(run.OptionsJson, run.InputsJson), tenantName, agentName ?? (run.AgentId is null ? "Legacy target unavailable" : null), NetRatel.Shared.ClientEnvironment.None, run.AgentId);
    private static bool Terminal(JobRunState s) => s is JobRunState.Succeeded or JobRunState.Failed or JobRunState.Cancelled or JobRunState.TimedOut;
    private static long? Micros(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds() * 1000;
    private static async Task ReconcileCancellationAsync(JobRunInfo run, IJobRunService runs, CancellationToken ct)
    {
        await runs.UpsertRunAsync(new UpsertJobRunCommand(run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy, JobRunState.Cancelled, run.CurrentStepOrdinal, run.CreatedAtUtc, run.StartedAtUtc, DateTimeOffset.UtcNow, "Cancelled", run.InputsJson, run.OptionsJson, run.AgentId), ct);
    }
}
