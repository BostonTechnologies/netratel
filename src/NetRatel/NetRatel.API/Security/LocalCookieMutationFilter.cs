namespace NetRatel.API.Security;

internal static class LocalCookieMutationFilter
{
    public static ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var method = http.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method) &&
            http.User.HasClaim("auth_mode", "local") && http.Request.Headers["X-NetRatel-Account-Request"] != "1")
            return ValueTask.FromResult<object?>(Results.Forbid());

        return next(context);
    }
}
