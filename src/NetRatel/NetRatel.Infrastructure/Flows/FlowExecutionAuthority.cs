using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Flows;

/// <summary>The canonical authority rechecks the configuring principal and bound target without borrowing service roles.</summary>
public sealed class FlowExecutionAuthority(OrchestratorDbContext db, IFlowExecutionAuthorityVerifier verifier,
    IEffectiveAccessService access) : IKeyedExecutionAuthority
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

    public Task RequireScriptAsync(ExecutionAdmissionIdentity identity, Guid agentId, CancellationToken cancellationToken)
        => RequireAsync(identity, NetRatelPermissions.ScriptExecute, agentId, cancellationToken);

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
