using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

/// <summary>Bounded terminal session settings accepted by the V2 open workflow.</summary>
public sealed record McpOperatorTerminalOpenV2(string Shell, string WorkingDirectory, int? Columns = null, int? Rows = null);

/// <summary>Bounded terminal dimensions accepted by the V2 resize workflow.</summary>
public sealed record McpOperatorTerminalResizeV2(int Columns, int Rows);

/// <summary>Route-bound V2 terminal lifecycle for one exact tenant and agent.</summary>
public interface IMcpOperatorTerminalV2Client
{
    Task<JsonNode?> GetAvailabilityAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewOpenAsync(McpOperatorV2Target target, McpOperatorTerminalOpenV2 open, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmOpenAsync(McpOperatorV2Target target, McpOperatorTerminalOpenV2 open, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(McpOperatorV2Target target, string sessionId, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetDiagnosticsAsync(McpOperatorV2Target target, string sessionId, CancellationToken cancellationToken = default);
    Task<JsonNode?> SendInputAsync(McpOperatorV2Target target, string sessionId, string input, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetStreamWindowAsync(McpOperatorV2Target target, string sessionId, int? windowSeconds = null, int? maxRecords = null, CancellationToken cancellationToken = default);

    /// <summary>Resizes one owned terminal session after explicit caller confirmation.</summary>
    Task<JsonNode?> ResizeAsync(McpOperatorV2Target target, string sessionId, McpOperatorTerminalResizeV2 resize, CancellationToken cancellationToken = default);

    /// <summary>Closes one owned terminal session after explicit caller confirmation.</summary>
    Task<JsonNode?> CloseAsync(McpOperatorV2Target target, string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Additive replay capability without changing existing terminal client implementations.</summary>
public interface IMcpOperatorTerminalOutputV2Client
{
    /// <summary>Reads retained output after a previously returned cursor without consuming it.</summary>
    Task<JsonNode?> GetStreamWindowAfterAsync(McpOperatorV2Target target, string sessionId, ulong afterSequence,
        int? windowSeconds = null, int? maxRecords = null, CancellationToken cancellationToken = default);

}

/// <summary>
/// Route-bound V2 terminal façade. It has no legacy terminal fallback and
/// derives every target path from a persisted tenant-agent pair.
/// </summary>
public sealed class McpOperatorTerminalV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorTerminalV2Client, IMcpOperatorTerminalOutputV2Client
{
    private const int MaximumInputBytes = 16 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> GetAvailabilityAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, "/availability"), cancellationToken);

    public Task<JsonNode?> PreviewOpenAsync(McpOperatorV2Target target, McpOperatorTerminalOpenV2 open, CancellationToken cancellationToken = default) =>
        SendOpenAsync(target, open, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmOpenAsync(McpOperatorV2Target target, McpOperatorTerminalOpenV2 open, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendOpenAsync(target, open, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> GetAsync(McpOperatorV2Target target, string sessionId, CancellationToken cancellationToken = default) =>
        _client.GetAsync(SessionPath(target, sessionId), cancellationToken);

    public Task<JsonNode?> GetDiagnosticsAsync(McpOperatorV2Target target, string sessionId, CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{SessionPath(target, sessionId)}/diagnostics", cancellationToken);

    public Task<JsonNode?> SendInputAsync(McpOperatorV2Target target, string sessionId, string input, CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        return _client.SendAsync(HttpMethod.Post, $"{SessionPath(target, sessionId)}/input", new JsonObject { ["input"] = input }, cancellationToken);
    }

    public Task<JsonNode?> GetStreamWindowAsync(McpOperatorV2Target target, string sessionId, int? windowSeconds = null, int? maxRecords = null, CancellationToken cancellationToken = default) =>
        ReadStreamWindowAsync(target, sessionId, null, windowSeconds, maxRecords, cancellationToken);

    public Task<JsonNode?> GetStreamWindowAfterAsync(McpOperatorV2Target target, string sessionId, ulong afterSequence,
        int? windowSeconds = null, int? maxRecords = null, CancellationToken cancellationToken = default) =>
        ReadStreamWindowAsync(target, sessionId, afterSequence, windowSeconds, maxRecords, cancellationToken);

    private Task<JsonNode?> ReadStreamWindowAsync(McpOperatorV2Target target, string sessionId, ulong? afterSequence,
        int? windowSeconds, int? maxRecords, CancellationToken cancellationToken)
    {
        if (windowSeconds is < 1 or > 15 || maxRecords is < 1 or > 100) throw McpOperatorTaskV2Client.Invalid("terminal stream window");
        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(
            $"{SessionPath(target, sessionId)}/stream-window",
            ("windowSeconds", windowSeconds?.ToString(CultureInfo.InvariantCulture)),
            ("maxRecords", maxRecords?.ToString(CultureInfo.InvariantCulture)),
            ("afterSequence", afterSequence?.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    }

    public Task<JsonNode?> ResizeAsync(McpOperatorV2Target target, string sessionId, McpOperatorTerminalResizeV2 resize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resize);
        if (resize.Columns is < 40 or > 300 || resize.Rows is < 10 or > 120) throw McpOperatorTaskV2Client.Invalid("terminal resize");
        return _client.SendAsync(HttpMethod.Post, $"{SessionPath(target, sessionId)}/resize",
            new JsonObject { ["columns"] = resize.Columns, ["rows"] = resize.Rows }, cancellationToken);
    }

    public Task<JsonNode?> CloseAsync(McpOperatorV2Target target, string sessionId, CancellationToken cancellationToken = default) =>
        _client.SendAsync(HttpMethod.Post, $"{SessionPath(target, sessionId)}/close", null, cancellationToken);

    private Task<JsonNode?> SendOpenAsync(McpOperatorV2Target target, McpOperatorTerminalOpenV2 open, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var body = OpenBody(open);
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, Path(target, confirmed ? "/sessions/confirm" : "/sessions/preview"), body, cancellationToken);
    }

    private static JsonObject OpenBody(McpOperatorTerminalOpenV2 open)
    {
        ArgumentNullException.ThrowIfNull(open);
        var shell = open.Shell?.Trim();
        var workingDirectory = open.WorkingDirectory?.Trim();
        if (!McpOperatorTaskV2Client.IsText(shell, 32) ||
            !McpOperatorTaskV2Client.IsText(workingDirectory, 4096) ||
            open.Columns is < 40 or > 300 ||
            open.Rows is < 10 or > 120)
        {
            throw McpOperatorTaskV2Client.Invalid("terminal open");
        }

        return new JsonObject
        {
            ["shell"] = shell,
            ["workingDirectory"] = workingDirectory,
            ["columns"] = open.Columns,
            ["rows"] = open.Rows
        };
    }

    private static void ValidateInput(string? input)
    {
        if (input is null || input.Length is 0 or > MaximumInputBytes ||
            input.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw McpOperatorTaskV2Client.Invalid("terminal input");
        }

        try
        {
            if (Utf8.GetByteCount(input) > MaximumInputBytes) throw McpOperatorTaskV2Client.Invalid("terminal input");
        }
        catch (EncoderFallbackException)
        {
            throw McpOperatorTaskV2Client.Invalid("terminal input");
        }
    }

    private string SessionPath(McpOperatorV2Target target, string sessionId) =>
        $"{Path(target, "/sessions")}/{ValidateSessionId(sessionId)}";

    private static string ValidateSessionId(string? sessionId) =>
        sessionId is not null && McpOperatorTaskV2Client.IsHex(sessionId, 32)
            ? sessionId
            : throw McpOperatorTaskV2Client.Invalid("terminal sessionId");

    private string Path(McpOperatorV2Target target, string suffix)
    {
        target.Validate();
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "terminal");
        return $"/api/v2/mcp/operator/agents/{target.TenantId.ToString(CultureInfo.InvariantCulture)}/{target.AgentId:D}/terminal{suffix}";
    }
}
