using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

/// <summary>Bounded inputs for a policy-admitted V2 one-shot command.</summary>
public sealed record McpOperatorCommandExecutionV2(
    string Shell,
    string Command,
    string WorkingDirectory,
    int? TimeoutSeconds = null,
    int? MaximumOutputBytes = null,
    IReadOnlyList<string>? EnvironmentReferences = null);

/// <summary>
/// Route-bound V2 command lifecycle for one exact tenant and agent. Command
/// execution is destructive and must use the matching preview credentials.
/// </summary>
public interface IMcpOperatorCommandV2Client
{
    Task<JsonNode?> GetAvailabilityAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewExecuteAsync(McpOperatorV2Target target, McpOperatorCommandExecutionV2 command, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmExecuteAsync(McpOperatorV2Target target, McpOperatorCommandExecutionV2 command, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(McpOperatorV2Target target, string commandId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cancellation of one caller-owned command. The API route has no
    /// plan-token body; callers must collect explicit user confirmation before
    /// invoking this method.
    /// </summary>
    Task<JsonNode?> CancelAsync(McpOperatorV2Target target, string commandId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound V2 command façade. It derives every target path, has no legacy
/// command fallback, and never accepts an arbitrary outbound API path.
/// </summary>
public sealed class McpOperatorCommandV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorCommandV2Client
{
    private const int MaximumCommandBytes = 32 * 1024;
    private const int MaximumTimeoutSeconds = 60 * 60;
    private const int MaximumOutputBytes = 48 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> GetAvailabilityAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, "/availability"), cancellationToken);

    public Task<JsonNode?> PreviewExecuteAsync(McpOperatorV2Target target, McpOperatorCommandExecutionV2 command, CancellationToken cancellationToken = default) =>
        SendExecuteAsync(target, command, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmExecuteAsync(McpOperatorV2Target target, McpOperatorCommandExecutionV2 command, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendExecuteAsync(target, command, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> GetAsync(McpOperatorV2Target target, string commandId, CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{Path(target, string.Empty)}/{ValidateCommandId(commandId)}", cancellationToken);

    public Task<JsonNode?> CancelAsync(McpOperatorV2Target target, string commandId, CancellationToken cancellationToken = default) =>
        _client.SendAsync(HttpMethod.Post, $"{Path(target, string.Empty)}/{ValidateCommandId(commandId)}/cancel", null, cancellationToken);

    private Task<JsonNode?> SendExecuteAsync(McpOperatorV2Target target, McpOperatorCommandExecutionV2 command, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var body = ToBody(command);
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, confirmed ? "/confirm" : "/preview"), body, cancellationToken);
    }

    private static JsonObject ToBody(McpOperatorCommandExecutionV2 command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var shell = command.Shell?.Trim();
        var workingDirectory = command.WorkingDirectory?.Trim();
        if (!McpOperatorTaskV2Client.IsText(shell, 32) ||
            !McpOperatorTaskV2Client.IsText(workingDirectory, 4096) ||
            !IsCommand(command.Command) ||
            command.TimeoutSeconds is < 1 or > MaximumTimeoutSeconds ||
            command.MaximumOutputBytes is < 1 or > MaximumOutputBytes)
        {
            throw McpOperatorTaskV2Client.Invalid("command");
        }

        var references = NormalizeEnvironmentReferences(command.EnvironmentReferences);
        return new JsonObject
        {
            ["shell"] = shell,
            ["command"] = command.Command,
            ["workingDirectory"] = workingDirectory,
            ["environmentReferences"] = new JsonArray(references.Select(reference => JsonValue.Create(reference)).ToArray()),
            ["timeoutSeconds"] = command.TimeoutSeconds,
            ["maximumOutputBytes"] = command.MaximumOutputBytes
        };
    }

    private static IReadOnlyList<string> NormalizeEnvironmentReferences(IReadOnlyList<string>? values)
    {
        if (values is null) return [];
        if (values.Count > 32) throw McpOperatorTaskV2Client.Invalid("command environmentReferences");
        var normalized = values.Select(value => value?.Trim()).ToArray();
        if (normalized.Any(value => !McpOperatorTaskV2Client.IsIdentifier(value)) ||
            normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw McpOperatorTaskV2Client.Invalid("command environmentReferences");
        }

        return normalized.Select(value => value!).ToArray();
    }

    private static bool IsCommand(string? value)
    {
        if (value is null) return false;
        if (!McpOperatorTaskV2Client.IsText(value, MaximumCommandBytes)) return false;
        try
        {
            return Utf8.GetByteCount(value) <= MaximumCommandBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static string ValidateCommandId(string? commandId) =>
        commandId is not null && McpOperatorTaskV2Client.IsHex(commandId, 32)
            ? commandId
            : throw McpOperatorTaskV2Client.Invalid("commandId");

    private string Path(McpOperatorV2Target target, string suffix)
    {
        target.Validate();
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "command");
        return $"/api/v2/mcp/operator/agents/{target.TenantId.ToString(CultureInfo.InvariantCulture)}/{target.AgentId:D}/commands{suffix}";
    }
}
