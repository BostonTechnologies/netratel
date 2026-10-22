using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Tenants;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Security;

namespace NetRatel.API.Endpoints;

/// <summary>PostgreSQL-backed job definition management with Agent-ID targets.</summary>
public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/jobs").WithTags("Jobs").RequireAuthorization();
        group.MapGet("/", async (HttpContext http, IJobDefinitionService jobs, ITenantService tenants, [FromServices] IEffectiveAccessService access, [FromServices] OrchestratorDbContext db, [FromQuery] string? folder, [FromQuery] string? search, CancellationToken ct) =>
        {
            var agentLookup = await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            var tenantLookup = (await tenants.ListAsync(ct)).ToDictionary(x => x.TenantId, x => x.Name);
            var visible = await FilterAuthorizedAsync(await jobs.ListAsync(ct), http.User, access, ct);
            var summaries = await BuildSummariesAsync(db, visible.Select(x => x.Id).ToArray(), ct);
            var list = visible.Where(x => string.IsNullOrWhiteSpace(folder) || x.FolderPath.StartsWith(NormalizeFolder(folder), StringComparison.OrdinalIgnoreCase))
                .Select(x => MapJob(x, tenantLookup, agentLookup, summaries.GetValueOrDefault(x.Id))).Where(x => Matches(x, search)).OrderBy(x => x.FolderPath).ThenBy(x => x.Name).ToList();
            return Results.Ok(list);
        });
        group.MapGet("/{id:long}", async (ulong id, HttpContext http, IJobDefinitionService jobs, [FromServices] IEffectiveAccessService access, ITenantService tenants, [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct); if (job is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, job.TenantId, ct)) return Results.Forbid();
            var summaries = await BuildSummariesAsync(db, [id], ct);
            return Results.Ok(MapJob(job, (await tenants.ListAsync(ct)).ToDictionary(x => x.TenantId, x => x.Name), await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct), summaries.GetValueOrDefault(id)));
        });
        group.MapGet("/{id:long}/details", async (ulong id, HttpContext http, IJobDefinitionService jobs, [FromServices] IEffectiveAccessService access, ITenantService tenants, [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        {
            var detail = await jobs.GetDetailsAsync(id, ct); if (detail is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, detail.Job.TenantId, ct)) return Results.Forbid();
            var summaries = await BuildSummariesAsync(db, [id], ct);
            var dto = MapJob(detail.Job, (await tenants.ListAsync(ct)).ToDictionary(x => x.TenantId, x => x.Name), await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct), summaries.GetValueOrDefault(id));
            return Results.Ok(new JobWithDetailsDto(dto, detail.Steps.Select(MapStep).ToList(), detail.Params.Select(MapParam).ToList()));
        });
        group.MapPost("/", async ([FromBody] CreateJobRequest request, HttpContext http, [FromServices] IEffectiveAccessService access, IJobDefinitionService jobs, [FromServices] OrchestratorDbContext db, IEventRecorder events, ICorrelationContext correlation, CancellationToken ct) =>
        {
            if (!await CanManageAsync(http, access, request.TenantId, ct)) return Results.Forbid();
            var targetError = await ValidateTargetAsync(db, request.TenantId, request.AgentId, ct); if (targetError is not null) return targetError;
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest("Name is required.");
            if (request.Environment is { } environment && !Enum.IsDefined(environment)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["environment"] = ["Environment is invalid."] });
            var options = JobSchedulePolicy.MergeOptionsJson(JobExecutionRuntimePolicy.MergeOptionsJson(null, JobExecutionRuntimePolicy.FromValues(request.ExpectedRuntimeSeconds, request.GraceSeconds, request.HardTimeoutSeconds)), request.Schedule);
            options = JobExecutionContextPolicy.MergeOptionsJson(options, request.Environment);
            var created = await jobs.CreateAsync(new CreateJobDefinitionCommand(request.Name.Trim(), NormalizeFolder(request.FolderPath), request.Description, request.TenantId, string.Empty, options, request.AgentId), ct);
            await RecordAsync(events, correlation, NetRatelEventTypes.Job.Created, created, "created", ct);
            return Results.Created($"/api/v1/jobs/{created.Id}", MapJob(created, new Dictionary<int, string>(), await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct)));
        });
        group.MapPut("/{id:long}", async (ulong id, [FromBody] UpdateJobRequest request, HttpContext http, [FromServices] IEffectiveAccessService access, IJobDefinitionService jobs, [FromServices] OrchestratorDbContext db, IEventRecorder events, ICorrelationContext correlation, CancellationToken ct) =>
        {
            var current = await jobs.GetAsync(id, ct); if (current is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, current.TenantId, ct)) return Results.Forbid();
            var targetTenant = request.TenantId ?? current.TenantId; var targetAgent = request.AgentId ?? current.AgentId;
            if (!await CanManageAsync(http, access, targetTenant, ct)) return Results.Forbid();
            var targetError = await ValidateTargetAsync(db, targetTenant, targetAgent, ct); if (targetError is not null) return targetError;
            var options = JobSchedulePolicy.MergeOptionsJson(JobExecutionRuntimePolicy.MergeOptionsJson(current.OptionsJson, JobExecutionRuntimePolicy.FromValues(request.ExpectedRuntimeSeconds, request.GraceSeconds, request.HardTimeoutSeconds)), request.Schedule);
            if (request.Environment is { } environment && !Enum.IsDefined(environment)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["environment"] = ["Environment is invalid."] });
            options = JobExecutionContextPolicy.MergeOptionsJson(options, request.Environment);
            var updated = await jobs.UpdateAsync(new UpdateJobDefinitionCommand(id, request.Name, request.FolderPath is null ? null : NormalizeFolder(request.FolderPath), request.Description, targetTenant, string.Empty, options, targetAgent), ct);
            await RecordAsync(events, correlation, NetRatelEventTypes.Job.StateChanged, updated!, "updated", ct);
            return Results.Ok(MapJob(updated!, new Dictionary<int, string>(), await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct)));
        });
        group.MapDelete("/{id:long}", async (ulong id, HttpContext http, [FromServices] IEffectiveAccessService access, IJobDefinitionService jobs,
            IEventRecorder events, ICorrelationContext correlation, CancellationToken ct) =>
        {
            var current = await jobs.GetAsync(id, ct); if (current is null) return Results.NotFound();
            if (!await CanManageAsync(http, access, current.TenantId, ct)) return Results.Forbid();
            try
            {
                if (await jobs.DeleteAsync(id, ct) is not { } deleted) return Results.NotFound();
                await RecordAsync(events, correlation, NetRatelEventTypes.Job.StateChanged, deleted, "deleted", ct,
                    http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? http.User.Identity?.Name);
                return Results.NoContent();
            }
            catch (JobDefinitionInUseException)
            {
                return Results.Conflict(new { code = "job_has_active_runs", remediation = "Cancel or settle active runs before deleting the job." });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { code = "job_revision_conflict", remediation = "Reload the job before deleting it." });
            }
        });
        group.MapGet("/{id:long}/params", async (ulong id, HttpContext http, IJobDefinitionService jobs, [FromServices] IEffectiveAccessService access, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct); if (job is null) return Results.NotFound();
            return !await CanManageAsync(http, access, job.TenantId, ct) ? Results.Forbid() : Results.Ok((await jobs.ListParamsAsync(id, ct)).Select(MapParam));
        });
        group.MapPost("/{id:long}/params", async (ulong id, [FromBody] AddJobParamRequest r, HttpContext http, IJobDefinitionService jobs, [FromServices] IEffectiveAccessService access, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct); if (job is null) return Results.NotFound();
            return !await CanManageAsync(http, access, job.TenantId, ct) ? Results.Forbid() : (await jobs.AddParamAsync(new AddJobParameterCommand(id, r.Name, r.Type, r.Required, r.Default, r.Description, r.OptionsJson), ct)) is { } p ? Results.Ok(MapParam(p)) : Results.NotFound();
        });
        group.MapPut("/params/{id:long}", async (ulong id, [FromBody] UpdateJobParamRequest r, HttpContext http, [FromServices] IEffectiveAccessService access, [FromServices] OrchestratorDbContext db, IJobDefinitionService jobs, CancellationToken ct) =>
        {
            var scope = await JobScopeForParameterAsync(db, id, ct); if (scope is null) return Results.NotFound();
            return !await CanManageAsync(http, access, scope.TenantId, ct) ? Results.Forbid() : (await jobs.UpdateParamAsync(new UpdateJobParameterCommand(id, r.Name, r.Type, r.Required, r.Default, r.Description, r.OptionsJson), ct)) is { } p ? Results.Ok(MapParam(p)) : Results.NotFound();
        });
        group.MapDelete("/params/{id:long}", async (ulong id, HttpContext http, [FromServices] IEffectiveAccessService access, [FromServices] OrchestratorDbContext db, IJobDefinitionService jobs, CancellationToken ct) =>
        {
            var scope = await JobScopeForParameterAsync(db, id, ct); if (scope is null) return Results.NotFound();
            return !await CanManageAsync(http, access, scope.TenantId, ct) ? Results.Forbid() : await jobs.DeleteParamAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        });
        group.MapGet("/{id:long}/steps", async (ulong id, HttpContext http, IJobDefinitionService jobs, [FromServices] IEffectiveAccessService access, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct); if (job is null) return Results.NotFound();
            return !await CanManageAsync(http, access, job.TenantId, ct) ? Results.Forbid() : Results.Ok((await jobs.ListStepsAsync(id, ct)).Select(MapStep));
        });
        group.MapPost("/{id:long}/steps", async (ulong id, [FromBody] AddJobStepRequest r, HttpContext http, IJobDefinitionService jobs, [FromServices] IEffectiveAccessService access, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct); if (job is null) return Results.NotFound();
            return !await CanManageAsync(http, access, job.TenantId, ct) ? Results.Forbid() : (await jobs.AddStepAsync(new AddJobStepCommand(id, r.Ordinal, (JobStepKind)r.Type, r.Runner, r.Command, r.ScriptId, r.PayloadJson, r.Enabled), ct)) is { } s ? Results.Ok(MapStep(s)) : Results.NotFound();
        });
        group.MapPut("/steps/{id:long}", async (ulong id, [FromBody] UpdateJobStepRequest r, HttpContext http, [FromServices] IEffectiveAccessService access, [FromServices] OrchestratorDbContext db, IJobDefinitionService jobs, CancellationToken ct) =>
        {
            var scope = await JobScopeForStepAsync(db, id, ct); if (scope is null) return Results.NotFound();
            return !await CanManageAsync(http, access, scope.TenantId, ct) ? Results.Forbid() : (await jobs.UpdateStepAsync(new UpdateJobStepCommand(id, r.Type is null ? null : (JobStepKind)r.Type, r.Runner, r.Command, r.ScriptId, r.PayloadJson, r.Enabled), ct)) is { } s ? Results.Ok(MapStep(s)) : Results.NotFound();
        });
        group.MapPost("/steps/{id:long}/reorder", async (ulong id, [FromBody] ReorderJobStepRequest r, HttpContext http, [FromServices] IEffectiveAccessService access, [FromServices] OrchestratorDbContext db, IJobDefinitionService jobs, CancellationToken ct) =>
        {
            var scope = await JobScopeForStepAsync(db, id, ct); if (scope is null) return Results.NotFound();
            return !await CanManageAsync(http, access, scope.TenantId, ct) ? Results.Forbid() : (await jobs.ReorderStepAsync(id, r.NewOrdinal, ct)) is { } s ? Results.Ok(MapStep(s)) : Results.NotFound();
        });
        group.MapDelete("/steps/{id:long}", async (ulong id, HttpContext http, [FromServices] IEffectiveAccessService access, [FromServices] OrchestratorDbContext db, IJobDefinitionService jobs, CancellationToken ct) =>
        {
            var scope = await JobScopeForStepAsync(db, id, ct); if (scope is null) return Results.NotFound();
            return !await CanManageAsync(http, access, scope.TenantId, ct) ? Results.Forbid() : await jobs.DeleteStepAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        });
        return app;
    }

    private static async Task<IResult?> ValidateTargetAsync(OrchestratorDbContext db, int? tenantId, Guid? agentId, CancellationToken ct)
    {
        if (tenantId is null || agentId is null || agentId == Guid.Empty) return Results.ValidationProblem(new Dictionary<string, string[]> { ["target"] = ["TenantId and AgentId are required."] });
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == agentId, ct);
        return agent is null ? Results.BadRequest(new { code = "agent_not_found" }) : !agent.IsEnabled || agent.Status == AgentStatus.Disabled ? Results.Conflict(new { code = "agent_not_enabled" }) : null;
    }
    private static Task<bool> CanManageAsync(HttpContext http, IEffectiveAccessService access, int? tenantId, CancellationToken ct) =>
        access.AuthorizeAsync(http.User, NetRatelPermissions.JobManagement, tenantId, ct);
    private static async Task<IReadOnlyList<JobDefinitionInfo>> FilterAuthorizedAsync(IReadOnlyList<JobDefinitionInfo> jobs, System.Security.Claims.ClaimsPrincipal principal, IEffectiveAccessService access, CancellationToken ct)
    {
        var visible = new List<JobDefinitionInfo>();
        foreach (var job in jobs)
            if (await access.AuthorizeAsync(principal, NetRatelPermissions.JobManagement, job.TenantId, ct)) visible.Add(job);
        return visible;
    }
    private static Task<JobScope?> JobScopeForParameterAsync(OrchestratorDbContext db, ulong parameterId, CancellationToken ct) =>
        db.JobParameters.AsNoTracking().Where(x => x.Id == (long)parameterId).Select(x => new JobScope(x.Job.TenantId)).SingleOrDefaultAsync(ct);
    private static Task<JobScope?> JobScopeForStepAsync(OrchestratorDbContext db, ulong stepId, CancellationToken ct) =>
        db.JobSteps.AsNoTracking().Where(x => x.Id == (long)stepId).Select(x => new JobScope(x.Job.TenantId)).SingleOrDefaultAsync(ct);
    private sealed record JobScope(int? TenantId);
    private static JobDto MapJob(JobDefinitionInfo job, IReadOnlyDictionary<int, string> tenants, IReadOnlyDictionary<Guid, Agent> agents, string? summary = null)
    {
        agents.TryGetValue(job.AgentId ?? Guid.Empty, out var agent); tenants.TryGetValue(job.TenantId ?? -1, out var tenant);
        if (agent?.TenantId != job.TenantId) agent = null;
        var target = agent is null ? null : AgentDirectoryPresentation.Create(agent.TenantId, agent.Id,
            agent.Name, agent.IsEnabled, agent.DeviceInfoJson, tenant, agent.Tags, agent.FriendlyName);
        var policy = JobExecutionRuntimePolicy.FromJob(job);
        return new(job.Id, job.Name, job.FolderPath, job.Description, job.TenantId, job.ClientIdentity, job.CreatedAtUtc.ToUnixTimeMilliseconds() * 1000, job.UpdatedAtUtc.ToUnixTimeMilliseconds() * 1000, tenant, target?.HostName ?? target?.DisplayName ?? agent?.Name, JobExecutionContextPolicy.FromJob(job), policy.ExpectedRuntimeSeconds, policy.GraceSeconds, policy.HardTimeoutSeconds, JobSchedulePolicy.FromJob(job)?.ToDto(), job.AgentId, summary);
    }
    private static async Task<Dictionary<ulong, string>> BuildSummariesAsync(OrchestratorDbContext db, ulong[] jobIds, CancellationToken ct)
    {
        var ids = jobIds.Select(x => checked((long)x)).ToArray();
        var steps = await (from step in db.JobSteps.AsNoTracking()
            where ids.Contains(step.JobId) && step.Enabled
            join script in db.Scripts.AsNoTracking() on step.ScriptId equals (long?)script.Id into scripts
            from script in scripts.DefaultIfEmpty()
            orderby step.Ordinal
            select new { step.JobId, step.Type, step.Command, step.Runner, step.ScriptId,
                ScriptName = script == null ? null : script.Name, ScriptType = script == null ? null : script.ScriptType }).ToListAsync(ct);
        return steps.GroupBy(x => x.JobId).ToDictionary(x => (ulong)x.Key, group =>
            string.Join(" → ", group.Take(4).Select(step => step.Type == (int)JobStepKind.LibraryScript
                ? $"{step.ScriptName ?? $"Missing library script #{step.ScriptId}"} ({step.ScriptType ?? "type unknown"})"
                : step.Type == (int)JobStepKind.RunCommand ? CommandSummary(step.Command, step.Runner) : "HTTP action")) +
            (group.Count() > 4 ? $" · {group.Count()} steps" : string.Empty));
    }
    private static string CommandSummary(string? command, string? runner)
    {
        var label = OperatorOutputRedactor.Redact(command ?? "Command").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (label.Length > 80) label = label[..80] + "…";
        return string.IsNullOrWhiteSpace(runner) ? label : $"{runner}: {label}";
    }
    private static bool Matches(JobDto job, string? search) => string.IsNullOrWhiteSpace(search) || ($"{job.Name} {job.FolderPath} {job.TenantDisplayName} {job.ClientDisplayName}").Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
    private static JobParamDto MapParam(JobParameterInfo p) => new(p.Id, p.JobId, p.Name, p.Type, p.Required, p.DefaultValue, p.Description, p.OptionsJson);
    private static JobStepDto MapStep(JobStepInfo s) => new(s.Id, s.JobId, s.Ordinal, (JobStepTypeDto)s.Type, s.Runner, s.Command, s.ScriptId, s.PayloadJson, s.Enabled);
    private static string NormalizeFolder(string? folder) { var value = string.IsNullOrWhiteSpace(folder) ? "/" : folder.Replace('\\', '/'); return (value.StartsWith('/') ? value : "/" + value).TrimEnd('/') + "/"; }
    private static Task RecordAsync(IEventRecorder events, ICorrelationContext correlation, string type, JobDefinitionInfo job, string action, CancellationToken ct, string? actor = null) => events.RecordAsync(new DomainEvent { EventType = type, Source = "Orchestration", CorrelationId = correlation.GetOrCreate(), TenantId = job.TenantId?.ToString(), EntityId = job.Id.ToString(), Severity = "Info", Message = $"Job {job.Name} {action}.", Payload = new { job.Id, job.TenantId, job.AgentId, Actor = actor } }, ct);
}
