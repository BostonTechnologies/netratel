namespace NetRatel.Mcp.Core;

public enum NetRatelMcpTransport
{
    Stdio,
    StreamableHttp
}

/// <summary>
/// Per-host context shared by tools, resources, prompts, policy and outbound clients.
/// Instances are constructed at startup and are never derived from a request.
/// </summary>
public sealed record NetRatelMcpHostContext
{
    public NetRatelMcpHostContext(
        NetRatelMcpTarget target,
        NetRatelMcpTransport transport,
        string serverName,
        bool? operatorSurfaceEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new ArgumentException("MCP server name is required.", nameof(serverName));
        }

        Target = target;
        Transport = transport;
        ServerName = serverName.Trim();
        OperatorSurfaceEnabled = operatorSurfaceEnabled ?? string.Equals(target.Instance, "prod", StringComparison.Ordinal);
    }

    public NetRatelMcpTarget Target { get; }
    public NetRatelMcpTransport Transport { get; }
    public string ServerName { get; }

    /// <summary>
    /// Selects the policy-admitted V2 operator route family. It is on by
    /// default for Production and must be explicitly enabled for Development;
    /// it is never derived from a caller request.
    /// </summary>
    public bool OperatorSurfaceEnabled { get; }
}
