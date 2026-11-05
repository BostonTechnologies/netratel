using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

/// <summary>The canonical authority rechecks the configuring principal and bound target without borrowing service roles.</summary>
public sealed class FlowExecutionAuthority(OrchestratorDbContext db, IFlowExecutionAuthorityVerifier verifier,
    IEffectiveAccessService access, IFlowTransactionAdmission sourceAdmission, TimeProvider clock) : IKeyedExecutionAuthority
{
    public async Task RequireAllowedAsync(ExecutionAdmissionIdentity identity, JobDefinitionInfo pinnedDefinition,
        CancellationToken cancellationToken)
    {
        identity.Validate();
        if (pinnedDefinition.TenantId != identity.TenantId || pinnedDefinition.AgentId is not { } agentId ||
            agentId == Guid.Empty ||
            !await db.Jobs.AsNoTracking().AnyAsync(job => job.Id == checked((long)pinnedDefinition.Id) &&
                job.TenantId == identity.TenantId, cancellationToken))
            throw new ExecutionAdmissionDeniedException("The pinned source definition is no longer available in its authorized tenant.");
        await RequireAsync(identity, NetRatelPermissions.JobManagement, agentId, cancellationToken);
    }

    public async Task RequireScriptAsync(ExecutionAdmissionIdentity identity, ulong scriptId, Guid agentId, CancellationToken cancellationToken)
    {
        if (!await db.Scripts.AsNoTracking().AnyAsync(script => script.Id == checked((long)scriptId), cancellationToken))
            throw new ExecutionAdmissionDeniedException("The library definition is no longer available.");
        await RequireAsync(identity, NetRatelPermissions.ScriptExecute, agentId, cancellationToken);
    }

    public async Task RequireFirstDispatchAsync(ExecutionAdmissionIdentity identity, FlowRunLease? lease, CancellationToken ct)
    {
        identity.Validate();
        if (lease is null || lease.RunId != identity.FlowRunId || lease.Version.Id != identity.FlowVersionId ||
            lease.Event.TenantId != identity.TenantId || lease.Event.Authority.PrincipalId != identity.PrincipalId ||
            lease.Event.Authority.IntegrationCredentialId != identity.IntegrationCredentialId || !db.Database.IsNpgsql())
            throw new ExecutionAdmissionDeniedException("The unstarted action has no current flow lease.");
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var decision = await sourceAdmission.CanStartActionAsync(db, lease, ct);
        if (!decision.Allowed) throw new ExecutionAdmissionDeniedException(decision.Code);
        var run = await db.FlowRuns.FromSqlInterpolated($"""
            SELECT * FROM "FlowRuns" WHERE "TenantId"={identity.TenantId} AND "Id"={identity.FlowRunId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        if (run is null || run.FlowVersionId != identity.FlowVersionId || run.Status != FlowRunStatus.Running ||
            run.LeaseToken != lease.Token || run.LeaseOwner != lease.WorkerId || run.Fence != lease.Fence ||
            run.LeaseExpiresAtUtc != lease.ExpiresAtUtc ||
            run.EventFingerprint != FlowContractValidation.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lease.Event, Json))))
            throw new ExecutionAdmissionDeniedException("The flow lease was reclaimed or expired.");
        var version = await db.FlowVersions.AsNoTracking().SingleOrDefaultAsync(version =>
            version.TenantId == identity.TenantId && version.Id == identity.FlowVersionId, ct);
        if (version is null || version.ExecutionAuthorityJson is null ||
            JsonSerializer.Deserialize<FlowExecutionAuthorityDto>(version.ExecutionAuthorityJson, Json) != lease.Event.Authority ||
            version.ConfigurationHash != lease.Version.ConfigurationHash ||
            version.ConfigurationHash != FlowContractValidation.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lease.Version.Graph, Json))) ||
            !lease.Version.Graph.Nodes.Any(node => node.Id == identity.NodeId &&
                node.Kind is FlowNodeKind.RunJob or FlowNodeKind.RunLibraryScript or FlowNodeKind.RerunOriginalExecution) ||
            !await db.FlowRuntimeIdentity.AsNoTracking().AnyAsync(row => row.Id == 1 && row.SourceInstanceId == lease.SourceInstanceId, ct))
            throw new ExecutionAdmissionDeniedException("The immutable version or published execution authority differs.");
        var now = clock.GetUtcNow();
        var databaseNow = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (databaseNow > now) now = databaseNow;
        if (run.LeaseExpiresAtUtc <= now)
            throw new ExecutionAdmissionDeniedException("The flow lease was reclaimed or expired.");
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task RequireAsync(ExecutionAdmissionIdentity identity, string permission, Guid agentId, CancellationToken ct)
    {
        identity.Validate();
        var authority = new FlowExecutionAuthorityDto(identity.PrincipalId, identity.IntegrationCredentialId);
        if (!await verifier.AuthorizeAsync(identity.TenantId, authority, ct) ||
            !await access.AuthorizeAsync(Principal(authority), permission, identity.TenantId, ct) ||
            !await db.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == identity.TenantId && tenant.DeletedAtUtc == null, ct) ||
            !await db.Agents.AsNoTracking().AnyAsync(agent => agent.TenantId == identity.TenantId && agent.Id == agentId &&
                agent.IsEnabled && agent.Status == AgentStatus.Active && agent.DeletedAtUtc == null &&
                agent.RevokedAtUtc == null && agent.SupersededAtUtc == null && agent.SupersededByAgentId == null, ct))
            throw new ExecutionAdmissionDeniedException("Current execution permission or target authority was revoked.");
    }

    private static ClaimsPrincipal Principal(FlowExecutionAuthorityDto authority)
    {
        var claims = new List<Claim> { new("netratel_principal_id", authority.PrincipalId) };
        if (authority.IntegrationCredentialId is not null)
            claims.Add(new("netratel_integration_credential_id", authority.IntegrationCredentialId));
        return new(new ClaimsIdentity(claims, "FlowKeyedExecution"));
    }
}
