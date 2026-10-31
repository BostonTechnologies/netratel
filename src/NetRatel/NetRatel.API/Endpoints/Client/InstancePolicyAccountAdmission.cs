using System.Security.Claims;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Request-local proof of instance-account authority, installed only on the
/// interactive policy route. Raw roles, integration credentials and signed MCP
/// delegations cannot produce it. Effective access is re-read before persistence.
/// </summary>
internal sealed class InstancePolicyAccountAdmission(
    HttpContext http, IEffectiveAccessService effectiveAccess, string principalId)
{
    private static readonly object AdmissionKey = new();
    public string PrincipalId { get; } = principalId;

    public static async ValueTask<object?> FilterAsync(
        EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var http = invocation.HttpContext;
        if (TryInteractivePrincipal(http.User, out var principalId) &&
            http.RequestServices.GetService<IEffectiveAccessService>() is { } access)
        {
            var admission = new InstancePolicyAccountAdmission(http, access, principalId!);
            if (await admission.IsCurrentAsync(http.RequestAborted).ConfigureAwait(false))
                http.Items[AdmissionKey] = admission;
        }
        return await next(invocation).ConfigureAwait(false);
    }

    public static InstancePolicyAccountAdmission? FromContext(HttpContext http) =>
        http.Items.TryGetValue(AdmissionKey, out var value) ? value as InstancePolicyAccountAdmission : null;

    public async Task<bool> IsCurrentAsync(CancellationToken cancellationToken)
    {
        if (!TryInteractivePrincipal(http.User, out var currentId) || currentId != PrincipalId)
            return false;
        var snapshot = await effectiveAccess.GetSnapshotAsync(http.User, null, cancellationToken).ConfigureAwait(false);
        return snapshot.IsInstanceAdministrator &&
            (snapshot.PrincipalId == PrincipalId || snapshot.IsLegacyOperator);
    }

    internal static bool TryInteractivePrincipal(ClaimsPrincipal user, out string? principalId)
    {
        principalId = user.FindFirst("netratel_principal_id")?.Value;
        return user.Identity?.IsAuthenticated == true &&
            principalId is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(principalId) && !principalId.Any(char.IsControl) &&
            !user.HasClaim(claim => claim.Type == "netratel_integration_credential_id") &&
            !user.HasClaim("token_use", "netratel_service") && !user.HasClaim("auth_mode", "service") &&
            !user.HasClaim("auth_mode", "integration_credential") &&
            user.Identity.AuthenticationType != "NetclawSavedAuthority";
    }
}
