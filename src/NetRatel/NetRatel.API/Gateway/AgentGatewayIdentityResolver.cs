using System.Security.Claims;

namespace NetRatel.API.Gateway;

public sealed record AuthenticatedAgentIdentity(int TenantId, Guid AgentId)
{
    public string ClientId => AgentId.ToString("D");
}

public static class AgentGatewayIdentityResolver
{
    public static bool TryResolve(
        ClaimsPrincipal principal,
        out AuthenticatedAgentIdentity? identity,
        out string error)
    {
        identity = null;

        var hasAgentRole = principal.Claims.Any(claim =>
            (claim.Type == "role" || claim.Type == "roles" || claim.Type == ClaimTypes.Role) &&
            string.Equals(claim.Value, "agent", StringComparison.OrdinalIgnoreCase));
        if (!hasAgentRole)
        {
            error = "The authenticated principal is missing the agent role.";
            return false;
        }

        var subject = principal.FindFirst("sub")?.Value;
        var agentIdValue = principal.FindFirst("agent_id")?.Value;
        if (!Guid.TryParse(subject, out var subjectId) ||
            !Guid.TryParse(agentIdValue, out var agentId) ||
            subjectId == Guid.Empty ||
            subjectId != agentId)
        {
            error = "The sub and agent_id claims must contain the same non-empty GUID.";
            return false;
        }

        var tenantValue = principal.FindFirst("tenant_id")?.Value;
        if (!int.TryParse(tenantValue, out var tenantId) || tenantId <= 0)
        {
            error = "The tenant_id claim must contain a positive integer.";
            return false;
        }

        var hasConnectScope = principal.Claims
            .Where(claim => string.Equals(claim.Type, "scope", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(claim.Type, "scp", StringComparison.OrdinalIgnoreCase))
            .SelectMany(claim => claim.Value.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(scope => string.Equals(scope, "netratel:connect", StringComparison.Ordinal));
        if (!hasConnectScope)
        {
            error = "The authenticated principal is missing the netratel:connect scope.";
            return false;
        }

        identity = new AuthenticatedAgentIdentity(tenantId, agentId);
        error = string.Empty;
        return true;
    }
}
