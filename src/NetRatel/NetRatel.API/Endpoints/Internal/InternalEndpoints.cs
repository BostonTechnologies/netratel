using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Application.Requests.Contracts;
using NetRatel.API.Application.Requests.Services;
using NetRatel.API.Services.Events;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Endpoints;

/// <summary>
/// ExternalService's M2M catalogue and submission contract. PostgreSQL owns the
/// catalogue/request records and the fenced Akka gateway owns dispatch.
/// </summary>
public static class InternalEndpoints
{
    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal").RequireAuthorization("M2MOnly").WithTags("Internal");
        group.MapGet("/health", () => Results.Ok(new { ok = true, service = "NetRatel.API" }));

        group.MapGet("/catalog/jobs", async (IJobDefinitionService jobs, OrchestratorDbContext db, CancellationToken ct) =>
        {
            var definitions = await jobs.ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(await MapCatalogJobsAsync(db, definitions, ct).ConfigureAwait(false));
        });
        group.MapGet("/catalog/tenants", async (OrchestratorDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Tenants.AsNoTracking().OrderBy(tenant => tenant.Name)
                .Select(tenant => new NetRatelCatalogTenantDto { TenantId = tenant.Id, Name = tenant.Name, IsActive = true })
                .ToArrayAsync(ct).ConfigureAwait(false)));
        group.MapGet("/catalog/request-definitions", async (IJobDefinitionService jobs, OrchestratorDbContext db, CancellationToken ct) =>
        {
            var definitions = await jobs.ListAsync(ct).ConfigureAwait(false);
            var catalog = await MapCatalogJobsAsync(db, definitions, ct).ConfigureAwait(false);
            return Results.Ok(catalog.Select(ToRequestDefinition));
        });

