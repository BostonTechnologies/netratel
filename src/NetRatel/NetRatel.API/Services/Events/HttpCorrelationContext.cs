using NetRatel.Application.Events;

namespace NetRatel.API.Services.Events;

public sealed class HttpCorrelationContext(IHttpContextAccessor httpContextAccessor) : ICorrelationContext
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public string? Current
    {
        get
        {
            var context = _httpContextAccessor.HttpContext;
            if (context is null)
                return null;

            if (context.Items.TryGetValue(CorrelationConstants.HttpContextItemKey, out var value) && value is string stored && !string.IsNullOrWhiteSpace(stored))
                return stored;

            var header = context.Request.Headers[CorrelationConstants.HeaderName].FirstOrDefault();
            return string.IsNullOrWhiteSpace(header) ? null : header;
        }
    }

    public string GetOrCreate()
    {
        var existing = Current;
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var generated = $"corr-{Guid.NewGuid():N}";
        var context = _httpContextAccessor.HttpContext;
        if (context is not null)
        {
            context.Items[CorrelationConstants.HttpContextItemKey] = generated;
            context.Request.Headers[CorrelationConstants.HeaderName] = generated;
            context.Response.Headers[CorrelationConstants.HeaderName] = generated;
        }

        return generated;
    }
}
