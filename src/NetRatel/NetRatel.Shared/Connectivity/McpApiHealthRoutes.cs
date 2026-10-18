namespace NetRatel.Shared.Connectivity;

/// <summary>
/// Protected API probes under the operator prefix already forwarded by the Web gateway.
/// These routes describe API health, separately from the MCP listener's host probes.
/// </summary>
public static class McpApiHealthRoutes
{
    public const string Liveness = "/api/v2/mcp/operator/health/live";
    public const string Readiness = "/api/v2/mcp/operator/health/ready";
}
