using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using static NetRatel.Infrastructure.ServiceLinks.ServiceLinkValidation;

namespace NetRatel.Infrastructure.ServiceLinks;

public static class ServiceLinkGrantAuthority
{
    public static bool TryTenant(string? value, out int tenant) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out tenant) &&
        tenant > 0 && tenant.ToString(CultureInfo.InvariantCulture) == value;

    public static async Task ValidateLocalGrantAsync(OrchestratorDbContext db, ServiceLinkGrant grant,
        ClaimsPrincipal? actor, IEffectiveAccessService? access, CancellationToken ct)
    {
        var validTenant = TryTenant(grant.TargetTenantId, out var tenant);
        Require(grant.TargetProduct == "netratel" && validTenant &&
            grant.ResourceConstraints.TenantId == grant.TargetTenantId && grant.Scopes.Length > 0 &&
            grant.Scopes.All(x => ServiceIdentityScopes.Business.Contains(x, StringComparer.Ordinal)) &&
            await LocalResourcesCurrentAsync(db, grant.ResourceConstraints, ct),
            "grant-unavailable", "Choose existing permitted resources and request definitions in the exact NetRatel tenant.", 403);
        if (grant.Scopes.Contains(ServiceIdentityScopes.OrchestrationInvoke, StringComparer.Ordinal))
            Require(grant.ResourceConstraints.RequestDefinitionIds.Length > 0 && grant.ResourceConstraints.ResourceIds.Length > 0, "request-definition-required",
                "Invocation requires an explicitly approved existing request definition and its target agent.");
        if (actor is not null)
            Require(access is not null && await access.AuthorizeAsync(actor, NetRatelPermissions.IntegrationManagement, tenant, ct) &&
                await access.AuthorizeAsync(actor, NetRatelPermissions.JobManagement, tenant, ct), "grant-not-authorized",
                "Current integration-management and job-management authority are required for these local operations.", 403);
    }

    public static async Task<bool> LocalResourcesCurrentAsync(OrchestratorDbContext db, ServiceLinkResourceConstraints constraints, CancellationToken ct)
    {
        if (!TryTenant(constraints.TenantId, out var tenant) || constraints.OrganizationId is not null ||
            constraints.CustomerIds.Length != 0 || constraints.RequestIds.Length != 0 || constraints.TaskIds.Length != 0 ||
            constraints.ResourceIds.Length + constraints.RequestDefinitionIds.Length == 0 ||
            !await db.Tenants.AsNoTracking().AnyAsync(x => x.Id == tenant, ct)) return false;
        var resources = new List<Guid>();
        foreach (var resource in constraints.ResourceIds)
        {
            if (!Guid.TryParseExact(resource, "D", out var id) || id.ToString("D") != resource || resources.Contains(id)) return false;
            resources.Add(id);
        }
        var definitions = new List<long>();
        foreach (var definition in constraints.RequestDefinitionIds)
        {
            if (!long.TryParse(definition, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
                id.ToString(CultureInfo.InvariantCulture) != definition || definitions.Contains(id)) return false;
            definitions.Add(id);
        }
        if (await db.Agents.AsNoTracking().CountAsync(x => resources.Contains(x.Id) && x.TenantId == tenant &&
                x.IsEnabled && x.Status == AgentStatus.Active && x.RevokedAtUtc == null && x.SupersededByAgentId == null, ct) != resources.Count) return false;
        var jobs = await db.Jobs.AsNoTracking().Where(x => definitions.Contains(x.Id) && x.TenantId == tenant)
            .Select(x => new { x.Id, x.AgentId }).ToListAsync(ct);
        if (jobs.Count != definitions.Count || jobs.Any(x => x.AgentId is null || resources.Count > 0 && !resources.Contains(x.AgentId.Value))) return false;
        var targets = jobs.Select(x => x.AgentId!.Value).Distinct().ToArray();
        return await db.Agents.AsNoTracking().CountAsync(x => targets.Contains(x.Id) && x.TenantId == tenant &&
            x.IsEnabled && x.Status == AgentStatus.Active && x.RevokedAtUtc == null && x.SupersededByAgentId == null, ct) == targets.Length;
    }
}
