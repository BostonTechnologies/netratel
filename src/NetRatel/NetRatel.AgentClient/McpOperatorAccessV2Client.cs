using System.Globalization;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

/// <summary>
/// Delegation-bound V2 access inspection for one authenticated operator.
/// These calls explain access; they do not grant policy or dispatch work.
/// </summary>
public interface IMcpOperatorAccessV2Client
{
    Task<JsonNode?> WhoAmIAsync(CancellationToken cancellationToken = default);
    Task<JsonNode?> GetTargetAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetEffectiveAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> EvaluateAsync(McpOperatorV2Target target, string tool, string operation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound V2 access facade. It derives its target path from a persisted
/// tenant-agent pair and never accepts an arbitrary API route.
/// </summary>
public sealed class McpOperatorAccessV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorAccessV2Client
{
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        EnsureTarget();
        return _client.GetAsync("/api/v2/mcp/operator/access/whoami", cancellationToken);
    }

    public Task<JsonNode?> GetTargetAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(TargetPath(target), cancellationToken);

    public Task<JsonNode?> GetEffectiveAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{TargetPath(target)}/effective", cancellationToken);

    public Task<JsonNode?> EvaluateAsync(McpOperatorV2Target target, string tool, string operation, CancellationToken cancellationToken = default)
    {
        if (!IsCatalogIdentifier(tool, 128) || !IsCatalogIdentifier(operation, 128))
            throw McpOperatorTaskV2Client.Invalid("access tool/operation");

        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(
            $"{TargetPath(target)}/evaluate",
            ("tool", tool),
            ("operation", operation)), cancellationToken);
    }

    private string TargetPath(McpOperatorV2Target target)
    {
        target.Validate();
        EnsureTarget();
        return $"/api/v2/mcp/operator/access/agents/{target.TenantId.ToString(CultureInfo.InvariantCulture)}/{target.AgentId:D}";
    }

    private void EnsureTarget() =>
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "access");

    private static bool IsCatalogIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
