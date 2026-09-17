using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Tenants;
using NetRatel.API.Services.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Endpoints;

/// <summary>PostgreSQL-backed job definition management with Agent-ID targets.</summary>
public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/jobs").WithTags("Jobs").RequireAuthorization("Operator");
        group.MapGet("/", async (IJobDefinitionService jobs, ITenantService tenants, [FromServices] OrchestratorDbContext db, [FromQuery] string? folder, [FromQuery] string? search, CancellationToken ct) =>
        {
            var agentLookup = await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            var tenantLookup = (await tenants.ListAsync(ct)).ToDictionary(x => x.TenantId, x => x.Name);
            var list = (await jobs.ListAsync(ct)).Where(x => string.IsNullOrWhiteSpace(folder) || x.FolderPath.StartsWith(NormalizeFolder(folder), StringComparison.OrdinalIgnoreCase))
                .Select(x => MapJob(x, tenantLookup, agentLookup)).Where(x => Matches(x, search)).OrderBy(x => x.FolderPath).ThenBy(x => x.Name).ToList();
            return Results.Ok(list);
        });
        group.MapGet("/{id:long}", async (ulong id, IJobDefinitionService jobs, ITenantService tenants, [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct); if (job is null) return Results.NotFound();
            return Results.Ok(MapJob(job, (await tenants.ListAsync(ct)).ToDictionary(x => x.TenantId, x => x.Name), await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct)));
        });
        group.MapGet("/{id:long}/details", async (ulong id, IJobDefinitionService jobs, ITenantService tenants, [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        {
            var detail = await jobs.GetDetailsAsync(id, ct); if (detail is null) return Results.NotFound();
            var dto = MapJob(detail.Job, (await tenants.ListAsync(ct)).ToDictionary(x => x.TenantId, x => x.Name), await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct));
            return Results.Ok(new JobWithDetailsDto(dto, detail.Steps.Select(MapStep).ToList(), detail.Params.Select(MapParam).ToList()));
        });
        group.MapPost("/", async ([FromBody] CreateJobRequest request, IJobDefinitionService jobs, [FromServices] OrchestratorDbContext db, IEventRecorder events, ICorrelationContext correlation, CancellationToken ct) =>
        {
            var targetError = await ValidateTargetAsync(db, request.TenantId, request.AgentId, ct); if (targetError is not null) return targetError;
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest("Name is required.");
            var options = JobSchedulePolicy.MergeOptionsJson(JobExecutionRuntimePolicy.MergeOptionsJson(null, JobExecutionRuntimePolicy.FromValues(request.ExpectedRuntimeSeconds, request.GraceSeconds, request.HardTimeoutSeconds)), request.Schedule);
            var created = await jobs.CreateAsync(new CreateJobDefinitionCommand(request.Name.Trim(), NormalizeFolder(request.FolderPath), request.Description, request.TenantId, string.Empty, options, request.AgentId), ct);
            await RecordAsync(events, correlation, NetRatelEventTypes.Job.Created, created, "created", ct);
            return Results.Created($"/api/v1/jobs/{created.Id}", MapJob(created, new Dictionary<int, string>(), await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct)));
        });
        group.MapPut("/{id:long}", async (ulong id, [FromBody] UpdateJobRequest request, IJobDefinitionService jobs, [FromServices] OrchestratorDbContext db, IEventRecorder events, ICorrelationContext correlation, CancellationToken ct) =>
        {
            var current = await jobs.GetAsync(id, ct); if (current is null) return Results.NotFound();
            var targetTenant = request.TenantId ?? current.TenantId; var targetAgent = request.AgentId ?? current.AgentId;
            var targetError = await ValidateTargetAsync(db, targetTenant, targetAgent, ct); if (targetError is not null) return targetError;
            var options = JobSchedulePolicy.MergeOptionsJson(JobExecutionRuntimePolicy.MergeOptionsJson(current.OptionsJson, JobExecutionRuntimePolicy.FromValues(request.ExpectedRuntimeSeconds, request.GraceSeconds, request.HardTimeoutSeconds)), request.Schedule);
            var updated = await jobs.UpdateAsync(new UpdateJobDefinitionCommand(id, request.Name, request.FolderPath is null ? null : NormalizeFolder(request.FolderPath), request.Description, targetTenant, string.Empty, options, targetAgent), ct);
            await RecordAsync(events, correlation, NetRatelEventTypes.Job.StateChanged, updated!, "updated", ct);
            return Results.Ok(MapJob(updated!, new Dictionary<int, string>(), await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct)));
        });
        group.MapDelete("/{id:long}", async (ulong id, IJobDefinitionService jobs, CancellationToken ct) => (await jobs.DeleteAsync(id, ct)) is null ? Results.NotFound() : Results.NoContent());
        group.MapGet("/{id:long}/params", async (ulong id, IJobDefinitionService jobs, CancellationToken ct) => await jobs.GetAsync(id, ct) is null ? Results.NotFound() : Results.Ok((await jobs.ListParamsAsync(id, ct)).Select(MapParam)));
        group.MapPost("/{id:long}/params", async (ulong id, [FromBody] AddJobParamRequest r, IJobDefinitionService jobs, CancellationToken ct) => (await jobs.AddParamAsync(new AddJobParameterCommand(id, r.Name, r.Type, r.Required, r.Default, r.Description, r.OptionsJson), ct)) is { } p ? Results.Ok(MapParam(p)) : Results.NotFound());
        group.MapPut("/params/{id:long}", async (ulong id, [FromBody] UpdateJobParamRequest r, IJobDefinitionService jobs, CancellationToken ct) => (await jobs.UpdateParamAsync(new UpdateJobParameterCommand(id, r.Name, r.Type, r.Required, r.Default, r.Description, r.OptionsJson), ct)) is { } p ? Results.Ok(MapParam(p)) : Results.NotFound());
        group.MapDelete("/params/{id:long}", async (ulong id, IJobDefinitionService jobs, CancellationToken ct) => await jobs.DeleteParamAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        group.MapGet("/{id:long}/steps", async (ulong id, IJobDefinitionService jobs, CancellationToken ct) => await jobs.GetAsync(id, ct) is null ? Results.NotFound() : Results.Ok((await jobs.ListStepsAsync(id, ct)).Select(MapStep)));
        group.MapPost("/{id:long}/steps", async (ulong id, [FromBody] AddJobStepRequest r, IJobDefinitionService jobs, CancellationToken ct) => (await jobs.AddStepAsync(new AddJobStepCommand(id, r.Ordinal, (JobStepKind)r.Type, r.Runner, r.Command, r.ScriptId, r.PayloadJson, r.Enabled), ct)) is { } s ? Results.Ok(MapStep(s)) : Results.NotFound());
        group.MapPut("/steps/{id:long}", async (ulong id, [FromBody] UpdateJobStepRequest r, IJobDefinitionService jobs, CancellationToken ct) => (await jobs.UpdateStepAsync(new UpdateJobStepCommand(id, r.Type is null ? null : (JobStepKind)r.Type, r.Runner, r.Command, r.ScriptId, r.PayloadJson, r.Enabled), ct)) is { } s ? Results.Ok(MapStep(s)) : Results.NotFound());
        group.MapPost("/steps/{id:long}/reorder", async (ulong id, [FromBody] ReorderJobStepRequest r, IJobDefinitionService jobs, CancellationToken ct) => (await jobs.ReorderStepAsync(id, r.NewOrdinal, ct)) is { } s ? Results.Ok(MapStep(s)) : Results.NotFound());
        group.MapDelete("/steps/{id:long}", async (ulong id, IJobDefinitionService jobs, CancellationToken ct) => await jobs.DeleteStepAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        return app;
    }

    private static async Task<IResult?> ValidateTargetAsync(OrchestratorDbContext db, int? tenantId, Guid? agentId, CancellationToken ct)
    {
        if (tenantId is null || agentId is null || agentId == Guid.Empty) return Results.ValidationProblem(new Dictionary<string, string[]> { ["target"] = ["TenantId and AgentId are required."] });
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == agentId, ct);
        return agent is null ? Results.BadRequest(new { code = "agent_not_found" }) : !agent.IsEnabled || agent.Status == AgentStatus.Disabled ? Results.Conflict(new { code = "agent_not_enabled" }) : null;
    }
    private static JobDto MapJob(JobDefinitionInfo job, IReadOnlyDictionary<int, string> tenants, IReadOnlyDictionary<Guid, Agent> agents)
    {
        agents.TryGetValue(job.AgentId ?? Guid.Empty, out var agent); tenants.TryGetValue(job.TenantId ?? -1, out var tenant);
        var policy = JobExecutionRuntimePolicy.FromJob(job);
        return new(job.Id, job.Name, job.FolderPath, job.Description, job.TenantId, job.ClientIdentity, job.CreatedAtUtc.ToUnixTimeMilliseconds() * 1000, job.UpdatedAtUtc.ToUnixTimeMilliseconds() * 1000, tenant, agent?.Name ?? (job.AgentId is null ? "Legacy target unavailable" : null), ClientEnvironment.None, policy.ExpectedRuntimeSeconds, policy.GraceSeconds, policy.HardTimeoutSeconds, JobSchedulePolicy.FromJob(job)?.ToDto(), job.AgentId);
    }
    private static bool Matches(JobDto job, string? search) => string.IsNullOrWhiteSpace(search) || ($"{job.Name} {job.FolderPath} {job.TenantDisplayName} {job.ClientDisplayName}").Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
    private static JobParamDto MapParam(JobParameterInfo p) => new(p.Id, p.JobId, p.Name, p.Type, p.Required, p.DefaultValue, p.Description, p.OptionsJson);
    private static JobStepDto MapStep(JobStepInfo s) => new(s.Id, s.JobId, s.Ordinal, (JobStepTypeDto)s.Type, s.Runner, s.Command, s.ScriptId, s.PayloadJson, s.Enabled);
    private static string NormalizeFolder(string? folder) { var value = string.IsNullOrWhiteSpace(folder) ? "/" : folder.Replace('\\', '/'); return (value.StartsWith('/') ? value : "/" + value).TrimEnd('/') + "/"; }
    private static Task RecordAsync(IEventRecorder events, ICorrelationContext correlation, string type, JobDefinitionInfo job, string action, CancellationToken ct) => events.RecordAsync(new DomainEvent { EventType = type, Source = "Orchestration", CorrelationId = correlation.GetOrCreate(), TenantId = job.TenantId?.ToString(), EntityId = job.Id.ToString(), Severity = "Info", Message = $"Job {job.Name} {action}.", Payload = new { job.Id, job.TenantId, job.AgentId } }, ct);
}
