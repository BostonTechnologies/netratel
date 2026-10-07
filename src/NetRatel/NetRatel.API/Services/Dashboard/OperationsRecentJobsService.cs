using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Monitoring;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.API.Services.Dashboard;

public sealed class OperationsRecentJobsService(IEffectiveAccessService access, IServiceScopeFactory scopes)
{
    public async Task<ImmutableArray<OperationsRecentJobDto>> ReadAsync(int tenantId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (tenantId <= 0) throw new MonitoringApiException(400, "invalid_dashboard_tenant");
        if (!await access.AuthorizeAsync(user, NetRatelPermissions.MonitoringRead, tenantId, cancellationToken).ConfigureAwait(false) ||
            !await access.AuthorizeAsync(user, NetRatelPermissions.JobManagement, tenantId, cancellationToken).ConfigureAwait(false))
            throw new MonitoringApiException(403, "job_permission_required");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var rows = await (from run in db.JobRuns.AsNoTracking()
                          join job in db.Jobs.AsNoTracking() on run.JobId equals job.Id into definitions
                          from job in definitions.DefaultIfEmpty()
                          where run.TenantId == tenantId
                          orderby run.CreatedAtUtc descending, run.Id descending
                          select new { run.Id, run.JobId, run.AgentId, run.Status,
                              StartedAtUtc = run.StartedAtUtc ?? run.CreatedAtUtc,
                              JobName = job != null && (job.TenantId == tenantId || job.TenantId == null) ? job.Name : "Job unavailable" })
            .Take(6).ToListAsync(cancellationToken).ConfigureAwait(false);
        var identities = await MonitoringIdentityProjection.ReadAsync(db, tenantId,
            rows.Where(row => row.AgentId.HasValue).Select(row => row.AgentId!.Value), cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new OperationsRecentJobDto(row.Id, row.JobId, row.JobName,
            row.AgentId is { } agentId ? identities[agentId] : null,
            (JobRunStatusDto)row.Status, row.StartedAtUtc)).ToImmutableArray();
    }
}
