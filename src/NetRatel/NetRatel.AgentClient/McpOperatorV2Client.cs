using System.Globalization;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

/// <summary>Exact target required by every policy-admitted operator V2 request.</summary>
public sealed record McpOperatorV2Target(int TenantId, Guid AgentId)
{
    internal void Validate()
    {
        if (TenantId <= 0 || AgentId == Guid.Empty)
            throw new AgentClientValidationException("A positive tenant ID and persisted agent UUID are required.");
    }
}

public sealed record McpOperatorTaskCommandV2(
    string Shell,
    string Command,
    string WorkingDirectory,
    int TimeoutSeconds,
    int MaximumOutputBytes,
    IReadOnlyList<string>? EnvironmentReferences = null);

public sealed record McpOperatorTaskScriptV2(
    long ScriptId,
    long Version,
    string ContentHash,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record McpOperatorRequestCreateV2(long JobId, string Summary);
public sealed record McpOperatorRequestUpdateV2(int RequestId, long ExpectedVersion, string Summary);
public sealed record McpOperatorRequestClaimV2(int RequestId, long ExpectedVersion, string ClaimReference);
public sealed record McpOperatorRequestResultV2(int RequestId, long ExpectedVersion, string ResultSummary);
public sealed record McpOperatorRequestCancelV2(int RequestId, long ExpectedVersion, string? ResultSummary = null);

/// <summary>Typed tenant values accepted by the control-plane tenant lifecycle.</summary>
public sealed record McpOperatorTenantDraftV2(
    string Name,
    IReadOnlyList<string> Domains,
    bool AutoUpdate,
    string? Description = null,
    string? Location = null,
    string? ContactPerson = null,
    string? ContactEmail = null,
    string? AutoUpdateChannel = null,
    string? AutoUpdateTargetVersion = null);

public sealed record McpOperatorTenantUpdateV2(int TenantId, long ExpectedVersion, McpOperatorTenantDraftV2 Tenant);
public sealed record McpOperatorTenantDeleteV2(int TenantId, long ExpectedVersion, bool Cascade);

public interface IMcpOperatorTaskV2Client
{
    Task<JsonNode?> ListAsync(McpOperatorV2Target target, string? state = null, DateTimeOffset? sinceUtc = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(McpOperatorV2Target target, long taskId, CancellationToken cancellationToken = default);
    Task<JsonNode?> ListLogsAsync(McpOperatorV2Target target, long taskId, long? sinceId = null, string? stream = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> ListLogsByRequestAsync(McpOperatorV2Target target, string requestId, long? sinceId = null, string? stream = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCreateCommandAsync(McpOperatorV2Target target, McpOperatorTaskCommandV2 command, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCreateCommandAsync(McpOperatorV2Target target, McpOperatorTaskCommandV2 command, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewRunLibraryScriptAsync(McpOperatorV2Target target, McpOperatorTaskScriptV2 script, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmRunLibraryScriptAsync(McpOperatorV2Target target, McpOperatorTaskScriptV2 script, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCancelAsync(McpOperatorV2Target target, long taskId, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCancelAsync(McpOperatorV2Target target, long taskId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound Production task facade. It contains no fallback to retired task
/// routes and never accepts a caller-supplied base path.
/// </summary>
public sealed class McpOperatorTaskV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorTaskV2Client
{
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> ListAsync(McpOperatorV2Target target, string? state = null, DateTimeOffset? sinceUtc = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync(WithQuery(Path(target, string.Empty), ("state", state), ("sinceUtc", sinceUtc?.ToString("O", CultureInfo.InvariantCulture)), ("limit", limit?.ToString(CultureInfo.InvariantCulture))), cancellationToken);

    public Task<JsonNode?> GetAsync(McpOperatorV2Target target, long taskId, CancellationToken cancellationToken = default) =>
        taskId > 0 ? _client.GetAsync(Path(target, $"/{taskId.ToString(CultureInfo.InvariantCulture)}"), cancellationToken) : throw Invalid("taskId");

    public Task<JsonNode?> ListLogsAsync(McpOperatorV2Target target, long taskId, long? sinceId = null, string? stream = null, int? limit = null, CancellationToken cancellationToken = default) =>
        taskId > 0 ? _client.GetAsync(WithQuery(Path(target, $"/{taskId.ToString(CultureInfo.InvariantCulture)}/logs"), ("sinceId", sinceId?.ToString(CultureInfo.InvariantCulture)), ("stream", stream), ("limit", limit?.ToString(CultureInfo.InvariantCulture))), cancellationToken) : throw Invalid("taskId");

    public Task<JsonNode?> ListLogsByRequestAsync(McpOperatorV2Target target, string requestId, long? sinceId = null, string? stream = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        if (!IsHex(requestId, 32)) throw Invalid("requestId");
        return _client.GetAsync(WithQuery(Path(target, "/logs"), ("requestId", requestId), ("sinceId", sinceId?.ToString(CultureInfo.InvariantCulture)), ("stream", stream), ("limit", limit?.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    }

    public Task<JsonNode?> PreviewCreateCommandAsync(McpOperatorV2Target target, McpOperatorTaskCommandV2 command, CancellationToken cancellationToken = default) => SendAsync(target, "create_command", CommandBody(command), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmCreateCommandAsync(McpOperatorV2Target target, McpOperatorTaskCommandV2 command, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync(target, "create_command", CommandBody(command), true, planToken, idempotencyKey, cancellationToken);
    public Task<JsonNode?> PreviewRunLibraryScriptAsync(McpOperatorV2Target target, McpOperatorTaskScriptV2 script, CancellationToken cancellationToken = default) => SendAsync(target, "run_library_script", ScriptBody(script), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmRunLibraryScriptAsync(McpOperatorV2Target target, McpOperatorTaskScriptV2 script, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync(target, "run_library_script", ScriptBody(script), true, planToken, idempotencyKey, cancellationToken);
    public Task<JsonNode?> PreviewCancelAsync(McpOperatorV2Target target, long taskId, CancellationToken cancellationToken = default) => SendAsync(target, "cancel", TaskIdBody(taskId), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmCancelAsync(McpOperatorV2Target target, long taskId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync(target, "cancel", TaskIdBody(taskId), true, planToken, idempotencyKey, cancellationToken);

    private Task<JsonNode?> SendAsync(McpOperatorV2Target target, string action, JsonObject body, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (confirmed) AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, $"/{(confirmed ? "confirm" : "preview")}/{action}"), body, cancellationToken);
    }

    private static JsonObject CommandBody(McpOperatorTaskCommandV2 command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsText(command.Shell, 32) || !IsText(command.Command, 32 * 1024) || !IsText(command.WorkingDirectory, 4096) || command.TimeoutSeconds is < 1 or > 3600 || command.MaximumOutputBytes is < 1 or > 48 * 1024 || command.EnvironmentReferences is { Count: > 32 }) throw Invalid("command");
        return new JsonObject { ["command"] = new JsonObject { ["shell"] = command.Shell, ["command"] = command.Command, ["workingDirectory"] = command.WorkingDirectory, ["timeoutSeconds"] = command.TimeoutSeconds, ["maximumOutputBytes"] = command.MaximumOutputBytes, ["environmentReferences"] = new JsonArray((command.EnvironmentReferences ?? []).Select(value => JsonValue.Create(value)).ToArray()) } };
    }

    private static JsonObject ScriptBody(McpOperatorTaskScriptV2 script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.ScriptId <= 0 || script.Version <= 0 || !IsHex(script.ContentHash, 64) || script.Parameters is { Count: > 32 }) throw Invalid("script");
        var parameters = new JsonObject();
        foreach (var pair in script.Parameters ?? new Dictionary<string, string>())
        {
            if (!IsIdentifier(pair.Key) || !IsText(pair.Value, 1024)) throw Invalid("script.parameters");
            parameters[pair.Key] = pair.Value;
        }
        return new JsonObject { ["script"] = new JsonObject { ["scriptId"] = script.ScriptId, ["version"] = script.Version, ["contentHash"] = script.ContentHash, ["parameters"] = parameters } };
    }

    private static JsonObject TaskIdBody(long taskId) => taskId > 0 ? new JsonObject { ["taskId"] = taskId } : throw Invalid("taskId");
    private string Path(McpOperatorV2Target target, string suffix)
    {
        ValidateTarget(target);
        return $"/api/v2/mcp/operator/agents/{target.TenantId}/{target.AgentId:D}/tasks{suffix}";
    }
    private void ValidateTarget(McpOperatorV2Target target)
    {
        target.Validate();
        EnsureOperatorTarget(_client, "task");
    }
    internal static void AddPlan(JsonObject body, string? planToken, string? idempotencyKey)
    {
        if (!IsOpaque(planToken) || !IsOpaque(idempotencyKey)) throw Invalid("planToken/idempotencyKey");
        body["planToken"] = planToken;
        body["idempotencyKey"] = idempotencyKey;
    }
    internal static string WithQuery(string path, params (string Name, string? Value)[] values)
    {
        var query = values.Where(value => !string.IsNullOrWhiteSpace(value.Value)).Select(value => $"{Uri.EscapeDataString(value.Name)}={Uri.EscapeDataString(value.Value!)}");
        var serialized = string.Join('&', query);
        return string.IsNullOrEmpty(serialized) ? path : $"{path}?{serialized}";
    }
    internal static bool IsText(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !value.Contains('\0');
    internal static bool IsHex(string? value, int length) => value is { Length: var actual } && actual == length && value.All(char.IsAsciiHexDigit);
    internal static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } && (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    internal static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    internal static AgentClientValidationException Invalid(string field) => new($"The policy-admitted V2 operator {field} value is invalid.");
    internal static void EnsureOperatorTarget(INetRatelMcpOutboundClient client, string facade)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.Target.Instance is not ("dev" or "prod"))
            throw new AgentClientValidationException($"The V2 operator {facade} client requires a dev or prod target.");
    }
}

public interface IMcpOperatorRequestV2Client
{
    Task<JsonNode?> ListAsync(McpOperatorV2Target target, string? state = null, long? jobId = null, DateTimeOffset? sinceUtc = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(McpOperatorV2Target target, int requestId, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCreateAsync(McpOperatorV2Target target, McpOperatorRequestCreateV2 request, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCreateAsync(McpOperatorV2Target target, McpOperatorRequestCreateV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewUpdateAsync(McpOperatorV2Target target, McpOperatorRequestUpdateV2 request, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmUpdateAsync(McpOperatorV2Target target, McpOperatorRequestUpdateV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewClaimAsync(McpOperatorV2Target target, McpOperatorRequestClaimV2 request, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmClaimAsync(McpOperatorV2Target target, McpOperatorRequestClaimV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCompleteAsync(McpOperatorV2Target target, McpOperatorRequestResultV2 request, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCompleteAsync(McpOperatorV2Target target, McpOperatorRequestResultV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewFailAsync(McpOperatorV2Target target, McpOperatorRequestResultV2 request, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmFailAsync(McpOperatorV2Target target, McpOperatorRequestResultV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCancelAsync(McpOperatorV2Target target, McpOperatorRequestCancelV2 request, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCancelAsync(McpOperatorV2Target target, McpOperatorRequestCancelV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>Route-bound Production request facade with ETag and plan inputs.</summary>
public sealed class McpOperatorRequestV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorRequestV2Client
{
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> ListAsync(McpOperatorV2Target target, string? state = null, long? jobId = null, DateTimeOffset? sinceUtc = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.GetAsync(McpOperatorTaskV2Client.WithQuery(Path(target, string.Empty), ("state", state), ("jobId", jobId?.ToString(CultureInfo.InvariantCulture)), ("sinceUtc", sinceUtc?.ToString("O", CultureInfo.InvariantCulture)), ("limit", limit?.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    public Task<JsonNode?> GetAsync(McpOperatorV2Target target, int requestId, CancellationToken cancellationToken = default) =>
        requestId > 0 ? _client.GetAsync(Path(target, $"/{requestId.ToString(CultureInfo.InvariantCulture)}"), cancellationToken) : throw McpOperatorTaskV2Client.Invalid("requestId");
    public Task<JsonNode?> PreviewCreateAsync(McpOperatorV2Target target, McpOperatorRequestCreateV2 request, CancellationToken cancellationToken = default) => SendAsync(target, "create", CreateBody(request), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmCreateAsync(McpOperatorV2Target target, McpOperatorRequestCreateV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync(target, "create", CreateBody(request), true, planToken, idempotencyKey, cancellationToken);
    public Task<JsonNode?> PreviewUpdateAsync(McpOperatorV2Target target, McpOperatorRequestUpdateV2 request, CancellationToken cancellationToken = default) => SendAsync(target, "update", UpdateBody(request), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmUpdateAsync(McpOperatorV2Target target, McpOperatorRequestUpdateV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync(target, "update", UpdateBody(request), true, planToken, idempotencyKey, cancellationToken);
    public Task<JsonNode?> PreviewClaimAsync(McpOperatorV2Target target, McpOperatorRequestClaimV2 request, CancellationToken cancellationToken = default) => SendAsync(target, "claim", ClaimBody(request), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmClaimAsync(McpOperatorV2Target target, McpOperatorRequestClaimV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync(target, "claim", ClaimBody(request), true, planToken, idempotencyKey, cancellationToken);
    public Task<JsonNode?> PreviewCompleteAsync(McpOperatorV2Target target, McpOperatorRequestResultV2 request, CancellationToken cancellationToken = default) => SendAsync(target, "complete", ResultBody(request), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmCompleteAsync(McpOperatorV2Target target, McpOperatorRequestResultV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync(target, "complete", ResultBody(request), true, planToken, idempotencyKey, cancellationToken);
    public Task<JsonNode?> PreviewFailAsync(McpOperatorV2Target target, McpOperatorRequestResultV2 request, CancellationToken cancellationToken = default) => SendAsync(target, "fail", ResultBody(request), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmFailAsync(McpOperatorV2Target target, McpOperatorRequestResultV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync(target, "fail", ResultBody(request), true, planToken, idempotencyKey, cancellationToken);
    public Task<JsonNode?> PreviewCancelAsync(McpOperatorV2Target target, McpOperatorRequestCancelV2 request, CancellationToken cancellationToken = default) => SendAsync(target, "cancel", CancelBody(request), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmCancelAsync(McpOperatorV2Target target, McpOperatorRequestCancelV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync(target, "cancel", CancelBody(request), true, planToken, idempotencyKey, cancellationToken);

    private Task<JsonNode?> SendAsync(McpOperatorV2Target target, string action, JsonObject body, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, $"/{(confirmed ? "confirm" : "preview")}/{action}"), body, cancellationToken);
    }
    private static JsonObject CreateBody(McpOperatorRequestCreateV2 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.JobId > 0 && McpOperatorTaskV2Client.IsText(request.Summary, 4096) ? new JsonObject { ["jobId"] = request.JobId, ["summary"] = request.Summary } : throw McpOperatorTaskV2Client.Invalid("request create");
    }
    private static JsonObject UpdateBody(McpOperatorRequestUpdateV2 request) => request.RequestId > 0 && request.ExpectedVersion > 0 && McpOperatorTaskV2Client.IsText(request.Summary, 4096) ? new JsonObject { ["requestId"] = request.RequestId, ["expectedVersion"] = request.ExpectedVersion, ["summary"] = request.Summary } : throw McpOperatorTaskV2Client.Invalid("request update");
    private static JsonObject ClaimBody(McpOperatorRequestClaimV2 request) => request.RequestId > 0 && request.ExpectedVersion > 0 && McpOperatorTaskV2Client.IsText(request.ClaimReference, 128) ? new JsonObject { ["requestId"] = request.RequestId, ["expectedVersion"] = request.ExpectedVersion, ["claimReference"] = request.ClaimReference } : throw McpOperatorTaskV2Client.Invalid("request claim");
    private static JsonObject ResultBody(McpOperatorRequestResultV2 request) => request.RequestId > 0 && request.ExpectedVersion > 0 && McpOperatorTaskV2Client.IsText(request.ResultSummary, 48 * 1024) ? new JsonObject { ["requestId"] = request.RequestId, ["expectedVersion"] = request.ExpectedVersion, ["resultSummary"] = request.ResultSummary } : throw McpOperatorTaskV2Client.Invalid("request result");
    private static JsonObject CancelBody(McpOperatorRequestCancelV2 request)
    {
        if (request.RequestId <= 0 || request.ExpectedVersion <= 0 || (request.ResultSummary is not null && !McpOperatorTaskV2Client.IsText(request.ResultSummary, 48 * 1024))) throw McpOperatorTaskV2Client.Invalid("request cancel");
        return new JsonObject { ["requestId"] = request.RequestId, ["expectedVersion"] = request.ExpectedVersion, ["resultSummary"] = request.ResultSummary };
    }
    private string Path(McpOperatorV2Target target, string suffix)
    {
        target.Validate();
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "request");
        return $"/api/v2/mcp/operator/agents/{target.TenantId}/{target.AgentId:D}/requests{suffix}";
    }
}

public interface IMcpOperatorTenantV2Client
{
    Task<JsonNode?> ListAsync(int? cursor = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(int tenantId, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCreateAsync(McpOperatorTenantDraftV2 tenant, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCreateAsync(McpOperatorTenantDraftV2 tenant, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewUpdateAsync(McpOperatorTenantUpdateV2 tenant, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmUpdateAsync(McpOperatorTenantUpdateV2 tenant, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewDeleteAsync(McpOperatorTenantDeleteV2 tenant, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmDeleteAsync(McpOperatorTenantDeleteV2 tenant, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound Production control-plane tenant facade. Tenant mutation request
/// values never form an agent target: admission is evaluated only against the
/// caller's signed ControlPlane policy.
/// </summary>
public sealed class McpOperatorTenantV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorTenantV2Client
{
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> ListAsync(int? cursor = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        EnsureProduction();
        if (cursor is < 0 || limit is < 1 or > 100) throw McpOperatorTaskV2Client.Invalid("tenant list");
        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(Path, ("cursor", cursor?.ToString(CultureInfo.InvariantCulture)), ("limit", limit?.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    }

    public Task<JsonNode?> GetAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        EnsureProduction();
        return tenantId > 0
            ? _client.GetAsync($"{Path}/{tenantId.ToString(CultureInfo.InvariantCulture)}", cancellationToken)
            : throw McpOperatorTaskV2Client.Invalid("tenantId");
    }

    public Task<JsonNode?> PreviewCreateAsync(McpOperatorTenantDraftV2 tenant, CancellationToken cancellationToken = default) => SendAsync("create", CreateBody(tenant), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmCreateAsync(McpOperatorTenantDraftV2 tenant, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync("create", CreateBody(tenant), true, planToken, idempotencyKey, cancellationToken);
    public Task<JsonNode?> PreviewUpdateAsync(McpOperatorTenantUpdateV2 tenant, CancellationToken cancellationToken = default) => SendAsync("update", UpdateBody(tenant), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmUpdateAsync(McpOperatorTenantUpdateV2 tenant, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync("update", UpdateBody(tenant), true, planToken, idempotencyKey, cancellationToken);
    public Task<JsonNode?> PreviewDeleteAsync(McpOperatorTenantDeleteV2 tenant, CancellationToken cancellationToken = default) => SendAsync("delete", DeleteBody(tenant), false, null, null, cancellationToken);
    public Task<JsonNode?> ConfirmDeleteAsync(McpOperatorTenantDeleteV2 tenant, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) => SendAsync("delete", DeleteBody(tenant), true, planToken, idempotencyKey, cancellationToken);

    private Task<JsonNode?> SendAsync(string action, JsonObject body, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        EnsureProduction();
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, $"{Path}/{(confirmed ? "confirm" : "preview")}/{action}", body, cancellationToken);
    }

    private static JsonObject CreateBody(McpOperatorTenantDraftV2 tenant) => TenantBody(tenant);

    private static JsonObject UpdateBody(McpOperatorTenantUpdateV2 update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.TenantId <= 0 || update.ExpectedVersion <= 0) throw McpOperatorTaskV2Client.Invalid("tenant update");
        var body = TenantBody(update.Tenant);
        body["tenantId"] = update.TenantId;
        body["expectedVersion"] = update.ExpectedVersion;
        return body;
    }

    private static JsonObject DeleteBody(McpOperatorTenantDeleteV2 delete)
    {
        ArgumentNullException.ThrowIfNull(delete);
        if (delete.TenantId <= 0 || delete.ExpectedVersion <= 0) throw McpOperatorTaskV2Client.Invalid("tenant delete");
        return new JsonObject { ["tenantId"] = delete.TenantId, ["expectedVersion"] = delete.ExpectedVersion, ["cascade"] = delete.Cascade };
    }

    private static JsonObject TenantBody(McpOperatorTenantDraftV2 tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (!TenantText(tenant.Name, 160) || tenant.Domains is null || tenant.Domains.Count > 64 || tenant.Domains.Any(domain => !TenantText(domain, 253)) ||
            tenant.Domains.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tenant.Domains.Count ||
            !OptionalTenantText(tenant.Description, 4_096) || !OptionalTenantText(tenant.Location, 256) || !OptionalTenantText(tenant.ContactPerson, 256) ||
            !OptionalTenantText(tenant.ContactEmail, 320) || !OptionalTenantText(tenant.AutoUpdateTargetVersion, 128) ||
            (tenant.AutoUpdateChannel is not null && tenant.AutoUpdateChannel is not ("stable" or "prerelease")))
            throw McpOperatorTaskV2Client.Invalid("tenant");

        var body = new JsonObject
        {
            ["name"] = tenant.Name,
            ["domains"] = new JsonArray(tenant.Domains.Select(domain => JsonValue.Create(domain)).ToArray()),
            ["autoUpdate"] = tenant.AutoUpdate
        };
        if (tenant.Description is not null) body["description"] = tenant.Description;
        if (tenant.Location is not null) body["location"] = tenant.Location;
        if (tenant.ContactPerson is not null) body["contactPerson"] = tenant.ContactPerson;
        if (tenant.ContactEmail is not null) body["contactEmail"] = tenant.ContactEmail;
        if (tenant.AutoUpdateChannel is not null) body["autoUpdateChannel"] = tenant.AutoUpdateChannel;
        if (tenant.AutoUpdateTargetVersion is not null) body["autoUpdateTargetVersion"] = tenant.AutoUpdateTargetVersion;
        return body;
    }

    private void EnsureProduction()
    {
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "tenant");
    }

    private static bool TenantText(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool OptionalTenantText(string? value, int maximum) => value is null || (value.Length <= maximum && !value.Any(char.IsControl));
    private const string Path = "/api/v2/mcp/operator/tenants";
}

/// <summary>Tenant-scoped Production enrollment-code request. Prospective clients have no agent ID yet.</summary>
public sealed record McpOperatorOnboardingEnrollmentV2(string Runtime, int ValidForMinutes, int MaxUses);

public interface IMcpOperatorOnboardingV2Client
{
    Task<JsonNode?> GetCollateralAsync(int tenantId, string runtime, CancellationToken cancellationToken = default);
    Task<JsonNode?> DownloadCollateralAsync(int tenantId, string runtime, CancellationToken cancellationToken = default);
    Task<JsonNode?> ListEnrollmentsAsync(int tenantId, string? status = null, long? cursor = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetEnrollmentAsync(int tenantId, Guid enrollmentCodeId, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCreateEnrollmentAsync(int tenantId, McpOperatorOnboardingEnrollmentV2 enrollment, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCreateEnrollmentAsync(int tenantId, McpOperatorOnboardingEnrollmentV2 enrollment, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewRevokeEnrollmentAsync(int tenantId, Guid enrollmentCodeId, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmRevokeEnrollmentAsync(int tenantId, Guid enrollmentCodeId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound Production pre-enrollment facade. This is intentionally tenant
/// scoped: creating or revoking an enrollment code must never invent an agent
/// target before a client has enrolled.
/// </summary>
public sealed class McpOperatorOnboardingV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorOnboardingV2Client
{
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> GetCollateralAsync(int tenantId, string runtime, CancellationToken cancellationToken = default)
    {
        EnsureProduction();
        ValidateTenant(tenantId);
        ValidateRuntime(runtime);
        return _client.GetAsync($"{Path(tenantId)}/collateral/{runtime}", cancellationToken);
    }

    public Task<JsonNode?> DownloadCollateralAsync(int tenantId, string runtime, CancellationToken cancellationToken = default)
    {
        EnsureProduction();
        ValidateTenant(tenantId);
        ValidateRuntime(runtime);
        return _client.GetAsync($"{Path(tenantId)}/collateral/{runtime}/download", cancellationToken);
    }

    public Task<JsonNode?> ListEnrollmentsAsync(int tenantId, string? status = null, long? cursor = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        EnsureProduction();
        ValidateTenant(tenantId);
        if (status is not null and not ("active" or "expired" or "revoked" or "all") || cursor < 0 || limit is < 1 or > 100)
            throw McpOperatorTaskV2Client.Invalid("onboarding list");
        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(Path(tenantId) + "/enrollments",
            ("status", status), ("cursor", cursor?.ToString(CultureInfo.InvariantCulture)), ("limit", limit?.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    }

    public Task<JsonNode?> GetEnrollmentAsync(int tenantId, Guid enrollmentCodeId, CancellationToken cancellationToken = default)
    {
        EnsureProduction();
        ValidateTenant(tenantId);
        ValidateEnrollment(enrollmentCodeId);
        return _client.GetAsync($"{Path(tenantId)}/enrollments/{enrollmentCodeId:D}", cancellationToken);
    }

    public Task<JsonNode?> PreviewCreateEnrollmentAsync(int tenantId, McpOperatorOnboardingEnrollmentV2 enrollment, CancellationToken cancellationToken = default) =>
        SendCreateAsync(tenantId, enrollment, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmCreateEnrollmentAsync(int tenantId, McpOperatorOnboardingEnrollmentV2 enrollment, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendCreateAsync(tenantId, enrollment, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewRevokeEnrollmentAsync(int tenantId, Guid enrollmentCodeId, CancellationToken cancellationToken = default) =>
        SendRevokeAsync(tenantId, enrollmentCodeId, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmRevokeEnrollmentAsync(int tenantId, Guid enrollmentCodeId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendRevokeAsync(tenantId, enrollmentCodeId, true, planToken, idempotencyKey, cancellationToken);

    private Task<JsonNode?> SendCreateAsync(int tenantId, McpOperatorOnboardingEnrollmentV2 enrollment, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        EnsureProduction();
        ValidateTenant(tenantId);
        ArgumentNullException.ThrowIfNull(enrollment);
        ValidateRuntime(enrollment.Runtime);
        if (enrollment.ValidForMinutes is < 5 or > 10 || enrollment.MaxUses is < 1 or > 10)
            throw McpOperatorTaskV2Client.Invalid("onboarding enrollment");
        var body = new JsonObject { ["runtime"] = enrollment.Runtime, ["validForMinutes"] = enrollment.ValidForMinutes, ["maxUses"] = enrollment.MaxUses };
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, $"{Path(tenantId)}/{(confirmed ? "confirm" : "preview")}/create-enrollment", body, cancellationToken);
    }

    private Task<JsonNode?> SendRevokeAsync(int tenantId, Guid enrollmentCodeId, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        EnsureProduction();
        ValidateTenant(tenantId);
        ValidateEnrollment(enrollmentCodeId);
        var body = new JsonObject { ["enrollmentCodeId"] = enrollmentCodeId.ToString("D") };
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, $"{Path(tenantId)}/{(confirmed ? "confirm" : "preview")}/revoke-enrollment", body, cancellationToken);
    }

    private void EnsureProduction()
    {
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "onboarding");
    }

    private static void ValidateTenant(int tenantId)
    {
        if (tenantId <= 0) throw McpOperatorTaskV2Client.Invalid("tenantId");
    }

    private static void ValidateRuntime(string? runtime)
    {
        if (runtime is not ("linux-x64" or "win-x64")) throw McpOperatorTaskV2Client.Invalid("onboarding runtime");
    }

    private static void ValidateEnrollment(Guid enrollmentCodeId)
    {
        if (enrollmentCodeId == Guid.Empty) throw McpOperatorTaskV2Client.Invalid("enrollmentCodeId");
    }

    private static string Path(int tenantId) => $"/api/v2/mcp/operator/tenants/{tenantId.ToString(CultureInfo.InvariantCulture)}/onboarding";
}

/// <summary>Exact-target Production client inspection facade.</summary>
public interface IMcpOperatorClientV2Client
{
    Task<JsonNode?> GetAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetPresenceAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetCapabilitiesAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetBindingAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetTelemetryAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetUpdateAttemptsAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetUpdateMetadataAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewPingAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmPingAsync(McpOperatorV2Target target, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewSoftwareUpdateAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmSoftwareUpdateAsync(McpOperatorV2Target target, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewDisableAsync(McpOperatorV2Target target, string reason, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmDisableAsync(McpOperatorV2Target target, string reason, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewEnableAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmEnableAsync(McpOperatorV2Target target, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewDeleteAsync(McpOperatorV2Target target, string reason, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmDeleteAsync(McpOperatorV2Target target, string reason, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound Production client facade. It permits only the policy-admitted,
/// delegated V2 reads and deliberately has no fallback to browser or legacy
/// client-identity endpoints.
/// </summary>
public sealed class McpOperatorClientV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorClientV2Client
{
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> GetAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, string.Empty), cancellationToken);

    public Task<JsonNode?> GetPresenceAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, "/presence"), cancellationToken);

    public Task<JsonNode?> GetCapabilitiesAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, "/capabilities"), cancellationToken);

    public Task<JsonNode?> GetBindingAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, "/binding"), cancellationToken);

    public Task<JsonNode?> GetTelemetryAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, "/telemetry"), cancellationToken);

    public Task<JsonNode?> GetUpdateAttemptsAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, "/update-attempts"), cancellationToken);

    public Task<JsonNode?> GetUpdateMetadataAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, "/update-metadata"), cancellationToken);

    public Task<JsonNode?> PreviewPingAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.SendAsync(HttpMethod.Post, Path(target, "/ping/preview"), null, cancellationToken);

    public Task<JsonNode?> ConfirmPingAsync(McpOperatorV2Target target, string planToken, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject();
        McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, "/ping/confirm"), body, cancellationToken);
    }

    public Task<JsonNode?> PreviewSoftwareUpdateAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.SendAsync(HttpMethod.Post, Path(target, "/software-update/preview"), null, cancellationToken);

    public Task<JsonNode?> ConfirmSoftwareUpdateAsync(McpOperatorV2Target target, string planToken, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject();
        McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, "/software-update/confirm"), body, cancellationToken);
    }

    public Task<JsonNode?> PreviewDisableAsync(McpOperatorV2Target target, string reason, CancellationToken cancellationToken = default) =>
        _client.SendAsync(HttpMethod.Post, Path(target, "/disable/preview"), DisableBody(reason), cancellationToken);

    public Task<JsonNode?> ConfirmDisableAsync(McpOperatorV2Target target, string reason, string planToken, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = DisableBody(reason);
        McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, "/disable/confirm"), body, cancellationToken);
    }

    public Task<JsonNode?> PreviewEnableAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.SendAsync(HttpMethod.Post, Path(target, "/enable/preview"), null, cancellationToken);

    public Task<JsonNode?> ConfirmEnableAsync(McpOperatorV2Target target, string planToken, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject();
        McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, "/enable/confirm"), body, cancellationToken);
    }

    public Task<JsonNode?> PreviewDeleteAsync(McpOperatorV2Target target, string reason, CancellationToken cancellationToken = default) =>
        _client.SendAsync(HttpMethod.Post, Path(target, "/delete/preview"), DeleteBody(reason), cancellationToken);

    public Task<JsonNode?> ConfirmDeleteAsync(McpOperatorV2Target target, string reason, string planToken, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = DeleteBody(reason);
        McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, "/delete/confirm"), body, cancellationToken);
    }

    private static JsonObject DisableBody(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 256 || reason.Any(char.IsControl))
            throw McpOperatorTaskV2Client.Invalid("disable reason");
        return new JsonObject { ["reason"] = reason.Trim() };
    }

    private static JsonObject DeleteBody(string? reason) => DisableBody(reason);

    private string Path(McpOperatorV2Target target, string suffix)
    {
        target.Validate();
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "client");
        return $"/api/v2/mcp/operator/agents/{target.TenantId.ToString(CultureInfo.InvariantCulture)}/{target.AgentId:D}/clients{suffix}";
    }
}

/// <summary>Production delegated-operator notification facade.</summary>
public interface IMcpOperatorNotificationV2Client
{
    Task<JsonNode?> ListAsync(CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(Guid notificationId, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetSummaryAsync(CancellationToken cancellationToken = default);
    Task<JsonNode?> GetUnreadErrorsAsync(CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewMarkReadAsync(IReadOnlyList<Guid> notificationIds, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmMarkReadAsync(IReadOnlyList<Guid> notificationIds, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound Production notification facade. The API derives the receipt
/// owner from the signed delegated human operator; this facade never supplies
/// a user ID or uses browser notification routes.
/// </summary>
public sealed class McpOperatorNotificationV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorNotificationV2Client
{
    private const string Path = "/api/v2/mcp/operator/notifications";
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> ListAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync(ProductionPath(), cancellationToken);

    public Task<JsonNode?> GetAsync(Guid notificationId, CancellationToken cancellationToken = default) =>
        notificationId != Guid.Empty
            ? _client.GetAsync($"{ProductionPath()}/{notificationId:D}", cancellationToken)
            : throw McpOperatorTaskV2Client.Invalid("notificationId");

    public Task<JsonNode?> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{ProductionPath()}/summary", cancellationToken);

    public Task<JsonNode?> GetUnreadErrorsAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{ProductionPath()}/unread-errors", cancellationToken);

    public Task<JsonNode?> PreviewMarkReadAsync(IReadOnlyList<Guid> notificationIds, CancellationToken cancellationToken = default) =>
        SendMarkReadAsync(notificationIds, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmMarkReadAsync(IReadOnlyList<Guid> notificationIds, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendMarkReadAsync(notificationIds, true, planToken, idempotencyKey, cancellationToken);

    private Task<JsonNode?> SendMarkReadAsync(IReadOnlyList<Guid> notificationIds, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var body = MarkReadBody(notificationIds);
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, $"{ProductionPath()}/{(confirmed ? "confirm" : "preview")}/mark-read", body, cancellationToken);
    }

    private string ProductionPath()
    {
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "notification");
        return Path;
    }

    private static JsonObject MarkReadBody(IReadOnlyList<Guid>? notificationIds)
    {
        if (notificationIds is not { Count: > 0 and <= 200 } || notificationIds.Any(id => id == Guid.Empty) || notificationIds.Distinct().Count() != notificationIds.Count)
            throw McpOperatorTaskV2Client.Invalid("notificationIds");
        return new JsonObject { ["ids"] = new JsonArray(notificationIds.Select(id => JsonValue.Create(id.ToString("D"))).ToArray()) };
    }
}

/// <summary>Production delegated-operator facade for redacted outbox events.</summary>
public interface IMcpOperatorEventV2Client
{
    Task<JsonNode?> ListAsync(int? page = null, int? pageSize = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewRetryAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmRetryAsync(Guid eventId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewDisableAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmDisableAsync(Guid eventId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>Route-bound Production event facade with no legacy event route fallback.</summary>
public sealed class McpOperatorEventV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorEventV2Client
{
    private const string BasePath = "/api/v2/mcp/operator/events";
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> ListAsync(int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        if (page is < 1 || pageSize is < 1 or > 100) throw McpOperatorTaskV2Client.Invalid("event page");
        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(ProductionPath(), ("page", page?.ToString(CultureInfo.InvariantCulture)), ("pageSize", pageSize?.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    }

    public Task<JsonNode?> GetAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        eventId != Guid.Empty
            ? _client.GetAsync($"{ProductionPath()}/{eventId:D}", cancellationToken)
            : throw McpOperatorTaskV2Client.Invalid("eventId");

    public Task<JsonNode?> PreviewRetryAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        PreviewAsync(eventId, "retry", cancellationToken);

    public Task<JsonNode?> ConfirmRetryAsync(Guid eventId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        ConfirmAsync(eventId, "retry", planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewDisableAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        PreviewAsync(eventId, "disable", cancellationToken);

    public Task<JsonNode?> ConfirmDisableAsync(Guid eventId, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        ConfirmAsync(eventId, "disable", planToken, idempotencyKey, cancellationToken);

    private Task<JsonNode?> PreviewAsync(Guid eventId, string action, CancellationToken cancellationToken) =>
        eventId != Guid.Empty
            ? _client.SendAsync(HttpMethod.Post, $"{ProductionPath()}/{eventId:D}/{action}/preview", null, cancellationToken)
            : throw McpOperatorTaskV2Client.Invalid("eventId");

    private Task<JsonNode?> ConfirmAsync(Guid eventId, string action, string planToken, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (eventId == Guid.Empty) throw McpOperatorTaskV2Client.Invalid("eventId");
        var body = new JsonObject();
        McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, $"{ProductionPath()}/{eventId:D}/{action}/confirm", body, cancellationToken);
    }

    private string ProductionPath()
    {
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "event");
        return BasePath;
    }
}

/// <summary>Production delegated-operator facade for server-owned connectivity.</summary>
public interface IMcpOperatorConnectivityV2Client
{
    Task<JsonNode?> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<JsonNode?> GetNetRatelAsync(CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewTestAsync(CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmTestAsync(string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>Route-bound Production connectivity facade; it accepts no endpoint override.</summary>
public sealed class McpOperatorConnectivityV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorConnectivityV2Client
{
    private const string BasePath = "/api/v2/mcp/operator/connectivity";
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{ProductionPath()}/settings", cancellationToken);

    public Task<JsonNode?> GetNetRatelAsync(CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{ProductionPath()}/netratel", cancellationToken);

    public Task<JsonNode?> PreviewTestAsync(CancellationToken cancellationToken = default) =>
        _client.SendAsync(HttpMethod.Post, $"{ProductionPath()}/test/preview", null, cancellationToken);

    public Task<JsonNode?> ConfirmTestAsync(string planToken, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject();
        McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, $"{ProductionPath()}/test/confirm", body, cancellationToken);
    }

    private string ProductionPath()
    {
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "connectivity");
        return BasePath;
    }
}
