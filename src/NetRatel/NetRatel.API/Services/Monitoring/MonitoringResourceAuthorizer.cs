using System.Security.Claims;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Services.Monitoring;

public enum MonitoringResourceKind { Tenant, Agent, Rule, Group, Series, Bypass }

/// <summary>A permission is always evaluated with the resource's exact application tenant.</summary>
public sealed record MonitoringResource(int TenantId, MonitoringResourceKind Kind, Guid? ResourceId = null, Guid? AgentId = null, string? ResourceKey = null);

public interface IMonitoringResourceAuthorizer
{
    Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, MonitoringResource resource, CancellationToken cancellationToken);
}

/// <summary>Tenant grants authorize owned resources; callers still validate their authoritative ownership.</summary>
public sealed class MonitoringResourceAuthorizer(IEffectiveAccessService access) : IMonitoringResourceAuthorizer
{
    private static readonly HashSet<string> Permissions = new(StringComparer.Ordinal)
    {
        NetRatelPermissions.MonitoringRead, NetRatelPermissions.MonitoringManage,
        NetRatelPermissions.MonitoringAcknowledge, NetRatelPermissions.MonitoringClear,
        NetRatelPermissions.MonitoringBypass, NetRatelPermissions.MonitoringAllTargets
    };

    public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, MonitoringResource resource, CancellationToken cancellationToken)
    {
        if (resource.TenantId <= 0 || !Enum.IsDefined(resource.Kind) || !Permissions.Contains(permission) ||
            resource.ResourceId == Guid.Empty || resource.AgentId == Guid.Empty ||
            (resource.Kind is MonitoringResourceKind.Agent or MonitoringResourceKind.Series && resource.AgentId is null))
            return Task.FromResult(false);
        return access.AuthorizeAsync(principal, permission, resource.TenantId, cancellationToken);
    }
}
