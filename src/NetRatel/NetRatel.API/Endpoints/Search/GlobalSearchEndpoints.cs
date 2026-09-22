using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Scripts;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Endpoints.Search;

/// <summary>Current business search over PostgreSQL directory and orchestration projections.</summary>
public static class GlobalSearchEndpoints
{
    private const int MaxPageSize = 25;

    public static IEndpointRouteBuilder MapGlobalSearchEndpoints(this IEndpointRouteBuilder app)
    {
        // Search is an interactive application feature. The individual
        // handlers resolve the caller's current tenant permission before
        // building their database query; an Operator claim is deliberately
        // not used as a substitute for tenant-scoped visibility.
        var group = app.MapGroup("/api/v1/global-search").WithTags("Global Search").RequireAuthorization("InteractiveAccount");
        group.MapGet("/tenants", SearchTenantsAsync);
        group.MapGet("/scripts", SearchScriptsAsync);
        group.MapGet("/jobs", SearchJobsAsync);
        group.MapGet("/requests", SearchRequestsAsync);
        group.MapGet("/clients", SearchAgentsAsync);
        group.MapGet("/tasks", SearchTasksAsync);
        return app;
    }

    private static async Task<IResult> SearchTenantsAsync(
        [FromServices] OrchestratorDbContext db,
        [FromServices] IEffectiveAccessService access,
        ClaimsPrincipal principal,
        [FromQuery] string? q,
        [FromQuery] int pageSize = 6,
        CancellationToken ct = default)
    {
        var term = NormalizeTerm(q);
        var take = NormalizePageSize(pageSize);
        var tenantIds = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.TenantAdministration, ct)
            .ConfigureAwait(false);
        var query = ScopedTenants(db, tenantIds);
        if (term is not null)
        {
            var like = Like(term);
            var hasId = int.TryParse(term, out var id);
            query = db.Database.IsNpgsql()
                ? query.Where(tenant =>
                    EF.Functions.ILike(tenant.Name, like) ||
                    (tenant.Description != null && EF.Functions.ILike(tenant.Description, like)) ||
                    (tenant.Location != null && EF.Functions.ILike(tenant.Location, like)) ||
                    (tenant.ContactPerson != null && EF.Functions.ILike(tenant.ContactPerson, like)) ||
                    (tenant.ContactEmail != null && EF.Functions.ILike(tenant.ContactEmail, like)) ||
                    (hasId && tenant.Id == id))
                : query.Where(tenant =>
                    EF.Functions.Like(tenant.Name.ToUpper(), like.ToUpper(), "\\") ||
                    (tenant.Description != null && EF.Functions.Like(tenant.Description.ToUpper(), like.ToUpper(), "\\")) ||
                    (tenant.Location != null && EF.Functions.Like(tenant.Location.ToUpper(), like.ToUpper(), "\\")) ||
                    (tenant.ContactPerson != null && EF.Functions.Like(tenant.ContactPerson.ToUpper(), like.ToUpper(), "\\")) ||
                    (tenant.ContactEmail != null && EF.Functions.Like(tenant.ContactEmail.ToUpper(), like.ToUpper(), "\\")) ||
                    (hasId && tenant.Id == id));
        }

