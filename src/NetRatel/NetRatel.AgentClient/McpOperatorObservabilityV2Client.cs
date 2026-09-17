using System.Globalization;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

/// <summary>Bounded V2 client-log history or search filters.</summary>
public sealed record McpOperatorLogQueryV2(
    string SourceId,
    string? Cursor = null,
    int? PageSize = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    IReadOnlyList<string>? Severity = null,
    IReadOnlyList<string>? Prefix = null,
    IReadOnlyList<string>? Category = null,
    IReadOnlyList<string>? Provider = null,
    IReadOnlyList<long>? EventId = null,
    string? Text = null);

/// <summary>Bounded V2 client-log tail settings for one advertised source.</summary>
public sealed record McpOperatorLogTailV2(string SourceId, int? WindowSeconds = null, int? MaximumRecords = null);

/// <summary>Bounded V2 telemetry live-window settings.</summary>
public sealed record McpOperatorTelemetryWindowV2(int? WindowSeconds = null, int? MaximumSamples = null);

/// <summary>
/// Route-bound V2 client observability. Log resync is preview/confirm-only;
/// all other operations are bounded reads for one exact tenant and agent.
/// </summary>
public interface IMcpOperatorObservabilityV2Client
{
    Task<JsonNode?> GetLogSourcesAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetLogHistoryAsync(McpOperatorV2Target target, McpOperatorLogQueryV2 query, CancellationToken cancellationToken = default);
    Task<JsonNode?> SearchLogsAsync(McpOperatorV2Target target, McpOperatorLogQueryV2 query, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetLogTailAsync(McpOperatorV2Target target, McpOperatorLogTailV2 tail, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewLogResyncAsync(McpOperatorV2Target target, string sourceId, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmLogResyncAsync(McpOperatorV2Target target, string sourceId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetTelemetrySnapshotAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetTelemetryWindowAsync(McpOperatorV2Target target, McpOperatorTelemetryWindowV2 window, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound V2 observability facade. It only derives current operator
/// routes and contains no fallback to retired client log or telemetry paths.
/// </summary>
public sealed class McpOperatorObservabilityV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorObservabilityV2Client
{
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> GetLogSourcesAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, "/logs/sources"), cancellationToken);

    public Task<JsonNode?> GetLogHistoryAsync(McpOperatorV2Target target, McpOperatorLogQueryV2 query, CancellationToken cancellationToken = default) =>
        _client.GetAsync(QueryPath(target, "/logs/history", query, requiresText: false), cancellationToken);

    public Task<JsonNode?> SearchLogsAsync(McpOperatorV2Target target, McpOperatorLogQueryV2 query, CancellationToken cancellationToken = default) =>
        _client.GetAsync(QueryPath(target, "/logs/search", query, requiresText: true), cancellationToken);

    public Task<JsonNode?> GetLogTailAsync(McpOperatorV2Target target, McpOperatorLogTailV2 tail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tail);
        var sourceId = ValidateSourceId(tail.SourceId);
        if (tail.WindowSeconds is < 1 or > 15 || tail.MaximumRecords is < 1 or > 100)
            throw McpOperatorTaskV2Client.Invalid("log tail");

        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(
            Path(target, "/logs/tail"),
            ("sourceId", sourceId),
            ("windowSeconds", tail.WindowSeconds?.ToString(CultureInfo.InvariantCulture)),
            ("maxRecords", tail.MaximumRecords?.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    }

    public Task<JsonNode?> PreviewLogResyncAsync(McpOperatorV2Target target, string sourceId, CancellationToken cancellationToken = default) =>
        _client.SendAsync(
            HttpMethod.Post,
            Path(target, "/logs/resync/preview"),
            new JsonObject { ["sourceId"] = ValidateSourceId(sourceId) },
            cancellationToken);

    public Task<JsonNode?> ConfirmLogResyncAsync(McpOperatorV2Target target, string sourceId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["sourceId"] = ValidateSourceId(sourceId) };
        McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, "/logs/resync/confirm"), body, cancellationToken);
    }

    public Task<JsonNode?> GetTelemetrySnapshotAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, "/telemetry/snapshot"), cancellationToken);

