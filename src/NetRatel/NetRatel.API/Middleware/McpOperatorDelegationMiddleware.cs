using NetRatel.Shared.Operations;

namespace NetRatel.API.Middleware;

/// <summary>
/// Verifies a caller-attribution assertion from the isolated MCP host. This is
/// intentionally separate from API authentication: the API still authenticates
/// the MCP service using its own app token, and no external bearer is relayed.
/// New operator endpoints must explicitly require a value from this middleware.
/// </summary>
public sealed class McpOperatorDelegationMiddleware(
    RequestDelegate next,
    McpOperatorDelegationOptions options,
    McpOperatorDelegationTokenService tokens)
{
    public const string HttpContextItemKey = "netratel.mcp.operator.delegation";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.Enabled)
        {
            await next(context);
            return;
        }

        var headers = context.Request.Headers[McpOperatorDelegationOptions.HeaderName];
        if (headers.Count == 0)
        {
            await next(context);
            return;
        }

        if (headers.Count != 1 || !tokens.TryValidate(headers[0], out var delegation))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "delegated_identity_invalid",
                layer = "delegation"
            }, context.RequestAborted);
            return;
        }

        context.Items[HttpContextItemKey] = delegation;
        await next(context);
    }
}

public static class McpOperatorDelegationHttpContextExtensions
{
    public static bool TryGetMcpOperatorDelegation(this HttpContext context, out McpOperatorDelegation? delegation)
    {
        ArgumentNullException.ThrowIfNull(context);
        delegation = context.Items.TryGetValue(McpOperatorDelegationMiddleware.HttpContextItemKey, out var value)
            ? value as McpOperatorDelegation
            : null;
        return delegation is not null;
    }
}
