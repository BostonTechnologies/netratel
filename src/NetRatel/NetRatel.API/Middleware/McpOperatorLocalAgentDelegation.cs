using NetRatel.Shared.Operations;

namespace NetRatel.API.Middleware;

/// <summary>
/// Binds a direct local CLI/stdio request to its authenticated client
/// credential without granting that credential the ability to name another
/// caller. The separate configuration allowlist keeps this path unavailable
/// to the broad MCP service credential unless an owner explicitly opts in.
/// </summary>
public static class McpOperatorLocalAgentDelegation
{
    public static bool TryCreateControlPlane(
        HttpContext http,
        IHostEnvironment environment,
        McpOperatorLocalAgentOptions options,
        string tool,
        string operation,
        out McpOperatorDelegation? delegation)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        delegation = null;

        if (!environment.IsProduction() || !options.Allows(http.User) ||
            !McpOperatorDelegationIdentityFactory.TryCreate(http.User, out var identity))
        {
            return false;
        }

        delegation = new McpOperatorDelegation(
            identity!, options.ServicePrincipal, tool, operation, Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddMinutes(1), "netratel-local-agent", "prod", null, null,
            Guid.NewGuid().ToString("N"));
        return true;
    }

    public static bool TryCreate(
        HttpContext http,
        IHostEnvironment environment,
        McpOperatorLocalAgentOptions options,
        string tool,
        string operation,
        int tenantId,
        Guid agentId,
        out McpOperatorDelegation? delegation)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        delegation = null;

        if (!environment.IsProduction() || tenantId <= 0 || agentId == Guid.Empty ||
            !options.Allows(http.User) || !McpOperatorDelegationIdentityFactory.TryCreate(http.User, out var identity))
        {
            return false;
        }

        delegation = new McpOperatorDelegation(
            identity!,
            options.ServicePrincipal,
            tool,
            operation,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            "netratel-local-agent",
            "prod",
            tenantId,
            agentId,
            Guid.NewGuid().ToString("N"));
        return true;
    }
}