        var rows = await query
            .OrderBy(tenant => tenant.Name)
            .Take(take)
            .Select(tenant => new TenantDto(
                tenant.Id,
                tenant.Name,
                tenant.Description,
                tenant.Location,
                tenant.Domains,
                tenant.ContactPerson,
                tenant.ContactEmail,
                tenant.AutoUpdate,
                tenant.CreatedAtUtc,
                tenant.UpdatedAtUtc))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return Results.Ok(Page(rows, take));
    }

    private static async Task<IResult> SearchScriptsAsync(
        [FromServices] OrchestratorDbContext db,
        [FromServices] IEffectiveAccessService access,
        ClaimsPrincipal principal,
        [FromQuery] string? q,
        [FromQuery] int pageSize = 6,
        CancellationToken ct = default)
    {
        var term = NormalizeTerm(q);
        var take = NormalizePageSize(pageSize);
        if (!await access.HasInstancePermissionAsync(principal, NetRatelPermissions.ScriptEdit, ct).ConfigureAwait(false))
        {
            return Results.Ok(Page(Array.Empty<ScriptDto>(), take));
        }

        var query = db.Scripts.AsNoTracking();
        if (term is not null)
        {
            var like = Like(term);
            query = db.Database.IsNpgsql()
                ? query.Where(script =>
                    EF.Functions.ILike(script.Name, like) ||
                    EF.Functions.ILike(script.FolderPath, like) ||
                    EF.Functions.ILike(script.Description, like) ||
                    EF.Functions.ILike(script.ScriptType, like))
                : query.Where(script =>
                    EF.Functions.Like(script.Name.ToUpper(), like.ToUpper(), "\\") ||
                    EF.Functions.Like(script.FolderPath.ToUpper(), like.ToUpper(), "\\") ||
                    EF.Functions.Like(script.Description.ToUpper(), like.ToUpper(), "\\") ||
                    EF.Functions.Like(script.ScriptType.ToUpper(), like.ToUpper(), "\\"));
        }

        var rows = await query
            .OrderBy(script => script.FolderPath)
            .ThenBy(script => script.Name)
            .Take(take)
            .Select(script => new ScriptDto(
                (ulong)script.Id,
                script.Name,
                script.FolderPath,
                script.Description,
                null,
                script.ScriptType,
                script.CreatedAtUtc,
                script.UpdatedAtUtc))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return Results.Ok(Page(rows, take));
    }

    private static async Task<IResult> SearchAgentsAsync(
        [FromServices] OrchestratorDbContext db,
        [FromServices] IEffectiveAccessService access,
        ClaimsPrincipal principal,
        [FromServices] DatabaseCommandMetricsInterceptor commandMetrics,
        ILoggerFactory loggerFactory,
        [FromQuery] string? q,
        [FromQuery] int pageSize = 6,
        CancellationToken ct = default)
    {
        var term = NormalizeTerm(q);
        var take = NormalizePageSize(pageSize);
        var tenantIds = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.ClientManagement, ct)
            .ConfigureAwait(false);
        commandMetrics.Reset();
        var started = Stopwatch.GetTimestamp();
        var queryStarted = Stopwatch.GetTimestamp();
        var rows = await BuildAgentQuery(db, term, tenantIds)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var queryElapsed = Stopwatch.GetElapsedTime(queryStarted);
        var mappingStarted = Stopwatch.GetTimestamp();
        var items = rows.Select(MapAgent).ToList();
        var mappingElapsed = Stopwatch.GetElapsedTime(mappingStarted);
        LogSearch(loggerFactory, commandMetrics, "clients", term, rows.Count, items.Count, started, queryElapsed, mappingElapsed);
        return Results.Ok(Page(items, take));
    }

    private static async Task<IResult> SearchJobsAsync(
        [FromServices] OrchestratorDbContext db,
        [FromServices] IEffectiveAccessService access,
        ClaimsPrincipal principal,
        [FromServices] DatabaseCommandMetricsInterceptor commandMetrics,
        ILoggerFactory loggerFactory,
        [FromQuery] string? q,
        [FromQuery] int pageSize = 6,
        CancellationToken ct = default)
    {
        var term = NormalizeTerm(q);
        var take = NormalizePageSize(pageSize);
        var tenantIds = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.JobManagement, ct)
            .ConfigureAwait(false);
        commandMetrics.Reset();
        var started = Stopwatch.GetTimestamp();
        var queryStarted = Stopwatch.GetTimestamp();
        var rows = await BuildJobQuery(db, term, tenantIds)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var queryElapsed = Stopwatch.GetElapsedTime(queryStarted);
        var mappingStarted = Stopwatch.GetTimestamp();
        var items = rows.Select(MapJob).ToList();
        var mappingElapsed = Stopwatch.GetElapsedTime(mappingStarted);
        LogSearch(loggerFactory, commandMetrics, "jobs", term, rows.Count, items.Count, started, queryElapsed, mappingElapsed);
        return Results.Ok(Page(items, take));
    }

    private static async Task<IResult> SearchRequestsAsync(
        [FromServices] OrchestratorDbContext db,
        [FromServices] IEffectiveAccessService access,
        ClaimsPrincipal principal,
        [FromServices] DatabaseCommandMetricsInterceptor commandMetrics,
        ILoggerFactory loggerFactory,
        [FromQuery] string? q,
        [FromQuery] int pageSize = 6,
        CancellationToken ct = default)
    {
        var term = NormalizeTerm(q);
        var take = NormalizePageSize(pageSize);
        var tenantIds = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.JobManagement, ct)
            .ConfigureAwait(false);
        commandMetrics.Reset();
        var started = Stopwatch.GetTimestamp();
        var queryStarted = Stopwatch.GetTimestamp();
        var rows = await BuildRequestQuery(db, term, tenantIds)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var queryElapsed = Stopwatch.GetElapsedTime(queryStarted);
        var mappingStarted = Stopwatch.GetTimestamp();
        var items = rows.Select(MapRequest).ToList();
        var mappingElapsed = Stopwatch.GetElapsedTime(mappingStarted);
        LogSearch(loggerFactory, commandMetrics, "requests", term, rows.Count, items.Count, started, queryElapsed, mappingElapsed);
        return Results.Ok(Page(items, take));
    }

    private static async Task<IResult> SearchTasksAsync(
        [FromServices] OrchestratorDbContext db,
        [FromServices] IEffectiveAccessService access,
        ClaimsPrincipal principal,
        [FromServices] DatabaseCommandMetricsInterceptor commandMetrics,
        ILoggerFactory loggerFactory,
        [FromQuery] string? q,
        [FromQuery] int pageSize = 6,
        CancellationToken ct = default)
    {
        var term = NormalizeTerm(q);
        var take = NormalizePageSize(pageSize);
        var tenantIds = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.JobManagement, ct)
            .ConfigureAwait(false);
        commandMetrics.Reset();
        var started = Stopwatch.GetTimestamp();
        var queryStarted = Stopwatch.GetTimestamp();
        var rows = await BuildTaskQuery(db, term, tenantIds)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var queryElapsed = Stopwatch.GetElapsedTime(queryStarted);
        var mappingStarted = Stopwatch.GetTimestamp();
        var items = rows.Select(MapTask).ToList();
        var mappingElapsed = Stopwatch.GetElapsedTime(mappingStarted);
        LogSearch(loggerFactory, commandMetrics, "tasks", term, rows.Count, items.Count, started, queryElapsed, mappingElapsed);
        return Results.Ok(Page(items, take));
    }

    internal static IQueryable<AgentDirectoryRow> BuildAgentQuery(OrchestratorDbContext db, string? term, int[]? tenantIds = null) =>
        AgentDirectorySearch.Query(db, term, tenantIds);

    internal static IQueryable<JobSearchRow> BuildJobQuery(OrchestratorDbContext db, string? term, int[]? tenantIds = null)
    {
        var matchingAgents = AgentDirectorySearch.MatchingAgents(db, term);
        var matchingTenantIds = MatchingTenantIds(db, term);
        var jobs = db.Jobs.AsNoTracking();
        if (tenantIds is not null)
        {
            jobs = jobs.Where(job => job.TenantId.HasValue && tenantIds.Contains(job.TenantId.Value));
        }
        if (term is not null)
        {
            var like = Like(term);
            var hasId = long.TryParse(term, out var id);
            jobs = db.Database.IsNpgsql()
                ? jobs.Where(job =>
                    EF.Functions.ILike(job.Name, like) || EF.Functions.ILike(job.FolderPath, like) ||
                    (job.Description != null && EF.Functions.ILike(job.Description, like)) || EF.Functions.ILike(job.ClientIdentity, like) ||
                    (hasId && job.Id == id) || matchingTenantIds.Any(tenantId => (int?)tenantId == job.TenantId) ||
                    matchingAgents.Any(match => (int?)match.TenantId == job.TenantId && (Guid?)match.Id == job.AgentId))
                : jobs.Where(job =>
                    EF.Functions.Like(job.Name.ToUpper(), like.ToUpper(), "\\") || EF.Functions.Like(job.FolderPath.ToUpper(), like.ToUpper(), "\\") ||
                    (job.Description != null && EF.Functions.Like(job.Description.ToUpper(), like.ToUpper(), "\\")) || EF.Functions.Like(job.ClientIdentity.ToUpper(), like.ToUpper(), "\\") ||
                    (hasId && job.Id == id) || matchingTenantIds.Any(tenantId => (int?)tenantId == job.TenantId) ||
                    matchingAgents.Any(match => (int?)match.TenantId == job.TenantId && (Guid?)match.Id == job.AgentId));
        }

        return from job in jobs
               join tenantValue in db.Tenants.AsNoTracking()
                   on job.TenantId equals (int?)tenantValue.Id into tenantJoin
               from tenant in tenantJoin.DefaultIfEmpty()
               join agentValue in db.Agents.AsNoTracking()
                   on new { job.TenantId, job.AgentId }
                   equals new { TenantId = (int?)agentValue.TenantId, AgentId = (Guid?)agentValue.Id } into agentJoin
               from agent in agentJoin.DefaultIfEmpty()
               orderby job.FolderPath, job.Name, job.Id
               select new JobSearchRow(
                   job.Id,
                   job.Name,
                   job.FolderPath,
                   job.Description,
                   job.TenantId,
                   job.AgentId,
                   job.ClientIdentity,
                   job.CreatedAtUtc,
                   job.UpdatedAtUtc,
                   tenant == null ? null : tenant.Name,
                   agent == null ? null : agent.Name,
                   agent == null ? null : agent.IsEnabled,
                   agent == null ? null : agent.DeviceInfoJson);

    }

    internal static IQueryable<RequestSearchRow> BuildRequestQuery(OrchestratorDbContext db, string? term, int[]? tenantIds = null)
    {
        var matchingAgents = AgentDirectorySearch.MatchingAgents(db, term);
        var matchingTenantIds = MatchingTenantIds(db, term);
        var matchingJobIds = MatchingJobIds(db, term);
        var requests = db.Requests.AsNoTracking();
        if (tenantIds is not null)
        {
            requests = requests.Where(request => request.TargetTenantId.HasValue && tenantIds.Contains(request.TargetTenantId.Value));
        }
        if (term is not null)
        {
            var like = Like(term);
            var hasId = int.TryParse(term, out var id);
            requests = db.Database.IsNpgsql()
                ? requests.Where(request =>
                    (hasId && request.Id == id) || EF.Functions.ILike(request.SourceSystem, like) || EF.Functions.ILike(request.Status, like) ||
                    (request.JobDefinitionId != null && EF.Functions.ILike(request.JobDefinitionId, like)) ||
                    (request.ExecutionId != null && EF.Functions.ILike(request.ExecutionId, like)) ||
                    (request.ResultMessage != null && EF.Functions.ILike(request.ResultMessage, like)) || EF.Functions.ILike(request.TargetClientIdentity, like) ||
                    matchingTenantIds.Any(tenantId => (int?)tenantId == request.TargetTenantId) ||
                    matchingAgents.Any(match => (int?)match.TenantId == request.TargetTenantId && (Guid?)match.Id == request.TargetAgentId) ||
                    (request.JobDefinitionId != null && matchingJobIds.Any(jobId => jobId.ToString() == request.JobDefinitionId)))
                : requests.Where(request =>
                    (hasId && request.Id == id) || EF.Functions.Like(request.SourceSystem.ToUpper(), like.ToUpper(), "\\") || EF.Functions.Like(request.Status.ToUpper(), like.ToUpper(), "\\") ||
                    (request.JobDefinitionId != null && EF.Functions.Like(request.JobDefinitionId.ToUpper(), like.ToUpper(), "\\")) ||
                    (request.ExecutionId != null && EF.Functions.Like(request.ExecutionId.ToUpper(), like.ToUpper(), "\\")) ||
                    (request.ResultMessage != null && EF.Functions.Like(request.ResultMessage.ToUpper(), like.ToUpper(), "\\")) || EF.Functions.Like(request.TargetClientIdentity.ToUpper(), like.ToUpper(), "\\") ||
                    matchingTenantIds.Any(tenantId => (int?)tenantId == request.TargetTenantId) ||
                    matchingAgents.Any(match => (int?)match.TenantId == request.TargetTenantId && (Guid?)match.Id == request.TargetAgentId) ||
                    (request.JobDefinitionId != null && matchingJobIds.Any(jobId => jobId.ToString() == request.JobDefinitionId)));
        }

        return from request in requests
               join tenantValue in db.Tenants.AsNoTracking()
                   on request.TargetTenantId equals (int?)tenantValue.Id into tenantJoin
               from tenant in tenantJoin.DefaultIfEmpty()
               join agentValue in db.Agents.AsNoTracking()
                   on new { TenantId = request.TargetTenantId, AgentId = request.TargetAgentId }
                   equals new { TenantId = (int?)agentValue.TenantId, AgentId = (Guid?)agentValue.Id } into agentJoin
               from agent in agentJoin.DefaultIfEmpty()
               orderby request.UpdatedAtUtc descending, request.Id descending
               select new RequestSearchRow(
                   request.Id,
                   request.SourceSystem,
                   request.Status,
                   request.JobDefinitionId,
                   request.ExecutionId,
                   request.ResultMessage,
                   request.TargetTenantId,
                   request.TargetAgentId,
                   request.TargetClientIdentity,
                   request.UpdatedAtUtc,
                   tenant == null ? null : tenant.Name,
                   agent == null ? null : agent.Name,
                   agent == null ? null : agent.IsEnabled,
                   agent == null ? null : agent.DeviceInfoJson);

    }

    internal static IQueryable<TaskSearchRow> BuildTaskQuery(OrchestratorDbContext db, string? term, int[]? tenantIds = null)
    {
        var matchingAgents = AgentDirectorySearch.MatchingAgents(db, term);
        var matchingTenantIds = MatchingTenantIds(db, term);
        var tasks = db.JobTaskActivities.AsNoTracking();
        if (tenantIds is not null)
        {
            tasks = tasks.Where(task => task.TenantId.HasValue && tenantIds.Contains(task.TenantId.Value));
        }
        if (term is not null)
        {
            var like = Like(term);
            var hasId = long.TryParse(term, out var id);
            tasks = db.Database.IsNpgsql()
                ? tasks.Where(task =>
                    (hasId && task.Id == id) || EF.Functions.ILike(task.RequestId, like) || EF.Functions.ILike(task.TaskType, like) ||
                    EF.Functions.ILike(task.Status, like) || (task.Error != null && EF.Functions.ILike(task.Error, like)) ||
                    EF.Functions.ILike(task.ClientIdentity, like) || matchingTenantIds.Any(tenantId => (int?)tenantId == task.TenantId) ||
                    matchingAgents.Any(match => (int?)match.TenantId == task.TenantId && (Guid?)match.Id == task.AgentId))
                : tasks.Where(task =>
                    (hasId && task.Id == id) || EF.Functions.Like(task.RequestId.ToUpper(), like.ToUpper(), "\\") || EF.Functions.Like(task.TaskType.ToUpper(), like.ToUpper(), "\\") ||
                    EF.Functions.Like(task.Status.ToUpper(), like.ToUpper(), "\\") || (task.Error != null && EF.Functions.Like(task.Error.ToUpper(), like.ToUpper(), "\\")) ||
                    EF.Functions.Like(task.ClientIdentity.ToUpper(), like.ToUpper(), "\\") || matchingTenantIds.Any(tenantId => (int?)tenantId == task.TenantId) ||
                    matchingAgents.Any(match => (int?)match.TenantId == task.TenantId && (Guid?)match.Id == task.AgentId));
        }

        return from task in tasks
               join agentValue in db.Agents.AsNoTracking()
                   on new { task.TenantId, task.AgentId }
                   equals new { TenantId = (int?)agentValue.TenantId, AgentId = (Guid?)agentValue.Id } into agentJoin
               from agent in agentJoin.DefaultIfEmpty()
               orderby task.CreatedAtUtc descending, task.Id descending
               select new TaskSearchRow(
                   task.Id,
                   task.RequestId,
                   task.ClientIdentity,
                   task.TenantId,
                   task.AgentId,
                   task.TaskType,
                   task.Status,
                   task.Error,
                   task.CreatedAtUtc,
                   task.CompletedAtUtc,
                   agent == null ? null : agent.Name,
                   agent == null ? null : agent.IsEnabled,
                   agent == null ? null : agent.DeviceInfoJson);

    }

    private static IQueryable<int> MatchingTenantIds(OrchestratorDbContext db, string? term)
    {
        var query = db.Tenants.AsNoTracking();
        if (term is not null)
        {
            var like = Like(term);
            query = db.Database.IsNpgsql()
                ? query.Where(tenant => EF.Functions.ILike(tenant.Name, like))
                : query.Where(tenant => EF.Functions.Like(tenant.Name.ToUpper(), like.ToUpper(), "\\"));
        }

        return query.Select(tenant => tenant.Id);
    }

    private static IQueryable<long> MatchingJobIds(OrchestratorDbContext db, string? term)
    {
        var query = db.Jobs.AsNoTracking();
        if (term is not null)
        {
            var like = Like(term);
            var hasId = long.TryParse(term, out var id);
            query = db.Database.IsNpgsql()
                ? query.Where(job =>
                    EF.Functions.ILike(job.Name, like) ||
                    EF.Functions.ILike(job.FolderPath, like) ||
                    (job.Description != null && EF.Functions.ILike(job.Description, like)) ||
                    (hasId && job.Id == id))
                : query.Where(job =>
                    EF.Functions.Like(job.Name.ToUpper(), like.ToUpper(), "\\") ||
                    EF.Functions.Like(job.FolderPath.ToUpper(), like.ToUpper(), "\\") ||
                    (job.Description != null && EF.Functions.Like(job.Description.ToUpper(), like.ToUpper(), "\\")) ||
                    (hasId && job.Id == id));
        }

        return query.Select(job => job.Id);
    }

    private static IQueryable<Tenant> ScopedTenants(OrchestratorDbContext db, int[]? tenantIds) =>
        tenantIds is null
            ? db.Tenants.AsNoTracking()
            : db.Tenants.AsNoTracking().Where(tenant => tenantIds.Contains(tenant.Id));

    internal static GlobalSearchAgentDto MapAgent(AgentDirectoryRow row)
    {
        var agent = AgentDirectoryPresentation.Create(
            row.TenantId,
            row.AgentId,
            row.Name,
            row.IsEnabled,
            row.DeviceInfoJson,
            row.TenantName);
        return new(
            agent.TenantId,
            agent.AgentId,
            agent.DisplayName,
            agent.HostName,
            agent.TenantName,
            agent.OperatingSystem,
            null,
            agent.IsEnabled);
    }

    internal static JobDto MapJob(JobSearchRow row)
    {
        var agent = MapAgentPresentation(row.TenantId, row.AgentId, row.AgentName, row.AgentIsEnabled, row.AgentDeviceInfoJson, row.TenantName);
        var agentLabel = agent?.DisplayName ?? (row.AgentId is null ? "Legacy target unavailable" : null);
        return new(
            (ulong)row.Id,
            row.Name,
            row.FolderPath,
            row.Description,
            row.TenantId,
            row.ClientIdentity,
            row.CreatedAtUtc.ToUnixTimeMilliseconds() * 1000,
            row.UpdatedAtUtc.ToUnixTimeMilliseconds() * 1000,
            row.TenantName,
            agentLabel,
            ClientEnvironment.None,
            AgentId: row.AgentId);
    }

    internal static GlobalSearchRequestDto MapRequest(RequestSearchRow row)
    {
        var agent = MapAgentPresentation(row.TargetTenantId, row.TargetAgentId, row.AgentName, row.AgentIsEnabled, row.AgentDeviceInfoJson, row.TenantName);
        return new(
            row.Id,
            row.SourceSystem,
            row.Status,
            row.JobDefinitionId,
            row.ExecutionId,
            row.ResultMessage,
            row.TargetTenantId,
            row.TargetAgentId,
            row.TenantName,
            agent?.DisplayName,
            agent?.HostName,
            row.TargetClientIdentity);
    }

    internal static TaskDto MapTask(TaskSearchRow row)
    {
        var agent = MapAgentPresentation(row.TenantId, row.AgentId, row.AgentName, row.AgentIsEnabled, row.AgentDeviceInfoJson, tenantName: null);
        return new(
            row.Id > int.MaxValue ? 0 : (int)row.Id,
            row.RequestId,
            row.ClientIdentity,
            row.TenantId,
            ClientEnvironment.None,
            row.TaskType,
            row.Status,
            row.Error,
            null,
            row.CreatedAtUtc,
            null,
            row.CompletedAtUtc,
            null,
            agent?.DisplayName,
            agent?.HostName,
            agent?.DisplayName,
            row.AgentId);
    }

    private static AgentDirectoryPresentation? MapAgentPresentation(
        int? tenantId,
        Guid? agentId,
        string? agentName,
        bool? isEnabled,
        string? deviceInfoJson,
        string? tenantName)
    {
        if (!tenantId.HasValue || !agentId.HasValue || !isEnabled.HasValue)
        {
            return null;
        }

        return AgentDirectoryPresentation.Create(
            tenantId.Value,
            agentId.Value,
            agentName,
            isEnabled.Value,
            deviceInfoJson,
            tenantName ?? $"Tenant {tenantId.Value}");
    }

    private static void LogSearch(
        ILoggerFactory loggerFactory,
        DatabaseCommandMetricsInterceptor commandMetrics,
        string section,
        string? term,
        int candidateCount,
        int resultCount,
        long started,
        TimeSpan queryElapsed,
        TimeSpan mappingElapsed)
    {
        var database = commandMetrics.Snapshot();
        loggerFactory.CreateLogger("NetRatel.API.GlobalSearch").LogInformation(
            "Global search source completed. Section={Section} QueryLength={QueryLength} QueryHash={QueryHash} DatabaseCommandCount={DatabaseCommandCount} DatabaseElapsedMs={DatabaseElapsedMs:F3} QueryElapsedMs={QueryElapsedMs:F3} MappingElapsedMs={MappingElapsedMs:F3} CandidateCount={CandidateCount} ResultCount={ResultCount} ElapsedMs={ElapsedMs:F3}",
            section,
            term?.Length ?? 0,
            QueryHash(term),
            database.CommandCount,
            database.Elapsed.TotalMilliseconds,
            queryElapsed.TotalMilliseconds,
            mappingElapsed.TotalMilliseconds,
            candidateCount,
            resultCount,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static PagedResult<T> Page<T>(IReadOnlyList<T> rows, int pageSize) => new(rows, 1, pageSize, rows.Count);
    private static int NormalizePageSize(int pageSize) => Math.Clamp(pageSize, 1, MaxPageSize);
    private static string? NormalizeTerm(string? q) => string.IsNullOrWhiteSpace(q) ? null : q.Trim();
    private static string Like(string term) => $"%{term.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
    private static string QueryHash(string? term) => term is null ? "empty" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(term)))[..12];
}

internal sealed record JobSearchRow(
    long Id,
    string Name,
    string FolderPath,
    string? Description,
    int? TenantId,
    Guid? AgentId,
    string ClientIdentity,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? TenantName,
    string? AgentName,
    bool? AgentIsEnabled,
    string? AgentDeviceInfoJson);

internal sealed record RequestSearchRow(
    int Id,
    string SourceSystem,
    string Status,
    string? JobDefinitionId,
    string? ExecutionId,
    string? ResultMessage,
    int? TargetTenantId,
    Guid? TargetAgentId,
    string TargetClientIdentity,
    DateTimeOffset UpdatedAtUtc,
    string? TenantName,
    string? AgentName,
    bool? AgentIsEnabled,
    string? AgentDeviceInfoJson);

internal sealed record TaskSearchRow(
    long Id,
    string RequestId,
    string ClientIdentity,
    int? TenantId,
    Guid? AgentId,
    string TaskType,
    string Status,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? AgentName,
    bool? AgentIsEnabled,
    string? AgentDeviceInfoJson);