    public Task<JsonNode?> GetTelemetryWindowAsync(McpOperatorV2Target target, McpOperatorTelemetryWindowV2 window, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.WindowSeconds is < 1 or > 15 || window.MaximumSamples is < 1 or > 20)
            throw McpOperatorTaskV2Client.Invalid("telemetry window");

        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(
            Path(target, "/telemetry/stream-window"),
            ("windowSeconds", window.WindowSeconds?.ToString(CultureInfo.InvariantCulture)),
            ("maxSamples", window.MaximumSamples?.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    }

    private string QueryPath(McpOperatorV2Target target, string suffix, McpOperatorLogQueryV2 query, bool requiresText)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sourceId = ValidateSourceId(query.SourceId);
        if (query.Cursor is { } cursor && !IsBoundedText(cursor, 256) ||
            query.PageSize is < 1 or > 100 ||
            query.FromUtc is { } fromUtc && query.ToUtc is { } toUtc && fromUtc > toUtc ||
            query.Text is { } text && !IsBoundedText(text, 512) ||
            requiresText && !IsBoundedText(query.Text, 512))
        {
            throw McpOperatorTaskV2Client.Invalid("log query");
        }

        var values = new List<(string Name, string? Value)>
        {
            ("sourceId", sourceId),
            ("cursor", query.Cursor),
            ("pageSize", query.PageSize?.ToString(CultureInfo.InvariantCulture)),
            ("fromUtc", query.FromUtc?.ToString("O", CultureInfo.InvariantCulture)),
            ("toUtc", query.ToUtc?.ToString("O", CultureInfo.InvariantCulture)),
            ("text", query.Text)
        };
        AddBoundedValues(values, "severity", query.Severity, 64);
        AddBoundedValues(values, "prefix", query.Prefix, 256);
        AddBoundedValues(values, "category", query.Category, 256);
        AddBoundedValues(values, "provider", query.Provider, 256);
        if (query.EventId is { Count: > 32 } || query.EventId?.Any(value => value < 0) == true)
            throw McpOperatorTaskV2Client.Invalid("log eventId");
        if (query.EventId is not null)
        {
            foreach (var eventId in query.EventId)
            {
                values.Add(("eventId", eventId.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return Query(Path(target, suffix), values);
    }

    private static void AddBoundedValues(List<(string Name, string? Value)> destination, string name, IReadOnlyList<string>? values, int maximumLength)
    {
        if (values is null) return;
        if (values.Count > 32 || values.Any(value => !IsBoundedText(value, maximumLength)))
            throw McpOperatorTaskV2Client.Invalid($"log {name}");

        foreach (var value in values)
        {
            destination.Add((name, value));
        }
    }

    private static string Query(string path, IEnumerable<(string Name, string? Value)> values)
    {
        var query = values
            .Where(value => !string.IsNullOrWhiteSpace(value.Value))
            .Select(value => $"{Uri.EscapeDataString(value.Name)}={Uri.EscapeDataString(value.Value!)}");
        var serialized = string.Join('&', query);
        return string.IsNullOrEmpty(serialized) ? path : $"{path}?{serialized}";
    }

    private static string ValidateSourceId(string? sourceId) =>
        IsBoundedText(sourceId, 128)
            ? sourceId!
            : throw McpOperatorTaskV2Client.Invalid("log sourceId");

    private static bool IsBoundedText(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && !value.Any(char.IsControl);

    private string Path(McpOperatorV2Target target, string suffix)
    {
        target.Validate();
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "observability");
        return $"/api/v2/mcp/operator/agents/{target.TenantId.ToString(CultureInfo.InvariantCulture)}/{target.AgentId:D}{suffix}";
    }
}
