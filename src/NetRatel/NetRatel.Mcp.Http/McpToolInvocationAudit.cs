using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NetRatel.Mcp.Core;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;

namespace NetRatel.Mcp.Http;

/// <summary>
/// Records a bounded audit event for each parsed MCP tool call without logging
/// authorization headers, raw request bodies, or tool result payloads.
/// </summary>
public sealed class McpToolInvocationAudit(NetRatelMcpHostContext hostContext, ILogger<McpToolInvocationAudit> logger)
{
    public async ValueTask<CallToolResult> InvokeAsync(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            var outcome = DescribeOutcome(result);
            logger.LogInformation(
                "MCP tool invocation completed. Instance={Instance} Transport={Transport} Tool={Tool} Operation={Operation} Caller={Caller} Groups={Groups} Scopes={Scopes} Success={Success} ResultStatus={ResultStatus} AuditSchemaVersion={AuditSchemaVersion} TransportSucceeded={TransportSucceeded} ProtocolSucceeded={ProtocolSucceeded} OperationSucceeded={OperationSucceeded} OverallSucceeded={OverallSucceeded} DurationMs={DurationMs} TraceId={TraceId}",
                hostContext.Target.Instance,
                hostContext.Transport,
                Token(context.Params.Name),
                Operation(context.Params.Arguments),
                Caller(context.User),
                Claims(context.User, "groups"),
                Scopes(context.User),
                result.IsError is not true,
                outcome.ResultStatus,
                2,
                outcome.TransportSucceeded,
                outcome.ProtocolSucceeded,
                outcome.OperationSucceeded,
                outcome.OverallSucceeded,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                Activity.Current?.TraceId.ToString());
            return result;
        }
        catch (Exception)
        {
            logger.LogWarning(
                "MCP tool invocation failed. Instance={Instance} Transport={Transport} Tool={Tool} Operation={Operation} Caller={Caller} Groups={Groups} Scopes={Scopes} DurationMs={DurationMs} TraceId={TraceId} AuditSchemaVersion={AuditSchemaVersion} TransportSucceeded={TransportSucceeded} ProtocolSucceeded={ProtocolSucceeded} OperationSucceeded={OperationSucceeded} OverallSucceeded={OverallSucceeded}",
                hostContext.Target.Instance,
                hostContext.Transport,
                Token(context.Params.Name),
                Operation(context.Params.Arguments),
                Caller(context.User),
                Claims(context.User, "groups"),
                Scopes(context.User),
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                Activity.Current?.TraceId.ToString(),
                2, false, false, false, false);
            throw;
        }
    }

    /// <summary>Legacy Success remains IsError based; explicit outcomes prevent structured failures appearing successful.</summary>
    public sealed record Outcome(bool TransportSucceeded, bool ProtocolSucceeded, bool? OperationSucceeded, bool? OverallSucceeded, string ResultStatus);
    public static Outcome DescribeOutcome(CallToolResult result)
    {
        bool? operationSucceeded = result.IsError is true ? false : null;
        var resultStatus = result.IsError is true ? "error" : "unknown";
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } content)
        {
            if (content.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String) resultStatus = Token(status.GetString());
            if (content.TryGetProperty("success", out var success) && success.ValueKind is JsonValueKind.True or JsonValueKind.False)
                operationSucceeded = result.IsError is not true && success.GetBoolean();
            if (resultStatus is "failed" or "invalid_request" or "confirmation_required" or "error") operationSucceeded = false;
        }
        return new(true, true, operationSucceeded, operationSucceeded, resultStatus);
    }

    private static string Operation(IDictionary<string, JsonElement>? arguments)
        => arguments is not null && arguments.TryGetValue("operation", out var operation) && operation.ValueKind == JsonValueKind.String
            ? Token(operation.GetString())
            : "unknown";

    private static string Caller(ClaimsPrincipal? user)
        => Token(user?.FindFirstValue(ClaimTypes.NameIdentifier)
                 ?? user?.FindFirstValue("sub")
                 ?? user?.FindFirstValue("client_id")
                 ?? user?.FindFirstValue("azp")
                 ?? "anonymous");

    private static string[] Scopes(ClaimsPrincipal? user)
        => user?.FindAll("scope").Concat(user.FindAll("scp"))
               .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
               .Select(Token).Where(value => value != "unknown").Distinct(StringComparer.Ordinal).Order().ToArray()
           ?? [];

    private static string[] Claims(ClaimsPrincipal? user, string type)
        => user?.FindAll(type).Select(claim => Token(claim.Value)).Where(value => value != "unknown").Distinct(StringComparer.Ordinal).Order().ToArray()
           ?? [];

    private static string Token(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length > 128
            ? "unknown"
            : value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '/' or '.' or ':') ? value : "unknown";
}