        group.MapPost("/catalog/request-definitions", async (
            CreateNetRatelCatalogRequestDefinitionRequest request,
            IJobDefinitionService jobs,
            OrchestratorDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { message = "Name is required." });
            var target = await ResolveTargetAsync(db, request.TenantId, request.ClientIdentity, ct).ConfigureAwait(false);
            if (target is null) return Results.UnprocessableEntity(TargetProblem(request.TenantId, request.ClientIdentity));
            var job = await jobs.CreateAsync(new CreateJobDefinitionCommand(
                request.Name.Trim(), NormalizeFolder(request.FolderPath), NormalizeOptional(request.Description), target.TenantId,
                target.ClientIdentity, AgentId: target.AgentId), ct).ConfigureAwait(false);
            var item = (await MapCatalogJobsAsync(db, [job], ct).ConfigureAwait(false)).Single();
            return Results.Created($"/internal/catalog/request-definitions/{job.Id}", ToRequestDefinition(item));
        });

        group.MapPost("/catalog/request-definitions/{requestDefinitionId}/inputs/sync", async (
            [FromRoute] string requestDefinitionId,
            SyncNetRatelCatalogRequestDefinitionInputsRequest request,
            IJobDefinitionService jobs,
            OrchestratorDbContext db,
            CancellationToken ct) =>
        {
            if (!ulong.TryParse(requestDefinitionId, out var jobId)) return Results.BadRequest(new { message = "Request definition id must be a valid NetRatel job id." });
            var job = await jobs.GetAsync(jobId, ct).ConfigureAwait(false);
            if (job is null) return Results.NotFound(new { message = $"No NetRatel request definition matched '{requestDefinitionId}'." });

            var desired = request.Inputs.Where(input => !string.IsNullOrWhiteSpace(input.Key))
                .GroupBy(input => input.Key.Trim(), StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
            var existing = await jobs.ListParamsAsync(jobId, ct).ConfigureAwait(false);
            var byName = existing.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
            var desiredNames = desired.Select(input => input.Key.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var input in desired)
            {
                var name = input.Key.Trim();
                if (byName.TryGetValue(name, out var current))
                {
                    await jobs.UpdateParamAsync(new UpdateJobParameterCommand(current.Id, name, NormalizeType(input.Type), input.Required,
                        NormalizeOptional(input.DefaultValue), NormalizeOptional(input.HelpText), NormalizeOptional(input.OptionsJson)), ct).ConfigureAwait(false);
                }
                else
                {
                    await jobs.AddParamAsync(new AddJobParameterCommand(jobId, name, NormalizeType(input.Type), input.Required,
                        NormalizeOptional(input.DefaultValue), NormalizeOptional(input.HelpText), NormalizeOptional(input.OptionsJson)), ct).ConfigureAwait(false);
                }
            }
            foreach (var parameter in existing.Where(parameter => !desiredNames.Contains(parameter.Name)))
                await jobs.DeleteParamAsync(parameter.Id, ct).ConfigureAwait(false);

            return Results.Ok(ToRequestDefinition((await MapCatalogJobsAsync(db, [job], ct).ConfigureAwait(false)).Single()));
        });

        group.MapPost("/ingest", async (
            NetRatelIngestRequest request,
            HttpContext http,
            IRequestService requests,
            IJobDefinitionService jobs,
            IAkkaJobAuthorityService authority,
            IRequestEventBus requestEvents,
            OrchestratorDbContext db,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.JobName)) return Results.BadRequest(new { message = "JobName is required." });
            var correlationId = FirstNonEmpty(request.CorrelationId, http.Request.Headers["X-Correlation-Id"].FirstOrDefault(), http.TraceIdentifier) ?? $"corr-{Guid.NewGuid():N}";
            var job = await ResolveJobAsync(jobs, request, ct).ConfigureAwait(false);
            if (job is null) return Results.NotFound(new { message = $"No NetRatel job definition matched '{request.JobName}'." });
            var existing = await ExternalServiceIngestRequestMatcher.FindExistingAsync(requests, request, ct).ConfigureAwait(false);
            if (existing is not null) return Results.Ok(new NetRatelIngestResponse { RequestId = existing.Id.ToString(), RunId = existing.ExecutionId, ExecutionId = existing.ExecutionId ?? existing.Id.ToString(), Status = existing.Status, Message = existing.ResultMessage });
            var target = await ValidateTargetAsync(db, job, ct).ConfigureAwait(false);
            if (target is null)
            {
                var problem = TargetProblem(job.TenantId, job.ClientIdentity, job.Id); problem.CorrelationId = correlationId;
                return Results.UnprocessableEntity(problem);
            }

            var created = await requests.CreateAsync(new CreateRequestCommand("external-service.api", job.ClientIdentity, job.Id.ToString(), request.PayloadJson, target.TenantId, target.AgentId), ct).ConfigureAwait(false);
            requestEvents.Publish(new RequestChangedEvent(created.Id, "external-service-created", created.UpdatedAtUtc));
            try
            {
                var inputs = EnrichPayload(request, created.Id, job);
                var run = await authority.StartAsync(job.Id, new RunJobRequest("external-service.api", inputs, null, request.ExpectedRuntimeSeconds, request.GraceSeconds, request.HardTimeoutSeconds), ct).ConfigureAwait(false);
                var logs = created.Logs.Concat([$"[{DateTimeOffset.UtcNow:O}] Accepted via /internal/ingest. JobRun {run.Id} started for job {job.Name} ({job.Id})."]).ToArray();
                var updated = await requests.UpdateAsync(new UpdateRequestCommand(created.Id, null, job.ClientIdentity, job.Id.ToString(), run.Id.ToString(), "Processing", null, null, inputs, logs), ct).ConfigureAwait(false);
                if (updated is not null) requestEvents.Publish(new RequestChangedEvent(updated.Id, "external-service-accepted", updated.UpdatedAtUtc));
                return Results.Ok(new NetRatelIngestResponse { RequestId = created.Id.ToString(), RunId = run.Id.ToString(), ExecutionId = run.Id.ToString(), Status = "Accepted", Message = $"Request {created.Id} accepted for job '{job.Name}'." });
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            {
                loggers.CreateLogger("NetRatel.API.Endpoints.Internal").LogWarning(exception, "ExternalService ingest could not dispatch job {JobId}. correlationId={CorrelationId}", job.Id, correlationId);
                var logs = created.Logs.Concat([$"[{DateTimeOffset.UtcNow:O}] Failed to start NetRatel job: {exception.Message}"]).ToArray();
                var failed = await requests.UpdateAsync(new UpdateRequestCommand(created.Id, null, job.ClientIdentity, job.Id.ToString(), null, "Failed", exception.Message, null, request.PayloadJson, logs), ct).ConfigureAwait(false);
                if (failed is not null) requestEvents.Publish(new RequestChangedEvent(failed.Id, "external-service-failed", failed.UpdatedAtUtc));
                return Results.Conflict(new { code = "job_authority_unavailable", detail = exception.Message, correlationId });
            }
        });

        group.MapPost("/requests/queue", async (QueueRequestDto request, IExecutionQueue queue, HttpContext http, CancellationToken ct) =>
        {
            var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
            await queue.EnqueueAsync(new QueuedItem(request.RequestId, request.Tenant, request.TypeKey, request.Inputs, request.CallbackUrl, correlationId), ct).ConfigureAwait(false);
            return Results.Accepted($"/internal/requests/{request.RequestId}");
        });
        group.MapPost("/notify-external-service", async (IHttpClientFactory factory, CancellationToken ct) =>
        {
            var response = await factory.CreateClient("ExternalServiceApi").PostAsJsonAsync("/internal/ingest", new { source = "orchestrator", at = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
            return Results.StatusCode((int)response.StatusCode);
        });
        return app;
    }

    private static async Task<IReadOnlyList<NetRatelCatalogJobDto>> MapCatalogJobsAsync(OrchestratorDbContext db, IReadOnlyList<JobDefinitionInfo> definitions, CancellationToken ct)
    {
        var jobIds = definitions.Select(job => checked((long)job.Id)).ToArray();
        var tenantIds = definitions.Where(job => job.TenantId.HasValue).Select(job => job.TenantId!.Value).Distinct().ToArray();
        var agentIds = definitions.Where(job => job.AgentId.HasValue).Select(job => job.AgentId!.Value).Distinct().ToArray();
        var tenants = await db.Tenants.AsNoTracking().Where(tenant => tenantIds.Contains(tenant.Id)).ToDictionaryAsync(tenant => tenant.Id, tenant => tenant.Name, ct).ConfigureAwait(false);
        var agents = await db.Agents.AsNoTracking().Where(agent => agentIds.Contains(agent.Id)).ToDictionaryAsync(agent => agent.Id, ct).ConfigureAwait(false);
        var parameters = await db.JobParameters.AsNoTracking().Where(parameter => jobIds.Contains(parameter.JobId))
            .Select(parameter => new CatalogParameter(parameter.JobId, parameter.Name, parameter.Type, parameter.Required, parameter.DefaultValue, parameter.Description, parameter.OptionsJson))
            .ToArrayAsync(ct).ConfigureAwait(false);
        var parametersByJob = parameters.ToLookup(parameter => parameter.JobId);
        var scriptTypes = await (from step in db.JobSteps.AsNoTracking()
                                 join script in db.Scripts.AsNoTracking() on step.ScriptId equals script.Id
                                 where jobIds.Contains(step.JobId) && step.Type == (int)JobStepKind.LibraryScript
                                 select new CatalogScriptType(step.JobId, script.ScriptType))
            .ToArrayAsync(ct).ConfigureAwait(false);
        var scriptTypesByJob = scriptTypes.ToLookup(item => item.JobId, item => item.ScriptType);
        var result = new List<NetRatelCatalogJobDto>(definitions.Count);
        foreach (var job in definitions)
        {
            agents.TryGetValue(job.AgentId ?? Guid.Empty, out var agent); tenants.TryGetValue(job.TenantId ?? -1, out var tenantName);
            var policy = JobExecutionRuntimePolicy.FromJob(job);
            var name = NormalizeOptional(agent?.Name);
            result.Add(new NetRatelCatalogJobDto
            {
                Id = job.Id.ToString(),
                Name = job.Name,
                DisplayName = DisplayName(job.FolderPath, job.Name),
                FolderPath = NormalizeFolder(job.FolderPath),
                TenantId = job.TenantId,
                TenantName = tenantName,
                Description = job.Description,
                ClientIdentity = job.ClientIdentity,
                ClientDisplayName = NetRatelCatalogMetadataProjection.BuildClientDisplayName(name, name, NetRatelCatalogMetadataProjection.BuildClientShortId(job.ClientIdentity)),
                ClientName = name,
                ClientShortId = NetRatelCatalogMetadataProjection.BuildClientShortId(job.ClientIdentity),
                ScriptType = NetRatelCatalogMetadataProjection.ResolveScriptType(scriptTypesByJob[checked((long)job.Id)]),
                ExpectedRuntimeSeconds = policy.ExpectedRuntimeSeconds,
                GraceSeconds = policy.GraceSeconds,
                HardTimeoutSeconds = policy.HardTimeoutSeconds,
                Inputs = parametersByJob[checked((long)job.Id)].OrderBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase).Select(MapInput).ToArray()
            });
        }
        return result.OrderBy(job => job.FolderPath, StringComparer.OrdinalIgnoreCase).ThenBy(job => job.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static NetRatelCatalogRequestDefinitionDto ToRequestDefinition(NetRatelCatalogJobDto job) => new()
    {
        RequestDefinitionId = job.Id,
        RequestDefinitionName = job.Name,
        DisplayName = job.DisplayName,
        Description = job.Description,
        FolderPath = job.FolderPath,
        NetRatelJobDefinitionId = job.Id,
        NetRatelJobDefinitionName = job.Name,
        TenantId = job.TenantId,
        TenantName = job.TenantName,
        ClientIdentity = job.ClientIdentity,
        ClientDisplayName = job.ClientDisplayName,
        ClientHostName = job.ClientHostName,
        ClientName = job.ClientName,
        ClientShortId = job.ClientShortId,
        ScriptType = job.ScriptType,
        ExpectedRuntimeSeconds = job.ExpectedRuntimeSeconds,
        GraceSeconds = job.GraceSeconds,
        HardTimeoutSeconds = job.HardTimeoutSeconds,
        Inputs = job.Inputs
    };

    private static async Task<JobDefinitionInfo?> ResolveJobAsync(IJobDefinitionService jobs, NetRatelIngestRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.NetRatelJobDefinitionId) && ulong.TryParse(request.NetRatelJobDefinitionId, out var requestedId)) return await jobs.GetAsync(requestedId, ct).ConfigureAwait(false);
        if (ulong.TryParse(request.JobName, out var numericId)) return await jobs.GetAsync(numericId, ct).ConfigureAwait(false);
        var name = request.JobName.Trim();
        var matches = (await jobs.ListAsync(ct).ConfigureAwait(false)).Where(job => string.Equals($"{job.FolderPath.TrimEnd('/')}/{job.Name}".Trim(), name, StringComparison.OrdinalIgnoreCase) || string.Equals(job.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static async Task<ResolvedTarget?> ResolveTargetAsync(OrchestratorDbContext db, int? tenantId, string? clientIdentity, CancellationToken ct)
    {
        if (tenantId is not { } tenant || string.IsNullOrWhiteSpace(clientIdentity)) return null;
        var identity = clientIdentity.Trim();
        Guid? agentId = Guid.TryParse(identity, out var direct) ? direct : null;
        if (agentId is null)
        {
            var normalized = PrimaryClientAgentBindingService.NormalizePrimaryClientIdentity(identity);
            agentId = await db.PrimaryClientAgentBindings.AsNoTracking().Where(binding => binding.TenantId == tenant && binding.PrimaryClientIdentity == normalized && binding.Status == NetRatel.Application.Agents.PrimaryClientAgentBindingStatus.Bound).Select(binding => binding.AgentId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }
        if (agentId is not { } id) return null;
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.TenantId == tenant && candidate.Id == id, ct).ConfigureAwait(false);
        return agent is { IsEnabled: true, Status: AgentStatus.Active, RevokedAtUtc: null } ? new ResolvedTarget(tenant, id, identity) : null;
    }

    private static Task<ResolvedTarget?> ValidateTargetAsync(OrchestratorDbContext db, JobDefinitionInfo job, CancellationToken ct) => ResolveTargetAsync(db, job.TenantId, job.AgentId?.ToString() ?? job.ClientIdentity, ct);

    private static NetRatelIngestProblem TargetProblem(int? tenantId, string? identity, ulong? jobId = null) => new()
    {
        ErrorCode = "target_client_not_found",
        Message = tenantId is null ? "A tenant-bound, active Agent target is required before this request can be dispatched." : "The target client is not bound to an active gateway Agent for this tenant. Rebind or retarget it before retrying.",
        NetRatelJobDefinitionId = jobId?.ToString(),
        TargetClientIdentity = identity
    };

    private static string EnrichPayload(NetRatelIngestRequest request, int requestId, JobDefinitionInfo job)
    {
        JsonObject root;
        try { root = JsonNode.Parse(string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson)?.AsObject() ?? new JsonObject(); }
        catch (Exception exception) { throw new InvalidOperationException($"PayloadJson is invalid JSON: {exception.Message}", exception); }
        var metadata = root["meta"] as JsonObject ?? new JsonObject(); root["meta"] = metadata;
        metadata["netratelRequestId"] = requestId.ToString(); metadata["netratelRunId"] = null; metadata["netratelJobDefinitionId"] = request.NetRatelJobDefinitionId ?? job.Id.ToString(); metadata["netratelJobDefinitionName"] = job.Name;
        var policy = JobExecutionRuntimePolicy.FromJobAndIngest(job, request); metadata["expectedRuntimeSeconds"] = policy.ExpectedRuntimeSeconds; metadata["graceSeconds"] = policy.GraceSeconds; metadata["hardTimeoutSeconds"] = policy.HardTimeoutSeconds;
        Add(metadata, "automationBindingId", request.AutomationBindingId); Add(metadata, "netratelRequestDefinitionId", request.NetRatelRequestDefinitionId); Add(metadata, "correlationId", request.CorrelationId); Add(metadata, "requestTaskId", request.RequestTaskId); Add(metadata, "requestId", request.RequestId);
        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static void Add(JsonObject root, string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) root[name] = value; }
    private static NetRatelCatalogInputDefinitionDto MapInput(CatalogParameter parameter) => new() { Key = parameter.Name, Label = parameter.Name, Type = parameter.Type, Required = parameter.Required, DefaultValue = parameter.DefaultValue, HelpText = parameter.Description, OptionsJson = parameter.OptionsJson, Order = 0 };
    private static string NormalizeFolder(string? value) { var result = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim(); return result.StartsWith('/') ? result : $"/{result}"; }
    private static string DisplayName(string? folder, string name) { var value = NormalizeFolder(folder).TrimEnd('/'); return string.IsNullOrEmpty(value) ? name : $"{value}/{name}"; }
    private static string NormalizeType(string? value) => value?.Trim().ToLowerInvariant() switch { "textarea" => "textarea", "number" => "number", "boolean" => "boolean", "select" => "select", "radio" => "radio", "date" => "date", "datetime" => "datetime", _ => "text" };
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    private sealed record ResolvedTarget(int TenantId, Guid AgentId, string ClientIdentity);
    private sealed record CatalogParameter(long JobId, string Name, string Type, bool Required, string? DefaultValue, string? Description, string? OptionsJson);
    private sealed record CatalogScriptType(long JobId, string ScriptType);
}
