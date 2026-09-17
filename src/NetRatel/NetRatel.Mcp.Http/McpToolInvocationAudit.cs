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
            logger.LogInformation(
                "MCP tool invocation completed. Instance={Instance} Transport={Transport} Tool={Tool} Operation={Operation} Caller={Caller} Groups={Groups} Scopes={Scopes} Success={Success} ResultStatus={ResultStatus} DurationMs={DurationMs} TraceId={TraceId}",
                hostContext.Target.Instance,
                hostContext.Transport,
                Token(context.Params.Name),
                Operation(context.Params.Arguments),
                Caller(context.User),
                Claims(context.User, "groups"),
                Scopes(context.User),
                result.IsError is not true,
                ResultStatus(result),
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                Activity.Current?.TraceId.ToString());
            return result;
        }
        catch (Exception)
        {
            logger.LogWarning(
                "MCP tool invocation failed. Instance={Instance} Transport={Transport} Tool={Tool} Operation={Operation} Caller={Caller} Groups={Groups} Scopes={Scopes} DurationMs={DurationMs} TraceId={TraceId}",
                hostContext.Target.Instance,
                hostContext.Transport,
                Token(context.Params.Name),
                Operation(context.Params.Arguments),
                Caller(context.User),
                Claims(context.User, "groups"),
                Scopes(context.User),
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                Activity.Current?.TraceId.ToString());
            throw;
        }
    }

    private static string ResultStatus(CallToolResult result)
        => result.StructuredContent is { ValueKind: JsonValueKind.Object } content && content.TryGetProperty("status", out var status)
            ? Token(status.GetString())
            : result.IsError is true ? "error" : "unknown";

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
