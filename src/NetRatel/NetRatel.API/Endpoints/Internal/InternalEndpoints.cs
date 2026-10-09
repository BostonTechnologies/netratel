using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Events;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Endpoints;

/// <summary>
/// Paired RatelDesk scoped catalogue and submission contract. PostgreSQL owns the
/// catalogue/request records and the fenced Akka gateway owns dispatch.
/// </summary>
public static class InternalEndpoints
{
    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal").WithTags("Internal");
        group.MapGet("/health", () => Results.Ok(new { ok = true, service = "NetRatel.API" })).RequireAuthorization(OrchestrationManagedAuthorization.ReadPolicy);

        group.MapGet("/catalog/jobs", async (HttpContext http, IJobDefinitionService jobs, OrchestratorDbContext db, CancellationToken ct) =>
        {
            var principal = await OrchestrationManagedAuthorization.ResolveAsync(http, OrchestrationManagedAuthorization.ReadScope, ct);
            if (principal is null) return Results.Forbid();
            var definitions = await FilterDefinitionsAsync(db, await jobs.ListAsync(ct), principal, ct);
            return Results.Ok(await MapCatalogJobsAsync(db, definitions, ct));
        }).RequireAuthorization(OrchestrationManagedAuthorization.ReadPolicy);
        group.MapGet("/catalog/tenants", async (HttpContext http, OrchestratorDbContext db, CancellationToken ct) =>
        {
            var principal = await OrchestrationManagedAuthorization.ResolveAsync(http, OrchestrationManagedAuthorization.ReadScope, ct);
            if (principal is null) return Results.Forbid();
            var tenants = db.Tenants.AsNoTracking();
            tenants = tenants.Where(tenant => tenant.Id == principal.TenantId);
            return Results.Ok(await tenants.OrderBy(tenant => tenant.Name)
                .Select(tenant => new NetRatelCatalogTenantDto { TenantId = tenant.Id, Name = tenant.Name, IsActive = true }).ToArrayAsync(ct));
        }).RequireAuthorization(OrchestrationManagedAuthorization.ReadPolicy);
        group.MapGet("/catalog/request-definitions", async (HttpContext http, IJobDefinitionService jobs, OrchestratorDbContext db, CancellationToken ct) =>
        {
            var principal = await OrchestrationManagedAuthorization.ResolveAsync(http, OrchestrationManagedAuthorization.ReadScope, ct);
            if (principal is null) return Results.Forbid();
            var definitions = await FilterDefinitionsAsync(db, await jobs.ListAsync(ct), principal, ct);
            var catalog = await MapCatalogJobsAsync(db, definitions, ct);
            return Results.Ok(catalog.Select(ToRequestDefinition));
        }).RequireAuthorization(OrchestrationManagedAuthorization.ReadPolicy);

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
            var principal = await OrchestrationManagedAuthorization.ResolveAsync(http, OrchestrationManagedAuthorization.InvokeScope, ct);
            if (principal is null) return Results.Forbid();
            if (!ValidIdentifier(request.RequestId) || !ValidIdentifier(request.RequestTaskId) || !ValidIdentifier(request.CorrelationId) ||
                http.Request.Headers["X-Correlation-Id"].FirstOrDefault() is { Length: > 0 } header && header != request.CorrelationId)
                return Results.BadRequest(new { code = "invalid_correlation" });
            var correlationId = request.CorrelationId!;
            JsonObject payload;
            try { payload = JsonNode.Parse(string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { return Results.BadRequest(new { code = "invalid_payload" }); }
            var id = FirstNonEmpty(request.NetRatelRequestDefinitionId, request.NetRatelJobDefinitionId);
            if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var jobId) || jobId.ToString(CultureInfo.InvariantCulture) != id ||
                request.NetRatelJobDefinitionId is { Length: > 0 } alternate && alternate != id ||
                !OrchestrationManagedAuthorization.Constraints(principal).RequestDefinitionIds.Contains(id, StringComparer.Ordinal))
                return Results.Forbid();
            var job = await jobs.GetAsync(jobId, ct);
            if (job is null || (await FilterDefinitionsAsync(db, [job], principal, ct)).Count != 1) return Results.Forbid();
            var target = await ValidateTargetAsync(db, job, ct);
            if (target is null || principal.LinkId is not { Length: > 0 } mappingId) return Results.Forbid();
            PairingBusinessProfile profile;
            try { profile = await http.RequestServices.GetRequiredService<PairingBusinessProfileService>()
                .ResolveAsync(principal.TenantId, mappingId, "rateldesk.orchestration.callback", ct); }
            catch (PairingException) { return Results.Forbid(); }
            if (profile.Revision != principal.LinkRevision || profile.AuthorityHash != principal.GrantHash ||
                profile.Peer.InstallationId != principal.PeerInstanceId || profile.Mapping.RatelDeskOrganizationId != principal.PeerTenantId)
                return Results.Forbid();
            var callbackUrl = profile.Peer.ApiOrigin + "/api/v1/orchestration/provider/callback";
            if (request.CallbackUrl is { Length: > 0 } suppliedCallback && suppliedCallback != callbackUrl) return Results.Forbid();
            var fingerprint = IngestFingerprint(request, job, payload);
            var priorBinding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.ServicePrincipalId == principal.Id && x.ParentRequestId == request.RequestId && x.RequestTaskId == request.RequestTaskId, ct);
            if (priorBinding is not null)
            {
                if (priorBinding.IngestFingerprint != fingerprint || priorBinding.CorrelationId != correlationId ||
                    priorBinding.TenantId != target.TenantId || priorBinding.AgentId != target.AgentId || priorBinding.LinkId != principal.LinkId ||
                    priorBinding.LinkRevision != principal.LinkRevision || priorBinding.GrantHash != principal.GrantHash)
                    return Results.Conflict(new { code = "ingest_binding_conflict" });
                var prior = await ManagedOrchestrationRecovery.RecoverAsync(db, requests,
                    http.RequestServices.GetRequiredService<IJobRunService>(), priorBinding, ct);
                if (prior is null || prior.ExecutionId != priorBinding.ExecutionId || prior.ExecutionId is not { } executionId)
                    return Results.Conflict(new { code = "ingest_recovery_required" });
                return Results.Ok(new NetRatelIngestResponse { RequestId = prior.Id.ToString(), RunId = executionId, ExecutionId = executionId, Status = prior.Status, Message = prior.ResultMessage });
            }
            var binding = new ManagedOrchestrationRequestBinding
            {
                ServicePrincipalId = principal.Id, TenantId = target.TenantId, AgentId = target.AgentId,
                JobDefinitionId = job.Id.ToString(CultureInfo.InvariantCulture), ParentRequestId = request.RequestId,
                RequestTaskId = request.RequestTaskId, CorrelationId = correlationId, IngestFingerprint = fingerprint,
                LinkId = principal.LinkId, LinkRevision = principal.LinkRevision, GrantHash = principal.GrantHash,
                PeerInstanceId = principal.PeerInstanceId, PeerTenantId = principal.PeerTenantId, CallbackUrl = callbackUrl,
                CreatedAtUtc = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()
            };
            RequestInfo created;
            await using (var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null)
            {
                created = await requests.CreateAsync(new CreateRequestCommand(binding.SourceSystem, job.ClientIdentity, job.Id.ToString(), request.PayloadJson, target.TenantId, target.AgentId), ct);
                binding.RequestId = created.Id;
                // Persist the request, scoped binding and proven-undispatched run intent before the first actor call.
                var runId = http.RequestServices.GetRequiredService<JobAuthorityIdGenerator>().Next();
                var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                var runs = http.RequestServices.GetRequiredService<IJobRunService>();
                await runs.UpsertRunAsync(new(runId, job.Id, target.TenantId, job.ClientIdentity, binding.SourceSystem,
                    JobRunState.Pending, 0, now, null, null, null, EnrichPayload(request, created.Id, job),
                    JobExecutionRuntimePolicy.MergeOptionsJson(job.OptionsJson, JobExecutionRuntimePolicy.FromJobAndIngest(job, request)), target.AgentId), ct);
                db.Set<JobRunControlRecord>().Add(new() { RunId = checked((long)runId) });
                binding.ExecutionId = runId.ToString(CultureInfo.InvariantCulture);
                created = (await requests.UpdateAsync(new(created.Id, null, null, null, binding.ExecutionId,
                    null, null, null, null, null), ct))!;
                db.Set<ManagedOrchestrationRequestBinding>().Add(binding);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { return Results.Conflict(new { code = "ingest_binding_conflict" }); }
                if (transaction is not null) await transaction.CommitAsync(ct);
            }
            requestEvents.Publish(new RequestChangedEvent(created.Id, "external-service-created", created.UpdatedAtUtc));
            try
            {
                var inputs = EnrichPayload(request, created.Id, job);
                var invocation = new RunJobRequest(binding.SourceSystem, inputs, null, request.ExpectedRuntimeSeconds, request.GraceSeconds, request.HardTimeoutSeconds);
                var run = await authority.StartManagedAsync(job.Id, invocation, created.Id, http.User, ct);
                var logs = created.Logs.Concat([$"[{DateTimeOffset.UtcNow:O}] Accepted via /internal/ingest. JobRun {run.Id} started for job {job.Name} ({job.Id})."]).ToArray();
                binding.ExecutionId = run.Id.ToString(CultureInfo.InvariantCulture);
                var updated = await requests.UpdateAsync(new UpdateRequestCommand(created.Id, null, job.ClientIdentity, job.Id.ToString(), run.Id.ToString(), null, null, null, inputs, logs), ct).ConfigureAwait(false);
                if (updated is not null) requestEvents.Publish(new RequestChangedEvent(updated.Id, "external-service-accepted", updated.UpdatedAtUtc));
                return Results.Ok(new NetRatelIngestResponse { RequestId = created.Id.ToString(), RunId = run.Id.ToString(), ExecutionId = run.Id.ToString(), Status = "Accepted", Message = $"Request {created.Id} accepted for job '{job.Name}'." });
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            {
                loggers.CreateLogger("NetRatel.API.Endpoints.Internal").LogWarning(exception, "Paired ingest could not dispatch job {JobId}. correlationId={CorrelationId}", job.Id, correlationId);
                var logs = created.Logs.Concat([$"[{DateTimeOffset.UtcNow:O}] Failed to start NetRatel job: {exception.Message}"]).ToArray();
                // An uncertain actor outcome remains recoverable through this exact durable binding.
                var failed = await requests.UpdateAsync(new UpdateRequestCommand(created.Id, null, job.ClientIdentity, job.Id.ToString(), null, null, null, null, request.PayloadJson, logs), ct).ConfigureAwait(false);
                if (failed is not null) requestEvents.Publish(new RequestChangedEvent(failed.Id, "external-service-failed", failed.UpdatedAtUtc));
                return Results.Conflict(new { code = "job_authority_unavailable", detail = exception.Message, correlationId });
            }
        }).RequireAuthorization(OrchestrationManagedAuthorization.InvokePolicy);

        return app;
    }

    private static async Task<IReadOnlyList<JobDefinitionInfo>> FilterDefinitionsAsync(OrchestratorDbContext db,
        IReadOnlyList<JobDefinitionInfo> definitions, ServicePrincipalRegistration principal, CancellationToken ct)
    {
        var constraints = OrchestrationManagedAuthorization.Constraints(principal);
        if (constraints.TenantId != principal.TenantId.ToString(CultureInfo.InvariantCulture)) return [];
        var result = new List<JobDefinitionInfo>();
        foreach (var job in definitions.Where(job => job.TenantId == principal.TenantId &&
            constraints.RequestDefinitionIds.Contains(job.Id.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)))
        {
            var target = await ValidateTargetAsync(db, job, ct);
            if (target is not null && constraints.ResourceIds.Contains(target.AgentId.ToString("D"), StringComparer.Ordinal)) result.Add(job);
        }
        return result;
    }

    private static bool ValidIdentifier(string? value) => value is { Length: > 0 and <= 256 } && value == value.Trim() && !value.Any(char.IsControl);

    private static string IngestFingerprint(NetRatelIngestRequest request, JobDefinitionInfo job, JsonObject payload)
    {
        var json = JsonSerializer.Serialize(new { JobId = job.Id.ToString(CultureInfo.InvariantCulture), request.RequestId,
            request.RequestTaskId, request.CorrelationId, request.AutomationBindingId, Payload = payload,
            request.ExpectedRuntimeSeconds, request.GraceSeconds, request.HardTimeoutSeconds });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
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
        return agent is { IsEnabled: true, Status: AgentStatus.Active, RevokedAtUtc: null, DeletedAtUtc: null, SupersededByAgentId: null, SupersededAtUtc: null } ? new ResolvedTarget(tenant, id, identity) : null;
    }

    private static Task<ResolvedTarget?> ValidateTargetAsync(OrchestratorDbContext db, JobDefinitionInfo job, CancellationToken ct) => ResolveTargetAsync(db, job.TenantId, job.AgentId?.ToString() ?? job.ClientIdentity, ct);

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
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    private sealed record ResolvedTarget(int TenantId, Guid AgentId, string ClientIdentity);
    private sealed record CatalogParameter(long JobId, string Name, string Type, bool Required, string? DefaultValue, string? Description, string? OptionsJson);
    private sealed record CatalogScriptType(long JobId, string ScriptType);
}
