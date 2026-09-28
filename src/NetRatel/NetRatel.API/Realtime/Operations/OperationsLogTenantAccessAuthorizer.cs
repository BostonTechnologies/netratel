using System.Security.Claims;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Realtime.Operations;

/// <summary>
/// Resolves tenant scope through the application's effective permission evaluator.
/// Browser-supplied tenant claims cannot add authority beyond stored assignments;
/// instance administrators and legacy operators retain instance-wide access.
/// </summary>
public interface IOperationsLogTenantAuthorizer
{
    Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, int tenantId, CancellationToken cancellationToken = default);
}

public sealed class OperationsLogTenantAccessAuthorizer(IEffectiveAccessService access) : IOperationsLogTenantAuthorizer
{
    public Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, int tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (tenantId <= 0 || principal.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(false);
        }

        return access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, tenantId, cancellationToken);
    }
}
