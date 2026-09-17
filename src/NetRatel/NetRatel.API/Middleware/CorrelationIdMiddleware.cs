using NetRatel.Application.Events;

namespace NetRatel.API.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[CorrelationConstants.HeaderName].FirstOrDefault();
        var correlationId = string.IsNullOrWhiteSpace(incoming)
            ? $"corr-{Guid.NewGuid():N}"
            : incoming!;

        context.Items[CorrelationConstants.HttpContextItemKey] = correlationId;
        context.Request.Headers[CorrelationConstants.HeaderName] = correlationId;
        context.Response.Headers[CorrelationConstants.HeaderName] = correlationId;

        await _next(context);
    }
}
