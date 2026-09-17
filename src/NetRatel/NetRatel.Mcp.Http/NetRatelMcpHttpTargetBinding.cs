using NetRatel.AgentClient;
using NetRatel.Mcp.Core;

namespace NetRatel.Mcp.Http;

/// <summary>
/// Resolves one non-ambient outbound identity. The HTTP host configuration picks
/// the environment allowlist entry; the referenced deployment file supplies the
/// credentials and must name that same API endpoint.
/// </summary>
public sealed record NetRatelMcpHttpTargetBinding(
    NetRatelMcpTarget Target,
    NetRatelMcpOutboundOptions OutboundOptions,
    string ConfigurationPath)
{
    public const string ConfigurationEnvironmentVariable = NetRatelMcpHttpOptions.ConfigurationPathEnvironmentVariable;

    public static NetRatelMcpHttpTargetBinding Resolve(NetRatelMcpHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configurationPath = string.IsNullOrWhiteSpace(options.ConfigurationPath)
            ? Environment.GetEnvironmentVariable(ConfigurationEnvironmentVariable)
            : options.ConfigurationPath;
        var configuration = AgentClientConfigurationResolver.LoadIsolated(configurationPath);
        var expectedApiBaseUrl = options.Instance.Trim().ToLowerInvariant() switch
        {
            "dev" => options.DevApiBaseUrl,
            "prod" => options.ProdApiBaseUrl,
            _ => throw new AgentClientValidationException("NetRatel:Mcp:Http:Instance must be dev or prod.")
        };

        var expectedTarget = ParseHttpsUri(expectedApiBaseUrl, $"NetRatel:Mcp:Http:{options.Instance}ApiBaseUrl");
        var configuredTarget = ParseHttpsUri(configuration.ApiBaseUrl, "the isolated MCP configuration apiBaseUrl");
        if (!Equivalent(expectedTarget, configuredTarget))
        {
            throw new AgentClientValidationException($"The isolated MCP configuration apiBaseUrl must match the {options.Instance} API allowlist entry.");
        }

        var target = new NetRatelMcpTarget(
            options.Instance,
            configuredTarget,
            new Uri("netratel://server/status", UriKind.Absolute),
            NetRatelMcpCatalog.Revision);
        return new NetRatelMcpHttpTargetBinding(target, NetRatelMcpOutboundOptions.FromIsolatedConfiguration(target, configuration), configurationPath!);
    }

    private static Uri ParseHttpsUri(string? value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new AgentClientValidationException($"{name} must be an absolute HTTPS URI without a query or fragment.");
        }

        return uri;
    }

    private static bool Equivalent(Uri left, Uri right)
    {
        var normalizedLeft = new UriBuilder(left) { Scheme = left.Scheme.ToLowerInvariant(), Host = left.Host.ToLowerInvariant(), Path = left.AbsolutePath.TrimEnd('/') }.Uri.AbsoluteUri.TrimEnd('/');
        var normalizedRight = new UriBuilder(right) { Scheme = right.Scheme.ToLowerInvariant(), Host = right.Host.ToLowerInvariant(), Path = right.AbsolutePath.TrimEnd('/') }.Uri.AbsoluteUri.TrimEnd('/');
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }
}
